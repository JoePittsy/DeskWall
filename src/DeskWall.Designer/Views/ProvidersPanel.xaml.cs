using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskWall.Core.Events;
using DeskWall.Core.Sources;
using DeskWall.Designer.Model;

namespace DeskWall.Designer.Views;

/// <summary>
/// What another program has pushed into DeskWall, and what it publishes.
/// <para>
/// Job: make a pushed provider bindable without the user telling the designer anything. The daemon
/// owns the pipe and is a separate process, so the only thing this can read is the file the daemon
/// writes (spec 3); it watches that file the way LayoutWatcher watches a layout, so a producer
/// started while this window is open appears without a restart.
/// </para>
/// <para>
/// Three verbs, and each answers a question the user will actually have. Forget: it accumulates
/// and nothing expires. Describe: the field list is a dump of last values until somebody names
/// them. Send: the widget has to be built before the producer runs at all.
/// </para>
/// </summary>
public partial class ProvidersPanel : UserControl
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly ProvidersModel _model;
    private readonly EventBus _bus;
    private readonly System.Threading.Timer _debounce;
    private FileSystemWatcher? _watcher;
    private IReadOnlyList<ProviderView> _views = [];
    private string? _selected;
    private bool _disposed;

    public ProvidersPanel() : this(ProvidersModel.Default(), new EventBus(SystemClock.Instance, EventBus.DefaultCoalesce, autoWake: false))
    {
    }

    /// <param name="bus">The designer's own bus, for test events. It is not the daemon's: two
    /// processes cannot own one pipe, so a test event is visible here and nowhere else, which is
    /// what makes it safe to press while the real wallpaper is live.</param>
    public ProvidersPanel(ProvidersModel model, EventBus bus)
    {
        _model = model;
        _bus = bus;
        InitializeComponent();
        _debounce = new System.Threading.Timer(_ => Dispatcher.BeginInvoke(new Action(Reload)), null, Timeout.Infinite, Timeout.Infinite);
        Loaded += (_, _) => { StartWatching(); Reload(); };
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Something worth putting on the window's status line.</summary>
    public event Action<string>? Status;

    /// <summary>The remembered records changed. The window pushes them into LiveSources so the
    /// binding picker offers them and the preview draws them.</summary>
    public event Action<IReadOnlyList<ProviderRecord>>? ProvidersChanged;

    public IReadOnlyList<ProviderRecord> Records => Merge();

    /// <summary>Re-read the file and the manifests and rebuild the list. Idempotent and cheap.</summary>
    public void Reload()
    {
        if (_disposed) return;
        _views = _model.Load();
        if (_model.LastError is { } problem) Status?.Invoke($"Providers: {problem}");

        var previous = _selected;
        Chooser.SelectionChanged -= Chooser_SelectionChanged;
        Chooser.Items.Clear();
        foreach (var v in _views)
        {
            var label = v.Remembered && v.ReceivedAt is { } at
                ? $"{v.Name}  ·  {at.LocalDateTime:HH:mm:ss}"
                : $"{v.Name}  ·  described";
            Chooser.Items.Add(new ComboBoxItem { Content = label, Tag = v.Name });
        }
        _selected = _views.Any(v => string.Equals(v.Name, previous, StringComparison.OrdinalIgnoreCase)) ? previous : _views.FirstOrDefault()?.Name;
        foreach (ComboBoxItem item in Chooser.Items)
            if (Equals(item.Tag, _selected)) Chooser.SelectedItem = item;
        Chooser.SelectionChanged += Chooser_SelectionChanged;

        Chooser.Visibility = _views.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyNote.Visibility = _views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSelected();
        ProvidersChanged?.Invoke(Merge());
    }

    /// <summary>The daemon's records plus anything sent from the test box, so the preview draws a
    /// provider that has never run here exactly as it will draw the real one.</summary>
    private IReadOnlyList<ProviderRecord> Merge()
    {
        var byName = new Dictionary<string, ProviderRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _model.Records) byName[r.Name] = r;
        // A test event wins: it is the newer statement, and it is the one the user is looking at.
        foreach (var (name, r) in _bus.Providers) byName[name] = r;
        return byName.Values.ToList();
    }

    private void ShowSelected()
    {
        Fields.Items.Clear();
        var view = _views.FirstOrDefault(v => string.Equals(v.Name, _selected, StringComparison.OrdinalIgnoreCase));
        ForgetButton.IsEnabled = view?.Remembered == true;
        DescribeButton.IsEnabled = view is not null;
        SendButton.IsEnabled = view is not null;
        if (view is null) { TestJson.Text = ""; return; }

        foreach (var f in view.Fields)
        {
            var row = new DockPanel();
            var value = new TextBlock
            {
                Text = f.Value ?? "-",
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush("TextFillColorTertiaryBrush"),
            };
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(new TextBlock
            {
                Text = f.Path,
                FontSize = 11,
                Foreground = Brush("TextFillColorPrimaryBrush"),
            });
            Fields.Items.Add(new ListBoxItem
            {
                Content = row,
                Tag = $"{view.Name}.{f.Path}",
                Padding = new Thickness(6, 2, 6, 2),
                ToolTip = f.Description ?? (f.Example is null ? view.Description : $"for example {f.Example}"),
            });
        }
        TestJson.Text = Seed(view);
    }

    /// <summary>A line the user can edit and send, prefilled from what the provider is known to
    /// publish, because an empty box is a question nobody can answer from memory.</summary>
    private static string Seed(ProviderView view)
    {
        var data = view.Fields
            .Where(f => f.Path.StartsWith("data.", StringComparison.Ordinal))
            .Select(f => $"\"{f.Path[5..]}\":{Literal(f)}")
            .ToList();
        return $"{{\"source\":\"{view.Name}\",\"data\":{{{string.Join(",", data)}}}}}";
    }

    private static string Literal(ProviderFieldView f)
    {
        var text = f.Value ?? f.Example ?? "";
        return f.Type is "number" or "bool" && text.Length > 0 ? text.ToLowerInvariant() : $"\"{text}\"";
    }

    private void Chooser_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = (Chooser.SelectedItem as ComboBoxItem)?.Tag as string;
        ShowSelected();
    }

    private void Fields_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((Fields.SelectedItem as ListBoxItem)?.Tag is not string path) return;
        try
        {
            Clipboard.SetText(path);
            Status?.Invoke($"Copied {path}");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Status?.Invoke(path);   // another process has the clipboard; saying it is most of the use
        }
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var name = _selected;
        try { _model.Forget(name); }
        catch (IOException ex) { Status?.Invoke($"Could not forget {name}: {ex.Message}"); return; }
        _bus.Forget(name);
        // The daemon holds its own copy and rewrites this file on its next save, so say what has
        // actually happened rather than implying the record is gone for good.
        Status?.Invoke($"Forgot {name}. A running daemon will write it back if the producer sends again.");
        Reload();
    }

    private void Describe_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var path = _model.Describe(_selected);
            Status?.Invoke($"Wrote {path}. Open it and name the fields.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status?.Invoke($"Could not describe {_selected}: {ex.Message}");
        }
        Reload();
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        var line = TestJson.Text.Trim();
        if (line.Length == 0) return;
        if (!_bus.Publish(line))
        {
            var why = _bus.Recent.LastOrDefault(r => !r.Accepted)?.Reason;
            Status?.Invoke($"Not sent: {why ?? "rejected"}");
            return;
        }
        Status?.Invoke("Sent into the designer. The daemon has not seen it.");
        Reload();
    }

    /// <summary>Watch events.json, not the whole runtime directory: the daemon rewrites
    /// frame-state.json on every tick and a directory watch would reload this panel once a
    /// minute for nothing.</summary>
    private void StartWatching()
    {
        if (_watcher is not null || _disposed) return;
        var path = EventStore.Path;
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var name = Path.GetFileName(path);
        try
        {
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
        }
        catch (ArgumentException)
        {
            return;   // the runtime dir went away between the check and here; nothing to watch
        }
        // The daemon writes to a temp file and renames, so the rename is the event that matters.
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        // One rename raises several events, and the daemon can save while the panel is reading:
        // settle first, then read once.
        try { _debounce.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _debounce.Dispose();
        _bus.Dispose();
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? SystemColors.ControlTextBrush;
}
