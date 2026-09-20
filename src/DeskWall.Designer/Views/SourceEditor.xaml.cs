using System.Windows;
using System.Windows.Controls;
using DeskWall.Core.Layout;

namespace DeskWall.Designer.Views;

/// <summary>Add or edit one source: name, type (fixed once created), refresh interval and its
/// settings as a key/value grid pre-populated with the keys that type knows. Settings are shown
/// verbatim, never resolved through Secrets, so a "{secret:name}" placeholder is what appears --
/// the real secret value is never read here.</summary>
public partial class SourceEditor : Window
{
    private static readonly string[] SourceTypes = ["time", "disks", "system", "command", "http", "rss", "file"];

    private static readonly IReadOnlyDictionary<string, string[]> KnownKeys = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["http"] = ["url", "timeout", "parse", "unixTimeFields"],
        ["rss"] = ["url", "timeout", "max"],
        ["file"] = ["path", "parse", "unixTimeFields"],
        ["command"] = ["command", "args", "workingDir", "timeout", "parse", "unixTimeFields"],
        ["time"] = [],
        ["disks"] = [],
        ["system"] = [],
    };

    private readonly bool _editingExisting;

    /// <summary>Set after ShowDialog() returns true.</summary>
    public SourceDef? Result { get; private set; }

    public SourceEditor() : this(null) { }

    public SourceEditor(SourceDef? existing)
    {
        InitializeComponent();
        _editingExisting = existing is not null;

        TypeCombo.ItemsSource = SourceTypes;
        if (_editingExisting)
        {
            TypeCombo.Visibility = Visibility.Collapsed;
            TypeText.Visibility = Visibility.Visible;
            TypeText.Text = existing!.Type;
            NameBox.Text = existing.Name;
            EveryBox.Text = existing.EverySeconds?.ToString() ?? "";
            RebuildSettingsRows(existing.Type, existing.Settings);
        }
        else
        {
            TypeCombo.SelectedIndex = 0;
            RebuildSettingsRows(SourceTypes[0], new Dictionary<string, string>());
        }
    }

    private string CurrentType => _editingExisting ? TypeText.Text : (string)(TypeCombo.SelectedItem ?? SourceTypes[0]);

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded && SettingsList is null) return;
        RebuildSettingsRows(CurrentType, new Dictionary<string, string>());
    }

    private void RebuildSettingsRows(string type, IReadOnlyDictionary<string, string> existingValues)
    {
        SettingsList.Children.Clear();
        var keys = new List<string>(KnownKeys.TryGetValue(type, out var known) ? known : []);
        foreach (var k in existingValues.Keys) if (!keys.Contains(k, StringComparer.OrdinalIgnoreCase)) keys.Add(k);
        foreach (var key in keys) AddSettingRow(key, existingValues.TryGetValue(key, out var v) ? v : "");
    }

    private void AddSettingRow(string key, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var keyBox = new TextBox { Text = key, Margin = new Thickness(0, 0, 4, 0) };
        var valueBox = new TextBox { Text = value, Margin = new Thickness(0, 0, 4, 0) };
        var removeButton = new Button { Content = "x", Width = 22 };
        removeButton.Click += (_, _) => SettingsList.Children.Remove(row);

        Grid.SetColumn(keyBox, 0);
        Grid.SetColumn(valueBox, 1);
        Grid.SetColumn(removeButton, 2);
        row.Children.Add(keyBox);
        row.Children.Add(valueBox);
        row.Children.Add(removeButton);
        SettingsList.Children.Add(row);
    }

    private void AddSettingButton_Click(object sender, RoutedEventArgs e) => AddSettingRow("", "");

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { MessageBox.Show(this, "Name is required.", "Source", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var settings = new Dictionary<string, string>();
        foreach (var child in SettingsList.Children)
        {
            if (child is not Grid row) continue;
            var key = ((TextBox)row.Children[0]).Text.Trim();
            var value = ((TextBox)row.Children[1]).Text;
            if (key.Length > 0) settings[key] = value;
        }

        int? every = int.TryParse(EveryBox.Text.Trim(), out var e2) && e2 > 0 ? e2 : null;

        Result = new SourceDef { Name = name, Type = CurrentType, EverySeconds = every, Settings = settings };
        DialogResult = true;
    }
}
