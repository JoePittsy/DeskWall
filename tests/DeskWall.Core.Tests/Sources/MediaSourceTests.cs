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

    private static string MediaDir() => Path.Combine(DeskWall.Core.Paths.RuntimeDir, "media");

    private static byte[] PngBytes()
    {
        var png = Path.Combine(TestRun.Root, "media-art-source.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        using (var s = DeskWall.Core.Render.Surface.Create(8, 8)) { s.Clear(DeskWall.Core.Render.Color.White); s.SavePng(png); }
        return File.ReadAllBytes(png);
    }

    /// <summary>Harden review finding 3. The cover is decoration; a thumbnail the decoder cannot read
    /// threw out of RefreshAsync and failed the whole source, so the title and artist went with it
    /// (and a paused-to-playing change was lost until the next media event).</summary>
    [Fact]
    public void Undecodable_Art_Is_No_Art_Not_A_Failed_Source()
    {
        using var s = new MediaSource("media");
        Assert.Equal("", s.SaveArt([0x42, 0x41, 0x44, 0x21]));
        Assert.Empty(Directory.GetFiles(MediaDir(), "*.tmp"));
    }

    /// <summary>The daemon and the designer share one runtime dir and each runs its own media source.
    /// With one fixed "thumbnail.tmp", either one holding it (or deleting it in its finally while the
    /// other was decoding it) failed the other's refresh.</summary>
    [Fact]
    public void Art_Is_Written_Through_Temp_Files_Of_Its_Own()
    {
        Directory.CreateDirectory(MediaDir());
        var other = Path.Combine(MediaDir(), "thumbnail.tmp");
        using (new FileStream(other, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using var s = new MediaSource("media");
            var art = s.SaveArt(PngBytes());
            Assert.Equal(Path.Combine(MediaDir(), "art.png"), art);
            Assert.True(File.Exists(art));
            Assert.Equal([other], Directory.GetFiles(MediaDir(), "*.tmp"));   // only the other writer's
        }
        File.Delete(other);
    }

    [Fact]
    public void The_Same_Art_Twice_Is_Not_Decoded_Again()
    {
        using var s = new MediaSource("media");
        var bytes = PngBytes();
        var art = s.SaveArt(bytes);
        var written = File.GetLastWriteTimeUtc(art);
        Thread.Sleep(20);
        Assert.Equal(art, s.SaveArt(bytes));
        Assert.Equal(written, File.GetLastWriteTimeUtc(art));
    }

    /// <summary>Harden review finding 4. Every layout edit disposes the sources and builds new ones;
    /// a media source that keeps its WinRT handlers is kept alive by them, one per reload.</summary>
    [Fact]
    public async Task Dispose_After_A_Refresh_Lets_Go_Of_The_Media_Service()
    {
        var s = new MediaSource("media");
        await s.RefreshAsync(default);
        Assert.True(s.HoldsSubscriptions);
        s.Dispose();
        Assert.False(s.HoldsSubscriptions);
    }

    /// <summary>Dispose while a refresh holds the gate cannot release the handlers itself; the refresh
    /// must, on its way out. (The narrower race this commit closes - Dispose landing between the
    /// refresh's disposed check and its gate release - is two instructions wide and is fixed by
    /// ordering, releasing the gate before looking.)</summary>
    [Fact]
    public async Task Dispose_During_A_Refresh_Is_Finished_By_That_Refresh()
    {
        var request = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager>();
        var s = new MediaSource("media", () => request.Task.AsAsyncOperation(), TimeSpan.FromSeconds(30));
        var refresh = s.RefreshAsync(default).AsTask();
        s.Dispose();
        request.SetResult(await GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        await refresh;
        Assert.False(s.HoldsSubscriptions);
    }

    private static readonly DateTimeOffset At = new(2026, 9, 30, 21, 0, 0, TimeSpan.FromHours(1));
    private static MediaSource.TimelineStamp Stamp(bool playing) => new(TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(60), At, playing);

    /// <summary>Harden review finding 5: the stamp is now one object, so a timeline event on a WinRT
    /// thread can no longer read an end time from one refresh and a position from another.</summary>
    [Fact]
    public void Ordinary_Progress_Is_Not_A_Surprise_But_A_Seek_Or_A_New_Length_Is()
    {
        Assert.True(MediaSource.Surprising(null, TimeSpan.FromMinutes(4), TimeSpan.Zero, At));   // nothing seen yet

        var playing = Stamp(playing: true);
        Assert.False(MediaSource.Surprising(playing, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(70), At.AddSeconds(10)));
        Assert.False(MediaSource.Surprising(playing, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(72), At.AddSeconds(10)));
        Assert.True(MediaSource.Surprising(playing, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(120), At.AddSeconds(10)));
        Assert.True(MediaSource.Surprising(playing, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(70), At.AddSeconds(10)));

        var paused = Stamp(playing: false);
        Assert.False(MediaSource.Surprising(paused, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(60), At.AddSeconds(30)));
        Assert.True(MediaSource.Surprising(paused, TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(90), At.AddSeconds(30)));
    }
}
