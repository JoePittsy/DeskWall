using System.Runtime.CompilerServices;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources.Notifications;

/// <summary>What is waiting in the Windows notification centre: how many toasts, from which apps,
/// and the newest one. Push: the only unprompted read is the sweep below. Publishes: status, count,
/// apps, latestApp, latestTitle, latestText, latestAt, changedAt (docs/sources.md "notifications").
/// <para>The shape is <c>AudioSource</c>'s: the reader tells the source something may have changed,
/// the source reads off the tick thread, and only a reading that differs from what was last
/// published raises <see cref="Changed"/>. That dedupe matters more here than for audio, because
/// the store the reader watches is also written by badge and tile updates and by other processes'
/// reads, and a notification read is not cheap (280-360 ms wall, 60-110 ms CPU, measured).</para>
/// <para>After every read that followed a change, one more read is made <see cref="ConfirmDelay"/>
/// later when the reader's own reads echo (<see cref="INotificationReader.EchoesReads"/>): a toast
/// that lands inside a read's suppressed echo would otherwise wait for the next unrelated change.
/// The confirming read never schedules another, so the pair is bounded.</para>
/// <para>The one read nobody asked for is the sweep: while the centre is not empty and the reader
/// is the store watcher, one read every <c>every</c> (default 300 s), on the whole minute so it
/// rides the clock's wake. The watcher sees every arrival but not every dismissal - measured, a
/// <c>History.Clear</c> wrote the WAL within a second in one run and not at all until the next read
/// in another, because NTFS updates last-write metadata lazily on a file its writer keeps open. An
/// empty centre has nothing to dismiss, so it is never swept.</para></summary>
public sealed class NotificationSource : ISource, ISignalSource, IDisposable
{
    /// <summary>A toast's arrival is some thirty store writes over about 600 ms; one read for all of them.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    public static readonly TimeSpan DefaultConfirm = TimeSpan.FromSeconds(2);

    private readonly INotificationReader _reader;
    private readonly IClock _clock;
    private readonly string[] _include, _exclude;
    private readonly TimeSpan _every;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly ITimer _debounce, _confirm;
    private readonly Action _onReaderChanged;
    private volatile bool _disposed, _started;
    private volatile Published? _form;
    private volatile StrongBox<NotificationAccess>? _access;
    private volatile StrongBox<DateTimeOffset>? _lastRead;
    private int _pending, _faults, _count;

    private sealed record Published(string Key, RecordValue Values);

    public NotificationSource(string name, INotificationReader reader, IClock clock,
        IReadOnlyList<string>? include = null, IReadOnlyList<string>? exclude = null,
        TimeSpan? every = null, TimeSpan? debounce = null, TimeSpan? confirm = null, TimeProvider? time = null)
    {
        // Only the two timers come from `time` (System unless a test passes a fake to advance);
        // timestamps still come from `clock`, like everywhere else in Core.
        time ??= TimeProvider.System;
        Name = name;
        _reader = reader;
        _clock = clock;
        _include = include?.ToArray() ?? [];
        _exclude = exclude?.ToArray() ?? [];
        _every = every ?? TimeSpan.FromSeconds(300);
        Debounce = debounce ?? DefaultDebounce;
        ConfirmDelay = confirm ?? DefaultConfirm;
        _debounce = time.CreateTimer(static s => ((NotificationSource)s!).Fire(confirming: false), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _confirm = time.CreateTimer(static s => ((NotificationSource)s!).Fire(confirming: true), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _onReaderChanged = OnReaderChanged;
        _reader.Changed += _onReaderChanged;
    }

    /// <summary>Settings: <c>include</c> and <c>exclude</c>, comma-separated app names (the display
    /// name or the app user model id, case-insensitive). <c>every</c> is the sweep interval, and
    /// the whole schedule when the reader has no way to be told (default 300 s).</summary>
    public static NotificationSource FromDef(SourceDef def, IClock clock)
        => new(def.Name, new WinRtNotificationReader(), clock,
            List(def.Settings.GetValueOrDefault("include")), List(def.Settings.GetValueOrDefault("exclude")),
            def.EverySeconds is > 0 ? TimeSpan.FromSeconds(def.EverySeconds.Value) : null);

    private static string[] List(string? text)
        => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string Name { get; }

    public TimeSpan Debounce { get; }

    public TimeSpan ConfirmDelay { get; }

    public event Action<ISource>? Changed;

    /// <summary>Background reads that threw. Core sources have no logger; this is what a test or a
    /// diagnostic reads instead.</summary>
    public int ReadFaults => Volatile.Read(ref _faults);

    public bool HasPending => Volatile.Read(ref _pending) != 0;

    /// <summary>Due now for the first reading and while a change is waiting to be published, then
    /// at the sweep (<see cref="SweepAt"/>) when there is one, otherwise never: event-only. Access
    /// refused is never due again (asked once; a layout reload asks again).</summary>
    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (lastRefresh is null || HasPending || !_started) return now;
        return SweepAt() ?? DateTimeOffset.MaxValue;
    }

    /// <summary>When the next unprompted read is due, or null for none: <c>every</c> after the last
    /// read of any kind, rounded up to the whole minute. Only while access is allowed and either the
    /// reader cannot push at all, or it is the store watcher and something could still be dismissed.</summary>
    public DateTimeOffset? SweepAt()
    {
        if (_access?.Value is not NotificationAccess.Allowed || _lastRead is not { } last) return null;
        if (_reader.CanPush && !(_reader.EchoesReads && Volatile.Read(ref _count) > 0)) return null;
        var due = last.Value + _every;
        var floor = new DateTimeOffset(due.Year, due.Month, due.Day, due.Hour, due.Minute, 0, due.Offset);
        return floor == due ? due : floor.AddMinutes(1);
    }

    public TimeSpan Interval(DateTimeOffset now) => TimeSpan.FromMinutes(1);

    public async ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        // Cleared before anything is read: a change landing mid-refresh sets it again and earns
        // the next tick. One signal buys one attempt (ISignalSource.HasPending).
        Volatile.Write(ref _pending, 0);
        if (!_started || SweepAt() <= _clock.Now)
        {
            // The first reading happens here, on the tick, so the first frame has it. A throw marks
            // the source failed and the scheduler retries on its back-off. So does a sweep's.
            await ReadAsync(background: false, ct).ConfigureAwait(false);
            if (!_started)
            {
                _started = true;
                if (_access?.Value is NotificationAccess.Allowed) _reader.Start();
            }
        }
        return _form!.Values;
    }

    private void OnReaderChanged()
    {
        if (_disposed || !_started) return;
        try { _debounce.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void Fire(bool confirming)
    {
        if (_disposed) return;
        _ = BackgroundAsync(confirming);
    }

    private async Task BackgroundAsync(bool confirming)
    {
        try
        {
            var changed = await ReadAsync(background: true, CancellationToken.None).ConfigureAwait(false);
            if (_disposed) return;
            if (changed)
            {
                Volatile.Write(ref _pending, 1);
                try { Changed?.Invoke(this); } catch (Exception) { }
            }
            if (!confirming && _reader.EchoesReads)
            {
                try { _confirm.Change(ConfirmDelay, Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { }
            }
        }
        catch (Exception)
        {
            // A timer callback lets nothing escape: that would take the daemon down.
            Interlocked.Increment(ref _faults);
        }
    }

    /// <summary>Read, and publish when the reading differs. True when it did.</summary>
    private async Task<bool> ReadAsync(bool background, CancellationToken ct)
    {
        await _readLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return false;
            NotificationReading reading;
            try
            {
                reading = await _reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (background)
            {
                Interlocked.Increment(ref _faults);
                return false;
            }
            _access = new StrongBox<NotificationAccess>(reading.Access);
            _lastRead = new StrongBox<DateTimeOffset>(_clock.Now);
            var (key, fields) = Build(reading);
            Volatile.Write(ref _count, fields.TryGetValue("count", out var c) && c is NumberValue n ? (int)n.Number : 0);
            if (_form is { } current && current.Key == key) return false;
            // When the published set last changed, not when a toast was created: a dismissal is a
            // change too, and a stable value keeps the content key stable between changes.
            if (reading.Access is NotificationAccess.Allowed) fields["changedAt"] = new TimeValue(_clock.Now);
            _form = new Published(key, new RecordValue(fields));
            return true;
        }
        finally
        {
            _readLock.Release();
        }
    }

    private (string Key, Dictionary<string, Value> Fields) Build(NotificationReading reading)
    {
        var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        if (reading.Access is not NotificationAccess.Allowed)
        {
            var status = reading.Access is NotificationAccess.Denied ? "denied" : "unavailable";
            d["status"] = new TextValue(status);
            return ("status:" + status, d);
        }

        var items = reading.Items.Where(Wanted).OrderByDescending(i => i.Created).ToList();
        var apps = items
            .GroupBy(i => i.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().App, Count: g.Count(), Latest: g.Max(i => i.Created)))
            .OrderByDescending(a => a.Count).ThenByDescending(a => a.Latest).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        d["status"] = new TextValue("ok");
        d["count"] = new NumberValue(items.Count);
        d["apps"] = new ListValue(apps.Select(a => new RecordValue(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new TextValue(a.Name),
            ["count"] = new NumberValue(a.Count),
        })).ToList(), "name");
        var latest = items.FirstOrDefault();
        d["latestApp"] = new TextValue(latest?.App ?? "");
        d["latestTitle"] = new TextValue(latest?.Title ?? "");
        d["latestText"] = new TextValue(latest?.Text ?? "");
        if (latest is not null) d["latestAt"] = new TimeValue(latest.Created.ToLocalTime());

        var key = string.Join('\u001f', new[] { "ok", items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            .Concat(apps.Select(a => a.Name + "=" + a.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Concat([latest?.App ?? "", latest?.Title ?? "", latest?.Text ?? "", latest?.Created.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""]));
        return (key, d);
    }

    private bool Wanted(NotificationItem item)
    {
        if (_include.Length > 0 && !_include.Any(f => Matches(item, f))) return false;
        return !_exclude.Any(f => Matches(item, f));
    }

    private static bool Matches(NotificationItem item, string filter)
        => string.Equals(item.App, filter, StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.AppId, filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Idempotent: nothing guarantees the host disposes a source exactly once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader.Changed -= _onReaderChanged;
        _debounce.Dispose();
        _confirm.Dispose();
        try { _reader.Dispose(); } catch (Exception) { }
    }
}
