using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DeskWall.Core;
using DeskWall.Core.Display;
using DeskWall.Core.Layout;
using DeskWall.Core.Shortcuts;
using DeskWall.Core.Wallpaper;
using DeskWall.Designer.Model;
using Microsoft.Win32;
using Windows.Win32;

namespace DeskWall.Designer.Views;

/// <summary>
/// Job: see whether the daemon is healthy and change the handful of things that are not part of a
/// layout (start at logon, tray icon, secrets, base image, the per-display layout table) without
/// ever opening a channel to the daemon itself - everything here reads the process list, the log
/// file or the registry, and writes only files the daemon already watches. Deliberately left out:
/// a general preferences grab-bag, anything that duplicates the canvas or the source panel, and any
/// icon-per-row decoration - five sections, exactly the ones the spec names, nothing else.
/// </summary>
public partial class SettingsPage : Window
{
    private readonly DesignerModel? _model;
    private readonly DisplaySignature _signature;
    private readonly LayoutStore _store;
    private readonly Settings _settings;
    private bool _loading;

    public SettingsPage(DesignerModel? model, DisplaySignature signature, LayoutStore store)
    {
        InitializeComponent();
        _model = model;
        _signature = signature;
        _store = store;
        _settings = Settings.Load();

        RefreshDaemonStatus();
        LoadWallpaperSection();
        LoadDesktopSection();
        LoadLayoutsGrid();
    }

    /// <summary>Escape returns to the canvas. The shell opens this page over the window it came
    /// from, and everything on it writes as it is changed, so there is nothing to confirm on the way
    /// out and no Close button earning its place.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    // ---- 1. Daemon -------------------------------------------------------------------------

    private void RefreshDaemonStatus()
    {
        var exeFound = FindDaemonExe() is not null;
        StartButton.IsEnabled = exeFound;
        RefreshNowButton.IsEnabled = exeFound;
        VerifyButton.IsEnabled = exeFound;

        var procs = Process.GetProcessesByName("deskwall");
        try
        {
            if (procs.Length == 0)
            {
                DaemonStatusText.Text = "Daemon: not running";
                DaemonFootprintText.Text = "";
            }
            else
            {
                var p = procs[0];
                DaemonStatusText.Text = $"Daemon: running (pid {p.Id})";
                DaemonFootprintText.Text =
                    $"{p.WorkingSet64 / 1048576.0:0.0} MB working set . {p.PrivateMemorySize64 / 1048576.0:0.0} MB private . " +
                    $"{p.HandleCount} handles . {p.Threads.Count} threads";
            }
        }
        finally { foreach (var p in procs) p.Dispose(); }

        var logPath = Paths.InRuntime("deskwall.log");
        DaemonLogText.Text = File.Exists(logPath)
            ? string.Join(Environment.NewLine, File.ReadAllLines(logPath).TakeLast(5))
            : "(no log yet)";

        _loading = true;
        StartAtLogonCheck.IsChecked = Startup.Installed() is not null;
        TrayIconCheck.IsChecked = _settings.TrayIcon;
        _loading = false;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var exe = FindDaemonExe();
        if (exe is null) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }); }
        catch (Exception ex) { ActionOutputText.Text = $"Could not start: {ex.Message}"; }
        RefreshDaemonStatus();
    }

    /// <summary>Posts WM_CLOSE to the daemon's hidden host window (class "DeskWallHost"), the same
    /// message a session end sends it - no channel of our own, just a message the daemon already
    /// handles (see HostWindow.WndProc).</summary>
    private unsafe void Stop_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = PInvoke.FindWindow("DeskWallHost", (string?)null);
        if (hwnd.IsNull)
        {
            ActionOutputText.Text = "DeskWall Host window not found; the daemon is not running.";
            return;
        }
        PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, 0, 0);
        ActionOutputText.Text = "Stop requested.";
        RefreshDaemonStatus();
    }

    private void RefreshNow_Click(object sender, RoutedEventArgs e)
    {
        var exe = FindDaemonExe();
        if (exe is null) return;
        RunDaemonCommand(exe, "tick");
    }

    private void StartAtLogon_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var exe = FindDaemonExe();
        if (exe is null)
        {
            _loading = true; StartAtLogonCheck.IsChecked = false; _loading = false;
            ActionOutputText.Text = "deskwall.exe was not found; cannot register it to start at logon.";
            return;
        }
        Startup.Install(exe);
    }

    private void StartAtLogon_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Startup.Uninstall();
    }

    private void TrayIcon_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.TrayIcon = TrayIconCheck.IsChecked == true;
        _settings.Save();
    }

    private void RunDaemonCommand(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var text = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
            ActionOutputText.Text = text.Length == 0 ? $"(exit {p.ExitCode}, no output)" : text;
        }
        catch (Exception ex)
        {
            ActionOutputText.Text = $"Failed to run '{exe} {string.Join(' ', args)}': {ex.Message}";
        }
        RefreshDaemonStatus();
    }

    /// <summary>deskwall.exe lives beside DeskWall.Designer.exe once installed; in a dev build it is
    /// somewhere under src/DeskWall.Daemon/bin. Buttons that need it disable themselves when neither
    /// is found rather than fail on click.</summary>
    private static string? FindDaemonExe()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "deskwall.exe");
        if (File.Exists(beside)) return beside;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var daemonProj = Path.Combine(dir.FullName, "src", "DeskWall.Daemon");
            if (Directory.Exists(daemonProj))
            {
                var found = Directory.EnumerateFiles(daemonProj, "deskwall.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (found is not null) return found;
            }
            dir = dir.Parent;
        }
        return null;
    }

    // ---- 2. Wallpaper (edits the open model, not settings.json) ----------------------------

    private void LoadWallpaperSection()
    {
        var has = _model is not null;
        WallpaperNoModelText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        WallpaperPanel.IsEnabled = has;
        if (!has) return;

        _loading = true;
        BaseImagePathText.Text = _model!.Layout.BaseImage;
        FitCombo.SelectedIndex = _model.Layout.BaseFit switch { Fit.Cover => 0, Fit.Contain => 1, _ => 2 };
        EncodeCombo.SelectedIndex = string.Equals(_model.Layout.Encode, "png", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        QualitySlider.Value = _model.Layout.JpegQuality;
        QualityValueText.Text = _model.Layout.JpegQuality.ToString();
        QualitySlider.IsEnabled = string.Equals(_model.Layout.Encode, "jpeg", StringComparison.OrdinalIgnoreCase);
        _loading = false;
    }

    private void BrowseBaseImage_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        var dlg = new OpenFileDialog { Filter = "Images (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png|All files (*.*)|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName;
        _model.Edit("Base image", l => l.BaseImage = path);
        BaseImagePathText.Text = path;
    }

    private void FitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _model is null) return;
        var fit = FitCombo.SelectedIndex switch { 0 => Fit.Cover, 1 => Fit.Contain, _ => Fit.Stretch };
        _model.Edit("Base fit", l => l.BaseFit = fit);
    }

    private void EncodeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _model is null) return;
        var encode = EncodeCombo.SelectedIndex == 1 ? "png" : "jpeg";
        _model.Edit("Encode", l => l.Encode = encode);
        QualitySlider.IsEnabled = encode == "jpeg";
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Slider coerces Value while InitializeComponent is still wiring up named fields (setting
        // Maximum/Minimum in XAML can itself raise this before QualityValueText exists), so this can
        // fire before the constructor body runs.
        if (QualityValueText is not null) QualityValueText.Text = ((int)e.NewValue).ToString();
    }

    private void QualitySlider_Committed(object sender, MouseButtonEventArgs e) => CommitQuality();
    private void QualitySlider_LostFocus(object sender, RoutedEventArgs e) => CommitQuality();

    private void CommitQuality()
    {
        if (_loading || _model is null) return;
        var q = (int)QualitySlider.Value;
        if (q == _model.Layout.JpegQuality) return;
        _model.Edit("JPEG quality", l => l.JpegQuality = q);
    }

    // ---- 3. Desktop icons -------------------------------------------------------------------

    private void LoadDesktopSection()
    {
        bool available;
        try { available = DesktopView.IsAvailable(); }
        catch (Exception) { available = false; }
        DesktopAvailableText.Text = available ? "Desktop icons: available" : "Desktop icons: not available (hidden, or Explorer not reachable)";

        int iconSize;
        try { iconSize = DesktopView.IconSize(); }
        catch (Exception) { iconSize = 48; }
        var scale = _signature.ScalePercent;
        var cal = Core.Shortcuts.Calibration.Load().Get(iconSize, scale);
        CalibrationText.Text = cal is { } a
            ? $"{iconSize}@{scale} -> ({a.Dx},{a.Dy},{a.Size})"
            : $"{iconSize}@{scale}: not calibrated yet";
    }

    private void CalibrateNow_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "This briefly changes the wallpaper and places a test icon on the primary monitor, then restores both. Continue?",
                "Calibrate now", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        CalibrateStatusText.Text = "Calibrating...";
        try
        {
            var monitor = Monitors.Enumerate().First(m => m.IsPrimary);
            var wallpaper = WallpaperSetter.Get(monitor.WallpaperMonitorId) ?? "";
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var explorer = Path.Combine(windowsDir, "explorer.exe");
            var result = Calibrator.Run(monitor, wallpaper,
                lnk => ShortcutFiles.Write(lnk, new ShortcutSpec(explorer, "", windowsDir, "DeskWall calibration probe", BlankIcon.Ensure())),
                line => CalibrateStatusText.Text = line);
            CalibrateStatusText.Text =
                $"Calibrated: {result.IconSize}px @ {result.ScalePercent}% -> arrow ({result.Arrow.Dx},{result.Arrow.Dy},{result.Arrow.Size})";
            LoadDesktopSection();
        }
        catch (Exception ex)
        {
            CalibrateStatusText.Text = $"Calibration failed: {ex.Message}";
        }
    }

    /// <summary>Phase 6's `deskwall verify` does not exist yet; until it does, `deskwall shortcuts`
    /// is the closest thing and its output is shown as-is.</summary>
    private void VerifyPlacement_Click(object sender, RoutedEventArgs e)
    {
        var exe = FindDaemonExe();
        if (exe is null) return;
        RunDaemonCommand(exe, "shortcuts");
    }

    // ---- 4. Secrets -------------------------------------------------------------------------

    private void EditSecrets_Click(object sender, RoutedEventArgs e) => new SecretsEditor { Owner = this }.ShowDialog();

    // ---- 5. Layouts -------------------------------------------------------------------------

    private void LoadLayoutsGrid()
    {
        LayoutsGrid.ItemsSource = _store.Entries
            .Select(kv => new LayoutEntryRow(kv.Key, kv.Value))
            .OrderBy(r => r.SignatureKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        UseForCurrentButton.IsEnabled = _model?.Path is not null;
    }

    private void RemoveLayout_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not LayoutEntryRow row) return;
        _store.Remove(DisplaySignature.Parse(row.SignatureKey));
        LoadLayoutsGrid();
    }

    private void UseForCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_model?.Path is not { } path) return;
        _store.Set(_signature, path);
        LoadLayoutsGrid();
    }
}

internal sealed record LayoutEntryRow(string SignatureKey, string Path);
