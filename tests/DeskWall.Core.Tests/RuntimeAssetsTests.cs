using DeskWall.Core;
using Xunit;

/// <summary>The shipped assets/ beside the exe reach the runtime dir without the designer: the
/// daemon copies what is missing at start, never overwriting (#90; the night icons of #45 are what
/// a publish-then-run otherwise drew as a fallback plate every night).</summary>
public class RuntimeAssetsTests
{
    /// <summary>A scratch "install dir" holding an assets/ tree, deleted afterwards.</summary>
    private sealed class Install : IDisposable
    {
        public string Root { get; } = Path.Combine(TestRun.Root, "runtime-assets", Guid.NewGuid().ToString("N"));
        public string Assets => Path.Combine(Root, "assets");
        public Install() => Directory.CreateDirectory(Assets);

        public string Add(string relative, string content)
        {
            var path = Path.Combine(Assets, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void Copies_Every_Missing_File_Keeping_Its_Folder()
    {
        using var install = new Install();
        using var home = new Install();
        install.Add(@"weather\0.png", "day");
        install.Add(@"weather\0-night.png", "night");
        install.Add(@"weather\LICENSE", "mit");
        install.Add(@"vapor\ridge-day.jpg", "ridge");

        var result = RuntimeAssets.CopyMissing(install.Assets, home.Assets);

        Assert.Equal(4, result.Copied);
        Assert.Empty(result.Failed);
        Assert.Equal("night", File.ReadAllText(Path.Combine(home.Assets, "weather", "0-night.png")));
        Assert.Equal("ridge", File.ReadAllText(Path.Combine(home.Assets, "vapor", "ridge-day.jpg")));
    }

    /// <summary>An upgrade adds the files the new build ships and leaves everything already there,
    /// including a file the owner replaced with their own art, exactly as it was.</summary>
    [Fact]
    public void Never_Overwrites_And_Copies_Only_What_Is_Missing()
    {
        using var install = new Install();
        using var home = new Install();
        install.Add(@"weather\0.png", "shipped-day");
        Directory.CreateDirectory(Path.Combine(home.Assets, "weather"));
        File.WriteAllText(Path.Combine(home.Assets, "weather", "0.png"), "owner-art");
        install.Add(@"weather\0-night.png", "shipped-night");

        var first = RuntimeAssets.CopyMissing(install.Assets, home.Assets);
        var second = RuntimeAssets.CopyMissing(install.Assets, home.Assets);

        Assert.Equal(1, first.Copied);
        Assert.Equal(0, second.Copied);
        Assert.Equal("owner-art", File.ReadAllText(Path.Combine(home.Assets, "weather", "0.png")));
        Assert.Equal("shipped-night", File.ReadAllText(Path.Combine(home.Assets, "weather", "0-night.png")));
    }

    [Fact]
    public void A_Missing_Shipped_Folder_Is_A_NoOp()
    {
        using var home = new Install();
        var result = RuntimeAssets.CopyMissing(Path.Combine(home.Root, "no-such-assets"), home.Assets);
        Assert.Equal(0, result.Copied);
        Assert.Empty(result.Failed);
    }

    /// <summary>The one-argument form writes into the runtime dir (DESKWALL_HOME under tests).</summary>
    [Fact]
    public void Defaults_To_The_Runtime_Assets_Folder()
    {
        using var install = new Install();
        var name = "rt-" + Guid.NewGuid().ToString("N") + ".png";
        install.Add(Path.Combine("weather", name), "x");
        try
        {
            RuntimeAssets.CopyMissing(install.Assets);
            Assert.True(File.Exists(Paths.InRuntime("assets", "weather", name)));
        }
        finally { File.Delete(Paths.InRuntime("assets", "weather", name)); }
    }

    /// <summary>The build ships the weather set, night icons included, where the daemon looks.</summary>
    [Fact]
    public void The_Build_Ships_The_Weather_Icons_Beside_The_Exe()
    {
        Assert.True(File.Exists(Path.Combine(RuntimeAssets.ShippedDir, "weather", "0-night.png")));
        Assert.True(File.Exists(Path.Combine(RuntimeAssets.ShippedDir, "weather", "LICENSE")));
    }
}
