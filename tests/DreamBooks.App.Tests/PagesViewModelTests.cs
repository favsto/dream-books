using System.Globalization;
using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

/// <summary>
/// The Pages tab's view model (FR-103, UI-04): one item per page with its displayed size, the current page following
/// the viewport, marks and notes flagged per page, go-to-page with Back history once per visit of the list, and the
/// document change announced so the thumbnails start over.
/// </summary>
public sealed class PagesViewModelTests
{
    private readonly ManualScheduler _scheduler = new();

    [Fact]
    public void Pages_list_has_one_row_per_page_with_the_correct_aspect_ratio_for_mixed_sizes()
    {
        // Portrait A4-ish, landscape (a rotated page is laid out with its displayed size), a tall strip, a square.
        var (viewport, _, pages, _) = Standalone(new PageDimensions(612, 792), new PageDimensions(792, 612), new PageDimensions(300, 1200), new PageDimensions(500, 500));

        Assert.Equal(4, pages.Pages.Count);
        Assert.Equal([0, 1, 2, 3], pages.Pages.Select(p => p.PageIndex));
        Assert.Equal([1, 2, 3, 4], pages.Pages.Select(p => p.Number));
        Assert.Equal(792.0 / 612, pages.Pages[0].AspectRatio, 6);
        Assert.Equal(612.0 / 792, pages.Pages[1].AspectRatio, 6);
        Assert.Equal(4.0, pages.Pages[2].AspectRatio, 6);
        Assert.Equal(1.0, pages.Pages[3].AspectRatio, 6);
        Assert.Equal((792.0, 612.0), (pages.Pages[1].WidthPt, pages.Pages[1].HeightPt));

        // Each row is named in Italian with its number; the current one says so.
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, Strings.PageRowNameFormat, 1) + ", " + Strings.PageRowCurrent,
            pages.Pages[0].AutomationName);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.PageRowNameFormat, 3), pages.Pages[2].AutomationName);

        // Another document: a new list, and the change is announced (the thumbnails start over).
        var changes = 0;
        pages.DocumentChanged += (_, _) => changes++;
        viewport.Attach(null, new DocumentLayout([new PageDimensions(612, 792), new PageDimensions(612, 792)]), null);
        Assert.Equal(1, changes);
        Assert.Equal(2, pages.Pages.Count);
        viewport.Attach(null, null, null);
        Assert.Empty(pages.Pages);
        Assert.Null(pages.CurrentItem);
    }

    [Fact]
    public void Current_page_follows_the_viewport()
    {
        var (viewport, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 10).ToArray());
        Assert.True(pages.Pages[0].IsCurrent);
        Assert.Same(pages.Pages[0], pages.CurrentItem);

        viewport.ScrollToPage(6, recordHistory: false);
        Assert.Equal(6, viewport.CurrentPage);
        Assert.Same(pages.Pages[6], pages.CurrentItem);
        Assert.Equal([6], pages.Pages.Where(p => p.IsCurrent).Select(p => p.PageIndex));
        Assert.Contains(Strings.PageRowCurrent, pages.Pages[6].AutomationName, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.PageRowCurrent, pages.Pages[0].AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void Going_to_a_page_records_history_once_per_visit_of_the_list()
    {
        var (viewport, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 10).ToArray());
        viewport.ScrollToPage(2, recordHistory: false);

        pages.BeginBrowsing();
        pages.GoToPageCommand.Execute(pages.Pages[7]);
        Assert.Equal(7, viewport.Position.PageIndex);
        Assert.Equal(7, viewport.CurrentPage);
        pages.GoToPageCommand.Execute(pages.Pages[4]); // browsing on in the same visit: no second entry
        Assert.Equal(4, viewport.Position.PageIndex);

        viewport.BackCommand.Execute(null);
        Assert.Equal(2, viewport.Position.PageIndex); // back where the reader was before opening the list
        Assert.False(viewport.BackCommand.CanExecute(null));

        // The next visit records again.
        pages.BeginBrowsing();
        pages.GoToPageCommand.Execute(pages.Pages[9]);
        Assert.True(viewport.BackCommand.CanExecute(null));
        Assert.False(pages.GoToPageCommand.CanExecute(null));
    }

    [Fact]
    public void A_tap_on_the_current_page_never_spends_the_visits_Back_entry()
    {
        var (viewport, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 10).ToArray());
        viewport.ScrollToPage(3, recordHistory: false);
        var jumps = 0;
        viewport.Jumped += (_, _) => jumps++;

        // Already at the top of the current page: nothing at all (no jump, no entry).
        pages.BeginBrowsing();
        pages.GoToPageCommand.Execute(pages.Pages[3]);
        Assert.Equal(0, jumps);
        Assert.False(viewport.BackCommand.CanExecute(null));

        // Further down the current page: back to its top, still no entry; the visit's entry stays for a real jump.
        viewport.SetScroll(0, (viewport.Layout!.PageTopPt(3) + 300) * Units.DipPerPt);
        Assert.Equal(3, viewport.CurrentPage);
        pages.GoToPageCommand.Execute(pages.Pages[3]);
        Assert.Equal(1, jumps);
        Assert.Equal((3, 0.0), (viewport.Position.PageIndex, viewport.Position.YPt));
        Assert.False(viewport.BackCommand.CanExecute(null));

        pages.GoToPageCommand.Execute(pages.Pages[8]);
        Assert.Equal(8, viewport.Position.PageIndex);
        viewport.BackCommand.Execute(null);
        Assert.Equal(3, viewport.Position.PageIndex);
    }

    [Fact]
    public void Pages_with_marks_or_notes_are_flagged()
    {
        var (_, marks, pages, index) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 10).ToArray());
        index.SetPage(2, [new Mark(2, MarkSide.Left, 100, 300, [Guid.NewGuid()])]);
        index.SetPage(5, [], [new Note(5, 100, 120, [Guid.NewGuid()])]);
        index.SetPage(7, [new Mark(7, MarkSide.Right, 100, 300, [Guid.NewGuid()])], [new Note(7, 400, 420, [Guid.NewGuid()])]);
        _scheduler.Advance(MarksViewModel.RefreshInterval);

        Assert.Equal([2, 7], pages.Pages.Where(p => p.HasMarks).Select(p => p.PageIndex));
        Assert.Equal([5, 7], pages.Pages.Where(p => p.HasNotes).Select(p => p.PageIndex));
        Assert.Contains(Strings.PageRowHasMarks, pages.Pages[2].AutomationName, StringComparison.Ordinal);
        Assert.Contains(Strings.PageRowHasNotes, pages.Pages[5].AutomationName, StringComparison.Ordinal);

        index.SetPage(2, []);
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        Assert.False(pages.Pages[2].HasMarks);

        // Ink changes of a page are passed on (any thread), for its thumbnail.
        var changed = new List<int>();
        pages.InkChanged += (_, e) => changed.Add(e.PageIndex);
        marks.NotifyInkChanged(3);
        Assert.Equal([3], changed);

        // A new mark index (the same file reopened) is a document change too.
        var changes = 0;
        pages.DocumentChanged += (_, _) => changes++;
        marks.Attach(new MarkIndex(10));
        Assert.Equal(1, changes);
        Assert.DoesNotContain(pages.Pages, p => p.HasMarks || p.HasNotes);
    }

    private (DocumentViewportViewModel Viewport, MarksViewModel Marks, PagesViewModel Pages, MarkIndex Index) Standalone(
        params PageDimensions[] sizes)
    {
        var viewport = new DocumentViewportViewModel(new ZoomViewModel(), _scheduler);
        viewport.Attach(null, new DocumentLayout(sizes), null);
        viewport.ViewportWidthDip = 816;
        viewport.ViewportHeightDip = 600;
        var marks = new MarksViewModel(viewport, _scheduler);
        var pages = new PagesViewModel(viewport, marks);
        var index = new MarkIndex(sizes.Length);
        marks.Attach(index);
        return (viewport, marks, pages, index);
    }
}
