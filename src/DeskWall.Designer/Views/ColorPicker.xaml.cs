using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DeskWall.Designer.Model;

namespace DeskWall.Designer.Views;

/// <summary>What <see cref="ColorPicker.ValueChanged"/> reports. <see cref="IsFinal"/> is true when
/// the change is finished: a drag released, a key pressed, a hex entered. On a final event after a
/// drag, <see cref="OldValue"/> is the colour before the drag began, so one undo entry covers it.</summary>
public sealed class ColorValueChangedEventArgs(string oldValue, string newValue, bool isFinal) : EventArgs
{
    public string OldValue { get; } = oldValue;
    public string NewValue { get; } = newValue;
    public bool IsFinal { get; } = isFinal;
}

/// <summary>Job: pick a colour, alpha included, by eye or by hex, and see a translucent one against
/// a checkerboard. A saturation/brightness area, hue and opacity strips, a swatch and a hex box.
/// <see cref="Value"/> is #AARRGGBB, the form the layout files store; hex typed in may be #RGB,
/// #RRGGBB or #AARRGGBB (Core's parser). Under them, <see cref="Swatches"/>: the colours already in
/// use, one click each. Leaves out: no eyedropper, no RGB/HSV number boxes.
///
/// Every change raises <see cref="ValueChanged"/>. A drag, and a run of arrow or page keys in the
/// area or a strip, is one gesture: non-final changes while it lasts, then one final change on the
/// release or the key-up (or when the focus leaves) whose <see cref="ColorValueChangedEventArgs.OldValue"/>
/// is the colour before it began. With <see cref="CommitOnRelease"/> set a gesture raises only that
/// final change. Setting <see cref="Value"/> from outside repaints the picker and raises nothing, so
/// a caller's own write never comes back as an edit.</summary>
public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("#FFFFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ColorPicker)d).OnValueSet((string?)e.NewValue)));

    public static readonly DependencyProperty CommitOnReleaseProperty = DependencyProperty.Register(
        nameof(CommitOnRelease), typeof(bool), typeof(ColorPicker), new PropertyMetadata(false));

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public bool CommitOnRelease { get => (bool)GetValue(CommitOnReleaseProperty); set => SetValue(CommitOnReleaseProperty, value); }

    public event EventHandler<ColorValueChangedEventArgs>? ValueChanged;

    private ColorModel _state = ColorModel.FromColor(Core.Render.Color.White);
    private bool _writing;     // our own write to Value: do not re-derive the state from it
    private bool _syncing;     // our own write to a slider: not an edit
    private string? _gestureStart;
    private readonly SolidColorBrush _hue = new(), _swatch = new();
    private readonly LinearGradientBrush _alpha = new() { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };

    public ColorPicker()
    {
        InitializeComponent();
        HueFill.Fill = _hue;
        Swatch.Background = _swatch;
        AlphaSlider.Background = _alpha;
        Area.Describe = () => $"saturation {_state.Saturation:P0}, brightness {_state.Value:P0}";
        foreach (var slider in new[] { HueSlider, AlphaSlider })
        {
            slider.PreviewMouseLeftButtonDown += (_, _) => BeginGesture();
            slider.PreviewMouseLeftButtonUp += (_, _) => EndGesture();
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => EndGesture()));
        }
        // A held arrow key is a burst of KeyDowns and one KeyUp: one gesture, so one undo entry.
        PreviewKeyDown += (_, e) => { if (IsStepKey(e) && !HexBox.IsKeyboardFocusWithin) BeginGesture(); };
        PreviewKeyUp += (_, e) => { if (IsStepKey(e)) EndGesture(); };
        IsKeyboardFocusWithinChanged += (_, e) => { if (e.NewValue is false && !Area.IsMouseCaptured) EndGesture(); };
        Paint();
    }

    private static bool IsStepKey(KeyEventArgs e)
        => e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;

    /// <summary>The colours to offer under the picker (#AARRGGBB each; the first eight are shown).</summary>
    public IReadOnlyList<string> Swatches
    {
        get => _swatches;
        set
        {
            _swatches = value ?? [];
            SwatchRow.Children.Clear();
            foreach (var hex in _swatches.Take(8))
            {
                if (!ColorModel.TryParseHex(hex, out var c)) continue;
                var fill = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)) };
                var chip = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = fill };
                chip.SetResourceReference(Border.BackgroundProperty, "Checker");
                chip.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                var button = new Button { Content = chip, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6), MinWidth = 0, MinHeight = 0, ToolTip = hex, Tag = hex };
                System.Windows.Automation.AutomationProperties.SetName(button, "Use " + hex);
                button.Click += (_, _) => Edit(ColorModel.FromColor(c, _state));
                SwatchRow.Children.Add(button);
            }
            SwatchRow.Visibility = SwatchRow.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private IReadOnlyList<string> _swatches = [];

    // ---- state in, state out ---------------------------------------------------------------------

    private void OnValueSet(string? value)
    {
        if (_writing || !ColorModel.TryParseHex(value, out var c)) return;
        _state = ColorModel.FromColor(c, _state);
        Paint();
    }

    /// <summary>An edit from any part of the picker.</summary>
    private void Edit(ColorModel next)
    {
        var old = Value;
        _state = next;
        Paint();
        if (_gestureStart is null) Publish(old, isFinal: true);
        else if (!CommitOnRelease) Publish(old, isFinal: false);
    }

    private void Publish(string old, bool isFinal)
    {
        var hex = _state.Hex;
        _writing = true;
        try { Value = hex; }
        finally { _writing = false; }
        if (hex != old)
            ValueChanged?.Invoke(this, new ColorValueChangedEventArgs(old, hex, isFinal));
    }

    private void BeginGesture() => _gestureStart ??= Value;

    private void EndGesture()
    {
        if (_gestureStart is not { } start) return;
        var hex = _state.Hex;
        _gestureStart = null;
        _writing = true;
        try { Value = hex; }
        finally { _writing = false; }
        if (hex != start) ValueChanged?.Invoke(this, new ColorValueChangedEventArgs(start, hex, true));
    }

    private void Paint()
    {
        var c = _state.ToColor();
        var h = _state.HueColor;
        _hue.Color = Color.FromRgb(h.R, h.G, h.B);
        _swatch.Color = Color.FromArgb(c.A, c.R, c.G, c.B);
        _alpha.GradientStops.Clear();
        _alpha.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
        _alpha.GradientStops.Add(new GradientStop(Color.FromArgb(255, c.R, c.G, c.B), 1));
        _syncing = true;
        try
        {
            HueSlider.Value = _state.Hue;
            AlphaSlider.Value = _state.Alpha;
        }
        finally { _syncing = false; }
        if (!HexBox.IsKeyboardFocusWithin) HexBox.Text = _state.Hex;
        PlaceAreaThumb();
    }

    private void PlaceAreaThumb()
    {
        Canvas.SetLeft(AreaThumb, _state.Saturation * Area.ActualWidth - 8);
        Canvas.SetTop(AreaThumb, (1 - _state.Value) * Area.ActualHeight - 8);
    }

    // ---- the saturation/brightness area ----------------------------------------------------------

    private void Area_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        AreaLayers.Clip = new RectangleGeometry(new Rect(e.NewSize), 4, 4);
        PlaceAreaThumb();
    }

    private void Area_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Area.Focus();
        BeginGesture();
        Area.CaptureMouse();
        PickAt(e.GetPosition(Area));
        e.Handled = true;
    }

    private void Area_MouseMove(object sender, MouseEventArgs e)
    {
        if (Area.IsMouseCaptured) PickAt(e.GetPosition(Area));
    }

    private void Area_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Area.ReleaseMouseCapture();

    private void Area_LostMouseCapture(object sender, MouseEventArgs e) => EndGesture();

    private void PickAt(Point p)
    {
        if (Area.ActualWidth <= 0 || Area.ActualHeight <= 0) return;
        Edit(_state with
        {
            Saturation = Math.Clamp(p.X / Area.ActualWidth, 0, 1),
            Value = Math.Clamp(1 - p.Y / Area.ActualHeight, 0, 1),
        });
    }

    /// <summary>Arrows move 1%, with Shift 10%: left and right are saturation, up and down brightness.</summary>
    private void Area_KeyDown(object sender, KeyEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.01;
        (double ds, double dv) = e.Key switch
        {
            Key.Left => (-step, 0.0),
            Key.Right => (step, 0.0),
            Key.Up => (0.0, step),
            Key.Down => (0.0, -step),
            _ => (0.0, 0.0),
        };
        if (ds == 0 && dv == 0) return;
        Edit(_state with
        {
            Saturation = Math.Clamp(Math.Round(_state.Saturation + ds, 2), 0, 1),
            Value = Math.Clamp(Math.Round(_state.Value + dv, 2), 0, 1),
        });
        e.Handled = true;
    }

    // ---- strips and hex --------------------------------------------------------------------------

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing) Edit(_state with { Hue = e.NewValue % 360 });
    }

    private void AlphaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing) Edit(_state with { Alpha = (byte)Math.Round(e.NewValue) });
    }

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; }
        else if (e.Key == Key.Escape) { HexBox.Text = _state.Hex; HexBox.SelectAll(); e.Handled = true; }
    }

    private void HexBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitHex();

    /// <summary>A hex that Core cannot parse puts the current one back rather than keeping a typo.</summary>
    private void CommitHex()
    {
        if (ColorModel.TryParseHex(HexBox.Text, out var c)) Edit(ColorModel.FromColor(c, _state));
        HexBox.Text = _state.Hex;
    }
}

/// <summary>The picker's saturation/brightness square: focusable, and exposed to UI Automation with
/// its position as a read-only value ("saturation 40%, brightness 80%"), which a plain Border is not.</summary>
public sealed class ColorArea : Border
{
    internal Func<string>? Describe { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(ColorArea owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetClassNameCore() => nameof(ColorArea);
        protected override bool IsControlElementCore() => true;
        public override object GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);
        public string Value => owner.Describe?.Invoke() ?? "";
        public bool IsReadOnly => true;
        public void SetValue(string value) => throw new InvalidOperationException("read-only");
    }
}
