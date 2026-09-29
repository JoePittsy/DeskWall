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
    /// <param name="expanded">Paths (this tree's own path syntax) to open. A rebuilt tree that
    /// forgets which nodes were open cannot be browsed at all: the owner expands a record and the
    /// next refresh closes it again.</param>
    public static void Populate(ItemsControl root, RecordValue tree, Action<string>? onPathClicked,
        IReadOnlySet<string>? expanded = null, bool pickOnSelect = false)
    {
        root.Items.Clear();
        foreach (var kv in tree.Fields.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            root.Items.Add(BuildNode(kv.Key, kv.Key, kv.Value, onPathClicked, expanded, pickOnSelect));
    }

    private static TreeViewItem BuildNode(string label, string path, Value value, Action<string>? onPathClicked,
        IReadOnlySet<string>? expanded, bool pickOnSelect)
    {
        // Auto, not star: a TreeViewItem header is measured at its content's width, so star columns
        // collapse to Auto anyway and the value ran into the name ("cpu0.268"). The value's left
        // margin is the gap; its MaxWidth is what lets a long value trim instead of widening the tree.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
                FontFamily = SystemFonts.MessageFontFamily,
                FontSize = SystemFonts.MessageFontSize,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(12, 0, 0, 0),
                MaxWidth = 240,
            };
            valueText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Grid.SetColumn(valueText, 1);
            header.Children.Add(valueText);
        }

        var item = new TreeViewItem { Header = header, Tag = path, IsExpanded = expanded?.Contains(path) == true };

        if (onPathClicked is not null && !isContainer)
        {
            pathText.Cursor = Cursors.Hand;
            pathText.TextDecorations = TextDecorations.Underline;
            pathText.MouseLeftButtonUp += (_, e) => { onPathClicked(path); e.Handled = true; };
            // Keyboard: Enter or Space picks the focused leaf. Not marked handled, so Enter in a
            // dialog still reaches its default button once the path is set.
            item.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) onPathClicked(path); };
            if (pickOnSelect) item.Selected += (_, e) => { if (ReferenceEquals(e.OriginalSource, item)) onPathClicked(path); };
        }

        switch (value)
        {
            case RecordValue r:
                foreach (var kv in r.Fields.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                    item.Items.Add(BuildNode(kv.Key, AppendName(path, kv.Key), kv.Value, onPathClicked, expanded, pickOnSelect));
                break;
            case ListValue l:
                for (var i = 0; i < l.Items.Count; i++)
                {
                    var key = l.KeyField is not null && l.Items[i].Get(l.KeyField) is { } kv2 ? kv2.ToText(null) : i.ToString();
                    item.Items.Add(BuildNode($"[{key}]", AppendIndex(path, key), l.Items[i], onPathClicked, expanded, pickOnSelect));
                }
                break;
        }
        return item;
    }

    private static string AppendName(string basePath, string name) => basePath.Length == 0 ? name : basePath + "." + name;
    private static string AppendIndex(string basePath, string key) => basePath + "[" + key + "]";
}
