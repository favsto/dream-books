using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Services;
using Microsoft.Win32;

namespace DreamBooks.App.Views;

/// <summary>
/// The left-edge panel shell (UI-04): a sheet of <c>EdgePanelWidth</c> that slides in from the left over the document
/// and hosts <see cref="Body"/> (the marks list in 3b-1, the Pages tab in 3b-2). It opens through
/// <see cref="OpenCommand"/> (the menu entry; <c>MainWindow</c> also opens it on a finger swipe from the left edge,
/// see <see cref="DocumentView.LeftEdgeSwiped"/>) and closes through <see cref="CloseCommand"/> (its close button), Esc
/// (routed by the window), a finger swipe to the left on the sheet, a press outside it (<see cref="RoutePress"/>), and
/// after a jump from its content (<see cref="ContentActivatedEvent"/>).
/// </summary>
/// <remarks>
/// <para>Pen safety (FR-30): closed, the panel is collapsed, so the pen at the edge inks on the page. Open, the pen on
/// the sheet never presses its controls: the press closes the panel and is taken (nothing inks under a sheet that hid
/// the page). The pen outside the sheet closes it and inks as usual. While it slides out, nothing of it hit-tests.</para>
/// <para>NFR-32: the slide uses the theme durations and is instant when Windows animations are off; with transparency
/// effects off the sheet is opaque and has no shadow.</para>
/// </remarks>
[ContentProperty(nameof(Body))]
public partial class EdgePanel : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EdgePanel), new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty HeaderDetailProperty = DependencyProperty.Register(
        nameof(HeaderDetail), typeof(object), typeof(EdgePanel), new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderDetailNameProperty = DependencyProperty.Register(
        nameof(HeaderDetailName), typeof(string), typeof(EdgePanel), new PropertyMetadata(null));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(EdgePanel), new PropertyMetadata(null, OnBodyChanged));

    /// <summary>Raised (bubbling) by the hosted content after it moved the document: the panel hides.</summary>
    public static readonly RoutedEvent ContentActivatedEvent = EventManager.RegisterRoutedEvent(
        "ContentActivated", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(EdgePanel));

    /// <summary>A leftward finger move on the sheet longer than this, and mostly horizontal, closes it.</summary>
    internal const double SwipeCloseDistanceDip = 48;

    private bool _listening;
    private bool _isOpen;
    private int _slideVersion;
    private Point? _swipeStart;
    private int? _swipeTouchId;
    private IInputElement? _focusBefore;

    public EdgePanel()
    {
        InitializeComponent();
        OpenCommand = new RelayCommand(Open);
        CloseCommand = new RelayCommand(Close);
        CloseButton.Command = CloseCommand;
        AddHandler(ContentActivatedEvent, new RoutedEventHandler((_, _) => Close()));
        Loaded += (_, _) =>
        {
            ApplyTransparency();
            if (!_listening)
            {
                _listening = true;
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            }
        };
        Unloaded += (_, _) =>
        {
            if (_listening)
            {
                _listening = false;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            }
        };
    }

    /// <summary>Raised when the panel opens or closes.</summary>
    public event EventHandler? IsOpenChanged;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Shown after the title (the marks count).</summary>
    public object? HeaderDetail
    {
        get => GetValue(HeaderDetailProperty);
        set => SetValue(HeaderDetailProperty, value);
    }

    /// <summary>The automation name of <see cref="HeaderDetail"/>.</summary>
    public string? HeaderDetailName
    {
        get => (string?)GetValue(HeaderDetailNameProperty);
        set => SetValue(HeaderDetailNameProperty, value);
    }

    /// <summary>The hosted content; a logical child, so it inherits the panel's data context while closed too.</summary>
    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public ICommand OpenCommand { get; }

    public ICommand CloseCommand { get; }

    /// <summary>True from <see cref="Open"/> until <see cref="Close"/> (not while it slides out).</summary>
    public bool IsOpen => _isOpen;

    /// <summary>Windows client-area animations (NFR-32); tests inject it.</summary>
    internal Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    /// <summary>The Windows transparency setting (NFR-32); tests inject it.</summary>
    internal Func<bool> TransparencyEnabled { get; set; } = TransparencySetting.IsEnabled;

    /// <summary>True while the sheet slides in or out.</summary>
    internal bool IsAnimating { get; private set; }

    protected override IEnumerator LogicalChildren
    {
        get
        {
            var children = new List<object>();
            var inner = base.LogicalChildren;
            while (inner?.MoveNext() == true)
            {
                children.Add(inner.Current);
            }

            if (Body is { } body)
            {
                children.Add(body);
            }

            return children.GetEnumerator();
        }
    }

    /// <summary>Shows the sheet (sliding in unless animations are off) and moves keyboard focus into it.</summary>
    public void Open()
    {
        if (_isOpen)
        {
            return;
        }

        _isOpen = true;
        _focusBefore = Keyboard.FocusedElement;
        Root.Visibility = Visibility.Visible;
        Root.IsHitTestVisible = true;
        SlideTo(0, from: -SheetWidth(), "EdgePanelOpenDuration", new CubicEase { EasingMode = EasingMode.EaseOut }, null);
        IsOpenChanged?.Invoke(this, EventArgs.Empty);

        // After layout, so the content (a list) can take focus: arrows and Enter work at once.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_isOpen && !IsKeyboardFocusWithin)
            {
                (Body as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        });
    }

    /// <summary>Hides the sheet: at once nothing of it hit-tests; it slides out unless animations are off.</summary>
    public void Close()
    {
        if (!_isOpen)
        {
            return;
        }

        _isOpen = false;
        _swipeStart = null;
        _swipeTouchId = null;
        Root.IsHitTestVisible = false;
        var restoreFocus = IsKeyboardFocusWithin ? _focusBefore : null;
        _focusBefore = null;
        SlideTo(-SheetWidth(), from: null, "EdgePanelCloseDuration", new CubicEase { EasingMode = EasingMode.EaseIn }, () =>
            Root.Visibility = Visibility.Collapsed);
        if (restoreFocus is UIElement { IsVisible: true } previous)
        {
            previous.Focus();
        }

        IsOpenChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A press at <paramref name="position"/> (this panel's coordinates) anywhere over the document while the panel
    /// may be open. Returns true when the press is taken. Closed: never taken. Outside the sheet: the panel closes; a
    /// finger or the mouse is taken (a light dismiss, it does not also press the page), the pen is not (it inks). On
    /// the sheet: a finger or the mouse is left to the sheet's controls; the pen closes the panel and is taken.
    /// </summary>
    internal bool RoutePress(Point position, PointerKind kind)
    {
        if (!_isOpen)
        {
            return false;
        }

        if (IsOnSheet(position))
        {
            if (kind != PointerKind.Pen)
            {
                return false;
            }

            Close();
            return true;
        }

        Close();
        return kind != PointerKind.Pen;
    }

    /// <summary>A finger lands on the sheet (sheet coordinates or any fixed frame): a leftward swipe may start.</summary>
    internal void SwipeStart(Point position) => _swipeStart = _isOpen ? position : null;

    /// <summary>
    /// The finger moves: a leftward move of <see cref="SwipeCloseDistanceDip"/>, more horizontal than vertical, closes
    /// the panel; a mostly vertical move (scrolling the list) ends the candidate.
    /// </summary>
    internal void SwipeMove(Point position)
    {
        if (_swipeStart is not { } start)
        {
            return;
        }

        var dx = position.X - start.X;
        var dy = Math.Abs(position.Y - start.Y);
        if (dy > SwipeCloseDistanceDip && dy > Math.Abs(dx))
        {
            _swipeStart = null; // a scroll of the list
            return;
        }

        if (-dx >= SwipeCloseDistanceDip && -dx > dy)
        {
            _swipeStart = null;
            Close();
        }
    }

    /// <summary>
    /// Opaque theme brush and no shadow when Windows transparency effects are off (NFR-32); the translucent one with
    /// its shadow otherwise. The values live in the theme dictionary.
    /// </summary>
    internal void ApplyTransparency()
    {
        if (TransparencyEnabled())
        {
            Sheet.SetResourceReference(Border.BackgroundProperty, "EdgePanelBackgroundBrush");
            Sheet.SetResourceReference(EffectProperty, "EdgePanelShadow");
        }
        else
        {
            Sheet.SetResourceReference(Border.BackgroundProperty, "EdgePanelBackgroundBrushOpaque");
            Sheet.Effect = null;
        }
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        System.Windows.Automation.AutomationProperties.SetName(d, (string?)e.NewValue ?? string.Empty);

    private static void OnBodyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (EdgePanel)d;
        if (e.OldValue is not null)
        {
            panel.RemoveLogicalChild(e.OldValue);
        }

        if (e.NewValue is not null)
        {
            panel.AddLogicalChild(e.NewValue);
        }
    }

    private static bool IsPen(StylusDevice? stylus) => stylus?.TabletDevice?.Type == TabletDeviceType.Stylus;

    private double SheetWidth() =>
        Sheet.ActualWidth > 0 ? Sheet.ActualWidth : TryFindResource("EdgePanelWidth") as double? ?? Sheet.Width;

    private bool IsOnSheet(Point position)
    {
        if (Root.Visibility != Visibility.Visible)
        {
            return false;
        }

        if (Sheet.ActualWidth <= 0)
        {
            UpdateLayout(); // opened in this very input event: lay the sheet out first
        }

        var bounds = Sheet.TransformToAncestor(this).TransformBounds(new Rect(Sheet.RenderSize));
        return bounds.Contains(position);
    }

    /// <summary>
    /// Moves the sheet to <paramref name="to"/> (DIPs along X): animated with the theme duration, or at once when
    /// animations are off. <paramref name="done"/> runs when it gets there (at once when instant); a newer slide wins.
    /// </summary>
    private void SlideTo(double to, double? from, string durationKey, IEasingFunction easing, Action? done)
    {
        var version = ++_slideVersion;
        if (!AnimationsEnabled() || TryFindResource(durationKey) is not TimeSpan duration || duration <= TimeSpan.Zero)
        {
            Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            Slide.X = to;
            IsAnimating = false;
            done?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(to, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop,
        };
        if (from is { } start)
        {
            animation.From = start;
        }

        animation.Completed += (_, _) =>
        {
            if (version != _slideVersion)
            {
                return; // a newer slide took over
            }

            Slide.X = to;
            IsAnimating = false;
            done?.Invoke();
        };
        Slide.X = to; // the base value under the animation: where it ends
        IsAnimating = true;
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animation);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyTransparency); // raised on another thread

    /// <summary>The pen on the sheet: never its controls (FR-30). <c>MainWindow</c> closes the panel (RoutePress).</summary>
    private void OnSheetStylusDown(object sender, StylusDownEventArgs e)
    {
        if (IsPen(e.StylusDevice))
        {
            e.Handled = true;
        }
    }

    /// <summary>Mouse presses promoted from the pen never reach the sheet's controls either.</summary>
    private void OnSheetMouseButton(object sender, MouseButtonEventArgs e)
    {
        if (IsPen(e.StylusDevice))
        {
            e.Handled = true;
        }
    }

    private void OnSheetTouchDown(object? sender, TouchEventArgs e)
    {
        if (IsPen(Stylus.CurrentStylusDevice))
        {
            e.Handled = true; // the pen's touch events: not the list's either
            return;
        }

        if (_swipeTouchId is null)
        {
            _swipeTouchId = e.TouchDevice.Id;
            SwipeStart(e.GetTouchPoint(this).Position); // not handled: the list still scrolls and its rows tap
        }
    }

    private void OnSheetTouchMove(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id == _swipeTouchId)
        {
            SwipeMove(e.GetTouchPoint(this).Position);
        }
    }

    private void OnSheetTouchUp(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id == _swipeTouchId)
        {
            _swipeTouchId = null;
            _swipeStart = null;
        }
    }
}
