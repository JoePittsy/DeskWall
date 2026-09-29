using System.Diagnostics;
using DeskWall.Core.Layout;
using Xunit;

namespace DeskWall.Core.Tests;

/// <summary>`deskwall --home &lt;scratch&gt; migrate` against the JIT deskwall.exe the project reference
/// copies next to the test binaries (with the shipped widgets beside it), as StopCommandTests does.
/// Every file it touches is a copy in a scratch home.</summary>
public class MigrateCommandTests
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "deskwall.exe");

    private static (int Exit, string Out) Deskwall(string home, params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--home");
        psi.ArgumentList.Add(home);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30_000), "deskwall did not exit");
        return (p.ExitCode, output);
    }

    /// <summary>A scratch home holding a copy of tests/fixtures/layouts-v1/clock-disks.json (the v1
    /// file with stamped widgets), registered in layouts.json so the default "every registered
    /// file" path is what runs.</summary>
    private static (string Home, string Layout) Scratch()
    {
        var home = Path.Combine(Path.GetTempPath(), "deskwall-tests", "migrate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        var layout = Path.Combine(home, "clock-disks.json");
        File.Copy(RepoFile("tests", "fixtures", "layouts-v1", "clock-disks.json"), layout);
        File.WriteAllText(Path.Combine(home, "layouts.json"),
            $$"""{ "layouts": { "TEST|3440x1440@100": {{System.Text.Json.JsonSerializer.Serialize(layout)}} } }""");
        return (home, layout);
    }

    private static string RepoFile(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "DeskWall.slnx"))) return Path.Combine([d.FullName, .. parts]);
        throw new InvalidOperationException("repo root not found");
    }

    private static void Clean(string home)
    {
        try { Directory.Delete(home, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Migrate_Refuses_A_File_Whose_Backup_Already_Exists_And_Leaves_Both_Alone()
    {
        var (home, layout) = Scratch();
        try
        {
            var backup = Path.ChangeExtension(layout, ".v1.json");
            File.WriteAllText(backup, "the only v1 copy");
            var before = File.ReadAllBytes(layout);

            var (exit, output) = Deskwall(home, "migrate");
            Assert.NotEqual(0, exit);
            Assert.Contains(layout, output);   // the default is every file in layouts.json
            Assert.Contains("refused: backup", output);
            Assert.Equal(before, File.ReadAllBytes(layout));
            Assert.Equal("the only v1 copy", File.ReadAllText(backup));
        }
        finally { Clean(home); }
    }

    [Fact]
    public void Migrate_Leaves_A_Version_2_File_Alone()
    {
        var (home, layout) = Scratch();
        try
        {
            File.WriteAllText(layout, """{ "version": 2, "baseImage": "x.jpg", "sources": [], "components": [] }""");
            var before = File.ReadAllBytes(layout);
            var (exit, output) = Deskwall(home, "migrate", layout);
            Assert.Equal(0, exit);
            Assert.Contains("nothing to do", output);
            Assert.Equal(before, File.ReadAllBytes(layout));
            Assert.False(File.Exists(Path.ChangeExtension(layout, ".v1.json")));
        }
        finally { Clean(home); }
    }

    [Fact]
    public void Check_Prints_Copies_And_Equivalence_And_Writes_Nothing()
    {
        var (home, layout) = Scratch();
        try
        {
            var before = File.ReadAllBytes(layout);
            var files = Directory.GetFiles(home).Order().ToList();

            var (exit, output) = Deskwall(home, "migrate", "--check");
            Assert.True(exit == 0, output);
            Assert.Contains("  copy ", output);
            Assert.Contains("equivalent: yes", output);
            // Each copy once: as its copy line, not again as a note.
            Assert.Equal(1, output.Split('\n').Count(l => l.StartsWith("  copy clock-1 ", StringComparison.Ordinal)));
            Assert.DoesNotContain("note: copy", output);
            Assert.DoesNotContain("note:   knob", output);
            Assert.Equal(before, File.ReadAllBytes(layout));
            Assert.Equal(files, Directory.GetFiles(home).Order().ToList());
        }
        finally { Clean(home); }
    }

    [Fact]
    public void Migrate_Writes_A_Backup_And_A_V2_File_And_A_Second_Run_Refuses()
    {
        var (home, layout) = Scratch();
        try
        {
            var before = File.ReadAllBytes(layout);
            var backup = Path.ChangeExtension(layout, ".v1.json");

            var (exit, output) = Deskwall(home, "migrate");
            Assert.True(exit == 0, output);
            Assert.Equal(before, File.ReadAllBytes(backup));
            var v2 = LayoutFile.Load(layout);
            Assert.Equal(2, v2.Version);
            Assert.NotEmpty(v2.Copies!);
            Assert.False(File.Exists(layout + ".tmp"));

            var migrated = File.ReadAllBytes(layout);
            (exit, output) = Deskwall(home, "migrate");
            Assert.NotEqual(0, exit);
            Assert.Contains("refused: backup", output);
            Assert.Equal(migrated, File.ReadAllBytes(layout));
            Assert.Equal(before, File.ReadAllBytes(backup));
        }
        finally { Clean(home); }
    }
}
