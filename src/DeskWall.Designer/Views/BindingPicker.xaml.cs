using System.Text;
using System.Windows;
using DeskWall.Core.Bindings;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Designer.Views;

/// <summary>Job: pick a path into the live value tree and an optional format by clicking, with a
/// live preview of the real resolved text, so a binding is typed by pointing rather than memorised
/// syntax. Leaves out: no search box beyond the tree itself, no separate raw/formatted views, no
/// multi-step wizard.</summary>
public partial class BindingPicker : Window
{
    private readonly RecordValue _tree;

    /// <summary>Set after ShowDialog() returns true.</summary>
    public PropertyValue? Result { get; private set; }

    public BindingPicker(RecordValue tree, Binding? current)
    {
        InitializeComponent();
        _tree = tree;
        ValueTreeView.Populate(TreeHost, tree, path => { PathBox.Text = path; });
        if (current is not null)
        {
            PathBox.Text = PathText(current);
            FormatBox.Text = current.Format ?? "";
        }
        UpdatePreview();
    }

    private static string PathText(Binding b)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < b.Path.Count; i++)
        {
            if (i > 0 && b.Path[i] is NameSegment) sb.Append('.');
            sb.Append(b.Path[i]);
        }
        return sb.ToString();
    }

    private bool TryBuildBinding(out Binding binding, out string error)
    {
        var path = PathBox.Text.Trim();
        var format = FormatBox.Text.Trim();
        var text = format.Length == 0 ? path : $"{path} | \"{format}\"";
        try
        {
            binding = Binding.Parse(text);
            error = "";
            return true;
        }
        catch (FormatException ex)
        {
            binding = null!;
            error = ex.Message;
            return false;
        }
    }

    private void UpdatePreview()
    {
        if (PathBox.Text.Trim().Length == 0) { PreviewText.Text = ""; return; }
        if (!TryBuildBinding(out var binding, out var error)) { PreviewText.Text = $"(invalid path: {error})"; return; }
        var resolved = BindingResolver.ResolveText(binding, _tree);
        PreviewText.Text = resolved is null ? "(no value at this path yet)" : $"Preview: {resolved}";
    }

    private void PathOrFormat_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreview();

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildBinding(out var binding, out var error))
        {
            MessageBox.Show(this, error, "Bind", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = PropertyValue.Bound(binding);
        DialogResult = true;
    }
}
