using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Search;
using DreamBooks.Pdf;
using Microsoft.Win32;

namespace DreamBooks.App.Views;

/// <summary>
/// The fading scroll rail on the document's right edge (UI-07, design B): a knob showing the position, a 40 px strip
/// where a tap jumps and a drag scrubs through the document (the position before is recorded for Back on the first
/// move of a press, FR-09), one large preview of the page at the knob while scrubbing, and the "12 / 48 · 125%"
/// indicator that opens a page field on tap. It appears while the user scrolls and near the edge and fades out after
/// the idle delay (<see cref="RailVisibilityState"/>; instant when Windows animations are off, opaque without shadow
/// when transparency effects are off, NFR-32).
/// </summary>
/// <remarks>
/// Nothing of the rail hit-tests: the pen always reaches the page under it and inks (FR-30). Fingers and the mouse
/// reach it through <see cref="DocumentView"/>, which routes their presses by position (<see cref="ZoneAt"/>) to
/// <see cref="PointerDown"/> / <see cref="OpenGoToPage"/>. The position fraction is the scroll offset over the
/// scrollable range (extent minus viewport), both from the view model, the only writer of the position (scrubbing
/// calls <see cref="DocumentViewportViewModel.SetScroll"/>). Nothing scrolls while a page is inking
/// (<see cref="CanScroll"/>). Only the page field, while open, takes keyboard focus. The marks' ticks (FR-101) are
/// one decorative layer over the bar and under the knob, placed with the knob's own mapping (see UpdateTicks).
/// </remarks>
public partial class ScrollRail : UserControl
{
    /// <summary>A touch contact wider or taller than this (about 16 mm) is a palm, not a fingertip.</summary>
    internal const double MaxFingerContactDip = 60;

    /// <summary>A press off the knob that moves more than this is not a tap.</summary>
    internal const double TapSlopDip = 10;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RailVisibilityState _state;
    private readonly DispatcherTimer _idleTimer;
    private DocumentViewportViewModel? _model;
    private MarksViewModel? _marks;
    private SearchViewModel? _search;
    private IReadOnlyList<SearchHit> _searchHits = [];
    private TickInputs _tickInputs;
    private RailTimings? _timings;
    private bool _rendering;
    private bool _dragging;
    private bool _tapPending;
    private bool _tapMoved;
    private double _tapY;
    private bool _hovering;
    private bool _editing;
    private bool _listening;
    private double _grab;
    private int _previewPage = -1;
    private CancellationTokenSource? _previewCancel;
    private bool _bubbleShown;
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
        Loaded += (_, _) =>
        {
            ApplyTransparency();
            if (!_listening)
            {
                _listening = true;
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            }

            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (_listening)
            {
                _listening = false;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            }

            CloseGoToPage(restoreFocus: false);
            if (_dragging)
            {
                EndDrag();
            }

            _tapPending = false;

            HideBubble();
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
                old.PropertyChanged -= OnModelPropertyChanged;
            }

            _model = value;
            if (value is not null)
            {
                value.PositionChanged += OnPositionChanged;
                value.LayoutChanged += OnLayoutChanged;
                value.PropertyChanged += OnModelPropertyChanged;
            }

            HideNow();
            UpdateKnob();
        }
    }

    /// <summary>The marks drawn as ticks on the track (FR-101); set by <see cref="DocumentView"/>.</summary>
    internal MarksViewModel? Marks
    {
        get => _marks;
        set
        {
            if (ReferenceEquals(value, _marks))
            {
                return;
            }

            if (_marks is { } old)
            {
                old.MarksRefreshed -= OnMarksRefreshed;
            }

            _marks = value;
            if (value is not null)
            {
                value.MarksRefreshed += OnMarksRefreshed;
            }

            UpdateTicks();
        }
    }

    /// <summary>
    /// The search whose results are drawn as ticks on the track (FR-105); set by <see cref="DocumentView"/>. The ticks
    /// follow its throttled <c>Refreshed</c> (raised on the UI thread), which also comes when the index is replaced (a
    /// new query) or cleared (no search, another document).
    /// </summary>
    internal SearchViewModel? Search
    {
        get => _search;
        set
        {
            if (ReferenceEquals(value, _search))
            {
                return;
            }

            if (_search is { } old)
            {
                old.Refreshed -= OnSearchRefreshed;
            }

            _search = value;
            if (value is not null)
            {
                value.Refreshed += OnSearchRefreshed;
            }

            OnSearchRefreshed(this, EventArgs.Empty);
        }
    }

    /// <summary>The preview of a page for the bubble (the bitmap coordinator's); a newer request cancels the older.</summary>
    internal Func<int, CancellationToken, Task<BitmapSource?>>? PreviewSource { get; set; }

    /// <summary>The reader's ink of a page, drawn over its preview (the view's page ink cache); cancelled like the preview.</summary>
    internal Func<int, CancellationToken, Task<PageInkPicture?>>? InkSource { get; set; }

    /// <summary>
    /// Whether the bubble's fades run: null follows the Windows animation setting (NFR-32); tests force it either way.
    /// </summary>
    internal bool? AnimationsOverride { get; set; }

    /// <summary>True from the press that shows the bubble to the release that hides it (it may still be fading out).</summary>
    internal bool IsBubbleShown => _bubbleShown;

    /// <summary>False while a page is inking: the rail then never moves the document under the pen.</summary>
    internal Func<bool>? CanScroll { get; set; }

    /// <summary>The Windows transparency setting (NFR-32); tests inject it.</summary>
    internal Func<bool> TransparencyEnabled { get; set; } = TransparencySetting.IsEnabled;

    internal RailVisibilityState State => _state;

    internal bool IsShown => _state.Phase != RailPhase.Hidden;

    internal bool IsDragging => _dragging;

    /// <summary>A finger or the mouse is down on the strip: dragging the knob, or a press off it that may be a tap.</summary>
    internal bool IsPressed => _dragging || _tapPending;

    internal bool IsEditing => _editing;

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

    /// <summary>
    /// The part of the rail under a point in rail coordinates while the rail is shown, otherwise none (the
    /// document's). For the pen only the knob counts (its bounds plus <c>RailKnobPenSlop</c>): anywhere else on the
    /// rail it writes. For a finger or the mouse the knob has a wider, taller zone reaching into the page, then the
    /// strip and the indicator.
    /// </summary>
    internal RailZone ZoneAt(Point point, PointerKind kind = PointerKind.Finger)
    {
        if (!IsShown || _model?.Layout is null || point.Y < 0 || point.Y > ActualHeight)
        {
            return RailZone.None;
        }

        if (kind == PointerKind.Pen)
        {
            return PenKnobZone().Contains(point) ? RailZone.Knob : RailZone.None;
        }

        if (FingerKnobZone().Contains(point))
        {
            return RailZone.Knob;
        }

        if (point.X >= ActualWidth - Strip.Width && point.X <= ActualWidth)
        {
            return RailZone.Strip;
        }

        return IndicatorButton.Visibility == Visibility.Visible && BoundsOf(IndicatorButton).Contains(point)
            ? RailZone.Indicator
            : RailZone.None;
    }

    /// <summary>True when a point (rail coordinates) is on the open page field.</summary>
    internal bool IsOnGoToPageBox(Point point) => _editing && BoundsOf(GoToPageBox).Contains(point);

    /// <summary>A point in rail coordinates as a y on the track (what <see cref="PointerDown"/> takes).</summary>
    internal double TrackYAt(Point point) => TranslatePoint(point, Track).Y;

    /// <summary>The mouse (not the pen) is over the strip or the indicator: the rail stays while it is.</summary>
    internal void SetHover(bool hovering)
    {
        if (hovering != _hovering)
        {
            _hovering = hovering;
            UpdateHeld();
        }
    }

    /// <summary>The user scrolled, or a finger or the mouse is near the edge: the rail shows, the idle delay restarts.</summary>
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

        _tapPending = false; // a press waiting to be a tap is dropped

        _hovering = false;
        _state.Hide();
        Refresh();
    }

    /// <summary>
    /// True for a touch contact larger than a fingertip (a palm or the side of the hand coming down to write), when the
    /// digitizer reports the contact size; such a contact never operates the rail (Windows' palm rejection only works
    /// while the pen hovers).
    /// </summary>
    internal static bool IsPalmContact(Size contact) =>
        !contact.IsEmpty && (contact.Width > MaxFingerContactDip || contact.Height > MaxFingerContactDip);

    /// <summary>
    /// A finger or the mouse goes down on the strip at <paramref name="trackY"/> (DIPs from the track's top). On the
    /// knob it grabs it where it was touched and drags. Elsewhere nothing moves yet: the press jumps there when it lifts
    /// without moving (<see cref="PointerUp"/>), so a hand landing on the rail does not throw the document away. Nothing
    /// (false) while a page is inking or without a document.
    /// </summary>
    /// <remarks>
    /// With <paramref name="onKnob"/> (the press is in the knob's zone, <see cref="ZoneAt"/>) it grabs the knob wherever
    /// in the zone it landed, without a jump.
    /// </remarks>
    internal bool PointerDown(double trackY, bool onKnob = false)
    {
        if (IsPressed || _model is not { Layout: not null } model || CanScroll?.Invoke() == false)
        {
            return false;
        }

        NotifyActivity();
        var knobTop = KnobOffset.Y;
        if (!onKnob && (trackY < knobTop || trackY > knobTop + KnobHeight))
        {
            _tapPending = true;
            _tapMoved = false;
            _tapY = trackY;
            UpdateHeld();
            return true;
        }

        _dragging = true;
        UpdateHeld();
        model.BeginRailDrag();
        _grab = trackY - knobTop;
        ShowBubble();
        ScrubTo(trackY);
        return true;
    }

    /// <summary>The pointer moved: a drag follows continuously; a press off the knob that moves is no longer a tap.</summary>
    internal void PointerMove(double trackY)
    {
        if (_dragging)
        {
            ScrubTo(trackY);
        }
        else if (_tapPending && Math.Abs(trackY - _tapY) > TapSlopDip)
        {
            _tapMoved = true;
        }
    }

    /// <summary>
    /// The press ends: a drag's bubble goes at once; a press off the knob that did not move jumps there (recorded for
    /// Back, FR-09). The rail fades out after the idle delay.
    /// </summary>
    internal void PointerUp()
    {
        if (_tapPending)
        {
            var jump = !_tapMoved;
            ClearTap();
            if (jump && _model is { Layout: not null } model && CanScroll?.Invoke() != false)
            {
                model.BeginRailDrag();
                _grab = KnobHeight / 2;
                ScrubTo(_tapY);
                model.EndRailDrag();
            }

            UpdateKnob();
            return;
        }

        if (!_dragging)
        {
            return;
        }

        EndDrag();
        UpdateKnob();
    }

    /// <summary>The press lost its capture (another window, a cancelled touch): a drag ends where it is, a tap is dropped.</summary>
    internal void PointerCancel()
    {
        if (_tapPending)
        {
            ClearTap();
        }
        else if (_dragging)
        {
            EndDrag();
        }

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

    /// <summary>Esc, or a press anywhere outside the field: closes it without moving.</summary>
    internal void CancelGoToPage() => CloseGoToPage(restoreFocus: true);

    /// <summary>
    /// Opaque theme brushes and no shadow when Windows transparency effects are off (NFR-32); the translucent ones
    /// otherwise. The values live in the theme dictionary.
    /// </summary>
    internal void ApplyTransparency()
    {
        var transparent = TransparencyEnabled();
        var suffix = transparent ? string.Empty : "Opaque";
        TrackBar.SetResourceReference(Border.BackgroundProperty, "RailTrackBrush" + suffix);
        Ticks.SetResourceReference(RailTickLayer.FillProperty, "RailMarkTickBrush" + suffix);
        NoteTicks.SetResourceReference(RailTickLayer.FillProperty, "RailNoteTickBrush" + suffix);
        SearchTicks.SetResourceReference(RailTickLayer.FillProperty, "RailSearchTickBrush" + suffix);
        PreviewBubble.SetResourceReference(Border.BackgroundProperty, "RailPreviewBackgroundBrush" + suffix);
        if (transparent)
        {
            PreviewBubble.SetResourceReference(EffectProperty, "RailPreviewShadow");
        }
        else
        {
            PreviewBubble.Effect = null;
        }
    }

    private static TimeSpan Require(Func<string, object?> find, string key) =>
        find(key) is TimeSpan value
            ? value
            : throw new InvalidOperationException($"The theme resource '{key}' is missing.");

    private static double Scrollable(DocumentViewportViewModel model, DocumentLayout layout) =>
        Math.Max(0, layout.ExtentHeightDip(model.Position.Zoom) - model.ViewportHeightDip);

    private Rect BoundsOf(FrameworkElement element) =>
        element.ActualWidth > 0 && element.ActualHeight > 0
            ? element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize))
            : Rect.Empty;

    private double Token(string key) => TryFindResource(key) is double value ? value : 0;

    /// <summary>Where the pen can grab the knob: its bounds plus <c>RailKnobPenSlop</c> all round (rail coordinates).</summary>
    private Rect PenKnobZone()
    {
        var knob = BoundsOf(Knob);
        if (knob.IsEmpty)
        {
            return Rect.Empty;
        }

        var slop = Token("RailKnobPenSlop");
        knob.Inflate(slop, slop);
        return knob;
    }

    /// <summary>
    /// Where a finger or the mouse grabs the knob: from the right edge at least <c>RailKnobFingerZoneWidth</c> to the
    /// left (into the page) and <c>RailKnobFingerZonePadding</c> above and below it, at least
    /// <c>RailKnobFingerZoneMinHeight</c> tall (rail coordinates).
    /// </summary>
    private Rect FingerKnobZone()
    {
        var knob = BoundsOf(Knob);
        if (knob.IsEmpty)
        {
            return Rect.Empty;
        }

        var width = Math.Max(Token("RailKnobFingerZoneWidth"), Strip.Width);
        var height = Math.Max(knob.Height + (2 * Token("RailKnobFingerZonePadding")), Token("RailKnobFingerZoneMinHeight"));
        var centreY = knob.Top + (knob.Height / 2);
        return new Rect(ActualWidth - width, centreY - (height / 2), width, height);
    }

    /// <summary>The pen's grip follows the knob.</summary>
    private void UpdateGrip()
    {
        var zone = PenKnobZone();
        if (zone.IsEmpty)
        {
            KnobGrip.Width = 0;
            KnobGrip.Height = 0;
            return;
        }

        Canvas.SetLeft(KnobGrip, zone.Left);
        Canvas.SetTop(KnobGrip, zone.Top);
        KnobGrip.Width = zone.Width;
        KnobGrip.Height = zone.Height;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyTransparency); // raised on another thread

    private void PrepareState()
    {
        _timings ??= TimingsFrom(key => TryFindResource(key));
        _state.Timings = _timings.Value;
        _state.ReducedMotion = !SystemParameters.ClientAreaAnimation;
    }

    private void UpdateHeld()
    {
        PrepareState();
        _state.IsHeld = IsPressed || _hovering || _editing;
        Refresh();
    }

    /// <summary>Applies the state: opacity, the bubble, and when to look again.</summary>
    private void Refresh()
    {
        var phase = _state.Phase;
        Opacity = _state.Opacity;
        KnobGrip.IsHitTestVisible = phase != RailPhase.Hidden && _model?.Layout is not null; // the pen inks when hidden
        UpdateGrip();
        if (phase == RailPhase.Hidden)
        {
            HideBubble();
        }
        else
        {
            UpdateTicks(); // brought up to date as it shows (a cheap check when nothing changed)
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

    private void OnPositionChanged(object? sender, EventArgs e) => UpdateKnob();

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        HideNow();
        UpdateKnob();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewportViewModel.ViewportHeightDip) or null or "")
        {
            UpdateTicks(); // the window's height in points is part of the mapping, even when the position stays
        }
    }

    private void OnMarksRefreshed(object? sender, EventArgs e) => UpdateTicks();

    /// <summary>
    /// The search's results were re-read (throttled), or its index replaced or cleared: a new snapshot of its hits (a
    /// truncated search keeps what it found), drawn now if the rail is shown, else when it shows.
    /// </summary>
    private void OnSearchRefreshed(object? sender, EventArgs e)
    {
        _searchHits = _search?.Index?.AllHits() ?? [];
        UpdateTicks();
    }

    /// <summary>
    /// The ticks at the marks' positions on the track (FR-101), with the knob's own mapping
    /// (<see cref="RailTickLayout"/>): each tick is centred where the knob's centre is when the view shows its mark, so
    /// the knob covers it there. Only while the rail is shown (hidden, nothing is recomputed; it catches up as it
    /// shows). Recomputed only when an input changed: the marks (a new list at each throttled refresh of the marks
    /// list, <see cref="MarksViewModel.RefreshInterval"/>), the layout, the zoom or window height, the track length.
    /// </summary>
    private void UpdateTicks()
    {
        if (!IsShown)
        {
            return;
        }

        var marks = _marks?.Marks ?? [];
        var notes = _marks?.Notes ?? [];
        var layout = _model?.Layout;
        var window = layout is not null ? _model!.WindowHeightPt : 0;
        var hits = _searchHits;
        var inputs = new TickInputs(marks, notes, hits, layout, window, TrackLength, KnobHeight);
        if (inputs == _tickInputs)
        {
            return;
        }

        _tickInputs = inputs;
        if (layout is null)
        {
            Ticks.SetCentres([]);
            NoteTicks.SetCentres([]);
            SearchTicks.SetCentres([]);
            return;
        }

        Ticks.SetCentres(marks.Count == 0 ? [] : Centres(RailTickLayout.Positions(marks, layout, window, inputs.TrackLength)));
        NoteTicks.SetCentres(notes.Count == 0 ? [] : Centres(RailTickLayout.NotePositions(notes, layout, window, inputs.TrackLength)));
        SearchTicks.SetCentres(hits.Count == 0 ? [] : Centres(RailTickLayout.SearchPositions(hits, layout, window, inputs.TrackLength)));
    }

    /// <summary>Knob tops on the track to tick centres: the knob covers a tick when the view shows its mark or note.</summary>
    private double[] Centres(IReadOnlyList<double> positions)
    {
        var half = KnobHeight / 2;
        var centres = new double[positions.Count];
        for (var i = 0; i < centres.Length; i++)
        {
            centres[i] = positions[i] + half;
        }

        return centres;
    }

    /// <summary>The knob at the position's fraction of the track (not while dragging: then it follows the pointer).</summary>
    private void UpdateKnob()
    {
        UpdateTicks(); // a zoom or a resize changes the mapping the ticks share with the knob
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
        UpdateGrip();
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
        var fraction = length > 0 ? top / length : 0;
        KnobOffset.Y = top;
        UpdateGrip();
        var (x, _) = layout.ToOffsets(model.Position, model.ViewportWidthDip, model.ViewportHeightDip);
        model.SetScroll(x, fraction * Scrollable(model, layout));
        UpdatePreview(layout, layout.PageAt(fraction * layout.TotalHeightPt));
        PlaceBubble();
    }

    private void ClearTap()
    {
        _tapPending = false;
        _tapMoved = false;
        UpdateHeld();
    }

    private void EndDrag()
    {
        _dragging = false;
        _model?.EndRailDrag();
        HideBubble(); // the preview shows only while the finger or the mouse is down
        UpdateHeld();
    }

    /// <summary>
    /// The bubble shows the page at the knob's fraction of the document: its number at once, then its preview with the
    /// reader's ink. The page shown so far stays behind until the new one fades in over it, so scrubbing never flashes
    /// white; the very first page shows the white placeholder until its preview arrives.
    /// </summary>
    private void UpdatePreview(DocumentLayout layout, int page)
    {
        if (!_bubbleShown || page == _previewPage || page < 0 || page >= layout.PageCount)
        {
            return;
        }

        _previewPage = page;
        PreviewText.Text = string.Format(CultureInfo.CurrentCulture, Strings.RailPreviewPageFormat, page + 1);
        if (PreviewImage.Source is not null)
        {
            // The page shown goes behind, fully visible, until the new one has faded in over it.
            PreviewBackImage.Source = PreviewImage.Source;
            PreviewBackInk.Picture = PreviewInk.Picture;
            SetOpacityNow(PreviewBack, 1);
        }

        PreviewImage.Source = null;
        PreviewInk.Picture = null;
        SetOpacityNow(PreviewFront, 0);
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

        CancelPreview();
        _previewCancel = new CancellationTokenSource();
        _ = LoadPreviewAsync(page, _previewCancel.Token);
        _ = LoadInkAsync(page, _previewCancel.Token);
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

        if (ct.IsCancellationRequested || page != _previewPage || bitmap is null)
        {
            return;
        }

        // The clean page fades in over the previous one (with its ink, if it arrived first); then the previous goes.
        PreviewImage.Source = bitmap;
        Fade(PreviewFront, 1, "RailPreviewCrossFadeDuration", () =>
        {
            if (page == _previewPage && ReferenceEquals(PreviewImage.Source, bitmap))
            {
                ClearBack();
            }
        });
    }

    /// <summary>
    /// The reader's ink of the page, drawn over its preview when it arrives: it is part of the front layer, which shows
    /// only once the page's preview does, so the clean page comes first and the ink joins it without flicker.
    /// </summary>
    private async Task LoadInkAsync(int page, CancellationToken ct)
    {
        if (InkSource is not { } source)
        {
            return;
        }

        PageInkPicture? ink;
        try
        {
            ink = await source(page, ct);
        }
#pragma warning disable CA1031 // The bubble shows the page without ink rather than failing (NFR-11).
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        if (!ct.IsCancellationRequested && page == _previewPage)
        {
            PreviewInk.Picture = ink;
        }
    }

    private void CancelPreview()
    {
        _previewCancel?.Cancel();
        _previewCancel?.Dispose();
        _previewCancel = null;
    }

    /// <summary>The bubble appears, fading in (instant when Windows animations are off, NFR-32).</summary>
    private void ShowBubble()
    {
        if (_bubbleShown)
        {
            return;
        }

        _bubbleShown = true;
        if (PreviewBubble.Visibility != Visibility.Visible)
        {
            SetOpacityNow(PreviewBubble, 0);
            PreviewBubble.Visibility = Visibility.Visible;
        }

        Fade(PreviewBubble, 1, "RailPreviewFadeInDuration");
    }

    /// <summary>The bubble goes: requests stop at once, it fades out, then it is collapsed and emptied.</summary>
    private void HideBubble()
    {
        CancelPreview();
        _previewPage = -1;
        if (!_bubbleShown)
        {
            return;
        }

        _bubbleShown = false;
        Fade(PreviewBubble, 0, "RailPreviewFadeOutDuration", () =>
        {
            if (_bubbleShown)
            {
                return; // shown again meanwhile
            }

            PreviewBubble.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;
            PreviewInk.Picture = null;
            SetOpacityNow(PreviewFront, 0);
            ClearBack();
        });
    }

    private void ClearBack()
    {
        PreviewBackImage.Source = null;
        PreviewBackInk.Picture = null;
    }

    private bool AnimationsOn => AnimationsOverride ?? SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Animates <paramref name="element"/>'s opacity from where it is to <paramref name="to"/> over the theme duration
    /// <paramref name="durationKey"/>, easing out (<c>RailPreviewEase</c>); at once when animations are off. Then runs
    /// <paramref name="completed"/> (not when another fade replaced this one: callers check their state again anyway).
    /// </summary>
    private void Fade(UIElement element, double to, string durationKey, Action? completed = null)
    {
        if (!AnimationsOn || TryFindResource(durationKey) is not TimeSpan duration || duration <= TimeSpan.Zero)
        {
            SetOpacityNow(element, to);
            completed?.Invoke();
            return;
        }

        var from = element.Opacity; // the current value, animated or not
        element.Opacity = to; // the base value, kept once the animation is removed
        var animation = new DoubleAnimation(from, to, new Duration(duration))
        {
            EasingFunction = TryFindResource("RailPreviewEase") as IEasingFunction,
        };
        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }

        element.BeginAnimation(OpacityProperty, animation);
    }

    private static void SetOpacityNow(UIElement element, double value)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = value;
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
        var hadFocus = GoToPageBox.IsKeyboardFocusWithin;
        GoToPageBox.Visibility = Visibility.Collapsed;
        IndicatorButton.Visibility = Visibility.Visible;
        UpdateHeld();
        if (restoreFocus && hadFocus && before is UIElement { IsVisible: true } element)
        {
            element.Focus();
        }
        else if (hadFocus)
        {
            Keyboard.ClearFocus(); // the keys go back to the window (and the navigation keys to the document)
        }
    }

    private void OnIndicatorClick(object sender, RoutedEventArgs e) => OpenGoToPage(); // UI Automation's Invoke

    /// <summary>What the ticks were last drawn from; the lists compare by reference (each refresh makes a new one).</summary>
    private readonly record struct TickInputs(
        IReadOnlyList<Mark>? Marks,
        IReadOnlyList<Note>? Notes,
        IReadOnlyList<SearchHit> SearchHits,
        DocumentLayout? Layout,
        double WindowHeightPt,
        double TrackLength,
        double KnobHeight);

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
            CloseGoToPage(restoreFocus: false); // focus moved elsewhere: closed, nothing moves
        }
    }
}
