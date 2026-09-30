using DeskWall.Core.Sources;
using Windows.Media.Control;
using Xunit;

/// <summary>The media source talks to the Windows global media session (GSMTC) through WinRT
/// async calls. The daemon's tick blocks on every due refresh, so none of them may wait forever.</summary>
public class MediaSourceTests
{
    private static async Task<bool> FinishesWithin(Task t, TimeSpan limit) => await Task.WhenAny(t, Task.Delay(limit)) == t;

    /// <summary>Finding 1 of the 2026-09-30 harden review. RequestAsync had no timeout and ignored
    /// the token, TickRunner awaits every refresh before it resolves, and DaemonLoop blocks the tick
    /// thread on the whole tick with CancellationToken.None: one hung call to the media service
    /// froze the wallpaper, and the gate it held made every later refresh wait behind it.</summary>
    [Fact]
    public async Task A_Session_Manager_That_Never_Answers_Fails_The_Refresh_Instead_Of_Hanging_The_Tick()
    {
        var never = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager>();
        using var s = new MediaSource("media", () => never.Task.AsAsyncOperation(), TimeSpan.FromMilliseconds(200));

        var first = s.RefreshAsync(default).AsTask();
        Assert.True(await FinishesWithin(first, TimeSpan.FromSeconds(10)), "refresh hung on a manager request that never answers");
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        var second = s.RefreshAsync(default).AsTask();
        Assert.True(await FinishesWithin(second, TimeSpan.FromSeconds(10)), "the first refresh left the gate held");
        await Assert.ThrowsAsync<TimeoutException>(() => second);
    }

    [Fact]
    public async Task A_Cancelled_Token_Ends_A_Pending_Manager_Request()
    {
        var never = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager>();
        using var s = new MediaSource("media", () => never.Task.AsAsyncOperation(), TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var refresh = s.RefreshAsync(cts.Token).AsTask();
        Assert.True(await FinishesWithin(refresh, TimeSpan.FromSeconds(10)), "refresh ignored its cancellation token");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
    }
}
