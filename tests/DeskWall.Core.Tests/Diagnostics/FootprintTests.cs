using DeskWall.Core.Diagnostics;
using Xunit;

public class FootprintTests
{
    [Fact]
    public void Current_Reports_Plausible_Numbers()
    {
        var f = Footprint.Current();
        Assert.InRange(f.WorkingSetBytes, 1_000_000, 2_000_000_000);
        Assert.InRange(f.PrivateBytes, 1_000_000, 2_000_000_000);
        Assert.InRange(f.Handles, 10, 100_000);
        Assert.InRange(f.Threads, 1, 1000);
        Assert.Contains("MB", f.Short());
    }

    /// <summary>Budget finding, 2026-09-21: after a tick the daemon only trimmed its working set,
    /// which pages memory out but keeps it committed; private bytes sat at 48-79 MB idle against a
    /// 10 MB budget. Release() is what runs after a tick instead: collect, compact the large object
    /// heap, hand the freed regions back, then trim. The 32 MB dropped here must not stay committed.</summary>
    [Fact]
    public void Release_Returns_Dropped_Large_Allocations_To_The_OS()
    {
        Footprint.Release();
        var baseline = Footprint.Current().PrivateBytes;

        var peak = FillAndDrop(32 * 1024 * 1024);
        Assert.True(peak - baseline >= 24 * 1024 * 1024, $"the allocation did not show in commit: {baseline:N0} -> {peak:N0}");

        Footprint.Release();
        var after = Footprint.Current().PrivateBytes;
        Assert.True(after - baseline < 8 * 1024 * 1024,
            $"private bytes {baseline:N0} -> {peak:N0} with the array held -> {after:N0} after Release(); the 32 MB is still committed");
    }

    /// <summary>Allocates, touches every page, reads commit while the array is live, and returns with
    /// the array unreachable. Its own frame, not inlined: a Debug JIT keeps a local reachable until
    /// the end of the method that declares it, null assignment or not.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long FillAndDrop(int bytes)
    {
        var a = new byte[bytes];
        for (var i = 0; i < a.Length; i += 4096) a[i] = 1;   // touch every page so it is really committed
        var peak = Footprint.Current().PrivateBytes;
        GC.KeepAlive(a);
        return peak;
    }

    [Fact]
    public void Trim_Does_Not_Throw_And_Working_Set_Does_Not_Grow()
    {
        var before = Footprint.Current().WorkingSetBytes;
        Footprint.Trim();
        var after = Footprint.Current().WorkingSetBytes;
        Assert.True(after <= before + 512 * 1024, $"working set grew from {before} to {after}");
    }
}
