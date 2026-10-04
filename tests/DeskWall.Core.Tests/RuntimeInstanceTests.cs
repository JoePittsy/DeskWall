using DeskWall.Core;
using Xunit;

/// <summary>The daemon and the designer derive their lock and window names from one place, and the
/// default home must keep the names older builds use, or an installed daemon and a new designer (or
/// a new `stop`) stop finding each other.</summary>
public class RuntimeInstanceTests
{
    private static readonly string DefaultHome =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskWall");

    [Fact]
    public void Default_home_keeps_the_old_names()
    {
        Assert.Equal(@"Local\DeskWall.Daemon", RuntimeInstance.DaemonLockName(DefaultHome));
        Assert.Equal(@"Local\DeskWall.Designer", RuntimeInstance.DesignerLockName(DefaultHome));
        Assert.Equal("DeskWallHost", RuntimeInstance.DaemonWindowTitle(DefaultHome));
        // Every producer script and the docs' one-liners name this pipe; the default home keeps it.
        Assert.Equal("DeskWall.Events", RuntimeInstance.EventPipeName(DefaultHome));
        // Case and a trailing separator do not make it a different home.
        Assert.Equal(@"Local\DeskWall.Daemon", RuntimeInstance.DaemonLockName(DefaultHome.ToUpperInvariant() + @"\"));
    }

    [Fact]
    public void Another_home_gets_a_stable_suffix_matching_the_daemons_original_derivation()
    {
        // 8E76... is SHA-256 over UTF-16 "C:\SCRATCH\HOME", first 8 bytes: what HostWindow.LockName
        // produced before the derivation moved to Core. A running daemon from an older build on this
        // home is therefore still found.
        Assert.Equal(@"Local\DeskWall.Daemon.8E76E78989D2DA7A", RuntimeInstance.DaemonLockName(@"C:\scratch\home"));
        Assert.Equal(@"Local\DeskWall.Daemon.8E76E78989D2DA7A", RuntimeInstance.DaemonLockName(@"c:\SCRATCH\home\"));
        Assert.Equal(@"Local\DeskWall.Designer.8E76E78989D2DA7A", RuntimeInstance.DesignerLockName(@"C:\scratch\home"));
        Assert.Equal(@"Local\DeskWall.Daemon.8E76E78989D2DA7A", RuntimeInstance.DaemonWindowTitle(@"C:\scratch\home"));
        Assert.NotEqual(RuntimeInstance.DaemonLockName(@"C:\scratch\home"), RuntimeInstance.DaemonLockName(@"C:\scratch\other"));
    }

    [Fact]
    public void Another_homes_event_pipe_carries_the_same_suffix_as_its_lock()
    {
        // Issue #28: two daemons on two homes both listened on DeskWall.Events and a producer's
        // connection landed on whichever instance was next.
        Assert.Equal("DeskWall.Events.8E76E78989D2DA7A", RuntimeInstance.EventPipeName(@"C:\scratch\home"));
        Assert.NotEqual(RuntimeInstance.EventPipeName(@"C:\scratch\home"), RuntimeInstance.EventPipeName(@"C:\scratch\other"));
    }
}
