# Event seam (phase 1) Implementation Plan

> **For agentic workers:** implement this plan task by task. Steps use checkbox (`- [ ]`) syntax
> for tracking. Every code task is test-first: write the test, run it, *see it fail with a
> wrong-value message*, then implement.

**Goal:** Any local process can push values into DeskWall over a named pipe, a layout can bind to
them with no declaration, and the daemon repaints promptly without polling.

**Architecture:** An event is a patch to the value tree plus a request to repaint. `SourceRegistry`
is already a flat map of name to values, so a pushed provider is one more entry in it. A bus in
Core accepts envelopes, merges them into provider records, coalesces the resulting wakes, keeps a
diagnostics ring and persists every provider's last record. The daemon owns the pipe and turns a
coalesced wake into the `WakeKind.SourceCompleted` it already posts for async sources. The designer
reads and watches the persisted file, which is what gives it a binding picker for providers it has
never been told about.

**Tech Stack:** .NET 10, C#, native AOT for Core and the daemon (JIT for the designer), xUnit,
`System.Text.Json` via `JsonDocument` (no reflection), `NamedPipeServerStream`.

**Spec:** `docs/superpowers/specs/2026-09-21-event-seam-design.md`. Read it before Task 1; this
plan argues from it and does not restate its reasoning.

## Global Constraints

- Target `net10.0-windows10.0.19041.0`. `dotnet build` must end **0 warnings** (`TreatWarningsAsErrors`, AOT analyzers).
- Core is native-AOT and has no logger. **No reflection-based JSON**: use `JsonDocument`, and reuse `DeskWall.Core.Sources.JsonValues.Parse(string, IReadOnlySet<string>)` to turn a payload into a `RecordValue`.
- Core code runs on the daemon's tick thread, which is also the message pump. Nothing added here may block it.
- Tests run under `DESKWALL_HOME` (set by `tests/.../AssemblyInfo.cs`). A test that writes to the real `%LOCALAPPDATA%\DeskWall` is a defect.
- ASCII only in `.cs`/`.xaml`; no em dashes. Comments explain why, not what.
- Time comes from `DeskWall.Core.Sources.IClock`, never `DateTimeOffset.Now` directly.
- Commit messages end with:
  `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`
  `Claude-Session: https://claude.ai/code/session_018TNZNGHy4oVCFTEZUAKTPf`

## File Structure

| File | Responsibility |
|---|---|
| `src/DeskWall.Core/Events/EventEnvelope.cs` | The parsed envelope, and `Parse` from one JSON line. |
| `src/DeskWall.Core/Events/ProviderRecord.cs` | One provider's accumulated state, and the merge that produces its `RecordValue`. |
| `src/DeskWall.Core/Events/EventBus.cs` | Accepts envelopes, owns providers, coalesces wakes, keeps the diagnostics ring. |
| `src/DeskWall.Core/Events/EventStore.cs` | Load and save `events.json`. |
| `src/DeskWall.Core/Events/ProviderManifest.cs` | Manifest shape, loader, and the shipped/runtime catalog. |
| `src/DeskWall.Core/Sources/SourceRegistry.cs` | *Modified*: providers merge into `Tree()`, layout sources win a clash. |
| `src/DeskWall.Daemon/Host/EventPipeServer.cs` | The named pipe, its ACL, and line framing. |
| `src/DeskWall.Daemon/DaemonLoop.cs` | *Modified*: owns the bus and the pipe, wires the wake. |
| `src/DeskWall.Designer/Views/ProvidersPanel.xaml(.cs)` | Lists providers, their values, the ring; Forget / Describe / Send test event. |

---

### Task 1: The envelope

**Files:**
- Create: `src/DeskWall.Core/Events/EventEnvelope.cs`
- Test: `tests/DeskWall.Core.Tests/Events/EventEnvelopeTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace DeskWall.Core.Events;

  public sealed record EventEnvelope(
      string Source,
      RecordValue Data,
      string? Type,
      string? Subject,
      string? Id,
      DateTimeOffset? SentAt,
      bool Replace,
      bool Wake);

  public static class EventEnvelopeParser
  {
      /// <summary>Null result means rejected; reason is never null then.</summary>
      public static (EventEnvelope? Event, string? Reason) Parse(string line);
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
public class EventEnvelopeTests
{
    private static (EventEnvelope? e, string? why) P(string s) => EventEnvelopeParser.Parse(s);

    [Fact]
    public void A_Minimal_Event_Parses()
    {
        var (e, why) = P("""{"source":"build","data":{"status":"green","failures":0}}""");
        Assert.Null(why);
        Assert.Equal("build", e!.Source);
        Assert.Equal("green", ((TextValue)e.Data.Get("status")!).Text);
        Assert.Equal(0, ((NumberValue)e.Data.Get("failures")!).Number);
        Assert.True(e.Wake);            // waking is the default
        Assert.False(e.Replace);        // merging is the default
    }

    [Fact]
    public void Unknown_Attributes_Are_Ignored_Not_Rejected()
    {
        var (e, why) = P("""{"specversion":"1.0","datacontenttype":"application/json","source":"a","data":{}}""");
        Assert.Null(why);
        Assert.NotNull(e);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("not json", "json")]
    [InlineData("""{"data":{}}""", "source")]
    [InlineData("""{"source":"a"}""", "data")]
    [InlineData("""{"source":"a","data":5}""", "data")]
    [InlineData("""{"source":"","data":{}}""", "source")]
    public void A_Bad_Envelope_Is_Rejected_With_A_Reason_Naming_The_Problem(string line, string word)
    {
        var (e, why) = P(line);
        Assert.Null(e);
        Assert.Contains(word, why!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Optional_Attributes_Are_Carried()
    {
        var (e, _) = P("""
            {"source":"a","data":{},"type":"build.done","subject":"main","id":"7",
             "time":"2026-09-21T20:00:00+01:00","replace":true,"wake":false}
            """);
        Assert.Equal("build.done", e!.Type);
        Assert.Equal("main", e.Subject);
        Assert.Equal("7", e.Id);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.FromHours(1)), e.SentAt);
        Assert.True(e.Replace);
        Assert.False(e.Wake);
    }

    /// <summary>A source name reaches the value tree as a binding path segment, so it has to be
    /// one: BindingParser's names are a letter or underscore then letters, digits, _ or -.</summary>
    [Theory]
    [InlineData("my build")]
    [InlineData("9build")]
    [InlineData("build.sub")]
    public void A_Source_Name_That_Is_Not_A_Binding_Name_Is_Rejected(string name)
    {
        var (e, why) = P($$"""{"source":"{{name}}","data":{}}""");
        Assert.Null(e);
        Assert.Contains("name", why!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_Unparseable_Time_Is_Dropped_Rather_Than_Failing_The_Event()
    {
        var (e, why) = P("""{"source":"a","data":{},"time":"yesterday"}""");
        Assert.Null(why);
        Assert.Null(e!.SentAt);
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/DeskWall.Core.Tests --filter "FullyQualifiedName~EventEnvelopeTests"`
Expected: does not compile (the type does not exist). Add the type with `throw new
NotImplementedException()` and re-run so the failure is a real assertion failure before you
implement.

- [ ] **Step 3: Implement**

`JsonDocument.Parse`, read the known properties, `JsonValues.Parse(dataElement.GetRawText(), emptySet)`
for the payload. Reject with a reason naming the offending attribute. Catch `JsonException` and
return the reason, never throw. Validate the source name with the same rule `BindingParser.ReadName`
uses; put the predicate somewhere both can call rather than copying the character class.

- [ ] **Step 4: Run them and watch them pass**
- [ ] **Step 5: Commit** `git commit -m "Events: the envelope, a CloudEvents subset"`

---

### Task 2: Provider records and the merge

**Files:**
- Create: `src/DeskWall.Core/Events/ProviderRecord.cs`
- Test: `tests/DeskWall.Core.Tests/Events/ProviderRecordTests.cs`

**Interfaces:**
- Consumes: `EventEnvelope` (Task 1).
- Produces:
  ```csharp
  public sealed record ProviderRecord(
      string Name,
      RecordValue Data,
      string? Type, string? Subject, string? Id,
      DateTimeOffset? SentAt,
      DateTimeOffset ReceivedAt)
  {
      public static ProviderRecord Empty(string name, DateTimeOffset at);
      /// <summary>Merge (or replace) the payload and take the new metadata.</summary>
      public ProviderRecord Apply(EventEnvelope e, DateTimeOffset at);
      /// <summary>What the value tree sees. ageSeconds is computed from `now`, not stored.</summary>
      public RecordValue ToValues(DateTimeOffset now);
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Data_Merges_So_A_Partial_Event_Does_Not_Blank_A_Field()
// apply {level:0.4, device:"Speakers"} then {level:0.2}; device survives, level is 0.2

[Fact] public void Replace_Drops_The_Fields_The_New_Event_Does_Not_Carry()
// same two events with replace:true on the second; device is gone

[Fact] public void ToValues_Publishes_Data_Metadata_And_An_Age()
// data is a RecordValue under "data"; receivedAt a TimeValue; ageSeconds a NumberValue
// computed as now - receivedAt, rounded, so a fixed clock 90 s later gives 90

[Fact] public void Absent_Metadata_Is_Absent_Not_Empty()
// no type/subject/id/sentAt in the envelope means Get("type") is null, per the
// project's "absent keys are absent" rule

[Fact] public void Nested_Objects_In_The_Payload_Merge_Only_At_The_Top_Level()
// {a:{x:1}} then {a:{y:2}} leaves a = {y:2}; document it, do not deep merge
```

- [ ] **Step 2: Run and watch them fail**
- [ ] **Step 3: Implement**. Top-level merge only; a deep merge has no obvious rule for lists and nobody has asked for it.
- [ ] **Step 4: Run and watch them pass**
- [ ] **Step 5: Commit** `git commit -m "Events: a provider's record, merged"`

---

### Task 3: The bus

**Files:**
- Create: `src/DeskWall.Core/Events/EventBus.cs`
- Test: `tests/DeskWall.Core.Tests/Events/EventBusTests.cs`

**Interfaces:**
- Consumes: `EventEnvelope`, `EventEnvelopeParser`, `ProviderRecord`.
- Produces:
  ```csharp
  public sealed record EventLogEntry(DateTimeOffset At, string? Source, bool Accepted, string? Reason, string Line);

  public sealed class EventBus : IDisposable
  {
      public const int RingSize = 50;
      public static readonly TimeSpan DefaultCoalesce = TimeSpan.FromMilliseconds(400);

      public EventBus(IClock clock, TimeSpan coalesce, bool autoWake = true);

      /// <summary>Coalesced. Never raised on the caller's thread when autoWake is true.</summary>
      public event Action? WakeRequested;
      /// <summary>Raised for every accepted event, uncoalesced, for the designer's panel.</summary>
      public event Action<string>? ProviderChanged;

      public bool Publish(string line);                 // parses, applies, logs; false if rejected
      public bool Publish(EventEnvelope e);
      public IReadOnlyDictionary<string, ProviderRecord> Providers { get; }
      public IReadOnlyList<EventLogEntry> Recent { get; }
      public void Forget(string provider);
      public void Restore(IEnumerable<ProviderRecord> records);
      /// <summary>Fires a due coalesced wake. Public so tests drive the clock instead of sleeping.</summary>
      public bool PumpWake(DateTimeOffset now);
  }
  ```

- [ ] **Step 1: Write the failing tests** (`autoWake: false` throughout, driving `PumpWake`)

```csharp
[Fact] public void An_Accepted_Event_Lands_In_Providers_And_The_Ring()
[Fact] public void A_Rejected_Line_Is_In_The_Ring_With_Its_Reason_And_No_Provider_Appears()
[Fact] public void The_Ring_Keeps_The_Last_Fifty_And_Drops_The_Oldest()
[Fact] public void A_Burst_Inside_The_Window_Wakes_Once()
// publish 50 events at t, PumpWake(t + 399ms) is false, PumpWake(t + 400ms) is true once,
// PumpWake again is false: the trailing wake fires and does not repeat
[Fact] public void An_Event_Arriving_After_A_Wake_Starts_A_New_Window()
[Fact] public void Wake_False_Updates_The_Provider_Without_Ever_Waking()
[Fact] public void An_Exact_Repeat_Of_The_Previous_Id_Is_Dropped()
[Fact] public void Forget_Removes_A_Provider_And_Is_Silent_About_An_Unknown_One()
[Fact] public void Restore_Puts_Records_Back_Without_Waking()
```

- [ ] **Step 2: Run and watch them fail**
- [ ] **Step 3: Implement.** Take one lock around providers and the ring; the pipe thread and the tick thread both touch them. With `autoWake` the coalescing uses one `System.Threading.Timer` created lazily, disposed with the bus, and its callback is wrapped exactly as `HardwareSource.SampleOnce` wraps its own: an exception out of a timer callback takes the process down.
- [ ] **Step 4: Run and watch them pass**
- [ ] **Step 5: Commit** `git commit -m "Events: the bus, coalescing and a diagnostics ring"`

---

### Task 4: Persistence

**Files:**
- Create: `src/DeskWall.Core/Events/EventStore.cs`
- Test: `tests/DeskWall.Core.Tests/Events/EventStoreTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class EventStore
  {
      public static string Path => Paths.InRuntime("events.json");
      public static IReadOnlyList<ProviderRecord> Load();     // never throws; missing or corrupt gives empty
      public static void Save(IEnumerable<ProviderRecord> records);
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void A_Round_Trip_Keeps_Data_And_Metadata()
[Fact] public void A_Missing_File_Loads_As_Empty()
[Fact] public void A_Corrupt_File_Loads_As_Empty_Rather_Than_Throwing()
[Fact] public void Save_Writes_Atomically_So_A_Crash_Cannot_Leave_A_Half_File()
// write, then assert no .tmp is left behind and the file parses
```

- [ ] **Step 2: Run and watch them fail**
- [ ] **Step 3: Implement.** `JsonDocument`/`Utf8JsonWriter`, no reflection. Write to a temp file in the same directory then `File.Move(overwrite: true)`. Value types round trip through the same shapes `JsonValues.Parse` produces; a `TimeValue` writes as an ISO-8601 round-trip string.
- [ ] **Step 4: Run and watch them pass**
- [ ] **Step 5: Commit** `git commit -m "Events: remember every provider across a restart"`

---

### Task 5: Providers in the value tree

**Files:**
- Modify: `src/DeskWall.Core/Sources/SourceRegistry.cs`
- Test: `tests/DeskWall.Core.Tests/Sources/SourceRegistryTests.cs` (add to the existing file)

**Interfaces:**
- Produces: `SourceRegistry.SetProvider(string name, RecordValue values)`, `RemoveProvider(string name)`, and both `Tree()` overloads including providers.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void A_Provider_Appears_In_The_Tree_Under_Its_Name()
[Fact] public void A_Layout_Source_Of_The_Same_Name_Wins_And_The_Provider_Is_Not_Merged()
[Fact] public void A_Provider_Is_Not_Subject_To_Schedule_Staleness()
// Tree(sources, now) with StaleAfter set still publishes a provider: it has no schedule
```

- [ ] **Step 2: Run and watch them fail**
- [ ] **Step 3: Implement.** Keep providers in their own dictionary; `Tree()` writes providers first, then layout sources over the top, so a clash resolves to the layout source. The clash is reported by the caller, not thrown here.
- [ ] **Step 4: Run and watch them pass**
- [ ] **Step 5: Commit** `git commit -m "Events: a provider is one more entry in the registry"`

---

### Task 6: Provider manifests

**Files:**
- Create: `src/DeskWall.Core/Events/ProviderManifest.cs`
- Test: `tests/DeskWall.Core.Tests/Events/ProviderManifestTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record ProviderField(string Path, string Type, string? Example, string? Description);

  public sealed class ProviderManifest
  {
      public required string Name { get; init; }
      public required string Description { get; init; }
      public IReadOnlyList<ProviderField> Fields { get; init; } = [];
      public int? ExpectEverySeconds { get; init; }
      public string? Path { get; init; }
      public static ProviderManifest Load(string path);
      public static string ToJson(ProviderManifest m);
  }

  public static class ProviderCatalog
  {
      public static string ShippedDir { get; }   // AppContext.BaseDirectory + "providers"
      public static string UserDir { get; }      // Paths.InRuntime("providers")
      public static IReadOnlyList<ProviderManifest> Load(params string[] dirs);
  }
  ```

- [ ] **Step 1: Write the failing tests**. Mirror `WidgetCatalogTests`: a later directory wins the same key but keeps the first position; a missing directory is skipped, not an error; a malformed file names itself in the exception.
- [ ] **Step 2: Run and watch them fail**
- [ ] **Step 3: Implement**, following `WidgetCatalog.Load` line for line where the shape is the same.
- [ ] **Step 4: Run and watch them pass**
- [ ] **Step 5: Commit** `git commit -m "Events: provider manifests, for what observation cannot say"`

---

### Task 7: The pipe, and the daemon

**Files:**
- Create: `src/DeskWall.Daemon/Host/EventPipeServer.cs`
- Modify: `src/DeskWall.Daemon/DaemonLoop.cs`
- Test: `tests/DeskWall.Core.Tests/Events/EventPipeServerTests.cs` (move to the daemon's own test project only if one exists; otherwise reference the daemon project as the budget tests already do)

**Interfaces:**
- Consumes: `EventBus.Publish(string)`.
- Produces:
  ```csharp
  public sealed class EventPipeServer : IDisposable
  {
      public const string PipeName = "DeskWall.Events";
      public EventPipeServer(Func<string, bool> onLine, Action<string>? onError = null);
      public void Start();
      public void Dispose();
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task A_Line_Written_To_The_Pipe_Reaches_The_Callback()
[Fact] public async Task Two_Producers_Can_Connect_At_Once()
[Fact] public async Task A_Producer_Can_Stream_Several_Lines_On_One_Connection()
[Fact] public async Task A_Malformed_Line_Does_Not_Drop_The_Connection()
[Fact] public async Task Dispose_Stops_The_Listener_And_Is_Idempotent()
```

Give them `[Trait("Category", "Pipe")]` so they can be excluded if they prove flaky in CI, and use
a per-test pipe name suffix so two tests never collide.

- [ ] **Step 2: Run and watch them fail**
- [ ] **Step 3: Implement.** `NamedPipeServerStreamAcl.Create` with a `PipeSecurity` granting only
  `WindowsIdentity.GetCurrent().User` read/write, so another user's process cannot write. Accept up
  to four concurrent instances. One listener thread blocked on `WaitForConnectionAsync`; read with a
  `StreamReader` line by line. Nothing may throw out of the loop: log through `onError` and carry on.
- [ ] **Step 4: Run and watch them pass**
- [ ] **Step 5: Wire the daemon.** In `DaemonLoop`, construct the bus with `EventBus.DefaultCoalesce`,
  `bus.Restore(EventStore.Load())`, start the server with `line => bus.Publish(line)`, and add
  `bus.WakeRequested += () => _win?.Post(WakeKind.SourceCompleted);` beside the existing
  `s.Completed += ...` line. Feed providers into the registry before each resolve, and save the
  store on shutdown and at most every few seconds. Log a rejected event at INFO with its reason.
- [ ] **Step 6: Verify by hand** against a scratch `DESKWALL_HOME`, never the live runtime dir:
  start `deskwall run --home <scratch>`, push an event with the PowerShell one-liner from Task 9,
  and confirm from the log that the daemon woke and the provider landed.
- [ ] **Step 7: Commit** `git commit -m "Events: the pipe, and the daemon that listens on it"`

---

### Task 8: The designer's providers panel

**Files:**
- Create: `src/DeskWall.Designer/Views/ProvidersPanel.xaml(.cs)`
- Modify: `src/DeskWall.Designer/Views/MainWindow.xaml(.cs)`, `src/DeskWall.Designer/Model/LiveSources.cs`
- Test: `tests/DeskWall.Designer.Tests/Events/ProviderViewTests.cs`

**Interfaces:**
- Consumes: `EventStore.Load`, `ProviderCatalog.Load`, `EventBus`.

- [ ] **Step 1: Write the failing model tests** (no UI automation, per the designer's testing rule)

```csharp
[Fact] public void Providers_Come_From_Manifests_And_Remembered_Records_Together()
[Fact] public void A_Remembered_Field_Wins_For_Value_And_The_Manifest_Wins_For_Description()
[Fact] public void Describe_Writes_A_Manifest_Seeded_From_The_Observed_Fields()
[Fact] public void Forget_Removes_The_Remembered_Record_And_Leaves_A_Manifest_Alone()
```

- [ ] **Step 2: Run and watch them fail**
- [ ] **Step 3: Implement the model**, then the panel: one row per provider, its values in the
  existing `ValueTreeView`, the recent ring underneath, and the three actions. The panel **watches
  `EventStore.Path`** with a `FileSystemWatcher` the way `LayoutWatcher` watches layout files, so a
  producer started while the designer is open appears without a restart. Debounce the reload.
- [ ] **Step 4: Run and watch the tests pass; build with 0 warnings**
- [ ] **Step 5: Merge providers into the designer's own value tree** so the binding picker offers
  them, and a bound component renders in the preview.
- [ ] **Step 6: Commit** `git commit -m "Designer: providers, and a binding picker that knows them"`

---

### Task 9: Docs, an example, and the measurements

**Files:**
- Modify: `docs/sources.md`, `README.md`, `CLAUDE.md`
- Create: `docs/superpowers/plans/2026-09-21-event-seam-results.md`

- [ ] **Step 1: Write the `docs/sources.md` section.** What an event is, the envelope table, the
  fields a provider publishes, merge versus replace, the wake flag, that a provider needs no
  declaration, what a manifest adds, and the security statement from spec section 9.
- [ ] **Step 2: Write the one-liners** and check that each actually works against a scratch daemon:

```powershell
$p = New-Object IO.Pipes.NamedPipeClientStream '.', 'DeskWall.Events', 'Out'
$p.Connect(2000); $w = New-Object IO.StreamWriter $p; $w.AutoFlush = $true
$w.WriteLine('{"source":"build","data":{"status":"green"}}')
$p.Dispose()
```

- [ ] **Step 3: Measure and record** in the results file: handles and threads before and after a
  layout using a provider is loaded; that an idle machine still wakes once a minute; the number of
  repaints a fifty-event burst causes and the final value; and the end-to-end cost of one event.
- [ ] **Step 4: Add the CLAUDE.md gotchas** you hit, in its voice, briefly.
- [ ] **Step 5: Commit** `git commit -m "Events: docs, an example a user can paste, and the numbers"`

---

## Self-review against the spec

| Spec section | Task |
|---|---|
| 3, events patch state; no second subscription registry | 2, 5 |
| 3, no event source type; provider is a registry entry | 5 |
| 3, every provider remembered; Forget | 4, 8 |
| 3, collision: layout source wins | 5 |
| 4, the envelope | 1 |
| 5, provider fields and `ageSeconds` | 2 |
| 5, manifests and `expectEvery` | 6 (`expectEvery` is carried and shown; enforcing staleness from it is **phase 2**, noted here so it is not silently missing) |
| 6, the pipe, ACL, line framing | 7 |
| 7, coalescing, wake, ring, persistence | 3, 4, 7 |
| 8, designer panel, watching, three actions | 8 |
| 9, security statement | 9 |
| 10, measurements | 9 |
