using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// Where the reader is in the continuous multi-page view (FR-04, FR-06, FR-07, FR-09, FR-10): the layout of the
/// whole document, the scroll position as a <see cref="ViewPosition"/> (page, offset inside the page in points,
/// zoom), the visible and retained pages, the current page (the one under the viewport centre), the back stack
/// and fit-width mode. Pure state: the view reports its size and scroll offsets and applies
/// <see cref="Position"/> through <see cref="DocumentLayout.ToOffsets"/>.
/// </summary>
/// <remarks>
/// The current page is pushed to the session (<see cref="DocumentSession.SetCurrentPageFromView"/>) 150 ms after
/// the view stops moving, so scrolling through a book never loads strokes or rewrites the journal for every page
/// passed; explicit jumps (go to page, back, Home/End, undo reveal) push it at once. The zoom is
/// <see cref="ZoomViewModel.Zoom"/>; <see cref="Position"/> always carries the same value.
/// </remarks>
public sealed class DocumentViewportViewModel : ObservableObject
{
    /// <summary>Delay between the last scroll and reporting the current page to the session.</summary>
    public static readonly TimeSpan CurrentPageDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Page Up / Page Down move by this fraction of the viewport height, so a line of context stays.</summary>
    public const double PageStepFraction = 0.9;

    /// <summary>Double tap outside fit-width mode zooms to this factor (FR-07).</summary>
    public const double DoubleTapZoom = 2.0;

    private const double _sameOffsetDip = 0.5;
    private const double _zoomEpsilon = 1e-9;

    private readonly ZoomViewModel _zoom;
    private readonly IDelayScheduler _scheduler;
    private DocumentSession? _session;
    private DocumentLayout? _layout;
    private ViewPosition _position = new(0, 0, 0, 1);
    private double _viewportWidthDip;
    private double _viewportHeightDip;
    private int _currentPage;
    private IReadOnlyList<int> _visiblePages = [];
    private (int First, int Last) _retainedRange = (0, -1);
    private IDisposable? _pendingReport;
    private bool _applyingZoom;
    private bool _railDrag;

    public DocumentViewportViewModel(ZoomViewModel zoom, IDelayScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(zoom);
        ArgumentNullException.ThrowIfNull(scheduler);
        _zoom = zoom;
        _scheduler = scheduler;
        _zoom.PropertyChanged += OnZoomPropertyChanged;

        BackCommand = new RelayCommand(GoBack, () => History.CanGoBack);
        GoToPageCommand = new AsyncRelayCommand<int>(GoToPageAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        PageUpCommand = new RelayCommand(() => PageStep(-1));
        PageDownCommand = new RelayCommand(() => PageStep(1));
        HomeCommand = new RelayCommand(GoHome);
        EndCommand = new RelayCommand(GoEnd);
        History.Changed += (_, _) => BackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised after every change of <see cref="Position"/>, including the ones the view reported itself.</summary>
    public event EventHandler? PositionChanged;

    /// <summary>Raised when a document's layout replaces the previous one (open, swap, close).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Page frames of the open document, built from every page's size at open; null without a document.</summary>
    public DocumentLayout? Layout => _layout;

    public int PageCount => _layout?.PageCount ?? 0;

    /// <summary>The scroll position: page of the top edge, offset inside it and column x in points, zoom.</summary>
    public ViewPosition Position => _position;

    /// <summary>Viewport width in DIPs, set by the view; in fit-width mode a change recomputes the zoom.</summary>
    public double ViewportWidthDip
    {
        get => _viewportWidthDip;
        set
        {
            var width = double.IsFinite(value) && value > 0 ? value : 0;
            if (SetProperty(ref _viewportWidthDip, width))
            {
                OnViewportResized();
            }
        }
    }

    /// <summary>Viewport height in DIPs, set by the view.</summary>
    public double ViewportHeightDip
    {
        get => _viewportHeightDip;
        set
        {
            var height = double.IsFinite(value) && value > 0 ? value : 0;
            if (SetProperty(ref _viewportHeightDip, height))
            {
                OnViewportResized();
            }
        }
    }

    /// <summary>True while the zoom follows the viewport width (fit to the widest page).</summary>
    public bool IsFitWidth => _zoom.IsFitWidth;

    /// <summary>Pages intersecting the viewport, top to bottom; empty without a document.</summary>
    public IReadOnlyList<int> VisiblePages => _visiblePages;

    /// <summary>The visible pages plus two on each side: the pages that keep a surface (NFR-05).</summary>
    public (int First, int Last) RetainedRange => _retainedRange;

    /// <summary>0-based page under the viewport centre (or the target of the last jump).</summary>
    public int CurrentPage => _currentPage;

    /// <summary>"12 / 48 · 125%"; empty without a document.</summary>
    public string IndicatorText => _layout is null
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            Strings.PageIndicatorFormat,
            _currentPage + 1,
            PageCount,
            Math.Round(_position.Zoom * 100));

    /// <summary>Positions to go back to after a jump (FR-09).</summary>
    public PositionHistory History { get; } = new();

    /// <summary>Returns to the position before the last jump; enabled while <see cref="History"/> has one.</summary>
    public IRelayCommand BackCommand { get; }

    /// <summary>Jumps to a 1-based page number (clamped), recording the current position for Back.</summary>
    public IAsyncRelayCommand<int> GoToPageCommand { get; }

    public IRelayCommand PageUpCommand { get; }

    public IRelayCommand PageDownCommand { get; }

    /// <summary>Jumps to the top of the document, recording the current position for Back.</summary>
    public IRelayCommand HomeCommand { get; }

    /// <summary>Jumps to the end of the document, recording the current position for Back.</summary>
    public IRelayCommand EndCommand { get; }

    /// <summary>
    /// Loads and shows a page after a go-to-page jump (set by <see cref="MainViewModel"/>: the session's
    /// <see cref="DocumentSession.GoToPageAsync"/> with its error handling). Without it the page is only reported
    /// as current.
    /// </summary>
    internal Func<int, Task>? PageNavigator { get; set; }

    /// <summary>The view scrolled: the new offsets in DIPs become the position. Equal offsets are ignored.</summary>
    public void SetScroll(double offsetXDip, double offsetYDip)
    {
        if (_layout is not { } layout || !double.IsFinite(offsetXDip) || !double.IsFinite(offsetYDip))
        {
            return;
        }

        if (HasViewport)
        {
            var (x, y) = layout.ToOffsets(_position, _viewportWidthDip, _viewportHeightDip);
            if (Math.Abs(x - offsetXDip) < _sameOffsetDip && Math.Abs(y - offsetYDip) < _sameOffsetDip)
            {
                return;
            }
        }

        MoveTo(layout.ToPosition(Math.Max(0, offsetXDip), Math.Max(0, offsetYDip), _viewportWidthDip, _position.Zoom), CurrentPageReport.Debounced);
    }

    /// <summary>
    /// Pinch or Ctrl+wheel: multiplies the zoom by <paramref name="factor"/> keeping the document point under
    /// <paramref name="anchorDip"/> (viewport DIPs) still. Ignored while <see cref="ZoomViewModel.IsLocked"/>.
    /// Turns fit-width mode off.
    /// </summary>
    public void ZoomAt(double factor, Point anchorDip)
    {
        if (_zoom.IsLocked || _layout is null || !double.IsFinite(factor) || factor <= 0)
        {
            return;
        }

        ApplyZoom(_position.Zoom * factor, anchorDip.X, anchorDip.Y, fit: false);
    }

    /// <summary>
    /// Double tap (FR-07): outside fit-width mode, fits the widest page to the width; in fit-width mode, zooms to
    /// 200%. The vertical anchor is the tap (the viewport centre when null); horizontally the column is centred.
    /// Ignored while <see cref="ZoomViewModel.IsLocked"/>.
    /// </summary>
    public void ToggleFitWidth(double? anchorYDip)
    {
        if (_zoom.IsLocked || _layout is not { } layout)
        {
            return;
        }

        var anchorY = anchorYDip is { } y && double.IsFinite(y) ? y : _viewportHeightDip / 2;
        if (_zoom.IsFitWidth)
        {
            ApplyZoom(DoubleTapZoom, _viewportWidthDip / 2, anchorY, fit: false);
        }
        else if (_viewportWidthDip > 0)
        {
            ApplyZoom(layout.FitWidthZoom(_viewportWidthDip), _viewportWidthDip / 2, anchorY, fit: true);
        }
    }

    /// <summary>
    /// Brings a page's top into view and makes it current at once (no debounce), e.g. to reveal the page an undo
    /// changed. With <paramref name="recordHistory"/> the previous position is pushed on <see cref="History"/>.
    /// </summary>
    public void ScrollToPage(int pageIndex, bool recordHistory) =>
        JumpToPage(pageIndex, recordHistory, CurrentPageReport.Immediate);

    /// <summary>A rail drag starts: the position before it is recorded for Back, once per drag.</summary>
    public void BeginRailDrag()
    {
        if (_railDrag || _layout is null)
        {
            return;
        }

        _railDrag = true;
        History.Push(_position);
    }

    public void EndRailDrag() => _railDrag = false;

    /// <summary>
    /// Makes a document (or none) the viewed one: its layout, and the position to show. A restored position is
    /// clamped to the document; its zoom is applied, or, when it was taken in fit-width mode
    /// (<paramref name="restoredFitWidth"/>), fit-width mode comes back and the zoom is recomputed for this viewport
    /// width keeping the top page and offset. Without one the view starts at the top of <paramref name="startPage"/>
    /// in fit-width mode. Clears the history. The current page is the centre page; the caller loads it.
    /// </summary>
    internal void Attach(
        DocumentSession? session, DocumentLayout? layout, ViewPosition? restored, int startPage = 0, bool restoredFitWidth = false)
    {
        CancelPendingReport();
        _railDrag = false;
        _session = session;
        _layout = layout;
        History.Clear();
        if (layout is null)
        {
            _position = new ViewPosition(0, 0, 0, _zoom.Zoom);
            _currentPage = 0;
            RaiseLayoutChanged();
            Refresh(CurrentPageReport.None, forceCurrent: 0);
            return;
        }

        if (restored is { } stored)
        {
            if (double.IsFinite(stored.Zoom) && stored.Zoom > 0)
            {
                _applyingZoom = true;
                try
                {
                    if (restoredFitWidth)
                    {
                        _zoom.ApplyFitZoom(stored.Zoom); // until the width is known, the zoom it had
                    }
                    else
                    {
                        _zoom.Zoom = stored.Zoom; // clamped by the zoom model; turns fit-width mode off
                    }
                }
                finally
                {
                    _applyingZoom = false;
                }
            }

            _position = Clamp(layout, stored with { Zoom = _zoom.Zoom });
            if (restoredFitWidth && _zoom.IsFitWidth && _viewportWidthDip > 0 && !_zoom.IsLocked)
            {
                _position = FitKeepingTop(layout);
            }

            _position = Normalize(_position);
        }
        else
        {
            _zoom.SetFitWidthMode(true);
            var page = Math.Clamp(startPage, 0, layout.PageCount - 1);
            _position = new ViewPosition(page, 0, layout.ColumnWidthPt / 2, _zoom.Zoom);
            if (_viewportWidthDip > 0 && !_zoom.IsLocked)
            {
                _position = FitKeepingTop(layout);
            }

            _position = Normalize(_position);
        }

        RaiseLayoutChanged();
        Refresh(CurrentPageReport.None, forceCurrent: null);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Go to page (1-based, clamped): a jump, recorded for Back; the page is loaded by <see cref="PageNavigator"/>.</summary>
    internal Task GoToPageAsync(int pageNumber) =>
        _layout is { } layout
            ? JumpToPageAsync(Math.Clamp(pageNumber - 1, 0, layout.PageCount - 1), recordHistory: true)
            : Task.CompletedTask;

    /// <summary>
    /// Previous / Next page: relative to <see cref="CurrentPage"/>, not recorded for Back (FR-09 records only jumps),
    /// and nothing at all before the first or after the last page.
    /// </summary>
    internal Task StepPageAsync(int delta)
    {
        var target = _currentPage + delta;
        return _layout is { } layout && target >= 0 && target < layout.PageCount
            ? JumpToPageAsync(target, recordHistory: false)
            : Task.CompletedTask;
    }

    /// <summary>Scrolls at once, then lets <see cref="PageNavigator"/> load and show the page (or reports it as current).</summary>
    private async Task JumpToPageAsync(int index, bool recordHistory)
    {
        JumpToPage(index, recordHistory, PageNavigator is null ? CurrentPageReport.Immediate : CurrentPageReport.None);
        if (PageNavigator is { } navigate)
        {
            await navigate(index);
        }
    }

    private bool HasViewport => _viewportWidthDip > 0 && _viewportHeightDip > 0;

    private void JumpToPage(int pageIndex, bool recordHistory, CurrentPageReport report)
    {
        if (_layout is not { } layout)
        {
            return;
        }

        var index = Math.Clamp(pageIndex, 0, layout.PageCount - 1);
        if (recordHistory)
        {
            History.Push(_position);
        }

        SetPosition(Normalize(new ViewPosition(index, 0, _position.ColumnXPt, _position.Zoom)));
        Refresh(report, forceCurrent: index);
    }

    private void GoBack()
    {
        if (_layout is not { } layout || !History.TryPop(out var previous))
        {
            return;
        }

        // The zoom stays as it is now: going back restores the place, not the magnification.
        SetPosition(Normalize(Clamp(layout, previous with { Zoom = _position.Zoom })));
        Refresh(CurrentPageReport.Immediate, forceCurrent: null);
    }

    private void GoHome() => JumpToPage(0, recordHistory: true, CurrentPageReport.Immediate);

    private void GoEnd()
    {
        if (_layout is not { } layout)
        {
            return;
        }

        var last = layout.PageCount - 1;
        History.Push(_position);
        SetPosition(Normalize(new ViewPosition(last, PageHeightPt(layout, last), _position.ColumnXPt, _position.Zoom)));
        Refresh(CurrentPageReport.Immediate, forceCurrent: HasViewport ? null : last);
    }

    private void PageStep(int direction)
    {
        if (_layout is not { } layout)
        {
            return;
        }

        if (!HasViewport)
        {
            JumpToPage(_currentPage + direction, recordHistory: false, CurrentPageReport.Immediate);
            return;
        }

        var (x, y) = layout.ToOffsets(_position, _viewportWidthDip, _viewportHeightDip);
        var target = Math.Max(0, y + (direction * _viewportHeightDip * PageStepFraction));
        MoveTo(layout.ToPosition(x, target, _viewportWidthDip, _position.Zoom), CurrentPageReport.Debounced);
    }

    private void MoveTo(ViewPosition position, CurrentPageReport report)
    {
        SetPosition(Normalize(position));
        Refresh(report, forceCurrent: null);
    }

    private void SetPosition(ViewPosition position)
    {
        if (position == _position)
        {
            return; // nothing moved: no event, no settle timer restarted
        }

        _position = position;
        OnPropertyChanged(nameof(Position));
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes the zoom keeping the document point under the anchor still; fit says which mode follows.</summary>
    private void ApplyZoom(double newZoom, double anchorXDip, double anchorYDip, bool fit)
    {
        if (_layout is not { } layout)
        {
            return;
        }

        var zoom = Math.Clamp(newZoom, LayoutConstants.MinZoom, LayoutConstants.MaxZoom);
        _applyingZoom = true;
        try
        {
            if (fit)
            {
                _zoom.ApplyFitZoom(zoom);
            }
            else
            {
                _zoom.Zoom = zoom; // manual: fit-width mode off
            }
        }
        finally
        {
            _applyingZoom = false;
        }

        zoom = _zoom.Zoom;
        OnPropertyChanged(nameof(IsFitWidth));
        if (Math.Abs(zoom - _position.Zoom) < _zoomEpsilon && !fit)
        {
            return; // at a zoom limit: nothing to move
        }

        var moved = layout.ZoomAt(_position, zoom, anchorXDip, anchorYDip, _viewportWidthDip);
        if (fit)
        {
            moved = moved with { ColumnXPt = layout.ColumnWidthPt / 2 };
        }

        MoveTo(moved, CurrentPageReport.Debounced);
    }

    /// <summary>
    /// Fit width after a resize, at open or when the zoom lock is released: applies the fit zoom and returns the
    /// current position at that zoom, with the same top page and offset and the column centred.
    /// </summary>
    private ViewPosition FitKeepingTop(DocumentLayout layout)
    {
        var zoom = layout.FitWidthZoom(_viewportWidthDip);
        _applyingZoom = true;
        try
        {
            _zoom.ApplyFitZoom(zoom);
        }
        finally
        {
            _applyingZoom = false;
        }

        return _position with { Zoom = _zoom.Zoom, ColumnXPt = layout.ColumnWidthPt / 2 };
    }

    private void OnViewportResized()
    {
        if (_layout is not { } layout)
        {
            return;
        }

        var position = _zoom.IsFitWidth && _viewportWidthDip > 0 && !_zoom.IsLocked ? FitKeepingTop(layout) : _position;
        MoveTo(position, CurrentPageReport.Debounced);
    }

    /// <summary>The zoom lock was released: a fit-width recompute skipped while a stroke was live happens now.</summary>
    private void CatchUpFitWidth()
    {
        if (_zoom.IsLocked || !_zoom.IsFitWidth || _viewportWidthDip <= 0 || _layout is not { } layout
            || Math.Abs(layout.FitWidthZoom(_viewportWidthDip) - _position.Zoom) < _zoomEpsilon)
        {
            return;
        }

        MoveTo(FitKeepingTop(layout), CurrentPageReport.Debounced);
    }

    /// <summary>A zoom change from elsewhere (zoom buttons, the old single-page view): anchored at the viewport centre.</summary>
    private void OnZoomPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ZoomViewModel.IsFitWidth))
        {
            OnPropertyChanged(nameof(IsFitWidth));
            return;
        }

        if (e.PropertyName == nameof(ZoomViewModel.IsLocked))
        {
            CatchUpFitWidth();
            return;
        }

        if (e.PropertyName != nameof(ZoomViewModel.Zoom) || _applyingZoom || _layout is not { } layout)
        {
            return;
        }

        if (_zoom.IsFitWidth && _viewportWidthDip > 0)
        {
            // A fit request computed elsewhere (e.g. from one page's width): fit the widest page instead.
            var fit = layout.FitWidthZoom(_viewportWidthDip);
            if (Math.Abs(fit - _zoom.Zoom) > _zoomEpsilon)
            {
                _applyingZoom = true;
                try
                {
                    _zoom.ApplyFitZoom(fit);
                }
                finally
                {
                    _applyingZoom = false;
                }
            }
        }

        if (Math.Abs(_zoom.Zoom - _position.Zoom) < _zoomEpsilon)
        {
            return;
        }

        var moved = layout.ZoomAt(_position, _zoom.Zoom, _viewportWidthDip / 2, _viewportHeightDip / 2, _viewportWidthDip);
        if (_zoom.IsFitWidth)
        {
            moved = moved with { ColumnXPt = layout.ColumnWidthPt / 2 };
        }

        MoveTo(moved, CurrentPageReport.Debounced);
    }

    /// <summary>Within the scrollable extent when the viewport size is known; otherwise unchanged.</summary>
    private ViewPosition Normalize(ViewPosition position)
    {
        if (_layout is not { } layout || !HasViewport)
        {
            return position;
        }

        var (x, y) = layout.ToOffsets(position, _viewportWidthDip, _viewportHeightDip);
        return layout.ToPosition(x, y, _viewportWidthDip, position.Zoom);
    }

    /// <summary>
    /// Best effort for any stored or journalled numbers: a valid page, an offset inside it (or in the gap above it,
    /// where a top edge between two pages belongs to the page below), a valid zoom.
    /// </summary>
    private ViewPosition Clamp(DocumentLayout layout, ViewPosition position)
    {
        var page = Math.Clamp(position.PageIndex, 0, layout.PageCount - 1);
        var y = double.IsFinite(position.YPt) ? Math.Clamp(position.YPt, -LayoutConstants.PageGapPt, PageHeightPt(layout, page)) : 0;
        var x = double.IsFinite(position.ColumnXPt)
            ? Math.Clamp(position.ColumnXPt, 0, layout.ColumnWidthPt)
            : layout.ColumnWidthPt / 2;
        var zoom = double.IsFinite(position.Zoom) && position.Zoom > 0
            ? Math.Clamp(position.Zoom, LayoutConstants.MinZoom, LayoutConstants.MaxZoom)
            : _zoom.Zoom;
        return new ViewPosition(page, y, x, zoom);
    }

    private static double PageHeightPt(DocumentLayout layout, int page)
    {
        var next = page + 1 < layout.PageCount ? layout.PageTopPt(page + 1) : layout.TotalHeightPt;
        return Math.Max(0, next - layout.PageTopPt(page) - LayoutConstants.PageGapPt);
    }

    /// <summary>
    /// Recomputes the visible and retained pages and the current page (the centre page unless
    /// <paramref name="forceCurrent"/> names it), then reports the current page to the session as asked.
    /// </summary>
    private void Refresh(CurrentPageReport report, int? forceCurrent)
    {
        IReadOnlyList<int> visible = [];
        (int First, int Last) retained = (0, -1);
        var current = 0;
        if (_layout is { } layout)
        {
            var scale = _position.Zoom * Units.DipPerPt;
            var topPt = layout.PageTopPt(_position.PageIndex) + _position.YPt;
            var heightPt = _viewportHeightDip / scale;
            var (first, last) = layout.VisibleRange(topPt, heightPt);
            visible = Enumerable.Range(first, last - first + 1).ToArray();
            retained = layout.RetainedRange(topPt, heightPt);
            current = forceCurrent ?? layout.PageAt(topPt + (heightPt / 2));
        }

        if (!visible.SequenceEqual(_visiblePages))
        {
            _visiblePages = visible;
            OnPropertyChanged(nameof(VisiblePages));
        }

        if (retained != _retainedRange)
        {
            _retainedRange = retained;
            OnPropertyChanged(nameof(RetainedRange));
        }

        if (current != _currentPage)
        {
            _currentPage = current;
            OnPropertyChanged(nameof(CurrentPage));
        }

        OnPropertyChanged(nameof(IndicatorText));
        ReportCurrentPage(report);
    }

    private void ReportCurrentPage(CurrentPageReport report)
    {
        // Any newer movement or jump replaces a pending report: a late one must not supersede a go-to-page load.
        CancelPendingReport();
        if (report == CurrentPageReport.None)
        {
            return;
        }

        var session = _session;
        if (session is null)
        {
            return;
        }

        if (report == CurrentPageReport.Immediate)
        {
            PushCurrentPage(session);
            return;
        }

        _pendingReport = _scheduler.Schedule(CurrentPageDelay, () =>
        {
            _pendingReport = null;
            PushCurrentPage(session);
        });
    }

    private void PushCurrentPage(DocumentSession session)
    {
        // A document replaced meanwhile is not told anything; a disposed one ignores the call.
        if (ReferenceEquals(session, _session) && _currentPage >= 0 && _currentPage < session.PageCount)
        {
            session.SetCurrentPageFromView(_currentPage);
        }
    }

    private void CancelPendingReport()
    {
        _pendingReport?.Dispose();
        _pendingReport = null;
    }

    private void RaiseLayoutChanged()
    {
        OnPropertyChanged(nameof(Layout));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(Position));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private enum CurrentPageReport
    {
        /// <summary>The session is not told (the caller loads the page itself).</summary>
        None,

        /// <summary>After <see cref="CurrentPageDelay"/> without further movement.</summary>
        Debounced,

        /// <summary>Right now: an explicit jump.</summary>
        Immediate,
    }
}
