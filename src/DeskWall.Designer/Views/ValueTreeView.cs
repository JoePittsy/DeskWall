using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskWall.Core.Values;

namespace DeskWall.Designer.Views;

/// <summary>Builds the two-column (path, value) tree shared by SourcesPanel's value inspector and
/// BindingPicker's path chooser, so both read the same value tree the same way. Path strings follow
/// Binding's own syntax exactly (name segments dot-joined, [key]/[index] appended with no dot) so a
/// clicked path round-trips through Binding.Parse.</summary>
internal static class ValueTreeView
{
    public static void Populate(ItemsControl root, RecordValue tree, Action<string>? onPathClicked)
    {
        root.Items.Clear();
        foreach (var kv in tree.Fields.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            root.Items.Add(BuildNode(kv.Key, kv.Key, kv.Value, onPathClicked));
    }

    private static TreeViewItem BuildNode(string label, string path, Value value, Action<string>? onPathClicked)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var pathText = new TextBlock
        {
            Text = label,
            FontFamily = SystemFonts.MessageFontFamily,
            FontSize = SystemFonts.MessageFontSize,
        };
        Grid.SetColumn(pathText, 0);
        header.Children.Add(pathText);

        var isContainer = value is RecordValue or ListValue;
        if (!isContainer)
        {
            var valueText = new TextBlock
            {
                Text = value.ToText(null),
                Foreground = SystemColors.GrayTextBrush,
                FontFamily = SystemFonts.MessageFontFamily,
                FontSize = SystemFonts.MessageFontSize,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(valueText, 1);
            header.Children.Add(valueText);
        }

        var item = new TreeViewItem { Header = header, Tag = path, IsExpanded = false };

        if (onPathClicked is not null && !isContainer)
        {
            pathText.Cursor = Cursors.Hand;
            pathText.TextDecorations = TextDecorations.Underline;
            pathText.MouseLeftButtonUp += (_, e) => { onPathClicked(path); e.Handled = true; };
        }

        switch (value)
        {
            case RecordValue r:
                foreach (var kv in r.Fields.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                    item.Items.Add(BuildNode(kv.Key, AppendName(path, kv.Key), kv.Value, onPathClicked));
                break;
            case ListValue l:
                for (var i = 0; i < l.Items.Count; i++)
                {
                    var key = l.KeyField is not null && l.Items[i].Get(l.KeyField) is { } kv2 ? kv2.ToText(null) : i.ToString();
                    item.Items.Add(BuildNode($"[{key}]", AppendIndex(path, key), l.Items[i], onPathClicked));
                }
                break;
        }
        return item;
    }

    private static string AppendName(string basePath, string name) => basePath.Length == 0 ? name : basePath + "." + name;
    private static string AppendIndex(string basePath, string key) => basePath + "[" + key + "]";
}
