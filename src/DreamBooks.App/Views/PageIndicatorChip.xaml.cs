using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using Microsoft.Win32;

namespace DreamBooks.App.Views;

/// <summary>
/// The page indicator chip at the bottom right (UI-05): "12 / 48 · 125%" (<see cref="DocumentViewportViewModel.IndicatorText"/>).
/// <list type="bullet">
/// <item>A tap opens the go-to-page field in its place, the current page selected (FR-09): Enter on a valid page number
/// jumps there (recorded for Back), anything else is ignored; Esc, a press outside it (<see cref="RoutePress"/>) or the
/// keyboard leaving it closes it without moving.</item>
/// <item>A long press (<c>LongPressDuration</c>, moving less than <c>LongPressSlop</c>), the mouse or the pen hovering
/// over it, or the keyboard on it shows the zoom − / + buttons (the Ctrl − / Ctrl + commands, FR-06) and fit width
/// (FR-07, the debug panel's button); a long press
/// keeps them until the chip hides.</item>
/// <item>It shows and hides with the rail (<see cref="SetShown"/>, fading in <c>NavigationFadeDuration</c>, instant when
/// Windows animations are off, NFR-32) and keeps the rail shown while it is used (<see cref="IsHolding"/>,
/// <see cref="HoldChanged"/>): hovered, the field open, the keyboard on it (Tab reaches it while it is hidden and
/// brings it up). Without a document it is collapsed.</item>
/// </list>
/// Pen rule (UI-02, FR-30): only its surface hit-tests and carries the inert stylus plug-in; on it the pen presses the
/// buttons and types in the field like a finger; around it the pen inks; hidden, it takes no input at once.
/// </summary>
public partial class PageIndicatorChip : UserControl
{
    private bool _shown;
    private bool _available;
    private bool _editing;
    private bool _hovered;
    private bool _keyboardHold;
    private bool _zoomPinned;
    private bool _holding;
    private bool _holdFired;
    private bool _swallowClick;
    private bool _listening;
    private Point _holdStart;
    private IDisposable? _pendingHold;
    private IInputElement? _focusBefore;

    public PageIndicatorChip()
    {
        InitializeComponent();
        Surface.Opacity = 0;
        Surface.IsHitTestVisible = false;
        Visibility = Visibility.Collapsed; // until the window says there is a document
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        IsKeyboardFocusWithinChanged += (_, e) =>
            SetKeyboardHold((bool)e.NewValue && InputManager.Current.MostRecentInputDevice is KeyboardDevice);
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

            CloseGoToPage(restoreFocus: false);
            CancelHold();
        };
        ApplyTransparency();
    }

    /// <summary>Raised when <see cref="IsHolding"/> changes: the window holds the rail (and with it this chip) shown.</summary>
    internal event EventHandler? HoldChanged;

    /// <summary>Windows client-area animations (NFR-32); tests inject it.</summary>
    internal Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    /// <summary>The Windows transparency setting (NFR-32); tests inject it.</summary>
    internal Func<bool> TransparencyEnabled { get; set; } = TransparencySetting.IsEnabled;

    /// <summary>Waits for the long press; tests drive time by hand.</summary>
    internal IDelayScheduler Scheduler { get; set; } = new DispatcherDelayScheduler();

    /// <summary>True while the chip is in use: hovered, its field open, or the keyboard on it.</summary>
    internal bool IsHolding => _hovered || _editing || _keyboardHold;

    /// <summary>True while shown (with a document and the rail shown): it then takes input.</summary>
    internal bool IsShown => _shown && _available;

    internal bool IsEditing => _editing;

    internal bool AreZoomButtonsShown => ZoomInButton.Visibility == Visibility.Visible;

    /// <summary>The duration of the last fade started (tests).</summary>
    internal TimeSpan? LastFadeDuration { get; private set; }

    private DocumentViewportViewModel? Viewport => (DataContext as MainViewModel)?.Viewport;

    /// <summary>
    /// Shown with the rail (<paramref name="shown"/>) when there is a document (<paramref name="available"/>): it fades
    /// in; otherwise it takes no input at once and fades out (without a document it is collapsed at once, its field
    /// closed). Zoom buttons shown by a long press go when it hides.
    /// </summary>
    internal void SetShown(bool shown, bool available)
    {
        var was = IsShown;
        _shown = shown;
        _available = available;
        Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            CloseGoToPage(restoreFocus: false);
        }

        var now = IsShown;
        Surface.IsHitTestVisible = now;
        if (!now)
        {
            _zoomPinned = false;
        }

        if (now != was || !available)
        {
            var duration = FloatingFade.Run(Surface, now ? 1 : 0, "NavigationFadeDuration", available && AnimationsEnabled());
            LastFadeDuration = duration ?? LastFadeDuration;
        }

        UpdateZoomButtons();
    }

    /// <summary>The mouse (or the pen in the air) is over the chip: the zoom buttons show and the rail stays.</summary>
    internal void SetHover(bool hovering)
    {
        if (hovering == _hovered)
        {
            return;
        }

        _hovered = hovering;
        UpdateZoomButtons();
        HoldChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The keyboard reached the chip (or left it): shown with the zoom buttons while it is there.</summary>
    internal void SetKeyboardHold(bool held)
    {
        if (held == _keyboardHold)
        {
            return;
        }

        _keyboardHold = held;
        UpdateZoomButtons();
        HoldChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A press on the indicator at <paramref name="position"/>: the long press starts.</summary>
    internal void BeginHold(Point position)
    {
        CancelHold();
        _holdFired = false;
        _swallowClick = false;
        _holding = true;
        _holdStart = position;
        var duration = TryFindResource("LongPressDuration") as TimeSpan? ?? TimeSpan.FromMilliseconds(600);
        _pendingHold = Scheduler.Schedule(duration, () =>
        {
            _pendingHold = null;
            if (!_holding)
            {
                return;
            }

            _holding = false;
            _holdFired = true;
            _zoomPinned = true;
            UpdateZoomButtons();
        });
    }

    /// <summary>The press moves: farther than <c>LongPressSlop</c> it is no longer a long press.</summary>
    internal void MoveHold(Point position)
    {
        var slop = TryFindResource("LongPressSlop") as double? ?? 8;
        if (_holding && (position - _holdStart).Length > slop)
        {
            CancelHold();
        }
    }

    /// <summary>The press ends: true when it was a long press (the tap that ends it opens nothing).</summary>
    internal bool EndHold()
    {
        var fired = _holdFired;
        CancelHold();
        _holdFired = false;
        _swallowClick = fired;
        return fired;
    }

    /// <summary>A tap on the indicator: the page field in its place, with the current page selected (FR-09).</summary>
    internal void OpenGoToPage()
    {
        if (_editing || Viewport is not { Layout: not null } viewport)
        {
            return;
        }

        _editing = true;
        _focusBefore = Keyboard.FocusedElement;
        IndicatorButton.Visibility = Visibility.Collapsed;
        GoToPageBox.Visibility = Visibility.Visible;
        GoToPageBox.Text = (viewport.CurrentPage + 1).ToString(CultureInfo.CurrentCulture);
        HoldChanged?.Invoke(this, EventArgs.Empty);

        // Focused once it is laid out (a collapsed element cannot take focus).
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_editing)
            {
                GoToPageBox.Focus();
                GoToPageBox.SelectAll();
            }
        });
    }

    /// <summary>Enter: a valid page number jumps there and closes the field; anything else is ignored (false).</summary>
    internal bool ConfirmGoToPage()
    {
        if (!_editing)
        {
            return false;
        }

        if (Viewport?.TryGoToPage(GoToPageBox.Text) == true)
        {
            CloseGoToPage(restoreFocus: true);
            return true;
        }

        GoToPageBox.SelectAll();
        return false;
    }

    /// <summary>Esc, or a press anywhere outside the field: closes it without moving.</summary>
    internal void CancelGoToPage() => CloseGoToPage(restoreFocus: true);

    /// <summary>A key in the open field: Enter confirms, Esc cancels (true: taken); anything else is the field's.</summary>
    internal bool HandleFieldKey(Key key)
    {
        switch (key)
        {
            case Key.Enter:
                ConfirmGoToPage();
                return true;
            case Key.Escape:
                CancelGoToPage();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A press anywhere over the document at <paramref name="position"/> (this chip's coordinates): outside the open
    /// field it closes it. Never taken: the press goes on to whatever is under it (the pen inks).
    /// </summary>
    internal void RoutePress(Point position)
    {
        if (_editing && !BoundsOf(GoToPageBox).Contains(position))
        {
            CancelGoToPage();
        }
    }

    /// <summary>Opaque theme brushes when Windows transparency effects are off (NFR-32), the tinted layers otherwise.</summary>
    internal void ApplyTransparency() => Surface.IsOpaque = !TransparencyEnabled();

    private Rect BoundsOf(FrameworkElement element) =>
        element.ActualWidth > 0 && element.ActualHeight > 0 && element.IsDescendantOf(this)
            ? element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize))
            : Rect.Empty;

    private void UpdateZoomButtons()
    {
        var show = IsShown && (_hovered || _zoomPinned || _keyboardHold);
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ZoomOutButton.Visibility = visibility;
        ZoomInButton.Visibility = visibility;
        FitWidthButton.Visibility = visibility;
    }

    private void CancelHold()
    {
        _pendingHold?.Dispose();
        _pendingHold = null;
        _holding = false;
    }

    private void CloseGoToPage(bool restoreFocus)
    {
        if (!_editing)
        {
            return;
        }

        _editing = false;
        var before = _focusBefore;
        _focusBefore = null;
        var hadFocus = GoToPageBox.IsKeyboardFocusWithin;
        GoToPageBox.Visibility = Visibility.Collapsed;
        IndicatorButton.Visibility = Visibility.Visible;
        HoldChanged?.Invoke(this, EventArgs.Empty);
        if (restoreFocus && hadFocus && before is UIElement { IsVisible: true } element)
        {
            element.Focus();
        }
        else if (hadFocus)
        {
            Keyboard.ClearFocus(); // the keys go back to the window (and the navigation keys to the document)
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyTransparency); // raised on another thread

    private void OnIndicatorClick(object sender, RoutedEventArgs e)
    {
        if (_swallowClick)
        {
            _swallowClick = false; // the end of a long press
            return;
        }

        OpenGoToPage();
    }

    private void OnIndicatorPressed(object sender, MouseButtonEventArgs e) => BeginHold(e.GetPosition(this));

    private void OnIndicatorMoved(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            MoveHold(e.GetPosition(this));
        }
    }

    // Before the button's own release (which raises Click): a long press swallows that click.
    private void OnIndicatorReleased(object sender, MouseButtonEventArgs e) => EndHold();

    // The button releases its capture before it clicks: only the pending timer goes, the long press result stays.
    private void OnIndicatorCaptureLost(object sender, MouseEventArgs e)
    {
        _pendingHold?.Dispose();
        _pendingHold = null;
        _holding = false;
    }

    private void OnGoToPageKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleFieldKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnGoToPageLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!GoToPageBox.IsKeyboardFocusWithin)
        {
            CloseGoToPage(restoreFocus: false); // focus moved elsewhere: closed, nothing moves
        }
    }
}
