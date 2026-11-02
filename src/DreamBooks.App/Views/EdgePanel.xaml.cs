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
/// and hosts <see cref="Body"/> (the marks list, 3b-1) and, as a second tab, <see cref="SecondBody"/> (the Pages tab,
/// 3b-2): with a second body a tab strip replaces the title, and <see cref="SelectedTab"/> picks the content shown (the
/// content implementing <see cref="IEdgePanelContent"/> is told when it is shown). It opens through
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
        nameof(HeaderDetail), typeof(object), typeof(EdgePanel), new PropertyMetadata(null, OnDetailChanged));

    public static readonly DependencyProperty HeaderDetailNameFormatProperty = DependencyProperty.Register(
        nameof(HeaderDetailNameFormat), typeof(string), typeof(EdgePanel), new PropertyMetadata(null, OnDetailChanged));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(EdgePanel), new PropertyMetadata(null, OnBodyChanged));

    public static readonly DependencyProperty SecondBodyProperty = DependencyProperty.Register(
        nameof(SecondBody), typeof(object), typeof(EdgePanel), new PropertyMetadata(null, OnBodyChanged));

    public static readonly DependencyProperty FirstTabHeaderProperty = DependencyProperty.Register(
        nameof(FirstTabHeader), typeof(string), typeof(EdgePanel), new PropertyMetadata(string.Empty, OnTabsChanged));

    public static readonly DependencyProperty SecondTabHeaderProperty = DependencyProperty.Register(
        nameof(SecondTabHeader), typeof(string), typeof(EdgePanel), new PropertyMetadata(string.Empty, OnTabsChanged));

    public static readonly DependencyProperty FirstTabNameProperty = DependencyProperty.Register(
        nameof(FirstTabName), typeof(string), typeof(EdgePanel), new PropertyMetadata(null, OnTabsChanged));

    public static readonly DependencyProperty SecondTabNameProperty = DependencyProperty.Register(
        nameof(SecondTabName), typeof(string), typeof(EdgePanel), new PropertyMetadata(null, OnTabsChanged));

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
        OpenTabCommand = new RelayCommand<object?>(parameter => Open(TabOf(parameter) ?? SelectedTab));
        ToggleCommand = new RelayCommand<object?>(parameter =>
        {
            var tab = TabOf(parameter) ?? SelectedTab;
            if (_isOpen && tab == SelectedTab)
            {
                Close();
            }
            else
            {
                Open(tab); // opens it, or switches to that tab when the other one is shown
            }
        });
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

    /// <summary>The automation name of <see cref="HeaderDetail"/>: a format with {0} for the detail ("Numero di segni: {0}").</summary>
    public string? HeaderDetailNameFormat
    {
        get => (string?)GetValue(HeaderDetailNameFormatProperty);
        set => SetValue(HeaderDetailNameFormatProperty, value);
    }

    /// <summary>The hosted content; a logical child, so it inherits the panel's data context while closed too.</summary>
    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public ICommand OpenCommand { get; }

    public ICommand CloseCommand { get; }

    /// <summary>
    /// The keyboard shortcut: with a tab index as parameter (0 or 1), opens the panel on that tab, switches to it when
    /// the other one is shown, and closes the panel when it is shown already; without one, opens or closes the panel
    /// on its current tab.
    /// </summary>
    public ICommand ToggleCommand { get; }

    /// <summary>Opens the panel on the tab given as parameter (0 or 1; the current one without): the menu entries.</summary>
    public ICommand OpenTabCommand { get; }

    /// <summary>The second tab's content (the Pages tab); with it the panel shows the tab strip instead of its title.</summary>
    public object? SecondBody
    {
        get => GetValue(SecondBodyProperty);
        set => SetValue(SecondBodyProperty, value);
    }

    /// <summary>The first tab's header ("Segni").</summary>
    public string FirstTabHeader
    {
        get => (string)GetValue(FirstTabHeaderProperty);
        set => SetValue(FirstTabHeaderProperty, value);
    }

    /// <summary>The second tab's header ("Pagine").</summary>
    public string SecondTabHeader
    {
        get => (string)GetValue(SecondTabHeaderProperty);
        set => SetValue(SecondTabHeaderProperty, value);
    }

    /// <summary>The first tab's automation name ("Scheda Segni"); the header without one.</summary>
    public string? FirstTabName
    {
        get => (string?)GetValue(FirstTabNameProperty);
        set => SetValue(FirstTabNameProperty, value);
    }

    /// <summary>The second tab's automation name ("Scheda Pagine"); the header without one.</summary>
    public string? SecondTabName
    {
        get => (string?)GetValue(SecondTabNameProperty);
        set => SetValue(SecondTabNameProperty, value);
    }

    /// <summary>The tab shown: 0 (<see cref="Body"/>) or 1 (<see cref="SecondBody"/>, when there is one).</summary>
    public int SelectedTab { get; private set; }

    private bool HasTabs => SecondBody is not null;

    private object? ShownBody => SelectedTab == 1 && HasTabs ? SecondBody : Body;

    /// <summary>True from <see cref="Open"/> until <see cref="Close"/> (not while it slides out).</summary>
    public bool IsOpen => _isOpen;

    /// <summary>Windows client-area animations (NFR-32); tests inject it.</summary>
    internal Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    /// <summary>The Windows transparency setting (NFR-32); tests inject it.</summary>
    internal Func<bool> TransparencyEnabled { get; set; } = TransparencySetting.IsEnabled;

    /// <summary>True while the sheet slides in or out.</summary>
    internal bool IsAnimating { get; private set; }

    /// <summary>How many times opening or a tab switch asked to move keyboard focus into the content (tests).</summary>
    internal int FocusRequests { get; private set; }

    /// <summary>The content keyboard focus was last moved into (tests: an unshown window takes no real focus).</summary>
    internal object? LastFocusTarget { get; private set; }

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

            if (SecondBody is { } second)
            {
                children.Add(second);
            }

            return children.GetEnumerator();
        }
    }

    /// <summary>Opens the panel on <paramref name="tab"/> (0 or 1), or switches to it when the panel is open.</summary>
    public void Open(int tab)
    {
        SelectTab(tab);
        Open();
    }

    /// <summary>
    /// Shows tab <paramref name="tab"/> (0, or 1 when there is a <see cref="SecondBody"/>): its content in place of the
    /// other's; the count belongs to the first. When the panel is open, the content is told it is shown.
    /// </summary>
    public void SelectTab(int tab)
    {
        var index = tab == 1 && HasTabs ? 1 : 0;
        var changed = index != SelectedTab;
        if (changed && _isOpen)
        {
            (ShownBody as IEdgePanelContent)?.OnHidden();
        }

        SelectedTab = index;
        (index == 0 ? FirstTab : SecondTab).IsChecked = true; // re-enters through OnTabChecked: no-op then
        ApplyTab();
        if (changed && _isOpen)
        {
            NotifyShown();

            // The focused element of the other tab was just collapsed: the keyboard follows into this one, so the
            // arrows, Enter and Tab stay in the panel (a shortcut switch, or a tap on the tab).
            RequestContentFocus(keepFocusInside: false);
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
        NotifyShown();
        RequestContentFocus(keepFocusInside: true);
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
        (ShownBody as IEdgePanelContent)?.OnHidden(); // e.g. the thumbnails stop loading
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

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (EdgePanel)d;
        var title = (string?)e.NewValue ?? string.Empty;
        panel.TitleText.Text = title;
        panel.ApplyTab(); // names the panel: its title, or the tab shown
    }

    /// <summary>The count and its automation name, on a text block (which has an automation peer).</summary>
    private static void OnDetailChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (EdgePanel)d;
        var text = Convert.ToString(panel.HeaderDetail, System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;
        panel.DetailText.Text = text;
        var format = panel.HeaderDetailNameFormat;
        System.Windows.Automation.AutomationProperties.SetName(
            panel.DetailText,
            format is null ? text : string.Format(System.Globalization.CultureInfo.CurrentCulture, format, text));
    }

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

        if (e.Property == SecondBodyProperty)
        {
            panel.ApplyTabs();
        }
    }

    private static void OnTabsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((EdgePanel)d).ApplyTabs();

    /// <summary>A tab index from a command parameter (an int, or its text from XAML), or null.</summary>
    private static int? TabOf(object? parameter) => parameter switch
    {
        int tab => tab,
        string text when int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tab) => tab,
        _ => null,
    };

    /// <summary>The tab strip (shown with a second body, in place of the title), its headers and names.</summary>
    private void ApplyTabs()
    {
        if (TabStrip is null)
        {
            return; // still being built
        }

        TabStrip.Visibility = HasTabs ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = HasTabs ? Visibility.Collapsed : Visibility.Visible;
        FirstTab.Content = FirstTabHeader;
        SecondTab.Content = SecondTabHeader;
        System.Windows.Automation.AutomationProperties.SetName(FirstTab, FirstTabName ?? FirstTabHeader);
        System.Windows.Automation.AutomationProperties.SetName(SecondTab, SecondTabName ?? SecondTabHeader);
        if (!HasTabs && SelectedTab != 0)
        {
            SelectTab(0);
            return;
        }

        ApplyTab();
    }

    /// <summary>Shows the selected tab's content, the count only with the first, and names the panel after the tab.</summary>
    private void ApplyTab()
    {
        if (SecondBodyHost is null)
        {
            return; // still being built
        }

        var second = SelectedTab == 1 && HasTabs;
        BodyHost.Visibility = second ? Visibility.Collapsed : Visibility.Visible;
        SecondBodyHost.Visibility = second ? Visibility.Visible : Visibility.Collapsed;
        DetailText.Visibility = second ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(
            this, HasTabs ? (second ? SecondTabHeader : FirstTabHeader) : Title);
    }

    /// <summary>A tab chosen by touch, mouse or keyboard.</summary>
    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        var tab = ReferenceEquals(sender, SecondTab) ? 1 : 0;
        if (SecondBodyHost is not null && tab != SelectedTab)
        {
            SelectTab(tab);
        }
    }

    /// <summary>
    /// After layout, so the content (a list) can take it: keyboard focus into the content shown, at its own starting
    /// point (<see cref="IEdgePanelContent.TryFocus"/>) or its first focusable element. With
    /// <paramref name="keepFocusInside"/>, focus already inside the panel stays where it is (opening from a tab).
    /// </summary>
    private void RequestContentFocus(bool keepFocusInside)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            FocusRequests++;
            if (!_isOpen || (keepFocusInside && IsKeyboardFocusWithin))
            {
                return;
            }

            var body = ShownBody;
            LastFocusTarget = body;
            if (body is IEdgePanelContent content && content.TryFocus())
            {
                return;
            }

            (body as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }

    /// <summary>Tells the content now shown that it is (the Pages list brings the current page into view).</summary>
    private void NotifyShown()
    {
        if (ShownBody is IEdgePanelContent content)
        {
            UpdateLayout(); // the content is measured at its real size first
            content.OnShown();
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
