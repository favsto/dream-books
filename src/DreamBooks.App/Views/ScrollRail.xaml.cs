using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Layout;
using DreamBooks.Pdf;

namespace DreamBooks.App.Views;

/// <summary>
/// The fading scroll rail on the document's right edge (UI-07, design B): a knob showing the position, a 40 px strip
/// where a tap jumps and a drag scrubs through the document (the position before is recorded for Back once per drag,
/// FR-09), one large preview of the page under the knob while scrubbing, and the "12 / 48 · 125%" indicator that opens
/// a page field on tap. It appears on scroll and near the edge and fades out after the idle delay
/// (<see cref="RailVisibilityState"/>; instant when Windows animations are off, NFR-32).
/// </summary>
/// <remarks>
/// The position fraction is the scroll offset over the scrollable range (extent minus viewport), both from the view
/// model, which is the only writer of the position: scrubbing calls <see cref="DocumentViewportViewModel.SetScroll"/>.
/// Nothing scrolls while a page is inking (<see cref="CanScroll"/>). Only the strip and the indicator hit-test, and
/// only while the rail is shown, so the pen inks everywhere else; nothing here takes keyboard focus except the page
/// field while it is open.
/// </remarks>
public partial class ScrollRail : UserControl
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RailVisibilityState _state;
    private readonly DispatcherTimer _idleTimer;
    private DocumentViewportViewModel? _model;
    private RailTimings? _timings;
    private bool _rendering;
    private bool _dragging;
    private bool _hovering;
    private bool _editing;
    private int? _touchId;
    private double _grab;
    private int _previewPage = -1;
    private CancellationTokenSource? _previewCancel;
    private IInputElement? _focusBefore;

    public ScrollRail()
    {
        InitializeComponent();
        _state = new RailVisibilityState(() => _clock.Elapsed, default);
        _idleTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            Refresh();
        };
        SizeChanged += (_, _) => UpdateKnob();
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) =>
        {
            _idleTimer.Stop();
            SetRendering(false);
        };
    }

    /// <summary>The document position the rail shows and moves; set by <see cref="DocumentView"/>.</summary>
    internal DocumentViewportViewModel? Model
    {
        get => _model;
        set
        {
            if (ReferenceEquals(value, _model))
            {
                return;
            }

            if (_model is { } old)
            {
                old.PositionChanged -= OnPositionChanged;
                old.LayoutChanged -= OnLayoutChanged;
            }

            _model = value;
            if (value is not null)
            {
                value.PositionChanged += OnPositionChanged;
                value.LayoutChanged += OnLayoutChanged;
            }

            HideNow();
            UpdateKnob();
        }
    }

    /// <summary>The preview of a page for the bubble (the bitmap coordinator's); a newer request cancels the older.</summary>
    internal Func<int, CancellationToken, Task<BitmapSource?>>? PreviewSource { get; set; }

    /// <summary>False while a page is inking: the rail then never moves the document under the pen.</summary>
    internal Func<bool>? CanScroll { get; set; }

    /// <summary>The mouse wheel over the strip (the delta of the event): scrolls the document.</summary>
    internal Action<int>? WheelScroll { get; set; }

    internal RailVisibilityState State => _state;

    internal bool IsDragging => _dragging;

    internal double KnobHeight => Knob.Height;

    /// <summary>How far the knob's top can travel, in DIPs of the track.</summary>
    internal double TrackLength => Math.Max(0, Track.ActualHeight - KnobHeight);

    /// <summary>The fade timings from the theme dictionary (<c>RailFadeInDuration</c> and the others).</summary>
    internal static RailTimings TimingsFrom(Func<string, object?> find)
    {
        ArgumentNullException.ThrowIfNull(find);
        return new RailTimings(
            Require(find, "RailFadeInDuration"), Require(find, "RailFadeOutDuration"), Require(find, "RailIdleDelay"));
    }

    /// <summary>Scrolling, the wheel, a finger or the mouse near the edge: the rail shows and its idle delay restarts.</summary>
    internal void NotifyActivity()
    {
        if (_model?.Layout is null)
        {
            return;
        }

        PrepareState();
        _state.Activity();
        UpdateKnob();
        Refresh();
    }

    /// <summary>Hidden at once (another document, or none): a drag ends, the page field closes.</summary>
    internal void HideNow()
    {
        CloseGoToPage(restoreFocus: false);
        if (_dragging)
        {
            EndDrag();
        }

        _hovering = false;
        _state.Hide();
        Refresh();
    }

    /// <summary>
    /// A finger, pen or mouse goes down on the strip at <paramref name="trackY"/> (DIPs from the track's top). On the
    /// knob it grabs it where it was touched; elsewhere the knob's centre jumps there. The position before is recorded
    /// for Back. Nothing (false) while a page is inking or without a document.
    /// </summary>
    internal bool PointerDown(double trackY)
    {
        if (_dragging || _model is not { Layout: not null } model || CanScroll?.Invoke() == false)
        {
            return false;
        }

        _dragging = true;
        NotifyActivity();
        UpdateHeld();
        model.BeginRailDrag();
        var knobTop = KnobOffset.Y;
        _grab = trackY >= knobTop && trackY <= knobTop + KnobHeight ? trackY - knobTop : KnobHeight / 2;
        PreviewBubble.Visibility = Visibility.Visible;
        ScrubTo(trackY);
        return true;
    }

    /// <summary>The pointer moved while dragging: the document follows continuously.</summary>
    internal void PointerMove(double trackY)
    {
        if (_dragging)
        {
            ScrubTo(trackY);
        }
    }

    /// <summary>The drag ends; the rail and the bubble fade out after the idle delay.</summary>
    internal void PointerUp()
    {
        if (!_dragging)
        {
            return;
        }

        EndDrag();
        UpdateKnob();
    }

    /// <summary>Tapping the indicator: a page field in its place, with the current page selected (FR-09).</summary>
    internal void OpenGoToPage()
    {
        if (_editing || _model is not { Layout: not null } model)
        {
            return;
        }

        _editing = true;
        _focusBefore = Keyboard.FocusedElement;
        IndicatorButton.Visibility = Visibility.Collapsed;
        GoToPageBox.Visibility = Visibility.Visible;
        GoToPageBox.Text = (model.CurrentPage + 1).ToString(CultureInfo.CurrentCulture);
        UpdateHeld();

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

        if (_model?.TryGoToPage(GoToPageBox.Text) == true)
        {
            CloseGoToPage(restoreFocus: true);
            return true;
        }

        GoToPageBox.SelectAll();
        return false;
    }

    /// <summary>Esc: closes the field without moving.</summary>
    internal void CancelGoToPage() => CloseGoToPage(restoreFocus: true);

    private static TimeSpan Require(Func<string, object?> find, string key) =>
        find(key) is TimeSpan value
            ? value
            : throw new InvalidOperationException($"The theme resource '{key}' is missing.");

    private static double Scrollable(DocumentViewportViewModel model, DocumentLayout layout) =>
        Math.Max(0, layout.ExtentHeightDip(model.Position.Zoom) - model.ViewportHeightDip);

    private void PrepareState()
    {
        _timings ??= TimingsFrom(key => TryFindResource(key));
        _state.Timings = _timings.Value;
        _state.ReducedMotion = !SystemParameters.ClientAreaAnimation;
    }

    private void UpdateHeld()
    {
        PrepareState();
        _state.IsHeld = _dragging || _hovering || _editing;
        Refresh();
    }

    /// <summary>Applies the state: opacity, hit-testing (none while hidden), the bubble, and when to look again.</summary>
    private void Refresh()
    {
        var phase = _state.Phase;
        Opacity = _state.Opacity;
        IsHitTestVisible = phase != RailPhase.Hidden;
        if (phase == RailPhase.Hidden)
        {
            HideBubble();
        }

        var fading = phase is RailPhase.FadingIn or RailPhase.FadingOut;
        SetRendering(fading && IsLoaded);
        _idleTimer.Stop();
        if (!fading && IsLoaded && _state.TimeToNextChange is { } wait)
        {
            _idleTimer.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromTicks(1);
            _idleTimer.Start();
        }
    }

    /// <summary>Every frame while fading, the opacity follows the state's eased curve.</summary>
    private void SetRendering(bool on)
    {
        if (on == _rendering)
        {
            return;
        }

        _rendering = on;
        if (on)
        {
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e) => Refresh();

    private void OnPositionChanged(object? sender, EventArgs e)
    {
        UpdateKnob();
        UpdatePreview();
    }

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        HideNow();
        UpdateKnob();
    }

    /// <summary>The knob at the position's fraction of the track (not while dragging: then it follows the pointer).</summary>
    private void UpdateKnob()
    {
        if (_dragging)
        {
            return;
        }

        var fraction = 0.0;
        if (_model is { Layout: { } layout } model && Scrollable(model, layout) is > 0 and var scrollable)
        {
            var (_, y) = layout.ToOffsets(model.Position, model.ViewportWidthDip, model.ViewportHeightDip);
            fraction = Math.Clamp(y / scrollable, 0, 1);
        }

        KnobOffset.Y = fraction * TrackLength;
        PlaceBubble();
    }

    /// <summary>The knob's top goes to <paramref name="trackY"/> minus the grab point; the document follows.</summary>
    private void ScrubTo(double trackY)
    {
        if (_model is not { Layout: { } layout } model || CanScroll?.Invoke() == false)
        {
            return; // never move a page under a stroke being written
        }

        var length = TrackLength;
        var top = Math.Clamp(trackY - _grab, 0, length);
        KnobOffset.Y = top;
        var (x, _) = layout.ToOffsets(model.Position, model.ViewportWidthDip, model.ViewportHeightDip);
        model.SetScroll(x, length > 0 ? top / length * Scrollable(model, layout) : 0);
        UpdatePreview();
        PlaceBubble();
    }

    private void EndDrag()
    {
        _dragging = false;
        _touchId = null;
        if (Strip.IsMouseCaptured)
        {
            Strip.ReleaseMouseCapture();
        }

        Strip.ReleaseAllTouchCaptures();
        _model?.EndRailDrag();
        UpdateHeld();
    }

    /// <summary>The bubble shows the page under the knob (the current page): its number, then its preview.</summary>
    private void UpdatePreview()
    {
        if (PreviewBubble.Visibility != Visibility.Visible || _model is not { Layout: { } layout } model)
        {
            return;
        }

        var page = model.CurrentPage;
        if (page == _previewPage || page < 0 || page >= layout.PageCount)
        {
            return;
        }

        _previewPage = page;
        PreviewText.Text = string.Format(CultureInfo.CurrentCulture, Strings.RailPreviewPageFormat, page + 1);
        PreviewImage.Source = null;
        var size = VirtualPagePanel.PageSizePt(layout, page);
        var width = PreviewBubble.Width - PreviewBubble.Padding.Left - PreviewBubble.Padding.Right;
        if (size.Width > 0 && size.Height > 0 && double.IsFinite(size.Width) && double.IsFinite(size.Height))
        {
            var (pixelWidth, pixelHeight) = PageBitmapRenderer.PreviewSize(new PageSize(size.Width, size.Height), 1);
            PreviewPlaceholder.Height = width * pixelHeight / pixelWidth;
        }
        else
        {
            PreviewPlaceholder.Height = width;
        }

        _previewCancel?.Cancel();
        _previewCancel?.Dispose();
        _previewCancel = new CancellationTokenSource();
        _ = LoadPreviewAsync(page, _previewCancel.Token);
    }

    private async Task LoadPreviewAsync(int page, CancellationToken ct)
    {
        if (PreviewSource is not { } source)
        {
            return;
        }

        BitmapSource? bitmap;
        try
        {
            bitmap = await source(page, ct);
        }
#pragma warning disable CA1031 // The bubble keeps its placeholder rather than failing (NFR-11).
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        if (!ct.IsCancellationRequested && page == _previewPage)
        {
            PreviewImage.Source = bitmap;
        }
    }

    private void HideBubble()
    {
        if (PreviewBubble.Visibility == Visibility.Collapsed && _previewPage < 0)
        {
            return;
        }

        PreviewBubble.Visibility = Visibility.Collapsed;
        _previewCancel?.Cancel();
        _previewCancel?.Dispose();
        _previewCancel = null;
        _previewPage = -1;
        PreviewImage.Source = null;
    }

    /// <summary>The bubble's centre beside the knob's centre, kept inside the rail.</summary>
    private void PlaceBubble()
    {
        if (PreviewBubble.Visibility != Visibility.Visible)
        {
            return;
        }

        var centre = Track.TranslatePoint(new Point(0, KnobOffset.Y + (KnobHeight / 2)), this).Y;
        var height = PreviewBubble.ActualHeight;
        BubbleOffset.Y = Math.Clamp(centre - (height / 2), 0, Math.Max(0, ActualHeight - height));
    }

    private void OnBubbleSizeChanged(object sender, SizeChangedEventArgs e) => PlaceBubble();

    private void CloseGoToPage(bool restoreFocus)
    {
        if (!_editing)
        {
            return;
        }

        _editing = false;
        var before = _focusBefore;
        _focusBefore = null;
        GoToPageBox.Visibility = Visibility.Collapsed;
        IndicatorButton.Visibility = Visibility.Visible;
        UpdateHeld();
        if (restoreFocus && before is UIElement { IsVisible: true } element)
        {
            element.Focus();
        }
    }

    private void OnIndicatorClick(object sender, RoutedEventArgs e) => OpenGoToPage();

    private void OnGoToPageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConfirmGoToPage();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelGoToPage();
        }
    }

    private void OnGoToPageLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!GoToPageBox.IsKeyboardFocusWithin)
        {
            CloseGoToPage(restoreFocus: false); // tapped elsewhere: closed, nothing moves
        }
    }

    private void OnMouseEnterRail(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice is null)
        {
            _hovering = true; // the mouse over the rail keeps it (not the pen hovering: it is on its way to ink)
            UpdateHeld();
        }
    }

    private void OnMouseLeaveRail(object sender, MouseEventArgs e)
    {
        if (_hovering)
        {
            _hovering = false;
            UpdateHeld();
        }
    }

    private void OnStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (PointerDown(e.GetPosition(Track).Y))
        {
            Strip.CaptureMouse();
        }
    }

    private void OnStripMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging && _touchId is null && Strip.IsMouseCaptured)
        {
            PointerMove(e.GetPosition(Track).Y);
        }
    }

    private void OnStripMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_touchId is null && _dragging)
        {
            e.Handled = true;
            PointerUp();
        }
    }

    private void OnStripLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_touchId is null)
        {
            PointerUp();
        }
    }

    private void OnStripTouchDown(object sender, TouchEventArgs e)
    {
        e.Handled = true; // not promoted to the mouse, never seen by the document's pan
        if (PointerDown(e.GetTouchPoint(Track).Position.Y))
        {
            _touchId = e.TouchDevice.Id;
            Strip.CaptureTouch(e.TouchDevice);
        }
    }

    private void OnStripTouchMove(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id == _touchId)
        {
            e.Handled = true;
            PointerMove(e.GetTouchPoint(Track).Position.Y);
        }
    }

    private void OnStripTouchUp(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id == _touchId)
        {
            e.Handled = true;
            PointerUp();
        }
    }

    private void OnStripLostTouchCapture(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id == _touchId)
        {
            PointerUp();
        }
    }

    private void OnStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        WheelScroll?.Invoke(e.Delta);
        NotifyActivity();
    }
}
