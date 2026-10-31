using System.Diagnostics;
using DreamBooks.Core.Marks;

namespace DreamBooks.Core.Tests.Marks;

public class MarkIndexTests
{
    private static Mark M(int page, double top, double bottom = -1)
        => new(page, MarkSide.Left, top, bottom < 0 ? top + 20 : bottom, [Guid.NewGuid()]);

    [Fact]
    public void SetPage_orders_marks_by_page_then_top()
    {
        var index = new MarkIndex(3);
        index.SetPage(2, [M(2, 50)]);
        index.SetPage(0, [M(0, 300), M(0, 100)]);

        var all = index.AllMarks();

        Assert.Equal([(0, 100.0), (0, 300.0), (2, 50.0)], all.Select(m => (m.PageIndex, m.TopPt)));
    }

    [Fact]
    public void Replacing_a_page_raises_Changed_once_and_identical_marks_do_not()
    {
        var index = new MarkIndex(2);
        var raised = new List<int>();
        index.Changed += (_, e) => raised.Add(e.PageIndex);
        var marks = new[] { M(1, 10), M(1, 90) };

        index.SetPage(1, marks);
        index.SetPage(1, marks.ToArray());
        index.SetPage(1, [M(1, 10)]);

        Assert.Equal([1, 1], raised);
    }

    [Fact]
    public void TryNext_from_inside_a_mark_goes_to_the_following_one()
    {
        var index = new MarkIndex(2);
        index.SetPage(0, [M(0, 100, 140), M(0, 400)]);

        Assert.True(index.TryNext(0, 120, out var mark));
        Assert.Equal(400, mark.TopPt);
        Assert.True(index.TryNext(0, 100, out mark));
        Assert.Equal(400, mark.TopPt);
    }

    [Fact]
    public void TryNext_returns_false_after_the_last_mark()
    {
        var index = new MarkIndex(2);
        index.SetPage(0, [M(0, 100)]);
        index.SetPage(1, [M(1, 100)]);

        Assert.True(index.TryNext(0, 100, out var mark));
        Assert.Equal(1, mark.PageIndex);
        Assert.False(index.TryNext(1, 100, out _));
    }

    [Fact]
    public void TryPrevious_returns_false_before_the_first_mark()
    {
        var index = new MarkIndex(2);
        index.SetPage(0, [M(0, 100)]);

        Assert.False(index.TryPrevious(0, 100, out _));
        Assert.False(index.TryPrevious(0, 50, out _));
    }

    [Fact]
    public void Repeated_previous_walks_back_through_every_mark()
    {
        var index = new MarkIndex(3);
        index.SetPage(0, [M(0, 100), M(0, 300)]);
        index.SetPage(2, [M(2, 200)]);
        var tops = new List<(int, double)>();
        var page = 2;
        var y = 500.0;

        while (index.TryPrevious(page, y, out var mark))
        {
            tops.Add((mark.PageIndex, mark.TopPt));
            page = mark.PageIndex;
            y = mark.TopPt;
        }

        Assert.Equal([(2, 200.0), (0, 300.0), (0, 100.0)], tops);
    }

    [Fact]
    public void Neighbour_lookup_skips_unscanned_pages_without_failing()
    {
        var index = new MarkIndex(5);
        index.SetPage(0, [M(0, 10)]);
        index.SetPage(1, []);
        index.SetPage(4, [M(4, 10)]);

        Assert.True(index.TryNext(0, 10, out var next));
        Assert.Equal(4, next.PageIndex);
        Assert.True(index.TryPrevious(3, 0, out var prev));
        Assert.Equal(0, prev.PageIndex);
        Assert.False(new MarkIndex(3).TryNext(0, 0, out _));
    }

    [Fact]
    public void ScannedCount_and_TotalMarkCount_track_updates()
    {
        var index = new MarkIndex(4);
        Assert.Equal(0, index.ScannedCount);
        Assert.False(index.IsScanned(0));

        index.SetPage(0, [M(0, 1), M(0, 2)]);
        index.SetPage(1, []);

        Assert.True(index.IsScanned(0));
        Assert.True(index.IsScanned(1));
        Assert.Equal(2, index.ScannedCount);
        Assert.Equal(2, index.TotalMarkCount);

        index.ClearPage(0);

        Assert.False(index.IsScanned(0));
        Assert.Equal(1, index.ScannedCount);
        Assert.Equal(0, index.TotalMarkCount);
        Assert.Empty(index.MarksOnPage(0));
    }

    [Fact]
    public void Concurrent_SetPage_and_reads_are_safe()
    {
        var index = new MarkIndex(50);

        Parallel.For(0, 50, p =>
        {
            for (var round = 0; round < 20; round++)
            {
                index.SetPage(p, [M(p, 10), M(p, 20 + round)]);
                _ = index.AllMarks();
                _ = index.TryNext(p, 0, out _);
                _ = index.TryPrevious(p, 1000, out _);
                _ = index.TotalMarkCount;
            }
        });

        Assert.Equal(50, index.ScannedCount);
        Assert.Equal(100, index.TotalMarkCount);
        Assert.Equal(100, index.AllMarks().Count);
    }

    [Fact]
    public void Reset_clears_everything_and_changes_page_count()
    {
        var index = new MarkIndex(2);
        index.SetPage(1, [M(1, 5)]);

        index.Reset(6);
        index.SetPage(5, [M(5, 5)]);

        Assert.Equal(1, index.ScannedCount);
        Assert.False(index.IsScanned(1));
        Assert.Single(index.AllMarks());
    }

    [Fact]
    public void TrySetPageIfUnscanned_sets_only_a_page_nobody_set_yet()
    {
        var index = new MarkIndex(3);
        var raised = new List<int>();
        index.Changed += (_, e) => raised.Add(e.PageIndex);
        var live = new[] { M(1, 10) };
        index.SetPage(1, live);

        Assert.False(index.TrySetPageIfUnscanned(1, [M(1, 300)]));
        Assert.True(index.TrySetPageIfUnscanned(2, []));
        Assert.False(index.TrySetPageIfUnscanned(2, [M(2, 5)]));

        Assert.Equal(live, index.MarksOnPage(1));
        Assert.True(index.IsScanned(2));
        Assert.Empty(index.MarksOnPage(2));
        Assert.Equal([1, 2], raised);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.TrySetPageIfUnscanned(3, []));
    }

    [Fact]
    public void Ten_thousand_marks_lookup_is_fast()
    {
        var index = new MarkIndex(100);
        for (var p = 0; p < 100; p++)
        {
            index.SetPage(p, Enumerable.Range(0, 100).Select(i => M(p, i * 5)).ToArray());
        }

        index.TryNext(50, 100, out _);
        index.TryPrevious(50, 100, out _);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            index.TryNext(i % 100, (i % 97) * 5 + 1, out _);
            index.TryPrevious(i % 100, (i % 97) * 5 + 1, out _);
        }

        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 50, $"took {sw.ElapsedMilliseconds} ms");
    }
}
