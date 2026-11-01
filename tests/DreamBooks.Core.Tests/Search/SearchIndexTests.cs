using DreamBooks.Core.Search;

namespace DreamBooks.Core.Tests.Search;

/// <summary>The document-wide index of search hits (FR-13, FR-105).</summary>
public class SearchIndexTests
{
    private static SearchHit H(int page, double top, double left = 72)
        => new(page, [new SearchRect(left, top, left + 30, top + 12)]);

    private static (int Page, double Top, double Left) Pos(SearchHit hit)
        => (hit.PageIndex, hit.Rects[0].TopPt, hit.Rects[0].LeftPt);

    [Fact]
    public void Hits_are_ordered_by_page_then_top()
    {
        var index = new SearchIndex(3);
        index.SetPage(2, [H(2, 50)]);

        // A rotated page returns its hits in PDFium's order, not by position: the index sorts them (top, then left).
        index.SetPage(0, [H(0, 300), H(0, 100, left: 200), H(0, 100, left: 80)]);

        Assert.Equal(
            [(0, 100.0, 80.0), (0, 100.0, 200.0), (0, 300.0, 72.0), (2, 50.0, 72.0)],
            index.AllHits().Select(Pos));
        Assert.Equal([100.0, 100.0, 300.0], index.HitsOnPage(0).Select(h => h.Rects[0].TopPt));
        Assert.Empty(index.HitsOnPage(1));
        Assert.Empty(index.HitsOnPage(-1));
        Assert.Empty(index.HitsOnPage(3));
        Assert.Equal(4, index.TotalHits);
        Assert.Equal(2, index.SearchedCount);
        Assert.True(index.IsSearched(0));
        Assert.False(index.IsSearched(1));
        Assert.False(index.IsSearched(3));
        Assert.Equal(3, index.PageCount);
    }

    [Fact]
    public void A_hit_spanning_lines_is_placed_by_its_topmost_rectangle()
    {
        var index = new SearchIndex(1);
        var spanning = new SearchHit(0, [new SearchRect(400, 210, 500, 222), new SearchRect(72, 196, 120, 208)]);
        index.SetPage(0, [H(0, 200), spanning]);

        Assert.Equal([spanning, H(0, 200)], index.HitsOnPage(0));
    }

    [Fact]
    public void SetPage_rejects_bad_input()
    {
        var index = new SearchIndex(2);

        Assert.Throws<ArgumentOutOfRangeException>(() => index.SetPage(2, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.SetPage(-1, []));
        Assert.Throws<ArgumentNullException>(() => index.SetPage(0, null!));
        Assert.Throws<ArgumentException>(() => index.SetPage(0, [H(1, 10)])); // a hit of another page
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchIndex(-1));
        Assert.Equal(0, index.SearchedCount);
    }

    [Fact]
    public void TryNext_finds_the_following_hit_and_wraps_when_asked()
    {
        var index = new SearchIndex(5);
        index.SetPage(1, [H(1, 100), H(1, 400)]);
        index.SetPage(3, [H(3, 50)]);

        Assert.True(index.TryNext(0, 700, wrap: false, out var hit));
        Assert.Equal((1, 100.0), (hit.PageIndex, hit.Rects[0].TopPt));

        // Strictly after the position: a hit exactly at it is not "next".
        Assert.True(index.TryNext(1, 100, wrap: false, out hit));
        Assert.Equal((1, 400.0), (hit.PageIndex, hit.Rects[0].TopPt));

        Assert.True(index.TryNext(1, 400, wrap: false, out hit));
        Assert.Equal(3, hit.PageIndex);

        // Past the last hit: back to the first one.
        Assert.True(index.TryNext(3, 50, wrap: true, out hit));
        Assert.Equal((1, 100.0), (hit.PageIndex, hit.Rects[0].TopPt));
        Assert.True(index.TryNext(4, 0, wrap: true, out hit));
        Assert.Equal((1, 100.0), (hit.PageIndex, hit.Rects[0].TopPt));

        // A single hit wraps onto itself.
        var single = new SearchIndex(2);
        single.SetPage(0, [H(0, 10)]);
        Assert.True(single.TryNext(0, 10, wrap: true, out hit));
        Assert.Equal(H(0, 10), hit);
    }

    [Fact]
    public void TryNext_without_wrap_returns_false_after_the_last_hit()
    {
        var index = new SearchIndex(4);
        index.SetPage(1, [H(1, 100)]);

        Assert.False(index.TryNext(1, 100, wrap: false, out var hit));
        Assert.Null(hit);
        Assert.False(index.TryNext(3, 0, wrap: false, out _));

        var empty = new SearchIndex(4);
        Assert.False(empty.TryNext(0, 0, wrap: true, out _));
        Assert.False(empty.TryPrevious(0, 0, wrap: true, out _));
    }

    [Fact]
    public void TryPrevious_mirror_cases()
    {
        var index = new SearchIndex(5);
        index.SetPage(1, [H(1, 100), H(1, 400)]);
        index.SetPage(3, [H(3, 50)]);

        Assert.True(index.TryPrevious(4, 0, wrap: false, out var hit));
        Assert.Equal(3, hit.PageIndex);

        // Strictly before, with a 1 pt tolerance: the hit the view was just aligned to is not "previous".
        Assert.True(index.TryPrevious(1, 400.5, wrap: false, out hit));
        Assert.Equal((1, 100.0), (hit.PageIndex, hit.Rects[0].TopPt));
        Assert.True(index.TryPrevious(1, 401.5, wrap: false, out hit));
        Assert.Equal((1, 400.0), (hit.PageIndex, hit.Rects[0].TopPt));

        Assert.False(index.TryPrevious(1, 100, wrap: false, out hit));
        Assert.Null(hit);
        Assert.False(index.TryPrevious(0, 900, wrap: false, out _));

        // Before the first hit: round to the last one.
        Assert.True(index.TryPrevious(1, 100, wrap: true, out hit));
        Assert.Equal(3, hit.PageIndex);
        Assert.True(index.TryPrevious(0, 0, wrap: true, out hit));
        Assert.Equal(3, hit.PageIndex);
    }

    [Fact]
    public void Stepping_from_a_hit_walks_hits_that_share_a_line()
    {
        var index = new SearchIndex(3);
        SearchHit a = H(0, 100, left: 72), b = H(0, 100, left: 150), c = H(0, 100, left: 300), d = H(2, 20);
        index.SetPage(0, [c, a, b]);
        index.SetPage(2, [d]);

        Assert.True(index.TryNext(a, wrap: false, out var hit));
        Assert.Equal(b, hit);
        Assert.True(index.TryNext(b, wrap: false, out hit));
        Assert.Equal(c, hit);
        Assert.True(index.TryNext(c, wrap: false, out hit));
        Assert.Equal(d, hit);
        Assert.False(index.TryNext(d, wrap: false, out _));
        Assert.True(index.TryNext(d, wrap: true, out hit));
        Assert.Equal(a, hit);

        Assert.True(index.TryPrevious(c, wrap: false, out hit));
        Assert.Equal(b, hit);
        Assert.False(index.TryPrevious(a, wrap: false, out _));
        Assert.True(index.TryPrevious(a, wrap: true, out hit));
        Assert.Equal(d, hit);

        // A hit the index does not hold steps from its position.
        Assert.True(index.TryNext(H(1, 0), wrap: false, out hit));
        Assert.Equal(d, hit);
        Assert.True(index.TryPrevious(H(1, 0), wrap: false, out hit));
        Assert.Equal(c, hit);
    }

    [Fact]
    public void Identical_page_results_raise_no_Changed()
    {
        var index = new SearchIndex(2);
        var raised = new List<int>();
        index.Changed += (_, e) => raised.Add(e.PageIndex);

        index.SetPage(1, [H(1, 10), H(1, 90)]);
        index.SetPage(1, [H(1, 90), H(1, 10)]); // equal by value, in another order
        index.SetPage(1, [H(1, 10)]);
        index.SetPage(0, []);
        index.SetPage(0, []);
        index.SetPage(0, [], hasText: false); // the text flag is part of the page's result

        Assert.Equal([1, 1, 0, 0], raised);
    }

    [Fact]
    public void The_text_flag_is_recorded_per_page()
    {
        var index = new SearchIndex(3);
        Assert.False(index.AnyPageHasText);

        index.SetPage(0, [], hasText: false);
        Assert.False(index.AnyPageHasText);

        index.SetPage(1, []);
        Assert.True(index.AnyPageHasText);

        index.SetPage(1, [], hasText: false);
        index.SetPage(2, [H(2, 5)], hasText: false); // a hit implies text
        Assert.True(index.AnyPageHasText);
    }

    [Fact]
    public void Truncated_flag_round_trips()
    {
        var index = new SearchIndex(1);
        var raised = 0;
        index.Changed += (_, _) => raised++;
        Assert.False(index.Truncated);

        index.Truncated = true;
        Assert.True(index.Truncated);

        index.Truncated = false;
        Assert.False(index.Truncated);
        Assert.Equal(0, raised); // the scanner reports the truncation through its Progress
    }

    [Fact]
    public async Task Concurrent_SetPage_and_reads_are_safe()
    {
        const int pages = 200;
        var index = new SearchIndex(pages);
        var changed = 0;
        index.Changed += (_, _) => Interlocked.Increment(ref changed);
        using var stop = new CancellationTokenSource();

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (var p = w; p < pages; p += 4)
            {
                index.SetPage(p, [H(p, 300), H(p, 100)]);
            }
        }));

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var all = index.AllHits();
                var keys = all.Select(h => (h.PageIndex, h.Rects[0].TopPt)).ToArray();
                Assert.Equal(keys.Order(), keys);
                Assert.Equal(0, all.Count % 2); // a page's hits are published together
                Assert.True(index.TotalHits >= 0 && index.SearchedCount <= pages);
                index.TryNext(pages / 2, 0, wrap: true, out var _);
                index.TryPrevious(pages / 2, 0, wrap: true, out var _);
                Assert.True(index.HitsOnPage(7).Count is 0 or 2);
            }
        }));

        var readersTask = Task.WhenAll(readers);
        await Task.WhenAll(writers);
        stop.Cancel();
        await readersTask;

        Assert.Equal(pages, index.SearchedCount);
        Assert.Equal(pages * 2, index.TotalHits);
        Assert.Equal(pages * 2, index.AllHits().Count);
        Assert.Equal(pages, Volatile.Read(ref changed));
    }
}
