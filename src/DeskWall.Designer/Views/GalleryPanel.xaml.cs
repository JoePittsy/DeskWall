using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskWall.Core.Values;
using DeskWall.Designer.Model;
using DeskWall.Designer.Model.Widgets;

namespace DeskWall.Designer.Views;

/// <summary>
/// The widget gallery, and the first thing anyone sees.
/// <para>
/// Job: let the owner recognise the widget he wants and put it on the wallpaper in one click. Every
/// card carries a real render of the widget on the real base photo, its name, one line about it,
/// and - only when there is one - the sentence that says what it needs. Deliberately left out:
/// categories, a search box (eight widgets), previews at any size but the one it will be, icons,
/// and any mention of the sources, bindings or coordinates behind the picture.
/// </para>
/// </summary>
public partial class GalleryPanel : UserControl
{
    private readonly ObservableCollection<CardView> _cards = new();
    private IReadOnlyList<WidgetTemplate> _templates = Array.Empty<WidgetTemplate>();
    private Func<RecordValue> _values = () => ValueTree.Empty;

    public GalleryPanel()
    {
        InitializeComponent();
        Cards.ItemsSource = _cards;
    }

    /// <summary>The owner clicked a card. The shell owns the layout, so the gallery only names the
    /// widget.</summary>
    public event Action<WidgetTemplate>? AddRequested;

    /// <summary>Fill the gallery and start rendering its pictures.</summary>
    public void Load(IReadOnlyList<WidgetTemplate> templates, Func<RecordValue> values)
    {
        _templates = templates;
        _values = values;
        _cards.Clear();
        foreach (var t in templates) _cards.Add(new CardView(t));
        EmptyNote.Visibility = templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderPictures();
    }

    /// <summary>Draw the pictures again against whatever the sources have published since. A card
    /// rendered before its source had run is a photo with nothing on it, which is exactly the icon
    /// this gallery is not allowed to be.</summary>
    public void Refresh() => RenderPictures();

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

    /// <summary>Render every card off the UI thread, one at a time, and hand each one over as it
    /// lands so the gallery fills in rather than appearing all at once after a pause.</summary>
    private void RenderPictures()
    {
        var cards = _cards.ToList();
        var values = _values;
        _ = Task.Run(() =>
        {
            foreach (var card in cards)
            {
                BitmapSource? image = null;
                try
                {
                    var rendered = CardRenderer.Render(card.Template, values());
                    image = BitmapSource.Create(rendered.Width, rendered.Height, 96, 96,
                        PixelFormats.Pbgra32, null, rendered.Bgra, rendered.Width * 4);
                    image.Freeze();
                }
                catch (Exception)
                {
                    // A card without its picture still names the widget; the gallery must not fail.
                }
                if (image is null) continue;
                var frozen = image;
                Dispatcher.BeginInvoke(new Action(() => card.Image = frozen));
            }
        });
    }

    /// <summary>One card. A view model rather than a hand-built visual tree, because the count badge
    /// changes on every add and remove and nothing else about the card does.</summary>
    public sealed class CardView : INotifyPropertyChanged
    {
        private ImageSource? _image;
        private int _count;

        public CardView(WidgetTemplate template) => Template = template;

        public WidgetTemplate Template { get; }
        public string Key => Template.Key;
        public string Name => Template.Name;
        public string Description => Template.Description;
        public string Requires => Template.Requires ?? "";
        public Visibility RequiresVisibility => Template.Requires is null ? Visibility.Collapsed : Visibility.Visible;

        public ImageSource? Image
        {
            get => _image;
            set { _image = value; Raise(); }
        }

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
