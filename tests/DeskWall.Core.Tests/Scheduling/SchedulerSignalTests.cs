using DeskWall.Core.Scheduling;
using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

/// <summary>A source that has failed once is scheduled from its back-off and its own NextDue is
/// ignored, which is finding 1 and must stay true. But a pushing source saying "due now" is not
/// asking to retry a failure, it is saying it already holds something new: a watcher fired, a
/// callback arrived. Both lanes of the push work hit this - a volume change or a file save was
/// swallowed for the whole back-off - so the scheduler asks whether the source is holding
/// anything before it applies the delay.</summary>
public class SchedulerSignalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private sealed class PushSource(string name, TimeSpan every) : PeriodicSource(name, every), ISignalSource
    {
        public event Action<ISource>? Changed;
        public bool HasPending { get; set; }
        public void Raise() { HasPending = true; Changed?.Invoke(this); }
        public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
        {
            HasPending = false;                    // cleared by the attempt, as every real one does
            return new(new RecordValue(new Dictionary<string, Value>()));
        }
        public override DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
            => HasPending ? now : base.NextDue(lastRefresh, now);
    }

    private sealed class PlainSource(string name, TimeSpan every) : PeriodicSource(name, every)
    {
        public override ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
            => new(new RecordValue(new Dictionary<string, Value>()));
    }

    private static SourceSnapshot Failing(string name, int times)
    {
        var snap = SourceSnapshot.Initial(name);
        for (var i = 0; i < times; i++) snap = snap.Failed("nope", T0);
        return snap;
    }

    [Fact]
    public void A_Failing_Source_Holding_Something_New_Is_Due_Now()
    {
        var s = new PushSource("file1", TimeSpan.FromSeconds(30));
        s.Raise();
        Assert.Equal(T0, Scheduler.DueAt(s, Failing("file1", 3), T0));
    }

    [Fact]
    public void A_Failing_Source_Holding_Nothing_Still_Backs_Off()
    {
        var s = new PushSource("file1", TimeSpan.FromSeconds(30));
        var due = Scheduler.DueAt(s, Failing("file1", 3), T0);
        Assert.True(due > T0, $"expected a back-off, got {due:O}");
    }

    /// <summary>Finding 1, unchanged: a source with no way to say it is holding anything must not
    /// escape the back-off, or a broken URL pins the daemon at MinDelay.</summary>
    [Fact]
    public void A_Failing_Source_That_Cannot_Push_Is_Unaffected()
    {
        var s = new PlainSource("http", TimeSpan.FromSeconds(600));
        var due = Scheduler.DueAt(s, Failing("http", 3), T0);
        Assert.True(due >= T0 + TimeSpan.FromSeconds(600), $"expected the back-off, got {due:O}");
    }

    /// <summary>The spin guard. One signal buys one attempt: the refresh clears the flag before it
    /// can throw, so a source that keeps failing goes straight back onto its back-off instead of
    /// being due forever.</summary>
    [Fact]
    public async Task One_Signal_Buys_One_Attempt_Not_A_Permanent_Reprieve()
    {
        var s = new PushSource("file1", TimeSpan.FromSeconds(30));
        s.Raise();
        Assert.Equal(T0, Scheduler.DueAt(s, Failing("file1", 3), T0));

        await s.RefreshAsync(default);             // the attempt, which in the real sources may throw
        var due = Scheduler.DueAt(s, Failing("file1", 4), T0);
        Assert.True(due > T0, $"expected the back-off to resume, got {due:O}");
    }

    [Fact]
    public void A_Healthy_Pushing_Source_Is_Unchanged()
    {
        var s = new PushSource("file1", TimeSpan.FromSeconds(30));
        var snap = SourceSnapshot.Initial("file1").Succeeded(new RecordValue(new Dictionary<string, Value>()), T0);
        Assert.Equal(T0 + TimeSpan.FromSeconds(30), Scheduler.DueAt(s, snap, T0));
    }
}
