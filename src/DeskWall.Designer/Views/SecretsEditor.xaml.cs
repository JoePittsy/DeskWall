using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DeskWall.Core;

namespace DeskWall.Designer.Views;

/// <summary>
/// Job: let the owner set the handful of API keys a layout needs, without ever showing one on
/// screen by accident. Deliberately left out: strength meters, categories, per-secret notes - it
/// is a flat key/value list because secrets.json is a flat key/value file.
/// </summary>
public partial class SecretsEditor : Window
{
    private readonly ObservableCollection<SecretRow> _rows = new();

    public SecretsEditor()
    {
        InitializeComponent();
        foreach (var (k, v) in LoadSecrets().OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            _rows.Add(new SecretRow { Key = k, Value = v });
        Rows.ItemsSource = _rows;
    }

    private static string SecretsPath => Paths.InRuntime("secrets.json");

    private static Dictionary<string, string> LoadSecrets()
    {
        if (!File.Exists(SecretsPath)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return new(JsonSerializer.Deserialize(File.ReadAllText(SecretsPath), SecretsFileJsonContext.Default.DictionaryStringString) ?? new(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => _rows.Add(new SecretRow { Revealed = true });

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is SecretRow row) _rows.Remove(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var key = row.Key.Trim();
            if (key.Length == 0) continue;
            map[key] = row.Value;
        }

        var tmp = SecretsPath + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(map, SecretsFileJsonContext.Default.DictionaryStringString));
            File.Move(tmp, SecretsPath, overwrite: true);
        }
        catch
        {
            // Same tidy-runtime-dir discipline as LayoutFile.Save / Settings.Save.
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        DialogResult = true;
    }
}

/// <summary>One row of the grid. A plain INotifyPropertyChanged class rather than a record: the
/// grid edits fields in place and needs change notification, not immutability.</summary>
public sealed class SecretRow : INotifyPropertyChanged
{
    private string _key = "";
    private string _value = "";
    private bool _revealed;

    public string Key { get => _key; set { _key = value; Raise(nameof(Key)); } }
    public string Value { get => _value; set { _value = value; Raise(nameof(Value)); Raise(nameof(Masked)); } }
    public bool Revealed { get => _revealed; set { _revealed = value; Raise(nameof(Revealed)); } }

    /// <summary>A fixed-length row of bullets (U+2022, written as an escape: ASCII-only sources):
    /// the length itself must not leak how long the secret is.</summary>
    public string Masked => string.IsNullOrEmpty(Value) ? "" : new string('\u2022', 10);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>bool -> Visibility, with an optional "Invert" ConverterParameter. Small enough not to
/// warrant a shared converters file for one screen.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class SecretsFileJsonContext : JsonSerializerContext;
