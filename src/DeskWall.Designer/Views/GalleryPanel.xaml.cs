using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Views;

/// <summary>
/// The widget gallery, and the first thing anyone sees.
/// <para>
/// Job: let the owner recognise the widget he wants and put it on the wallpaper in one click. A card
/// is its name, one line about it, and - only when there is one - the sentence that says what it
/// needs. Deliberately left out: categories, a search box (eight widgets), icons, and any mention of
/// the sources, bindings or coordinates behind it.
/// </para>
/// <para>
/// Cards used to carry a live render of the widget. That went because at card width the widgets are
/// mostly small white text on a photograph, so eight previews read as eight nearly identical grey
/// rectangles and told the owner less than the name did - while costing a background render per
/// card and a refresh timer to redraw them once their sources had published. The name and the
/// description do the recognising; the canvas shows the real thing the moment it is added.
/// </para>
/// </summary>
public partial class GalleryPanel : UserControl
{
    private readonly ObservableCollection<CardView> _cards = new();
    private IReadOnlyList<WidgetTemplate> _templates = Array.Empty<WidgetTemplate>();

    public GalleryPanel()
    {
        InitializeComponent();
        Cards.ItemsSource = _cards;
    }

    /// <summary>The owner clicked a card. The shell owns the layout, so the gallery only names the
    /// widget.</summary>
    public event Action<WidgetTemplate>? AddRequested;

    /// <summary>Build one from scratch.</summary>
    public event Action? NewRequested;

    /// <summary>Open one of the owner's own templates in the widget editor.</summary>
    public event Action<WidgetTemplate>? EditRequested;

    /// <summary>Open a copy of this template, shipped or not, as a new unsaved widget.</summary>
    public event Action<WidgetTemplate>? DuplicateRequested;

    /// <summary>Delete one of the owner's own template files.</summary>
    public event Action<WidgetTemplate>? DeleteRequested;

    /// <summary>Fill the gallery. Nothing here depends on what the sources have published, so unlike
    /// the preview cards this needs no refresh once the live values arrive.</summary>
    public void Load(IReadOnlyList<WidgetTemplate> templates)
    {
        _templates = templates;
        _cards.Clear();
        foreach (var t in templates) _cards.Add(new CardView(t));
        EmptyNote.Visibility = templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>How many of each widget are on the wallpaper now, for the count badge.</summary>
    public void SetCounts(IReadOnlyDictionary<string, int> byKey)
    {
        foreach (var card in _cards) card.Count = byKey.TryGetValue(card.Key, out var n) ? n : 0;
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        if (_templates.FirstOrDefault(t => t.Key == key) is { } t) AddRequested?.Invoke(t);
    }

    private void New_Click(object sender, RoutedEventArgs e) => NewRequested?.Invoke();

    private void Edit_Click(object sender, RoutedEventArgs e) => Raise(sender, EditRequested);

    private void Duplicate_Click(object sender, RoutedEventArgs e) => Raise(sender, DuplicateRequested);

    private void Delete_Click(object sender, RoutedEventArgs e) => Raise(sender, DeleteRequested);

    /// <summary>A context-menu item's DataContext is the card it was opened on, which is the only
    /// way back to the template from inside the item template.</summary>
    private static void Raise(object sender, Action<WidgetTemplate>? handler)
    {
        if (sender is FrameworkElement { DataContext: CardView card }) handler?.Invoke(card.Template);
    }

    /// <summary>One card. A view model rather than a hand-built visual tree, because the count badge
    /// changes on every add and remove and nothing else about the card does.</summary>
    public sealed class CardView : INotifyPropertyChanged
    {
        private int _count;

        public CardView(WidgetTemplate template) => Template = template;

        public WidgetTemplate Template { get; }
        public string Key => Template.Key;
        public string Name => Template.Name;
        public string Description => Template.Description;
        public string Requires => Template.Requires ?? "";
        public Visibility RequiresVisibility => Template.Requires is null ? Visibility.Collapsed : Visibility.Visible;

        public int Count
        {
            get => _count;
            set { if (_count == value) return; _count = value; Raise(); Raise(nameof(CountVisibility)); }
        }

        public Visibility CountVisibility => _count > 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Delete is for the owner's own files only: there is no file of his to remove
        /// for a shipped template he has never edited. Edit is offered on everything (a shipped
        /// one saves a copy under the same key, <see cref="WidgetDocument.ForEditing"/>).</summary>
        public Visibility MineVisibility => WidgetCatalog.IsUserTemplate(Template) ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Deleting an override is undoing an edit, not losing a widget, and the menu
        /// has to say which of the two it is about to do.</summary>
        public string DeleteHeader => Template.OverridesShipped ? "Reset to the out-of-the-box version" : "Delete";

        public Visibility EditedVisibility =>
            Template.OverridesShipped && WidgetCatalog.IsUserTemplate(Template) ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
