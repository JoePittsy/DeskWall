using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using Xunit;

/// <summary>Switching which layout the daemon paints (#31 `deskwall layouts use`, #73 the designer's
/// picker): the library both list, and the one switch both call.</summary>
public class LayoutLibraryTests
{
    private static readonly DisplaySignature Dell = new("DELL", 3440, 1440, 100);
    private static readonly DisplaySignature Apollo = new("APOLLO", 3440, 1440, 100);
    private static readonly DisplaySignature Laptop = new("LAPTOP", 1920, 1080, 100);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, uint size);

    /// <summary>The 8.3 form of an existing path, or the path itself where the volume makes none.</summary>
    private static string ShortPath(string path)
    {
        var buffer = new System.Text.StringBuilder(1024);
        return GetShortPathName(path, buffer, (uint)buffer.Capacity) is > 0 and < 1024 ? buffer.ToString() : path;
    }

    /// <summary>A runtime dir: layouts.json, a layouts\ library folder, and a templates\ folder
    /// standing in for the repo's layouts\.</summary>
    private sealed class Home
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "deskwall-tests", "library-" + Guid.NewGuid().ToString("N")[..8]);
        public string Library => Path.Combine(Root, "layouts");
        public string Templates => Path.Combine(Root, "templates");
        public string StorePath => Path.Combine(Root, "layouts.json");
        public List<string> Errors { get; } = [];

        public Home()
        {
            Directory.CreateDirectory(Library);
            Directory.CreateDirectory(Templates);
        }

        public LayoutStore Store() => new(StorePath, Errors.Add);

        public string Layout(string dir, string name, string text = "x", int version = 2)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllText(p, $$$"""{ "version": {{{version}}}, "baseImage": "x.jpg", "sources": [], "components": [ { "type": "text", "id": "t", "rect": [3340, 0, 100, 50], "text": "{{{text}}}" } ] }""");
            return p;
        }
    }

    [Fact]
    public void List_Is_The_Library_Folder_Plus_Every_Registered_File_Sorted_By_Name()
    {
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        var vapor = home.Layout(home.Library, "vapor.json");
        var elsewhere = home.Layout(home.Templates, "alpine.json");
        var store = home.Store();
        store.Set(Laptop, elsewhere);

        var names = LayoutLibrary.List(store, home.Library);

        Assert.Equal(["alpine.json", "rice.json", "vapor.json"], names.Select(c => c.Name));
        Assert.Equal([elsewhere, rice, vapor], names.Select(c => c.Path));
    }

    [Fact]
    public void List_Leaves_Out_Migrate_Backups_And_Lists_A_Shared_File_Once()
    {
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        home.Layout(home.Library, "rice.v1.json", version: 1);
        var store = home.Store();
        store.Set(Dell, rice);
        store.Set(Apollo, rice);

        Assert.Equal(["rice.json"], LayoutLibrary.List(store, home.Library).Select(c => c.Name));
    }

    [Fact]
    public void List_Of_A_Library_Folder_That_Does_Not_Exist_Is_Just_The_Registered_Files()
    {
        var home = new Home();
        Directory.Delete(home.Library);
        Assert.Empty(LayoutLibrary.List(home.Store(), home.Library));
    }

    [Fact]
    public void Use_Repoints_Every_Display_Sharing_The_Current_File_And_No_Other()
    {
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        var vapor = home.Layout(home.Library, "vapor.json");
        var laptop = home.Layout(home.Library, "laptop.json");
        var store = home.Store();
        store.Set(Dell, rice);
        store.Set(Apollo, rice);
        store.Set(Laptop, laptop);

        var result = LayoutLibrary.Use(store, Dell, "vapor", home.Library);

        Assert.True(result.Changed);
        Assert.Equal(rice, result.From);
        Assert.Equal(vapor, result.To);
        Assert.Equal([Apollo.Key, Dell.Key], result.Keys.Order(StringComparer.Ordinal));
        // On disk, where the daemon's watcher reads it, not only in this process.
        var again = home.Store().Entries;
        Assert.Equal(vapor, again[Dell.Key]);
        Assert.Equal(vapor, again[Apollo.Key]);
        Assert.Equal(laptop, again[Laptop.Key]);
    }

    [Fact]
    public void Use_Repoints_An_Entry_Naming_The_Same_File_By_Its_Short_Path()
    {
        // Found by the first real run: an entry written by hand through an 8.3 path (%TEMP% is
        // often C:\Users\JOSEPH~1\...) is the same file, and was left pointing at the old layout.
        var home = new Home();
        var rice = Path.GetFullPath(home.Layout(home.Library, "rice.json"));
        var vapor = home.Layout(home.Library, "vapor.json");
        var shortRice = ShortPath(rice);
        if (string.Equals(shortRice, rice, StringComparison.OrdinalIgnoreCase)) return;   // this volume makes no 8.3 names (JOES-PC's does)
        File.WriteAllText(home.StorePath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
        {
            ["layouts"] = new() { [Dell.Key] = rice, [Apollo.Key] = shortRice },
        }));
        var store = home.Store();

        Assert.Single(LayoutLibrary.List(store, home.Library), c => c.Name == "rice.json");
        var result = LayoutLibrary.Use(store, Dell, "vapor", home.Library);

        Assert.Equal(2, result.Keys.Count);
        Assert.Equal(vapor, store.Entries[Apollo.Key]);
    }

    [Fact]
    public void Use_Accepts_The_File_Name_With_Or_Without_Json_In_Any_Case()
    {
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        var vapor = home.Layout(home.Library, "vapor.json");
        var store = home.Store();
        store.Set(Dell, rice);

        Assert.Equal(vapor, LayoutLibrary.Use(store, Dell, "VAPOR.json", home.Library).To);
        Assert.Equal(rice, LayoutLibrary.Use(store, Dell, "Rice", home.Library).To);
    }

    [Fact]
    public void Use_From_A_Display_With_No_Entry_Repoints_The_Closest_Match_And_Leaves_It_Scaled()
    {
        // Over RDP: this session's signature is in nobody's entry and resolves, scaled, from the
        // console's. The switch must move what it is actually looking at, and must not give the RDP
        // signature an exact entry, which would paint a 3440-wide layout unscaled.
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        var vapor = home.Layout(home.Library, "vapor.json");
        var store = home.Store();
        store.Set(Dell, rice);
        var rdp = new DisplaySignature("DELL", 1920, 1200, 100);

        var result = LayoutLibrary.Use(store, rdp, "vapor", home.Library);

        Assert.Equal([Dell.Key], result.Keys);
        Assert.False(store.Entries.ContainsKey(rdp.Key));
        var res = store.Resolve(rdp)!;
        Assert.Equal(vapor, res.SourcePath);
        Assert.True(res.Scaled);
    }

    [Fact]
    public void Use_With_An_Empty_Store_Registers_This_Display()
    {
        var home = new Home();
        var vapor = home.Layout(home.Library, "vapor.json");
        var store = home.Store();

        var result = LayoutLibrary.Use(store, Dell, "vapor", home.Library);

        Assert.Null(result.From);
        Assert.Equal([Dell.Key], result.Keys);
        Assert.Equal(vapor, store.Entries[Dell.Key]);
    }

    [Fact]
    public void Use_Repoints_A_Display_Whose_Own_File_Is_Broken()
    {
        // A broken layout is exactly when the owner reaches for another one.
        var home = new Home();
        var rice = Path.Combine(home.Library, "rice.json");
        File.WriteAllText(rice, "{ not json");
        var vapor = home.Layout(home.Library, "vapor.json");
        var store = home.Store();
        store.Set(Dell, rice);

        Assert.Equal(vapor, LayoutLibrary.Use(store, Dell, "vapor", home.Library).To);
        Assert.Equal(vapor, store.Entries[Dell.Key]);
    }

    [Fact]
    public void Use_Of_The_Layout_Already_In_Use_Writes_Nothing()
    {
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        var store = home.Store();
        store.Set(Dell, rice);
        var written = File.GetLastWriteTimeUtc(home.StorePath);
        File.SetLastWriteTimeUtc(home.StorePath, written.AddMinutes(-5));

        var result = LayoutLibrary.Use(store, Dell, "rice", home.Library);

        Assert.False(result.Changed);
        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(home.StorePath));
    }

    [Fact]
    public void Use_Of_A_Path_Outside_The_Library_Copies_It_In_And_Uses_The_Copy()
    {
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        var template = home.Layout(home.Templates, "vapor.json", text: "pink");
        var store = home.Store();
        store.Set(Dell, rice);

        var result = LayoutLibrary.Use(store, Dell, template, home.Library);

        var copy = Path.Combine(home.Library, "vapor.json");
        Assert.True(result.Imported);
        Assert.Equal(copy, result.To);
        Assert.Equal(File.ReadAllText(template), File.ReadAllText(copy));
        Assert.Equal(copy, store.Entries[Dell.Key]);
    }

    [Fact]
    public void Use_Of_A_Path_Whose_Identical_Copy_Is_Already_In_The_Library_Reuses_It()
    {
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        var copy = home.Layout(home.Library, "vapor.json", text: "pink");
        var template = home.Layout(home.Templates, "vapor.json", text: "pink");
        var store = home.Store();
        store.Set(Dell, rice);

        var result = LayoutLibrary.Use(store, Dell, template, home.Library);

        Assert.False(result.Imported);
        Assert.Equal(copy, result.To);
    }

    [Fact]
    public void Use_Of_A_Path_Whose_Name_Is_Taken_By_A_Different_Library_File_Is_Refused()
    {
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        var mine = home.Layout(home.Library, "vapor.json", text: "edited in the designer");
        var template = home.Layout(home.Templates, "vapor.json", text: "pink");
        var store = home.Store();
        store.Set(Dell, rice);

        var ex = Assert.Throws<LayoutSwitchException>(() => LayoutLibrary.Use(store, Dell, template, home.Library));

        Assert.Contains("vapor.json", ex.Message);
        Assert.Contains("edited in the designer", File.ReadAllText(mine));
        Assert.Equal(rice, store.Entries[Dell.Key]);
    }

    [Fact]
    public void Use_Of_A_Registered_File_Outside_The_Library_Uses_It_In_Place()
    {
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        var elsewhere = home.Layout(home.Templates, "alpine.json");
        var store = home.Store();
        store.Set(Dell, rice);
        store.Set(Laptop, elsewhere);

        var result = LayoutLibrary.Use(store, Dell, "alpine", home.Library);

        Assert.False(result.Imported);
        Assert.Equal(elsewhere, result.To);
        Assert.False(File.Exists(Path.Combine(home.Library, "alpine.json")));
    }

    [Fact]
    public void An_Unknown_Name_Is_Refused_With_What_There_Is()
    {
        var home = new Home();
        home.Layout(home.Library, "rice.json");
        home.Layout(home.Library, "vapor.json");

        var ex = Assert.Throws<LayoutSwitchException>(() => LayoutLibrary.Use(home.Store(), Dell, "synthwave", home.Library));

        Assert.Contains("synthwave", ex.Message);
        Assert.Contains("rice", ex.Message);
        Assert.Contains("vapor", ex.Message);
    }

    [Fact]
    public void A_Name_Two_Files_Answer_To_Is_Refused_Naming_Both()
    {
        var home = new Home();
        var inLibrary = home.Layout(home.Library, "rice.json");
        var outside = home.Layout(home.Templates, "rice.json");
        var store = home.Store();
        store.Set(Laptop, outside);

        var ex = Assert.Throws<LayoutSwitchException>(() => LayoutLibrary.Use(store, Dell, "rice", home.Library));

        Assert.Contains(inLibrary, ex.Message);
        Assert.Contains(outside, ex.Message);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "version": 3, "baseImage": "x.jpg", "sources": [], "components": [] }""")]
    public void A_Layout_The_Daemon_Could_Not_Paint_Is_Refused_Before_Anything_Is_Written(string text)
    {
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        File.WriteAllText(Path.Combine(home.Library, "broken.json"), text);
        var template = Path.Combine(home.Templates, "broken2.json");
        File.WriteAllText(template, text);
        var store = home.Store();
        store.Set(Dell, rice);

        Assert.Throws<LayoutSwitchException>(() => LayoutLibrary.Use(store, Dell, "broken", home.Library));
        Assert.Throws<LayoutSwitchException>(() => LayoutLibrary.Use(store, Dell, template, home.Library));

        Assert.Equal(rice, home.Store().Entries[Dell.Key]);
        Assert.False(File.Exists(Path.Combine(home.Library, "broken2.json")));
    }

    [Fact]
    public void A_Path_That_Does_Not_Exist_Is_Refused()
    {
        var home = new Home();
        var ex = Assert.Throws<LayoutSwitchException>(() =>
            LayoutLibrary.Use(home.Store(), Dell, Path.Combine(home.Templates, "nope.json"), home.Library));
        Assert.Contains("nope.json", ex.Message);
    }

    [Fact]
    public void InUse_Is_The_File_This_Display_Draws_From()
    {
        var home = new Home();
        var rice = home.Layout(home.Library, "rice.json");
        var store = home.Store();
        Assert.Null(store.InUse(Dell));
        store.Set(Dell, rice);
        Assert.Equal(rice, store.InUse(Dell));
        Assert.Equal(rice, store.InUse(new DisplaySignature("DELL", 1920, 1200, 100)));
    }
}
