using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Documents;

namespace DreamBooks.App.Views;

/// <summary>
/// The Pages tab (FR-103, UI-04) over a <see cref="PagesViewModel"/>: one virtualised, recycled row per page with its
/// thumbnail (<see cref="PageThumbnail"/>, through one bounded <see cref="PageThumbnailCache"/>) and its number. A tap
/// that did not move (a scroll is not a tap) or Enter on the selected row goes to the page through
/// <see cref="PagesViewModel.GoToPageCommand"/>; the panel stays open. Each time the list is shown (the panel opens on
/// this tab, or the tab is chosen) it starts a new visit (<see cref="PagesViewModel.BeginBrowsing"/>) and brings the
/// current page's row into view, selected, so the arrows start from it.
/// </summary>
/// <remarks>
/// The thumbnails of the old document are dropped when the view model reports a document change (another file, or the
/// same one reopened without saving: no stale ink); a stroke change on a page (any thread) has its row read the ink
/// again on the UI thread.
/// </remarks>
public partial class PagesListView : UserControl, IEdgePanelContent
{
    private PagesViewModel? _pages;
    private PageThumbnailCache? _thumbnails;
    private Point? _pressAt;
    private int _scrollVersion;

    public PagesListView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as PagesViewModel);
    }

    /// <summary>The rows' thumbnails; set by the window (the bitmaps come from the document view). Tests inject one.</summary>
    internal PageThumbnailCache? Thumbnails
    {
        get => _thumbnails;
        set
        {
            _thumbnails = value;
            PageThumbnail.SetCache(this, value); // inherited by every row's thumbnail
        }
    }

    /// <summary>How many times the list was brought to the current page (tests).</summary>
    internal int ScrollToCurrentRequests { get; private set; }

    /// <summary>The list is shown (the hosting panel says so): a new visit, and the current page's row in view.</summary>
    public void OnShown()
    {
        _thumbnails?.SetPaused(false);
        _pages?.BeginBrowsing();
        ScrollToCurrent();
    }

    /// <summary>The list is hidden (panel closed, or the other tab): no thumbnail is read meanwhile.</summary>
    public void OnHidden() => _thumbnails?.SetPaused(true);

    /// <summary>Selects the current page's row and brings it into view (after layout, so the rows exist).</summary>
    internal void ScrollToCurrent()
    {
        ScrollToCurrentRequests++;
        var version = ++_scrollVersion;
        if (_pages?.CurrentItem is not { } current)
        {
            return;
        }

        List.SelectedItem = current;
        List.ScrollIntoView(current);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (version == _scrollVersion && ReferenceEquals(_pages?.CurrentItem, current))
            {
                List.ScrollIntoView(current); // again once laid out: estimated heights above it are now real
            }
        });
    }

    /// <summary>A press on the list (list coordinates): where a tap on a row starts.</summary>
    internal void PressRow(Point position) => _pressAt = position;

    /// <summary>
    /// The press lifts over a row (list coordinates): a tap (it moved at most <see cref="ScrollRail.TapSlopDip"/>) goes
    /// to its page; a scroll of the list does not. True when it went.
    /// </summary>
    internal bool ReleaseRow(PageItem item, Point position)
    {
        var start = _pressAt;
        _pressAt = null;
        if (start is not { } from || (position - from).Length > ScrollRail.TapSlopDip)
        {
            return false;
        }

        return Activate(item);
    }

    /// <summary>Enter: goes to the selected (or focused) row's page. True when there was one.</summary>
    internal bool ActivateSelected()
    {
        var item = List.SelectedItem as PageItem
            ?? (Keyboard.FocusedElement as ListBoxItem)?.DataContext as PageItem;
        return item is not null && Activate(item);
    }

    private bool Activate(PageItem item)
    {
        if (_pages?.GoToPageCommand is not { } command || !command.CanExecute(item))
        {
            return false;
        }

        command.Execute(item); // the panel stays open: no ContentActivated
        return true;
    }

    private void Attach(PagesViewModel? pages)
    {
        if (_pages is { } old)
        {
            old.DocumentChanged -= OnDocumentChanged;
            old.InkChanged -= OnInkChanged;
            old.PropertyChanged -= OnPagesPropertyChanged;
        }

        _pages = pages;
        if (pages is not null)
        {
            pages.DocumentChanged += OnDocumentChanged;
            pages.InkChanged += OnInkChanged;
            pages.PropertyChanged += OnPagesPropertyChanged;
        }
    }

    // UI thread: another document, or the same file reopened: nothing drawn for the old one stays.
    private void OnDocumentChanged(object? sender, EventArgs e) => Thumbnails?.Clear();

    // Any thread: the session raises it where the edit or the load happened.
    private void OnInkChanged(object? sender, PageStrokesChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Thumbnails?.InvalidateInk(e.PageIndex));

    private void OnPagesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PagesViewModel.Pages) && IsVisible)
        {
            ScrollToCurrent(); // a document opened while the list is shown
        }
    }

    private void OnListPress(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Stylus)
        {
            return; // the pen never presses the list (the panel closes)
        }

        PressRow(e.GetPosition(List));
    }

    private void OnRowRelease(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Stylus)
        {
            return;
        }

        if (sender is ListBoxItem { DataContext: PageItem item } && ReleaseRow(item, e.GetPosition(List)))
        {
            e.Handled = true;
        }
    }

    /// <summary>A finger fling past the end of the list: no window "bounce" (as for the document).</summary>
    private void OnListBoundaryFeedback(object? sender, ManipulationBoundaryFeedbackEventArgs e) => e.Handled = true;

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && ActivateSelected())
        {
            e.Handled = true;
        }
    }
}
