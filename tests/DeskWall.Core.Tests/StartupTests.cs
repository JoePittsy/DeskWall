using DeskWall.Core;
using Microsoft.Win32;
using Xunit;

public class StartupTests
{
    [Fact]
    public void Install_Then_Uninstall_RoundTrips_In_A_Private_Key()
    {
        var sub = @"Software\DeskWallTests\Run-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Assert.Null(Startup.Installed(sub));
            Startup.Install(@"C:\x\deskwall.exe", sub);
            Assert.Equal("\"C:\\x\\deskwall.exe\" run", Startup.Installed(sub));
            Startup.Uninstall(sub);
            Assert.Null(Startup.Installed(sub));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); Registry.CurrentUser.DeleteSubKeyTree(@"Software\DeskWallTests", throwOnMissingSubKey: false); }
    }
}
