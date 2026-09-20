using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Render;
using DeskWall.Core.Resolve;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using Microsoft.Win32;
using CoreRect = DeskWall.Core.Rect;

namespace DeskWall.Designer.Views;

/// <summary>
/// Job: get a layout onto this display in one click, or point at one the user already has.
/// Shown once per display signature the store has never seen. Deliberately left out: wizard
/// steps, a welcome paragraph, and any preview of today's live data (the preview renders against
/// an empty value tree - see <see cref="Render"/> - so choosing a starter needs no secrets and no
/// network first).
/// </summary>
public partial class FirstRun : Window
{
    private static readonly DisplaySignature StarterSignature = new("starter", 3440, 1440, 100);

    private readonly DisplaySignature _signature;
    private readonly LayoutStore _store;

    /// <summary>Set when the window closes with DialogResult true: the layout now registered in the
    /// store for the current signature, ready to open in the designer.</summary>
    public string? ChosenPath { get; private set; }

    public FirstRun(DisplaySignature signature, LayoutStore store)
    {
        InitializeComponent();
        _signature = signature;
        _store = store;
        LoadPreview(ColumnPreview, "starter-column.json");
        LoadPreview(ClockPreview, "starter-clock.json");
        LoadPreview(BlankPreview, "starter-blank.json");
    }

    /// <summary>Starters ship beside the exe (DeskWall.Designer.csproj copies layouts/starter-*.json
    /// into a "layouts" subfolder of the output directory, which covers both a dev build and a
    /// published one).</summary>
    private static string StarterPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "layouts", fileName);

    private void LoadPreview(Image target, string starterFile)
    {
        try
        {
            var path = StarterPath(starterFile);
            if (!File.Exists(path)) return;   // leave the preview blank; "Use this" still reports the error
            var scaled = LayoutScaler.Scale(LayoutFile.Load(path), StarterSignature, _signature);
            target.Source = Render(scaled);
        }
        catch (Exception)
        {
            // A broken starter must not stop the picker from showing at all; picking it below fails loudly.
        }
    }

    /// <summary>Full Core pipeline (BaseCache, LayoutResolver, FrameRenderer) against an empty value
    /// tree, matching what the daemon would draw with no sources running yet. Only Core draws; WPF
    /// only displays the resulting bitmap, scaled down by the Image control's own layout.</summary>
    private BitmapSource Render(LayoutFile layout)
    {
        var w = _signature.Width; var h = _signature.Height;
        var basePath = BaseCache.Ensure(layout.BaseImage, w, h, layout.BaseFit);
        var resolved = LayoutResolver.Resolve(layout, ValueTree.Empty);
        using var frame = new FrameRenderer(w, h).RenderAll(basePath, resolved);
        var pixels = new byte[w * h * 4];
        frame.CopyTo(pixels);
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    private void UseStarter_Click(object sender, RoutedEventArgs e)
    {
        var starterFile = (string)((Button)sender).Tag;
        try
        {
            var scaled = LayoutScaler.Scale(LayoutFile.Load(StarterPath(starterFile)), StarterSignature, _signature);
            var dest = ShellState.LayoutPathFor(_signature);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            scaled.Save(dest);
            _store.Set(_signature, dest);
            ChosenPath = dest;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not create the layout: {ex.Message}", "DeskWall", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenExisting_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Layout files (*.json)|*.json|All files (*.*)|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            LayoutFile.Load(dlg.FileName);   // fail fast on a bad file, before it is registered
            _store.Set(_signature, dlg.FileName);
            ChosenPath = dlg.FileName;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"'{dlg.FileName}' is not a valid layout: {ex.Message}", "DeskWall", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
