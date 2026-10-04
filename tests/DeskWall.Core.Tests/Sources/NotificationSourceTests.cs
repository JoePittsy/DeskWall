using DeskWall.Core.Layout;
using DeskWall.Core.Sources;
using DeskWall.Core.Sources.Notifications;
using DeskWall.Core.Values;
using Microsoft.Extensions.Time.Testing;
using Xunit;

/// <summary>A reader whose centre is a list the test edits, and whose "the store was written" is a
/// method call. Counts reads and starts so the debounce and the confirming read are observable.</summary>
internal sealed class FakeNotificationReader : INotificationReader
{
    private int _reads;

    public event Action? Changed;

    public List<NotificationItem> Items { get; } = [];
    public NotificationAccess Access { get; set; } = NotificationAccess.Allowed;
    public bool CanPush { get; set; } = true;
    public bool EchoesReads { get; set; }
    public bool ThrowOnRead { get; set; }
    public int Starts { get; private set; }
    public int Disposals { get; private set; }
    public int Reads => Volatile.Read(ref _reads);

    public void Start() => Starts++;

    public Task<NotificationReading> ReadAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _reads);
        if (ThrowOnRead) throw new InvalidOperationException("reader fault");
        lock (Items)
            return Task.FromResult(Access is NotificationAccess.Allowed
                ? new NotificationReading(Access, Items.ToList())
                : new NotificationReading(Access, []));
    }

    public void Add(string app, string title, string text, DateTimeOffset created, string? appId = null)
    {
        lock (Items) Items.Add(new NotificationItem(app, appId ?? app + ".id", title, text, created));
    }

    public void Raise() => Changed?.Invoke();

    public void Dispose() => Disposals++;
}

public class NotificationSourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 9, 30, 12, TimeSpan.Zero);

    private sealed class Clock : IClock { public DateTimeOffset Now { get; set; } = T0; }

    /// <summary>The timers always run on a fake: a test that wants them to fire passes its own and
    /// advances it, so nothing here waits on real time.</summary>
    private static NotificationSource Make(FakeNotificationReader r, Clock? clock = null,
        string[]? include = null, string[]? exclude = null, int debounceMs = 20, int confirmMs = 60, FakeTimeProvider? time = null)
        => new("notifications", r, clock ?? new Clock(), include, exclude,
            debounce: TimeSpan.FromMilliseconds(debounceMs), confirm: TimeSpan.FromMilliseconds(confirmMs), time: time ?? new FakeTimeProvider(T0));

    private static RecordValue Refresh(NotificationSource s) => s.RefreshAsync(CancellationToken.None).AsTask().Result;

    private static string Text(RecordValue r, string f) => Assert.IsType<TextValue>(r.Fields[f]).Text;

    private static double Num(RecordValue r, string f) => Assert.IsType<NumberValue>(r.Fields[f]).Number;

    [Fact]
    public void Construction_Reads_Nothing_And_Starts_Nothing()
    {
        var r = new FakeNotificationReader();
        using var s = Make(r);
        Assert.Equal(0, r.Reads);
        Assert.Equal(0, r.Starts);
        Assert.Equal(T0, s.NextDue(null, T0));
    }

    [Fact]
    public void First_Refresh_Reads_Inline_Then_Starts_The_Reader()
    {
        var r = new FakeNotificationReader();
        r.Add("Teams", "Alex", "lunch?", T0.AddMinutes(-3));
        r.Add("Outlook", "Standup", "in 10 minutes", T0.AddMinutes(-1));
        r.Add("Teams", "Sam", "PR is up", T0.AddMinutes(-5));
        using var s = Make(r);

        var v = Refresh(s);

        Assert.Equal(1, r.Reads);
        Assert.Equal(1, r.Starts);
        Assert.Equal("ok", Text(v, "status"));
        Assert.Equal(3, Num(v, "count"));
        Assert.Equal("Outlook", Text(v, "latestApp"));
        Assert.Equal("Standup", Text(v, "latestTitle"));
        Assert.Equal("in 10 minutes", Text(v, "latestText"));
        Assert.Equal(T0.AddMinutes(-1), Assert.IsType<TimeValue>(v.Fields["latestAt"]).Time);
        Assert.Equal(T0, Assert.IsType<TimeValue>(v.Fields["changedAt"]).Time);

        var apps = Assert.IsType<ListValue>(v.Fields["apps"]);
        Assert.Equal("name", apps.KeyField);
        Assert.Equal(["Teams", "Outlook"], apps.Items.Select(a => Text(a, "name")));
        Assert.Equal([2.0, 1.0], apps.Items.Select(a => Num(a, "count")));
    }

    [Fact]
    public void Apps_With_Equal_Counts_Put_The_Most_Recent_First()
    {
        var r = new FakeNotificationReader();
        r.Add("Teams", "a", "", T0.AddMinutes(-9));
        r.Add("Outlook", "b", "", T0.AddMinutes(-2));
        using var s = Make(r);
        var apps = Assert.IsType<ListValue>(Refresh(s).Fields["apps"]);
        Assert.Equal(["Outlook", "Teams"], apps.Items.Select(a => Text(a, "name")));
    }

    [Fact]
    public void Empty_Centre_Publishes_Zero_And_Empty_Strings_Without_LatestAt()
    {
        var r = new FakeNotificationReader();
        using var s = Make(r);
        var v = Refresh(s);
        Assert.Equal(0, Num(v, "count"));
        Assert.Equal("", Text(v, "latestApp"));
        Assert.Equal("", Text(v, "latestTitle"));
        Assert.Empty(Assert.IsType<ListValue>(v.Fields["apps"]).Items);
        Assert.False(v.Fields.ContainsKey("latestAt"));
    }

    [Fact]
    public void Denied_Publishes_Status_Only_Starts_Nothing_And_Is_Never_Due_Again()
    {
        var r = new FakeNotificationReader { Access = NotificationAccess.Denied };
        using var s = Make(r);
        var v = Refresh(s);
        Assert.Equal("denied", Text(v, "status"));
        Assert.Single(v.Fields);
        Assert.Equal(0, r.Starts);
        Assert.Equal(DateTimeOffset.MaxValue, s.NextDue(T0, T0.AddHours(1)));
        r.Raise();
        Assert.Equal(1, r.Reads);
    }

    [Fact]
    public void Unavailable_Is_Reported_As_Such()
    {
        var r = new FakeNotificationReader { Access = NotificationAccess.Unavailable };
        using var s = Make(r);
        Assert.Equal("unavailable", Text(Refresh(s), "status"));
    }

    [Fact]
    public void A_First_Read_That_Throws_Fails_The_Refresh_So_The_Backoff_Applies()
    {
        var r = new FakeNotificationReader { ThrowOnRead = true };
        using var s = Make(r);
        Assert.ThrowsAny<Exception>(() => Refresh(s));
        Assert.Equal(0, r.Starts);
        r.ThrowOnRead = false;
        Assert.Equal("ok", Text(Refresh(s), "status"));
        Assert.Equal(1, r.Starts);
    }

    [Fact]
    public void Push_Reader_Is_Event_Only_After_The_First_Reading()
    {
        var r = new FakeNotificationReader();
        using var s = Make(r);
        Refresh(s);
        Assert.Equal(DateTimeOffset.MaxValue, s.NextDue(T0, T0.AddHours(5)));
        Refresh(s);
        Assert.Equal(1, r.Reads);
    }

    [Fact]
    public void Reader_That_Cannot_Push_Reads_On_Every_On_The_Whole_Minute()
    {
        var r = new FakeNotificationReader { CanPush = false };
        var clock = new Clock();
        using var s = new NotificationSource("n", r, clock, every: TimeSpan.FromSeconds(120));
        Refresh(s);
        var due = new DateTimeOffset(2026, 9, 30, 9, 33, 0, TimeSpan.Zero);
        Assert.Equal(due, s.NextDue(T0, T0.AddSeconds(1)));
        r.Add("Teams", "x", "", T0);
        clock.Now = due.AddSeconds(-1);
        Refresh(s);
        Assert.Equal(1, r.Reads);
        clock.Now = due;
        Assert.Equal(1, Num(Refresh(s), "count"));
        Assert.Equal(2, r.Reads);
    }

    [Fact]
    public void Store_Watcher_Sweeps_Only_While_Something_Could_Be_Dismissed()
    {
        var r = new FakeNotificationReader { EchoesReads = true };
        var clock = new Clock();
        using var s = Make(r, clock);
        Refresh(s);
        Assert.Null(s.SweepAt());
        Assert.Equal(DateTimeOffset.MaxValue, s.NextDue(T0, T0));

        r.Add("Teams", "Alex", "lunch?", T0);
        clock.Now = T0.AddSeconds(10);
        using var s2 = Make(r, clock);
        Refresh(s2);
        var due = new DateTimeOffset(2026, 9, 30, 9, 36, 0, TimeSpan.Zero);
        Assert.Equal(due, s2.SweepAt());
        Assert.Equal(due, s2.NextDue(clock.Now, clock.Now));

        // Dismissed without the watcher noticing: the sweep finds it, and an empty centre stops sweeping.
        lock (r.Items) r.Items.Clear();
        clock.Now = due;
        Assert.Equal(0, Num(Refresh(s2), "count"));
        Assert.Null(s2.SweepAt());
    }

    [Fact]
    public void Denied_Is_Never_Swept()
    {
        var r = new FakeNotificationReader { Access = NotificationAccess.Denied, CanPush = false };
        using var s = Make(r);
        Refresh(s);
        Assert.Null(s.SweepAt());
    }

    [Fact]
    public void A_Burst_Of_Store_Events_Is_One_Read_And_One_Signal()
    {
        var r = new FakeNotificationReader();
        var clock = new Clock();
        var time = new FakeTimeProvider(T0);
        using var s = Make(r, clock, debounceMs: 50, time: time);
        Refresh(s);
        var signals = 0;
        s.Changed += _ => signals++;

        r.Add("Teams", "Alex", "lunch?", T0);
        clock.Now = T0.AddSeconds(30);
        for (var i = 0; i < 30; i++)
        {
            r.Raise();                                       // each one restarts the debounce
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        time.Advance(TimeSpan.FromMilliseconds(48));         // 49 ms after the last event
        Assert.Equal(1, r.Reads);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, r.Reads);
        Assert.Equal(1, signals);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, r.Reads);
        Assert.Equal(1, signals);
        Assert.True(s.HasPending);
        Assert.Equal(T0.AddMinutes(1), s.NextDue(T0, T0.AddMinutes(1)));

        var v = Refresh(s);
        Assert.False(s.HasPending);
        Assert.Equal(2, r.Reads);
        Assert.Equal(1, Num(v, "count"));
        Assert.Equal(T0.AddSeconds(30), Assert.IsType<TimeValue>(v.Fields["changedAt"]).Time);
    }

    [Fact]
    public void A_Store_Write_That_Changes_Nothing_Is_Not_A_Signal()
    {
        var r = new FakeNotificationReader();
        r.Add("Teams", "Alex", "lunch?", T0);
        var time = new FakeTimeProvider(T0);
        using var s = Make(r, time: time);
        Refresh(s);
        var signals = 0;
        s.Changed += _ => signals++;

        r.Raise();
        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(2, r.Reads);
        Assert.Equal(0, signals);
        Assert.False(s.HasPending);
    }

    [Fact]
    public void A_Dismissal_Is_A_Change()
    {
        var r = new FakeNotificationReader();
        r.Add("Teams", "Alex", "lunch?", T0);
        var time = new FakeTimeProvider(T0);
        using var s = Make(r, time: time);
        Refresh(s);
        var signals = 0;
        s.Changed += _ => signals++;

        lock (r.Items) r.Items.Clear();
        r.Raise();
        time.Advance(TimeSpan.FromMilliseconds(20));

        Assert.Equal(1, signals);
        Assert.Equal(0, Num(Refresh(s), "count"));
    }

    [Fact]
    public void An_Echoing_Reader_Gets_One_Confirming_Read_And_No_More()
    {
        var r = new FakeNotificationReader { EchoesReads = true };
        var time = new FakeTimeProvider(T0);
        using var s = Make(r, debounceMs: 20, confirmMs: 80, time: time);
        Refresh(s);
        var signals = 0;
        s.Changed += _ => signals++;

        r.Raise();
        time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(2, r.Reads);
        // A toast that landed during the first read's suppressed echo: only the confirmation sees it.
        r.Add("Outlook", "Standup", "", T0);
        time.Advance(TimeSpan.FromMilliseconds(79));
        Assert.Equal(2, r.Reads);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(3, r.Reads);
        Assert.Equal(1, signals);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(3, r.Reads);
        Assert.Equal(1, Num(Refresh(s), "count"));
    }

    [Fact]
    public void A_Reader_That_Does_Not_Echo_Gets_No_Confirming_Read()
    {
        var r = new FakeNotificationReader { EchoesReads = false };
        var time = new FakeTimeProvider(T0);
        using var s = Make(r, debounceMs: 20, confirmMs: 40, time: time);
        Refresh(s);
        r.Raise();
        time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(2, r.Reads);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, r.Reads);
    }

    [Fact]
    public void A_Background_Read_That_Throws_Is_Counted_Not_Thrown()
    {
        var r = new FakeNotificationReader();
        var time = new FakeTimeProvider(T0);
        using var s = Make(r, time: time);
        Refresh(s);
        r.ThrowOnRead = true;
        r.Raise();
        time.Advance(TimeSpan.FromMilliseconds(20));         // the fake runs the callback here; an escape would throw
        Assert.Equal(1, s.ReadFaults);
        Assert.False(s.HasPending);
    }

    [Fact]
    public void Events_Before_The_First_Refresh_Are_Ignored()
    {
        var r = new FakeNotificationReader();
        var time = new FakeTimeProvider(T0);
        using var s = Make(r, time: time);
        r.Raise();
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, r.Reads);
    }

    [Fact]
    public void Include_And_Exclude_Match_Name_Or_App_Id_Case_Insensitively()
    {
        var r = new FakeNotificationReader();
        r.Add("Microsoft Teams", "a", "", T0.AddMinutes(-1), appId: "MSTeams_8wekyb3d8bbwe!MSTeams");
        r.Add("Outlook", "b", "", T0.AddMinutes(-2));
        r.Add("Discord", "c", "", T0);

        using (var inc = Make(r, include: ["microsoft teams", "OUTLOOK"]))
            Assert.Equal(2, Num(Refresh(inc), "count"));
        using (var byId = Make(r, include: ["msteams_8wekyb3d8bbwe!msteams"]))
            Assert.Equal("Microsoft Teams", Text(Refresh(byId), "latestApp"));
        using (var exc = Make(r, exclude: ["discord"]))
        {
            var v = Refresh(exc);
            Assert.Equal(2, Num(v, "count"));
            Assert.Equal("Microsoft Teams", Text(v, "latestApp"));
        }
    }

    [Fact]
    public void FromDef_Reads_The_Filter_Lists()
    {
        var def = new SourceDef
        {
            Name = "n", Type = "notifications",
            Settings = new() { ["include"] = " Teams , Outlook ,", ["exclude"] = "" },
        };
        using var s = NotificationSource.FromDef(def, new Clock());
        Assert.Equal("n", s.Name);
    }

    [Fact]
    public void Dispose_Is_Idempotent_And_Disposes_The_Reader_Once()
    {
        var r = new FakeNotificationReader();
        var s = Make(r);
        s.Dispose();
        s.Dispose();
        Assert.Equal(1, r.Disposals);
        r.Raise();
    }
}

public class SharedReadGateTests
{
    private static string UniqueName() => @"Local\DeskWall.Tests.Gate." + Guid.NewGuid().ToString("N");

    [Fact]
    public void Two_Gates_On_One_Name_See_Each_Others_Reads()
    {
        long now = 1_000;
        var name = UniqueName();
        using var daemon = new SharedReadGate(name, TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(10), () => now);
        using var designer = new SharedReadGate(name, TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(10), () => now);
        Assert.True(daemon.IsShared);
        Assert.False(designer.Suppressed);

        daemon.Enter();
        Assert.True(designer.Suppressed);
        now += 300;
        daemon.Exit();
        Assert.True(designer.Suppressed);
        now += 749;
        Assert.True(designer.Suppressed);
        now += 1;
        Assert.False(designer.Suppressed);
        Assert.False(daemon.Suppressed);
    }

    [Fact]
    public void A_Reader_That_Never_Exits_Blinds_Nobody_Past_The_Cap()
    {
        long now = 0;
        using var g = new SharedReadGate(UniqueName(), TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(10), () => now);
        g.Enter();
        now += 9_999;
        Assert.True(g.Suppressed);
        now += 1;
        Assert.False(g.Suppressed);
    }

    [Fact]
    public void Deadlines_Only_Move_Later_And_The_Count_Never_Goes_Negative()
    {
        long now = 0;
        var name = UniqueName();
        using var a = new SharedReadGate(name, TimeSpan.FromMilliseconds(1000), TimeSpan.FromSeconds(10), () => now);
        using var b = new SharedReadGate(name, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10), () => now);
        a.Enter();
        a.Exit();
        b.Enter();
        b.Exit();
        b.Exit();
        now += 500;
        Assert.True(b.Suppressed);
        b.Enter();
        Assert.True(a.Suppressed);
        b.Exit();
        now += 1000;
        Assert.False(a.Suppressed);
    }

    [Fact]
    public void Without_A_Name_The_Gate_Is_Process_Local()
    {
        long now = 0;
        using var g = new SharedReadGate(null, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), () => now);
        Assert.False(g.IsShared);
        g.Enter();
        Assert.True(g.Suppressed);
        g.Exit();
        now += 100;
        Assert.False(g.Suppressed);
    }

    [Fact]
    public void Dispose_Is_Idempotent_And_A_Disposed_Gate_Suppresses_Nothing()
    {
        var g = new SharedReadGate(UniqueName());
        g.Enter();
        g.Dispose();
        g.Dispose();
        Assert.False(g.Suppressed);
        g.Exit();
    }
}
