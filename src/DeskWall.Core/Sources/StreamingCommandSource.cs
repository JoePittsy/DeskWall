using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeskWall.Core.Layout;
using DeskWall.Core.Scheduling;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>A `command` source with `stream: true`. The process starts on the first refresh and
/// stays up; every line it writes to stdout is parsed the way the non-streaming source parses its
/// whole output, becomes the current values, and raises Changed.
/// <para>This is what makes a cheap always-on producer possible without a process spawn per tick,
/// and it is the answer to "self serve providers" for anything that can print a line. A user who
/// cannot write C# and cannot load a plugin into a native-AOT binary can still push.</para>
/// <para>It is a sibling of <see cref="CommandSource"/> rather than a mode inside it because the
/// two share no lifecycle: one runs a process per refresh and races it against a timeout, the
/// other holds a process, a reader thread and a restart timer for the life of the layout.</para>
/// <para>Settings, on top of `command`/`args`/`workingDir`/`parse`/`unixTimeFields` as the
/// non-streaming source takes them: `stream` = true, and `every` (default 600 s) which is only the
/// re-check that keeps the process started, never the latency. `timeout` is accepted and ignored:
/// there is no single run to time out.</para></summary>
public sealed class StreamingCommandSource : ISource, ISignalSource, IDisposable
{
    /// <summary>The base for the restart back-off. Scheduler.BackOff doubles it per consecutive
    /// exit, so a process that exits at once restarts after 250 ms, 500 ms, 1 s ... capped at
    /// Scheduler.MaxDelay. The source's own `every` would be wrong as the base: it is 600 s by
    /// default, and a producer that crashed once should not be gone for ten minutes.</summary>
    private static readonly TimeSpan RestartBase = Scheduler.MinDelay;

    /// <summary>A run that lasted this long counts as healthy and resets the back-off. Without it a
    /// producer that has been up for a week and then crashes once waits out the full cap.</summary>
    private static readonly TimeSpan HealthyRun = TimeSpan.FromSeconds(30);

    /// <summary>Only the last stderr line is kept. A resident process' stderr is unbounded, and the
    /// value exists to say "it is complaining", not to be a log.</summary>
    private const int MaxStderr = 500;

    private readonly string _name;
    private readonly TimeSpan _every;
    private readonly string? _args;
    private readonly string? _parse;
    private readonly IReadOnlySet<string> _unixTimeFields;
    private readonly Secrets _secrets;
    private readonly IClock _clock;

    private readonly object _lock = new();
    private Process? _process;
    private Thread? _reader;
    private System.Threading.Timer? _restart;
    private Value? _payload;          // the last line that parsed, as json or text
    private string? _payloadKey;      // "json" or "text", fixed once decided
    private DateTimeOffset _payloadAt;
    private string? _stderr;
    private int _badLines;
    private int _exits;               // consecutive exits with no healthy run between them
    private int _starts;
    private bool _pending;            // a line has arrived that no refresh has published yet
    private bool _restartPending;     // the back-off timer is armed; a refresh must not start a second process
    private volatile bool _disposed;

    public StreamingCommandSource(string name, TimeSpan every, string command, string? args, string? workingDir, string? parse,
        IReadOnlySet<string> unixTimeFields, Secrets secrets, IClock clock)
    {
        _name = name;
        _every = every;
        _args = args;
        _parse = parse;
        _unixTimeFields = unixTimeFields;
        _secrets = secrets;
        _clock = clock;
        Command = Paths.ExpandPath(command);
        WorkingDir = workingDir is null ? "" : Paths.ExpandPath(workingDir);
    }

    /// <summary>Expanded once at construction, as CommandSource does it.</summary>
    public string Command { get; }

    public string WorkingDir { get; }

    public string Name => _name;

    /// <summary>How many times the process has been started, including the first. Published as
    /// `starts` and read by the test that proves a fast-exiting process does not spin.</summary>
    public int Starts { get { lock (_lock) return _starts; } }

    /// <summary>Raised on the reader thread when a line has parsed into new values.</summary>
    public event Action<ISource>? Changed;

    public static bool IsStreaming(SourceDef def)
        => def.Settings.TryGetValue("stream", out var s) && bool.TryParse(s, out var on) && on;

    public static StreamingCommandSource FromDef(SourceDef def, IClock clock, Secrets secrets)
    {
        var s = def.Settings;
        if (!s.TryGetValue("command", out var cmd) || string.IsNullOrWhiteSpace(cmd)) throw new ArgumentException($"command source '{def.Name}' needs settings.command");
        var unix = new HashSet<string>((s.TryGetValue("unixTimeFields", out var u) ? u : "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        return new StreamingCommandSource(def.Name, TimeSpan.FromSeconds(def.EverySeconds ?? 600), cmd, s.GetValueOrDefault("args"),
            s.GetValueOrDefault("workingDir"), s.GetValueOrDefault("parse"), unix, secrets, clock);
    }

    /// <summary>Due now while a line is waiting to be published, so the wake Changed asked for
    /// lands on a tick that refreshes this source. Otherwise the ordinary re-check, whose only job
    /// is to notice that the process is down and start it again.</summary>
    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (lastRefresh is null) return now;
        lock (_lock) if (_pending) return now;
        return lastRefresh.Value + _every;
    }

    public TimeSpan Interval(DateTimeOffset now) => _every;

    /// <summary>The latest parsed line, or the last good values when none has arrived since. Never
    /// throws for "nothing yet": a failure would put this source on the scheduler's back-off, and
    /// while a source is backed off the scheduler ignores NextDue entirely - so the wake the first
    /// line raises would find the source not due and the push path would never start.</summary>
    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        EnsureStarted();
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            _pending = false;
            if (_payload is not null && _payloadKey is not null)
            {
                d[_payloadKey] = _payload;
                d["ranAt"] = new TimeValue(_payloadAt);
            }
            d["running"] = new BoolValue(_process is { HasExited: false });
            d["starts"] = new NumberValue(_starts);
            d["badLines"] = new NumberValue(_badLines);
            if (!string.IsNullOrWhiteSpace(_stderr)) d["stderr"] = new TextValue(_stderr);
        }
        return new(new RecordValue(d));
    }

    private void EnsureStarted()
    {
        lock (_lock)
        {
            if (_disposed || _process is { HasExited: false } || _restartPending) return;
        }
        StartProcess();
    }

    private void StartProcess()
    {
        Process p;
        lock (_lock)
        {
            if (_disposed) return;
            var psi = new ProcessStartInfo
            {
                FileName = Command,
                Arguments = _args is null ? "" : _secrets.Substitute(_args),
                WorkingDirectory = WorkingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            try
            {
                p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {Command}");
            }
            catch (Exception ex)
            {
                // A command that cannot be started at all is the same case as one that exits at
                // once: back off and try again, rather than throwing out of a refresh and pinning
                // the source on the scheduler's failure path where its signals cannot reach it.
                _stderr = Trim(ex.Message);
                _exits++;
                ScheduleRestart();
                return;
            }
            _process = p;
            _starts++;
            // Stderr through the pool rather than a second dedicated thread: a resident source that
            // costs two threads each is exactly the footprint the owner's non-negotiable rules out.
            // Read it we must, though - an unread stderr pipe fills and blocks the producer.
            p.ErrorDataReceived += OnStderr;
            try { p.BeginErrorReadLine(); } catch (InvalidOperationException) { }
            _reader = new Thread(() => Read(p)) { IsBackground = true, Name = $"deskwall-stream-{_name}" };
            _reader.Start();
        }
    }

    /// <summary>The stdout reader. One thread per streaming source, blocked on a read, which costs
    /// no CPU at rest. Nothing may escape it: an exception on a background thread is an unhandled
    /// exception and takes the whole daemon down.</summary>
    private void Read(Process p)
    {
        var startedAt = _clock.Now;
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                if (_disposed) return;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (TryParse(line)) Raise();
            }
        }
        catch (Exception)
        {
            // A killed process tears the pipe down mid-read; that is an ordinary end, not a fault.
        }
        finally
        {
            if (!_disposed) OnExited(p, startedAt);
        }
    }

    /// <summary>The parse mode is decided once - by `parse` if it is set, otherwise by the first
    /// line - and then fixed. Deciding it per line would let one diagnostic line silently move a
    /// producer's values from `json` to `text` and blank every binding under it.</summary>
    private bool TryParse(string line)
    {
        var trimmed = line.TrimStart();
        var mode = _parse ?? (_payloadKey ?? (trimmed.StartsWith('{') || trimmed.StartsWith('[') ? "json" : "text"));
        try
        {
            var value = mode == "json" ? JsonValues.Parse(line, _unixTimeFields) : (Value)new TextValue(line.TrimEnd('\r', '\n'));
            lock (_lock)
            {
                _payload = value;
                _payloadKey = mode;
                _payloadAt = _clock.Now;
                _pending = true;
            }
            return true;
        }
        catch (Exception)
        {
            // Skipped and counted, and the last good values are left alone: a producer that prints
            // a diagnostic, or half a line because it was killed mid-write, must not blank a widget.
            lock (_lock) _badLines++;
            return false;
        }
    }

    private void Raise()
    {
        try { Changed?.Invoke(this); }
        catch (Exception) { }   // a subscriber's fault is not the reader thread's to die of
    }

    private void OnStderr(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null || string.IsNullOrWhiteSpace(e.Data)) return;
        lock (_lock) _stderr = Trim(e.Data);
    }

    private static string Trim(string s) => s.Length <= MaxStderr ? s : string.Concat(s.AsSpan(0, MaxStderr), "...");

    /// <summary>The process exiting is not fatal. Restarting it at once is, though: a command that
    /// exits immediately would be started as fast as the machine can fork.</summary>
    private void OnExited(Process p, DateTimeOffset startedAt)
    {
        lock (_lock)
        {
            if (_disposed || !ReferenceEquals(_process, p)) return;
            _exits = _clock.Now - startedAt >= HealthyRun ? 1 : _exits + 1;
            try { p.Dispose(); } catch (Exception) { }
            _process = null;
            ScheduleRestart();
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private void ScheduleRestart()
    {
        if (_disposed) return;
        var delay = Scheduler.BackOff(RestartBase, _exits);
        _restart ??= new System.Threading.Timer(static s => ((StreamingCommandSource)s!).OnRestartDue(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _restartPending = true;
        try { _restart.Change(delay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { _restartPending = false; }
    }

    private void OnRestartDue()
    {
        try
        {
            lock (_lock)
            {
                if (_disposed) return;
                _restartPending = false;
            }
            StartProcess();
        }
        catch (Exception)
        {
            // Nothing escapes a timer callback; the next refresh's EnsureStarted tries again.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Process? p;
        System.Threading.Timer? t;
        lock (_lock) { p = _process; _process = null; t = _restart; _restart = null; }
        t?.Dispose();
        if (p is not null)
        {
            // The whole tree, as CommandSource's timeout path does it: a producer script that
            // started a helper leaves it running forever otherwise.
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
            try { p.Dispose(); } catch (Exception) { }
        }
    }
}
