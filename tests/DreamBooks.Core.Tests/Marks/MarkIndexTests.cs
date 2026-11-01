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
        Assert.Equal([100.0, 300.0], index.MarksOnPage(0).Select(m => m.TopPt));
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
    public void Unscanned_and_out_of_range_pages_have_no_marks_and_are_not_scanned()
    {
        var index = new MarkIndex(3);
        index.SetPage(0, [M(0, 10)]);

        Assert.Empty(index.MarksOnPage(1));
        Assert.Empty(index.MarksOnPage(-1));
        Assert.Empty(index.MarksOnPage(3));
        Assert.False(index.IsScanned(1));
        Assert.False(index.IsScanned(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.SetPage(3, []));
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

        index.SetPage(0, []); // the line erased: the page stays scanned, without marks

        Assert.True(index.IsScanned(0));
        Assert.Equal(2, index.ScannedCount);
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
                _ = index.MarksOnPage((p + 1) % 50);
                _ = index.IsScanned((p + 7) % 50);
                _ = index.TotalMarkCount;
            }
        });

        Assert.Equal(50, index.ScannedCount);
        Assert.Equal(100, index.TotalMarkCount);
        Assert.Equal(100, index.AllMarks().Count);
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
}
