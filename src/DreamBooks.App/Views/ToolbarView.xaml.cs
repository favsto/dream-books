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
using DreamBooks.Core.Settings;
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
        WireGrip();

        // No boundary feedback from the flyouts moves the whole window (they do not pan; kept as a guard).
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

    private readonly TranslateTransform _drag = new();
    private bool _vertical;

    /// <summary>The edge the toolbar sits on (UI-02); set from the view model (the settings, or a drag by the grip).</summary>
    internal ToolbarEdge Edge { get; private set; } = ToolbarEdge.Bottom;

    /// <summary>True on the left and right edges: the pill is a column.</summary>
    internal bool IsVertical => _vertical;

    /// <summary>The length of the last snap animation after a drag; null when it was instant (animations off) or none happened.</summary>
    internal TimeSpan? LastSnapAnimationDuration { get; private set; }

    /// <summary>The toolbar grip, by which a finger or the mouse drags it.</summary>
    internal ToolbarGrip DragGrip => Grip;

    /// <summary>
    /// Puts the pill and the flyouts on <paramref name="edge"/>: the pill is a row on the top and bottom edges and a
    /// column on the left and right; the flyouts open towards the centre of the window. Bottom is exactly the layout 4a
    /// had. The visibility rules, the hit testing and the pen rules do not depend on the edge.
    /// </summary>
    internal void ApplyEdge(ToolbarEdge edge)
    {
        Edge = edge;
        _vertical = edge is ToolbarEdge.Left or ToolbarEdge.Right;
        Root.RowDefinitions.Clear();
        Root.ColumnDefinitions.Clear();
        switch (edge)
        {
            case ToolbarEdge.Top:
                AddAutoRows();
                SetSlots([Pill, FlyoutHost], row: true);
                Place(HorizontalAlignment.Center, VerticalAlignment.Top, Resource<Thickness>("ToolbarMarginTop"), Resource<Thickness>("FlyoutMarginTop"));
                FlyoutHost.VerticalAlignment = VerticalAlignment.Top;
                SetFlyoutAnchor(VerticalAlignment.Top, new Point(0.5, 0));
                break;
            case ToolbarEdge.Left:
                AddAutoColumns();
                SetSlots([Pill, FlyoutHost], row: false);
                Place(HorizontalAlignment.Left, VerticalAlignment.Center, Resource<Thickness>("ToolbarMarginLeft"), Resource<Thickness>("FlyoutMarginLeft"));
                FlyoutHost.HorizontalAlignment = HorizontalAlignment.Left;
                FlyoutHost.VerticalAlignment = VerticalAlignment.Center;
                SetFlyoutAnchor(VerticalAlignment.Center, new Point(0, 0.5));
                break;
            case ToolbarEdge.Right:
                AddAutoColumns();
                SetSlots([FlyoutHost, Pill], row: false);
                Place(HorizontalAlignment.Right, VerticalAlignment.Center, Resource<Thickness>("ToolbarMarginRight"), Resource<Thickness>("FlyoutMarginRight"));
                FlyoutHost.HorizontalAlignment = HorizontalAlignment.Right;
                FlyoutHost.VerticalAlignment = VerticalAlignment.Center;
                SetFlyoutAnchor(VerticalAlignment.Center, new Point(1, 0.5));
                break;
            default:
                AddAutoRows();
                SetSlots([FlyoutHost, Pill], row: true);
                Place(HorizontalAlignment.Center, VerticalAlignment.Bottom, Resource<Thickness>("ToolbarMargin"), Resource<Thickness>("FlyoutMargin"));
                FlyoutHost.VerticalAlignment = VerticalAlignment.Bottom;
                SetFlyoutAnchor(VerticalAlignment.Bottom, new Point(0.5, 1));
                break;
        }

        ButtonRow.Orientation = _vertical ? Orientation.Vertical : Orientation.Horizontal;
        Grip.IsVertical = _vertical;
        PillContent.Margin = Resource<Thickness>(_vertical ? "ToolbarPaddingVertical" : "ToolbarPadding");
        var line = Resource<double>("ToolbarSeparatorWidth");
        var length = Resource<double>("ToolbarSeparatorHeight");
        foreach (var separator in Separators)
        {
            separator.Width = _vertical ? length : line;
            separator.Height = _vertical ? line : length;
            separator.Margin = Resource<Thickness>(_vertical ? "ToolbarSeparatorMarginVertical" : "ToolbarSeparatorMargin");
            separator.VerticalAlignment = _vertical ? VerticalAlignment.Top : VerticalAlignment.Center;
            separator.HorizontalAlignment = _vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }

        InvalidateMeasure();
        UpdateLayout();
        MoveIndicator(animate: false);
    }

    private T Resource<T>(string key) => TryFindResource(key) is T value ? value : default!;

    private void AddAutoRows()
    {
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    }

    private void AddAutoColumns()
    {
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    }

    /// <summary>The pill and the flyout host on the grid rows (a row layout) or columns, in the order given.</summary>
    private static void SetSlots(FrameworkElement[] ordered, bool row)
    {
        for (var i = 0; i < ordered.Length; i++)
        {
            Grid.SetRow(ordered[i], row ? i : 0);
            Grid.SetColumn(ordered[i], row ? 0 : i);
        }
    }

    private void Place(HorizontalAlignment horizontal, VerticalAlignment vertical, Thickness pillMargin, Thickness flyoutMargin)
    {
        HorizontalAlignment = horizontal;
        VerticalAlignment = vertical;
        Pill.Margin = pillMargin;
        Pill.HorizontalAlignment = horizontal == HorizontalAlignment.Center ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        Pill.VerticalAlignment = vertical == VerticalAlignment.Center ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        FlyoutHost.Margin = flyoutMargin;
        FlyoutHost.HorizontalAlignment = HorizontalAlignment.Center;
    }

    private void SetFlyoutAnchor(VerticalAlignment alignment, Point origin)
    {
        foreach (var flyout in Flyouts())
        {
            flyout.VerticalAlignment = alignment;
            flyout.RenderTransformOrigin = origin;
        }
    }

    /// <summary>The grip drag: the toolbar follows the pointer, and where it is let go it snaps to the nearest edge (finger or mouse).</summary>
    private void WireGrip()
    {
        Grip.DragStarted += (_, _) =>
        {
            _drag.X = 0;
            _drag.Y = 0;
            RenderTransform = _drag;
            _viewModel?.ClosePanel();
        };
        Grip.DragDelta += (_, delta) =>
        {
            _drag.X = delta.X;
            _drag.Y = delta.Y;
        };
        Grip.DragCompleted += (_, release) =>
        {
            _drag.X = 0;
            _drag.Y = 0;
            var host = Grip.ReferenceElement as FrameworkElement ?? this;
            var edge = ToolbarPlacement.NearestEdge(release.X, release.Y, host.ActualWidth, host.ActualHeight);
            if (_viewModel is not null && edge != _viewModel.Edge)
            {
                _viewModel.Edge = edge;
                Snap();
            }
        };
        Loaded += (_, _) => Grip.ReferenceElement = VisualTreeHelper.GetParent(this) as UIElement;
    }

    /// <summary>The short animation of the toolbar landing on its new edge (a fade-in); none with Windows animations off (NFR-32).</summary>
    private void Snap()
    {
        LastSnapAnimationDuration = null;
        if (!AnimationsEnabled() || TryFindResource("ToolbarShowDuration") is not TimeSpan duration || duration <= TimeSpan.Zero)
        {
            return;
        }

        LastSnapAnimationDuration = duration;
        var from = Resource<double>("ToolbarSnapFromOpacity");
        BeginAnimation(OpacityProperty, new DoubleAnimation(from, 1, new Duration(duration)) { FillBehavior = FillBehavior.Stop });
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
        ApplyEdge(viewModel.Edge);
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
            case nameof(ToolbarViewModel.Edge):
                ApplyEdge(_viewModel.Edge);
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

        var at = active.TranslatePoint(new Point(0, 0), ButtonRow);

        // The indicator slides along the pill: across on the top and bottom edges, down on the left and right ones.
        var along = _vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        var target = _vertical ? at.Y : at.X;
        if (_vertical)
        {
            IndicatorSlide.BeginAnimation(TranslateTransform.XProperty, null);
            IndicatorSlide.X = at.X;
        }
        else
        {
            IndicatorSlide.BeginAnimation(TranslateTransform.YProperty, null);
            IndicatorSlide.Y = at.Y;
        }

        if (!animate || TryFindResource("ToolIndicatorSlideDuration") is not TimeSpan duration || duration <= TimeSpan.Zero)
        {
            IndicatorSlide.BeginAnimation(along, null);
            IndicatorSlide.SetValue(along, target);
            _indicatorPlaced = true;
            return;
        }

        var from = (double)IndicatorSlide.GetValue(along);
        IndicatorSlide.SetValue(along, target); // the base value under the animation: where it ends
        LastIndicatorAnimationDuration = duration;
        IndicatorSlide.BeginAnimation(
            along,
            new DoubleAnimation(from, target, new Duration(duration))
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
        if (_vertical)
        {
            // Beside the pill: the flyout is centred in the window, so it may take only what is free above and below the centre:
            // twice the larger of the two gaps (the title bar and the menu button above, the margin below), never under the title bar.
            return Math.Max(0, slotHeight - (2 * Math.Max(title, Math.Max(Pill.Margin.Top, Pill.Margin.Bottom))));
        }

        // Above (bottom edge) or below (top edge) the pill. On the top edge the pill's margin already holds the title bar.
        var pill = button + padding + Vertical(Pill.Margin);
        var reserved = Edge == ToolbarEdge.Top ? 0 : title;
        var gap = Edge == ToolbarEdge.Top ? ToolbarBottomGap() : 0;
        return Math.Max(0, slotHeight - reserved - pill - Vertical(FlyoutHost.Margin) - gap);
    }

    /// <summary>The space kept below a flyout that opens downwards from the top edge (the bottom edge's gap of the toolbar).</summary>
    private double ToolbarBottomGap() => TryFindResource("ToolbarMargin") is Thickness m ? m.Bottom : 0;
}
