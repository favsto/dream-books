using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using Microsoft.Win32;

namespace DreamBooks.App.Views;

/// <summary>
/// The floating toolbar and its option flyouts (UI-02, section 3), the view of a <see cref="ToolbarViewModel"/>.
/// <list type="bullet">
/// <item>The active tool has an indicator that slides to it (<c>ToolIndicatorSlideDuration</c>), an accent icon and
/// the "active" status for screen readers.</item>
/// <item>The flyout of <see cref="ToolbarViewModel.OpenPanel"/> appears above the pill (fade and scale from
/// <c>FlyoutOpenFromScale</c> in <c>FlyoutOpenDuration</c>, ease-out) and disappears (fade and scale to
/// <c>FlyoutCloseToScale</c> in <c>FlyoutCloseDuration</c>, ease-in); a closing flyout takes no input at once.</item>
/// <item>The automatic visibility (<see cref="ToolbarViewModel.Phase"/>): retracting fades the pill out in
/// <c>ToolbarRetractDuration</c>, coming back fades it in (<c>ToolbarShowDuration</c>); retracting or hidden, it takes
/// no input, so the pen writes under it.</item>
/// </list>
/// NFR-32: every animation is instant when Windows animations are off (and the view model's retract too); with
/// transparency effects off the surfaces are opaque.
/// Pen rule (FR-30, UI-02): only the pill and the open flyout hit-test, and both carry an inert stylus plug-in (no wet
/// ink under them, <see cref="FloatingSurface"/>). On them the pen presses the controls like a finger or the mouse (the
/// pen's back end too). <see cref="RoutePress"/> closes an open flyout on a press outside it (fingers and the mouse
/// are taken, the pen inks). This differs on purpose from the left-edge panel, where a pen press on the sheet closes it.
/// A flyout is never taller than the space between the title bar and the pill (<see cref="MeasureOverride"/>): its
/// content scrolls.
/// </summary>
public partial class ToolbarView : UserControl
{
    private readonly Dictionary<FrameworkElement, int> _flyoutVersions = [];
    private ToolbarViewModel? _viewModel;
    private bool _listening;
    private bool _indicatorPlaced;
    private readonly HashSet<FrameworkElement> _animatingFlyouts = [];
    private int _pillVersion;

    public ToolbarView()
    {
        InitializeComponent();
        foreach (var flyout in Flyouts())
        {
            flyout.RenderTransform = new ScaleTransform(1, 1);
            Stylus.SetIsPressAndHoldEnabled(flyout, false); // inherited: no right-click or promotion delay on a long press
            _flyoutVersions[flyout] = 0;
        }

        Separators = [Separator1, Separator2];

        // A flyout scrolled by a finger to its end must not move the whole window (the system's boundary feedback).
        FlyoutHost.AddHandler(ManipulationBoundaryFeedbackEvent, new EventHandler<ManipulationBoundaryFeedbackEventArgs>((_, e) => e.Handled = true));
        DataContextChanged += (_, e) => Attach(e.NewValue as ToolbarViewModel);
        ButtonRow.SizeChanged += (_, _) => MoveIndicator(animate: _indicatorPlaced && AnimationsEnabled());
        Loaded += (_, _) =>
        {
            ApplyTransparency();
            ApplyMotionSetting();
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
        ApplyTransparency();
    }

    /// <summary>Windows client-area animations (NFR-32); tests inject it.</summary>
    internal Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    /// <summary>The Windows transparency setting (NFR-32); tests inject it.</summary>
    internal Func<bool> TransparencyEnabled { get; set; } = TransparencySetting.IsEnabled;

    internal ToolbarViewModel? ViewModel => _viewModel;

    /// <summary>The two separators: after Selection and after Redo.</summary>
    internal IReadOnlyList<FrameworkElement> Separators { get; }

    /// <summary>The flyout of the open panel, or null.</summary>
    internal FrameworkElement? ShownFlyout =>
        _viewModel is { OpenPanel: not OptionsPanel.None } vm ? FlyoutFor(vm.OpenPanel) : null;

    /// <summary>True while a flyout fades in or out.</summary>
    internal bool IsFlyoutAnimating => _animatingFlyouts.Count > 0;

    /// <summary>The duration of the last flyout animation started (tests).</summary>
    internal TimeSpan? LastFlyoutAnimationDuration { get; private set; }

    /// <summary>The duration of the last indicator slide started (tests).</summary>
    internal TimeSpan? LastIndicatorAnimationDuration { get; private set; }

    /// <summary>The duration of the last retract fade started (tests).</summary>
    internal TimeSpan? LastRetractAnimationDuration { get; private set; }

    internal Button ButtonFor(ToolbarTool tool) => tool switch
    {
        ToolbarTool.Highlighter => HighlighterButton,
        ToolbarTool.Eraser => EraserButton,
        ToolbarTool.Selection => SelectionButton,
        _ => PenButton,
    };

    internal FrameworkElement FlyoutFor(OptionsPanel panel) => panel switch
    {
        OptionsPanel.Highlighter => HighlighterFlyout,
        OptionsPanel.Eraser => EraserFlyout,
        _ => PenFlyout,
    };

    internal FloatingSurface SurfaceOf(OptionsPanel panel) => panel switch
    {
        OptionsPanel.Highlighter => HighlighterFlyout.Surface,
        OptionsPanel.Eraser => EraserFlyout.Surface,
        _ => PenFlyout.Surface,
    };

    /// <summary>
    /// A press at <paramref name="position"/> (this view's coordinates) anywhere over the document. Returns true when it
    /// is taken. Without an open flyout: never taken. On the open flyout or on the pill: left to their controls, for every
    /// pointer (the pen presses them too). Elsewhere: the flyout closes; a finger or the mouse is taken (a light
    /// dismiss), the pen is not (it inks).
    /// </summary>
    internal bool RoutePress(Point position, PointerKind kind)
    {
        if (_viewModel is not { OpenPanel: not OptionsPanel.None } vm)
        {
            return false;
        }

        if (Contains(SurfaceOf(vm.OpenPanel), position) || (Pill.IsHitTestVisible && Contains(Pill, position)))
        {
            return false;
        }

        vm.ClosePanel();
        return kind != PointerKind.Pen;
    }

    /// <summary>Opaque theme brushes when Windows transparency effects are off (NFR-32), the tinted layers otherwise.</summary>
    internal void ApplyTransparency()
    {
        var opaque = !TransparencyEnabled();
        Pill.IsOpaque = opaque;
        PenFlyout.Surface.IsOpaque = opaque;
        HighlighterFlyout.Surface.IsOpaque = opaque;
        EraserFlyout.Surface.IsOpaque = opaque;
    }

    /// <summary>Windows animations off (NFR-32): the view model's retract is instant as well.</summary>
    internal void ApplyMotionSetting()
    {
        if (_viewModel is not null)
        {
            _viewModel.ReducedMotion = !AnimationsEnabled();
        }
    }

    /// <summary>Shows the automatic visibility phase: fading out, hidden or (fading) back.</summary>
    internal void ApplyPhase(ToolbarPhase phase)
    {
        var version = ++_pillVersion;
        switch (phase)
        {
            case ToolbarPhase.Visible:
                Pill.Visibility = Visibility.Visible;
                Pill.IsHitTestVisible = true;
                FadePill(1, "ToolbarShowDuration", EasingMode.EaseOut, version, track: false);
                break;
            case ToolbarPhase.Retracting:
                Pill.IsHitTestVisible = false;
                FadePill(0, "ToolbarRetractDuration", EasingMode.EaseIn, version, track: true);
                break;
            default:
                Pill.IsHitTestVisible = false;
                Pill.BeginAnimation(OpacityProperty, null);
                Pill.Opacity = 0;
                Pill.Visibility = Visibility.Hidden; // out of the tab order and of screen readers too
                break;
        }
    }

    private IEnumerable<FrameworkElement> Flyouts()
    {
        yield return PenFlyout;
        yield return HighlighterFlyout;
        yield return EraserFlyout;
    }

    private bool Contains(FrameworkElement element, Point position)
    {
        if (element.Visibility != Visibility.Visible || !element.IsHitTestVisible)
        {
            return false;
        }

        if (element.ActualWidth <= 0)
        {
            UpdateLayout(); // opened in this very input event: lay it out first
        }

        if (!element.IsDescendantOf(this))
        {
            return false;
        }

        var bounds = element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
        return bounds.Contains(position);
    }

    private void Attach(ToolbarViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged += OnViewModelChanged;
        ApplyMotionSetting();
        ShowActiveTool(animate: false);
        ShowPanel(viewModel.OpenPanel);
        ApplyPhase(viewModel.Phase);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ToolbarViewModel.ActiveTool):
                ShowActiveTool(animate: AnimationsEnabled());
                break;
            case nameof(ToolbarViewModel.OpenPanel):
                ShowPanel(_viewModel.OpenPanel);
                break;
            case nameof(ToolbarViewModel.Phase):
                ApplyPhase(_viewModel.Phase);
                break;
        }
    }

    /// <summary>The accent icon and the screen readers' "active" on the active tool, and the indicator under it.</summary>
    private void ShowActiveTool(bool animate)
    {
        var active = _viewModel is { } vm ? ButtonFor(vm.ActiveTool) : PenButton;
        foreach (var button in new[] { PenButton, HighlighterButton, EraserButton, SelectionButton })
        {
            if (ReferenceEquals(button, active))
            {
                button.SetResourceReference(ForegroundProperty, "ToolActiveForegroundBrush");
                AutomationProperties.SetItemStatus(button, Strings.ToolActive);
            }
            else
            {
                button.ClearValue(ForegroundProperty);
                AutomationProperties.SetItemStatus(button, string.Empty);
            }
        }

        MoveIndicator(animate && _indicatorPlaced);
    }

    private void MoveIndicator(bool animate)
    {
        var active = _viewModel is { } vm ? ButtonFor(vm.ActiveTool) : PenButton;
        if (!active.IsDescendantOf(ButtonRow) || ButtonRow.ActualWidth <= 0)
        {
            return; // not laid out yet: placed when the row gets its size
        }

        var x = active.TranslatePoint(new Point(0, 0), ButtonRow).X;
        var y = active.TranslatePoint(new Point(0, 0), ButtonRow).Y;
        IndicatorSlide.Y = y;
        if (!animate || TryFindResource("ToolIndicatorSlideDuration") is not TimeSpan duration || duration <= TimeSpan.Zero)
        {
            IndicatorSlide.BeginAnimation(TranslateTransform.XProperty, null);
            IndicatorSlide.X = x;
            _indicatorPlaced = true;
            return;
        }

        var from = IndicatorSlide.X;
        IndicatorSlide.X = x; // the base value under the animation: where it ends
        LastIndicatorAnimationDuration = duration;
        IndicatorSlide.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(from, x, new Duration(duration))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
    }

    /// <summary>Shows the flyout of <paramref name="panel"/> (none: hides them all).</summary>
    private void ShowPanel(OptionsPanel panel)
    {
        var shown = panel == OptionsPanel.None ? null : FlyoutFor(panel);
        foreach (var flyout in Flyouts())
        {
            if (!ReferenceEquals(flyout, shown))
            {
                HideFlyout(flyout);
            }
        }

        if (shown is not null)
        {
            ShowFlyout(shown);
        }
    }

    private void ShowFlyout(FrameworkElement flyout)
    {
        var keyboardInToolbar = IsKeyboardFocusWithin;
        flyout.Visibility = Visibility.Visible;
        flyout.IsHitTestVisible = true;
        var from = TryFindResource("FlyoutOpenFromScale") as double? ?? 1;
        AnimateFlyout(flyout, 0, 1, from, 1, "FlyoutOpenDuration", EasingMode.EaseOut, done: null);
        if (keyboardInToolbar)
        {
            // A keyboard user opened it: the keyboard follows into the flyout (Esc closes it and comes back).
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (flyout.Visibility == Visibility.Visible && flyout.IsHitTestVisible)
                {
                    flyout.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                }
            });
        }
    }

    private void HideFlyout(FrameworkElement flyout)
    {
        if (flyout.Visibility != Visibility.Visible)
        {
            return;
        }

        var hadKeyboard = flyout.IsKeyboardFocusWithin;
        flyout.IsHitTestVisible = false; // at once: the pen and fingers reach the page while it fades
        var to = TryFindResource("FlyoutCloseToScale") as double? ?? 1;
        AnimateFlyout(flyout, null, 0, null, to, "FlyoutCloseDuration", EasingMode.EaseIn, done: () =>
        {
            flyout.Visibility = Visibility.Collapsed;
            flyout.Opacity = 1;
            ((ScaleTransform)flyout.RenderTransform).ScaleX = 1;
            ((ScaleTransform)flyout.RenderTransform).ScaleY = 1;
        });
        if (hadKeyboard && _viewModel is { } vm)
        {
            ButtonFor(vm.ActiveTool).Focus();
        }
    }

    /// <summary>
    /// Fades and scales a flyout: animated with the theme duration, or at once when animations are off. <paramref
    /// name="done"/> runs at the end (at once when instant); a newer animation of the same flyout wins.
    /// </summary>
    private void AnimateFlyout(
        FrameworkElement flyout, double? fromOpacity, double toOpacity, double? fromScale, double toScale,
        string durationKey, EasingMode easing, Action? done)
    {
        var version = ++_flyoutVersions[flyout];
        var scale = (ScaleTransform)flyout.RenderTransform;
        if (!AnimationsEnabled() || TryFindResource(durationKey) is not TimeSpan duration || duration <= TimeSpan.Zero)
        {
            _animatingFlyouts.Remove(flyout);
            flyout.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            flyout.Opacity = toOpacity;
            scale.ScaleX = toScale;
            scale.ScaleY = toScale;
            done?.Invoke();
            return;
        }

        LastFlyoutAnimationDuration = duration;
        var ease = new CubicEase { EasingMode = easing };
        var opacity = new DoubleAnimation(toOpacity, new Duration(duration)) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        if (fromOpacity is { } startOpacity)
        {
            opacity.From = startOpacity;
        }

        var size = new DoubleAnimation(toScale, new Duration(duration)) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        if (fromScale is { } startScale)
        {
            size.From = startScale;
        }

        _animatingFlyouts.Add(flyout);
        opacity.Completed += (_, _) =>
        {
            if (version != _flyoutVersions[flyout])
            {
                return; // a newer animation took over
            }

            _animatingFlyouts.Remove(flyout);

            flyout.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            flyout.Opacity = toOpacity;
            scale.ScaleX = toScale;
            scale.ScaleY = toScale;
            done?.Invoke();
        };
        flyout.BeginAnimation(OpacityProperty, opacity);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, size);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, size);
    }

    private void FadePill(double to, string durationKey, EasingMode easing, int version, bool track)
    {
        if (!AnimationsEnabled() || TryFindResource(durationKey) is not TimeSpan duration || duration <= TimeSpan.Zero
            || Pill.Opacity == to)
        {
            Pill.BeginAnimation(OpacityProperty, null);
            Pill.Opacity = to;
            return;
        }

        if (track)
        {
            LastRetractAnimationDuration = duration;
        }

        var from = Pill.Opacity;
        Pill.Opacity = to; // the base value under the animation: where it ends
        var animation = new DoubleAnimation(from, to, new Duration(duration))
        {
            EasingFunction = new CubicEase { EasingMode = easing },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) =>
        {
            if (version == _pillVersion)
            {
                Pill.BeginAnimation(OpacityProperty, null);
            }
        };
        Pill.BeginAnimation(OpacityProperty, animation);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            ApplyTransparency();
            ApplyMotionSetting();
        }); // raised on another thread

    /// <summary>
    /// Bounds the flyouts to the space above the pill and below the title bar (<c>TitleBarClearance</c>) of the slot the
    /// toolbar has (the document's height): a taller flyout scrolls instead of reaching under the title bar, where its
    /// top would be out of reach in a short window.
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        FlyoutHost.MaxHeight = FlyoutHeightLimit(constraint.Height);
        return base.MeasureOverride(constraint);
    }

    /// <summary>The flyouts' maximum height in a slot <paramref name="slotHeight"/> tall.</summary>
    internal double FlyoutHeightLimit(double slotHeight)
    {
        if (double.IsInfinity(slotHeight) || double.IsNaN(slotHeight))
        {
            return double.PositiveInfinity;
        }

        static double Vertical(Thickness t) => t.Top + t.Bottom;
        var title = TryFindResource("TitleBarClearance") is Thickness clearance ? clearance.Top : 0;
        var button = TryFindResource("TouchTarget") as double? ?? 0;
        var padding = TryFindResource("ToolbarPadding") is Thickness p ? Vertical(p) : 0;
        var pill = button + padding + Vertical(Pill.Margin);
        return Math.Max(0, slotHeight - title - pill - Vertical(FlyoutHost.Margin));
    }
}
