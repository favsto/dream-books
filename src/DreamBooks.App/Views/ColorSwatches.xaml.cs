using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Model;
using DreamBooks.Core.Settings;

namespace DreamBooks.App.Views;

/// <summary>
/// The colour swatches of the pen and highlighter flyouts (UI-02, FR-25, FR-41): the tool's presets
/// (<see cref="ColorSwatch"/> or <see cref="HighlighterSwatch"/> items), the shared custom colours and a "+" that opens
/// the free colour picker; the colour chosen (<see cref="SelectedColor"/>, compared by hue, not alpha) has a ring. A tap
/// runs <see cref="SelectCommand"/>; the picker's colour goes to <see cref="AddCommand"/> (added to the palette and
/// selected); a long press (<c>LongPressDuration</c>, moving less than <c>LongPressSlop</c>) or Delete on a custom
/// colour runs <see cref="RemoveCommand"/>. Every swatch is a 40 DIP target with a name for screen readers (NFR-31).
/// </summary>
public partial class ColorSwatches : UserControl
{
    public static readonly DependencyProperty PresetsProperty = DependencyProperty.Register(
        nameof(Presets), typeof(IEnumerable), typeof(ColorSwatches), new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty CustomColorsProperty = DependencyProperty.Register(
        nameof(CustomColors), typeof(IEnumerable), typeof(ColorSwatches), new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(InkColor), typeof(ColorSwatches),
        new PropertyMetadata(default(InkColor), (d, _) => ((ColorSwatches)d).ShowChosen()));

    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(
        nameof(SelectCommand), typeof(ICommand), typeof(ColorSwatches));

    public static readonly DependencyProperty AddCommandProperty = DependencyProperty.Register(
        nameof(AddCommand), typeof(ICommand), typeof(ColorSwatches));

    public static readonly DependencyProperty RemoveCommandProperty = DependencyProperty.Register(
        nameof(RemoveCommand), typeof(ICommand), typeof(ColorSwatches));

    private readonly List<Button> _presetButtons = [];
    private readonly List<Button> _customButtons = [];
    private IDisposable? _pendingHold;
    private Button? _holdButton;
    private Point _holdStart;
    private bool _holdFired;
    private bool _swallowClick;

    public ColorSwatches()
    {
        InitializeComponent();
        AddButton = new Button
        {
            Style = (Style)FindResource("SwatchButtonStyle"),
            Content = FindResource("GlyphAdd"),
        };
        AutomationProperties.SetName(AddButton, Strings.AddCustomColor);
        AddButton.ToolTip = Strings.AddCustomColor;
        AddButton.Click += (_, _) => TogglePicker();
        Picker.Confirmed += (_, color) =>
        {
            ClosePicker();
            Run(AddCommand, color);
        };
        Picker.Cancelled += (_, _) => ClosePicker();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                ClosePicker(); // the flyout closed: it opens on the swatches next time
                CancelHold();
            }
        };
        Rebuild();
    }

    /// <summary>The tool's presets: <see cref="ColorSwatch"/> or <see cref="HighlighterSwatch"/> items.</summary>
    public IEnumerable? Presets
    {
        get => (IEnumerable?)GetValue(PresetsProperty);
        set => SetValue(PresetsProperty, value);
    }

    /// <summary>The shared custom colours (<see cref="InkColor"/> items), oldest first.</summary>
    public IEnumerable? CustomColors
    {
        get => (IEnumerable?)GetValue(CustomColorsProperty);
        set => SetValue(CustomColorsProperty, value);
    }

    public InkColor SelectedColor
    {
        get => (InkColor)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    /// <summary>Runs with the <see cref="InkColor"/> of a tapped swatch.</summary>
    public ICommand? SelectCommand
    {
        get => (ICommand?)GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    /// <summary>Runs with the picker's <see cref="InkColor"/>.</summary>
    public ICommand? AddCommand
    {
        get => (ICommand?)GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }

    /// <summary>Runs with the <see cref="InkColor"/> of a custom swatch held down (or deleted with the keyboard).</summary>
    public ICommand? RemoveCommand
    {
        get => (ICommand?)GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    /// <summary>Waits for the long press; tests drive time by hand.</summary>
    internal IDelayScheduler Scheduler { get; set; } = new DispatcherDelayScheduler();

    internal IReadOnlyList<Button> PresetButtons => _presetButtons;

    internal IReadOnlyList<Button> CustomButtons => _customButtons;

    /// <summary>The "+" swatch that opens the colour picker.</summary>
    internal Button AddButton { get; }

    internal bool IsPickerOpen => Picker.Visibility == Visibility.Visible;

    /// <summary>True for the swatch of <see cref="SelectedColor"/> (it shows the ring).</summary>
    internal bool IsChosen(Button swatch) => swatch.Tag is Swatch { } info && info.Ring.Visibility == Visibility.Visible;

    /// <summary>A press on <paramref name="swatch"/> at <paramref name="position"/>: on a custom colour the hold starts.</summary>
    internal void BeginHold(Button swatch, Point position)
    {
        CancelHold();
        _holdFired = false;
        _swallowClick = false;
        if (swatch.Tag is not Swatch { IsCustom: true } info)
        {
            return;
        }

        _holdButton = swatch;
        _holdStart = position;
        var duration = TryFindResource("LongPressDuration") as TimeSpan? ?? TimeSpan.FromMilliseconds(600);
        _pendingHold = Scheduler.Schedule(duration, () =>
        {
            _pendingHold = null;
            if (!ReferenceEquals(_holdButton, swatch))
            {
                return;
            }

            _holdButton = null;
            _holdFired = true;
            Run(RemoveCommand, info.Color);
        });
    }

    /// <summary>The press moves: farther than <c>LongPressSlop</c> it is no longer a hold.</summary>
    internal void MoveHold(Point position)
    {
        if (_holdButton is null)
        {
            return;
        }

        var slop = TryFindResource("LongPressSlop") as double? ?? 8;
        if ((position - _holdStart).Length > slop)
        {
            CancelHold();
        }
    }

    /// <summary>The press ends: true when the hold removed the colour (the tap that ends it selects nothing).</summary>
    internal bool EndHold()
    {
        var fired = _holdFired;
        CancelHold();
        _holdFired = false;
        _swallowClick = fired;
        return fired;
    }

    /// <summary>A key on a focused swatch: Delete (or Backspace) removes a custom colour. True when taken.</summary>
    internal bool HandleKey(Button swatch, Key key)
    {
        if (key is not (Key.Delete or Key.Back) || swatch.Tag is not Swatch { IsCustom: true } info)
        {
            return false;
        }

        Run(RemoveCommand, info.Color);
        return true;
    }

    internal void ClosePicker() => Picker.Visibility = Visibility.Collapsed;

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ColorSwatches)d).Rebuild();

    private static void Run(ICommand? command, InkColor color)
    {
        if (command?.CanExecute(color) == true)
        {
            command.Execute(color);
        }
    }

    private static bool SameHue(InkColor a, InkColor b) => a.R == b.R && a.G == b.G && a.B == b.B;

    private static (InkColor Color, string Name)? PresetOf(object? item) => item switch
    {
        ColorSwatch swatch => (swatch.Color, swatch.Name),
        HighlighterSwatch swatch => (swatch.Color, swatch.Name),
        _ => null,
    };

    private void TogglePicker()
    {
        if (IsPickerOpen)
        {
            ClosePicker();
            return;
        }

        Picker.Model.Reset(SelectedColor with { A = 255 });
        Picker.Visibility = Visibility.Visible;
    }

    private void CancelHold()
    {
        _pendingHold?.Dispose();
        _pendingHold = null;
        _holdButton = null;
    }

    /// <summary>The swatches again: presets, custom colours, "+".</summary>
    private void Rebuild()
    {
        if (SwatchPanel is null)
        {
            return; // still being built
        }

        CancelHold();
        SwatchPanel.Children.Clear();
        _presetButtons.Clear();
        _customButtons.Clear();
        foreach (var item in Presets ?? Array.Empty<object>())
        {
            if (PresetOf(item) is { } preset)
            {
                _presetButtons.Add(NewSwatch(preset.Color, preset.Name, isCustom: false));
            }
        }

        foreach (var item in CustomColors ?? Array.Empty<object>())
        {
            if (item is InkColor color)
            {
                var name = string.Format(CultureInfo.CurrentCulture, Strings.CustomColorNameFormat, HexColor.Format(color));
                _customButtons.Add(NewSwatch(color, name, isCustom: true));
            }
        }

        foreach (var button in _presetButtons.Concat(_customButtons))
        {
            SwatchPanel.Children.Add(button);
        }

        SwatchPanel.Children.Add(AddButton);
        ShowChosen();
    }

    private Button NewSwatch(InkColor color, string name, bool isCustom)
    {
        var ring = new Ellipse { StrokeThickness = (double)FindResource("SwatchRingThickness") };
        ring.SetResourceReference(WidthProperty, "SwatchRingSize");
        ring.SetResourceReference(HeightProperty, "SwatchRingSize");
        ring.SetResourceReference(Shape.StrokeProperty, "SwatchRingBrush");
        var fill = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        fill.Freeze();
        var disc = new Ellipse { Fill = fill, StrokeThickness = (double)FindResource("SwatchOutlineThickness") };
        disc.SetResourceReference(WidthProperty, "SwatchSize");
        disc.SetResourceReference(HeightProperty, "SwatchSize");
        disc.SetResourceReference(Shape.StrokeProperty, "SwatchOutlineBrush");
        var content = new Grid { IsHitTestVisible = false };
        content.Children.Add(ring);
        content.Children.Add(disc);
        var button = new Button
        {
            Style = (Style)FindResource("SwatchButtonStyle"),
            Content = content,
            ToolTip = name,
            Tag = new Swatch(color, isCustom, ring),
        };
        AutomationProperties.SetName(button, name);
        if (isCustom)
        {
            AutomationProperties.SetHelpText(button, Strings.CustomColorHelp);
        }

        button.Click += (_, _) =>
        {
            if (_swallowClick)
            {
                _swallowClick = false;
                return;
            }

            Run(SelectCommand, color);
        };
        button.PreviewMouseLeftButtonDown += (_, e) => BeginHold(button, e.GetPosition(this));
        button.PreviewMouseMove += (_, e) => MoveHold(e.GetPosition(this));
        button.PreviewMouseLeftButtonUp += (_, _) => EndHold();
        button.LostMouseCapture += (_, _) => CancelHold();
        button.PreviewKeyDown += (_, e) =>
        {
            if (HandleKey(button, e.Key))
            {
                e.Handled = true;
            }
        };
        return button;
    }

    private void ShowChosen()
    {
        foreach (var button in _presetButtons.Concat(_customButtons))
        {
            if (button.Tag is Swatch info)
            {
                var chosen = SameHue(info.Color, SelectedColor);
                info.Ring.Visibility = chosen ? Visibility.Visible : Visibility.Hidden;
                AutomationProperties.SetItemStatus(button, chosen ? Strings.SwatchChosen : string.Empty);
            }
        }
    }

    /// <summary>What a swatch button stands for.</summary>
    private sealed record Swatch(InkColor Color, bool IsCustom, Ellipse Ring);
}
