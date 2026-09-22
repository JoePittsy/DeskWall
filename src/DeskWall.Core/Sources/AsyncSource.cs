using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Periodic source whose work may be slow. RefreshAsync races the work against Timeout:
/// on timeout it throws TimeoutException (the tick marks the source failed) and lets the work
/// finish in the background; when it finishes, Changed fires and the next RefreshAsync returns the
/// result immediately without re-running.</summary>
public abstract class AsyncSource(string name, TimeSpan every, TimeSpan timeout) : PeriodicSource(name, every), ISignalSource
{
    private readonly object _lock = new();
    private Task<RecordValue>? _inFlight;
    private Task<RecordValue>? _notified;   // the overrunning task a Changed notification is already attached to

    public TimeSpan Timeout => timeout;

    /// <summary>Raised (on a pool thread) when a refresh that overran its timeout finally completes,
    /// successfully or not. This is ISignalSource.Changed: one name for one thing.</summary>
    public event Action<ISource>? Changed;

    /// <summary>Due now while an overrun fetch is sitting there finished, because the next
    /// RefreshAsync hands it straight back. Without this the wake that Changed asked for lands on a
    /// tick that declines to refresh, and an every=600 source waits out ten minutes for a result it
    /// already holds. The daemon reaches this only through Scheduler.DueAt, which uses the failure
    /// back-off instead while a source is failing, so in practice this is the designer's live
    /// panel; it is still the honest answer to "when are you next due".</summary>
    /// <inheritdoc />
    /// <remarks>A fetch that overran its tick and has since landed is exactly "something no refresh
    /// has published yet". Before the scheduler asked this, the Changed wake such a landing raises
    /// was a no-op in the daemon: the source was failing (it had timed out), so the back-off
    /// ignored NextDue and the result sat unused until the delay expired. Cleared by the attempt,
    /// because RefreshAsync takes the task out of _inFlight before awaiting it.</remarks>
    public bool HasPending
    {
        get { lock (_lock) return _inFlight is { IsCompleted: true }; }
    }

    public override DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        lock (_lock)
            if (_inFlight is { IsCompleted: true }) return now;
        return base.NextDue(lastRefresh, now);
    }

    protected abstract Task<RecordValue> FetchAsync(CancellationToken ct);

    public sealed override async ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        Task<RecordValue> work;
        Task<RecordValue>? done = null;
        lock (_lock)
        {
            if (_inFlight is { IsCompleted: true }) { done = _inFlight; _inFlight = null; }   // overran last time; hand back the result (or rethrow its failure)
            work = done ?? (_inFlight ??= Start());
        }
        if (done is not null) return await done.ConfigureAwait(false);
        // The loser of the race is cancelled rather than left running: an uncancelled Task.Delay keeps
        // a timer alive for the full timeout after every successful refresh.
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var finished = await Task.WhenAny(work, Task.Delay(timeout, race.Token)).ConfigureAwait(false);
        await race.CancelAsync().ConfigureAwait(false);
        if (finished != work)
        {
            // This refresh, and only this one, decided the work overran. Deciding it in Start's
            // continuation instead raced with the line below and reported an ordinary on-time refresh
            // as a late one, which the daemon turned into a spurious extra tick.
            NotifyWhenItLands(work);
            throw new TimeoutException($"source '{Name}' exceeded {timeout.TotalSeconds:0} s; still running");
        }
        lock (_lock) _inFlight = null;
        return await work.ConfigureAwait(false);
    }

    /// <summary>Raise Changed once when a fetch the tick has already given up on finishes - at once
    /// if it has already finished. Attached at most once per task, so a fetch that overruns several
    /// ticks still produces a single wake.</summary>
    private void NotifyWhenItLands(Task<RecordValue> work)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_notified, work)) return;
            _notified = work;
        }
        work.ContinueWith(_ => Changed?.Invoke(this), TaskScheduler.Default);
    }

    // Never cancelled by the tick's token: the work must finish and report so the next tick can use it.
    // Each source bounds its own fetch from inside FetchAsync.
    private Task<RecordValue> Start() => Task.Run(() => FetchAsync(CancellationToken.None));
}
