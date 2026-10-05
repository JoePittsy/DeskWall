using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Periodic source whose work may be slow, and which the tick never waits on once it has
/// something to show.
/// <para>The <em>first</em> RefreshAsync races the work against Timeout: there is nothing to serve
/// yet, and a one-shot <c>deskwall tick</c>, <c>verify</c> or <c>shortcuts</c> has no later tick to
/// pick it up. On timeout it throws TimeoutException (the tick marks the source failed).</para>
/// <para>Every later RefreshAsync starts the work and, if it has not already finished, throws
/// <see cref="SourcePendingException"/> at once (#20: a dead local endpoint held each such tick
/// ~2 s while Windows retried the refused connect). The tick leaves the snapshot alone, so the
/// previous values keep publishing and staleness and the failure count carry on from it.</para>
/// <para>Either way the work finishes in the background; when it does, Changed fires and the next
/// RefreshAsync returns the result (or rethrows its failure) immediately without re-running.</para></summary>
public abstract class AsyncSource(string name, TimeSpan every, TimeSpan timeout) : PeriodicSource(name, every), ISignalSource
{
    private readonly object _lock = new();
    private Task<RecordValue>? _inFlight;
    private Task<RecordValue>? _notified;   // the overrunning task a Changed notification is already attached to
    private bool _refreshed;                // RefreshAsync has run before: only the first one waits

    public TimeSpan Timeout => timeout;

    /// <summary>True while work started by a refresh the caller did not wait for is still running.
    /// The scheduler treats that as not due: Changed is what says it landed, and answering "now"
    /// from the old LastRefresh would pin the daemon at MinDelay for the length of the fetch.</summary>
    public bool Fetching
    {
        get { lock (_lock) return _inFlight is { IsCompleted: false }; }
    }

    /// <summary>Raised (on a pool thread) when a fetch no refresh waited for to the end - one that
    /// overran its timeout, or one a later refresh left running - finally completes, successfully or
    /// not. This is ISignalSource.Changed: one name for one thing.</summary>
    public event Action<ISource>? Changed;

    /// <summary>Due now while an overrun fetch is sitting there finished, because the next
    /// RefreshAsync hands it straight back. Without this the wake that Changed asked for lands on a
    /// tick that declines to refresh, and an every=600 source waits out ten minutes for a result it
    /// already holds. The daemon reaches this through Scheduler.DueAt: for a healthy source whose
    /// refresh was left running (#20) this is what makes the landing's wake harvest it; a failing
    /// source is let through by HasPending instead.</summary>
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
        bool wait;
        lock (_lock)
        {
            if (_inFlight is { IsCompleted: true }) { done = _inFlight; _inFlight = null; }   // overran last time; hand back the result (or rethrow its failure)
            work = done ?? (_inFlight ??= Start());
            wait = !_refreshed;
            _refreshed = true;
        }
        if (done is not null) return await done.ConfigureAwait(false);
        if (!wait)
        {
            // Not the first refresh, so the caller has the last result already published. Waiting
            // here is what held the daemon's tick for the whole fetch.
            NotifyWhenItLands(work);
            throw new SourcePendingException($"source '{Name}' still fetching; serving the previous values");
        }
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

    /// <summary>Raise Changed once when a fetch the tick did not wait for finishes - at once
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
