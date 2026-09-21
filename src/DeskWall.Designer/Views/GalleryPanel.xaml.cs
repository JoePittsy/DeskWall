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

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
