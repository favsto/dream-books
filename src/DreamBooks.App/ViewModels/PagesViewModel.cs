using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The Pages tab of the left-edge panel (FR-103, UI-04): one <see cref="PageItem"/> per page of the viewed document,
/// sized as laid out, the current page following the viewport, pages with marks or notes flagged from the marks list
/// (the rail's sources, FR-101), and go-to-page.
/// </summary>
/// <remarks>
/// <para><see cref="Pages"/> is one list per document, replaced when the layout changes (another document, or the same
/// one reopened), so a thousand pages never raise a thousand collection events.</para>
/// <para>Back history (FR-09): the panel stays open after a jump, so the reader may look at several pages in one visit
/// of the list. Only the first jump of a visit (<see cref="BeginBrowsing"/>) records the position, so Back returns to
/// where he was before opening the list. A tap on the current page never records (see <see cref="AtTopToleranceDip"/>).</para>
/// <para><see cref="DocumentChanged"/> tells the view that every thumbnail is stale (another layout, or another mark
/// index: a reopen without saving keeps the layout but not the ink); <see cref="InkChanged"/> passes on the strokes
/// of a page changing (any thread).</para>
/// </remarks>
public sealed class PagesViewModel : ObservableObject
{
    /// <summary>A view whose top is this close to a page's top is at that page's top (a tap there does nothing).</summary>
    public const double AtTopToleranceDip = 0.5;

    private readonly DocumentViewportViewModel _viewport;
    private readonly MarksViewModel _marks;
    private IReadOnlyList<PageItem> _pages = [];
    private PageItem? _current;
    private bool _browsingRecorded;

    public PagesViewModel(DocumentViewportViewModel viewport, MarksViewModel marks)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(marks);
        _viewport = viewport;
        _marks = marks;
        GoToPageCommand = new RelayCommand<PageItem>(GoToPage, item => item is not null);
        viewport.LayoutChanged += (_, _) => Rebuild();
        viewport.PropertyChanged += OnViewportPropertyChanged;
        marks.MarksRefreshed += (_, _) => UpdateTicks();
        marks.DocumentChanged += (_, _) => DocumentChanged?.Invoke(this, EventArgs.Empty);
        marks.InkChanged += (_, e) => InkChanged?.Invoke(this, e);
        Rebuild();
    }

    /// <summary>The thumbnails shown are no longer true (another document, or the same file reopened). UI thread.</summary>
    internal event EventHandler? DocumentChanged;

    /// <summary>The strokes of a page changed: its thumbnail's ink is read again. Raised on any thread.</summary>
    internal event EventHandler<PageStrokesChangedEventArgs>? InkChanged;

    /// <summary>One item per page, in order; replaced (never changed in length) when the document changes.</summary>
    public IReadOnlyList<PageItem> Pages
    {
        get => _pages;
        private set => SetProperty(ref _pages, value);
    }

    /// <summary>The item of the viewport's current page, or null without a document.</summary>
    public PageItem? CurrentItem
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    /// <summary>Brings the item's page to the top of the view; the first jump of a visit records Back history.</summary>
    public IRelayCommand<PageItem> GoToPageCommand { get; }

    /// <summary>The list is shown (opened, or its tab chosen): the next jump from it records the position left.</summary>
    public void BeginBrowsing() => _browsingRecorded = false;

    private void GoToPage(PageItem? item)
    {
        if (item is null || _viewport.Layout is not { } layout || item.PageIndex >= layout.PageCount)
        {
            return;
        }

        if (item.PageIndex == _viewport.CurrentPage)
        {
            // The page he is on: back to its top if he is further down, never a Back entry (it would spend the visit's
            // one entry on a page he never left); nothing at all when he is at its top already.
            var position = _viewport.Position;
            var atTop = position.PageIndex == item.PageIndex
                && Math.Abs(position.YPt * position.Zoom * Units.DipPerPt) <= AtTopToleranceDip;
            if (!atTop)
            {
                _viewport.ScrollToPage(item.PageIndex, recordHistory: false);
            }

            return;
        }

        var record = !_browsingRecorded;
        _browsingRecorded = true;
        _viewport.ScrollToPage(item.PageIndex, record);
    }

    private void Rebuild()
    {
        var items = Array.Empty<PageItem>();
        if (_viewport.Layout is { } layout)
        {
            items = new PageItem[layout.PageCount];
            for (var i = 0; i < items.Length; i++)
            {
                var (width, height) = DisplayedSizePt(layout, i);
                items[i] = new PageItem(i, width, height);
            }
        }

        CurrentItem = null;
        _browsingRecorded = false;
        Pages = items;
        UpdateCurrent();
        UpdateTicks();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A page's size as laid out (as <c>VirtualPagePanel.PageSizePt</c> computes it: rotation applied).</summary>
    private static (double Width, double Height) DisplayedSizePt(DocumentLayout layout, int pageIndex)
    {
        var width = layout.ColumnWidthPt - (2 * layout.PageLeftPt(pageIndex));
        var bottom = pageIndex + 1 < layout.PageCount ? layout.PageTopPt(pageIndex + 1) : layout.TotalHeightPt;
        var height = bottom - layout.PageTopPt(pageIndex) - LayoutConstants.PageGapPt;
        return (Math.Max(0, width), Math.Max(0, height));
    }

    private void OnViewportPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewportViewModel.CurrentPage) or null)
        {
            UpdateCurrent();
        }
    }

    private void UpdateCurrent()
    {
        var page = _viewport.CurrentPage;
        var next = page >= 0 && page < _pages.Count ? _pages[page] : null;
        if (ReferenceEquals(next, _current))
        {
            return;
        }

        if (_current is { } previous)
        {
            previous.IsCurrent = false;
        }

        if (next is not null)
        {
            next.IsCurrent = true;
        }

        CurrentItem = next;
    }

    private void UpdateTicks()
    {
        var marked = _marks.Marks.Select(m => m.PageIndex).ToHashSet();
        var noted = _marks.Notes.Select(n => n.PageIndex).ToHashSet();
        foreach (var item in _pages)
        {
            item.HasMarks = marked.Contains(item.PageIndex);
            item.HasNotes = noted.Contains(item.PageIndex);
        }
    }
}
