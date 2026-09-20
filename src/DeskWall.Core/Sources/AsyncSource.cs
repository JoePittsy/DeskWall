using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Periodic source whose work may be slow. RefreshAsync races the work against Timeout:
/// on timeout it throws TimeoutException (the tick marks the source failed) and lets the work
/// finish in the background; when it finishes, Completed fires with the result and the next
/// RefreshAsync returns it immediately without re-running.</summary>
public abstract class AsyncSource(string name, TimeSpan every, TimeSpan timeout) : PeriodicSource(name, every)
{
    private readonly object _lock = new();
    private Task<RecordValue>? _inFlight;

    public TimeSpan Timeout => timeout;

    /// <summary>Raised (on a pool thread) when a refresh that overran its timeout finally completes, successfully or not.</summary>
    public event Action<ISource>? Completed;

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
        // Cancel the loser: an abandoned Task.Delay keeps an armed timer for the whole timeout, one
        // per async source per tick, and the daemon passes a token that can never cancel it (finding 6).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var finished = await Task.WhenAny(work, Task.Delay(timeout, cts.Token)).ConfigureAwait(false);
        cts.Cancel();
        if (finished != work) throw new TimeoutException($"source '{Name}' exceeded {timeout.TotalSeconds:0} s; still running");
        lock (_lock) _inFlight = null;
        return await work.ConfigureAwait(false);
    }

    private Task<RecordValue> Start()
    {
        // Never cancelled by the tick's token: the work must finish and report so the next tick can use it.
        var t = Task.Run(() => FetchAsync(CancellationToken.None));
        t.ContinueWith(_ =>
        {
            bool overran;
            lock (_lock) overran = ReferenceEquals(_inFlight, t);
            if (overran) Completed?.Invoke(this);
        }, TaskScheduler.Default);
        return t;
    }
}
