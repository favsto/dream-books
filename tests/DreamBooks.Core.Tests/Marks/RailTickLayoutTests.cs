using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Marks;

public class RailTickLayoutTests
{
    private const double _zoom = 1.5;
    private const double _viewportWidthDip = 1000;
    private const double _viewportHeightDip = 800;
    private const double _track = 500;

    private static readonly double _windowPt = _viewportHeightDip / (_zoom * Units.DipPerPt);

    private static DocumentLayout Layout(int pages) =>
        new(Enumerable.Repeat(new PageDimensions(612, 792), pages).ToArray());

    private static Mark M(int page, double top, double bottom) => new(page, MarkSide.Left, top, bottom, [Guid.NewGuid()]);

    /// <summary>
    /// Where ScrollRail puts the knob's top for the view shown after jumping to the mark: the scroll offset (clamped by
    /// ToOffsets) over the scrollable range (extent minus viewport), times the track length.
    /// </summary>
    private static double KnobTop(DocumentLayout layout, Mark mark)
    {
        var topPt = MarkTarget.TopPt(mark, layout, _windowPt);
        var page = layout.PageAt(topPt);
        var position = new ViewPosition(page, topPt - layout.PageTopPt(page), layout.ColumnWidthPt / 2, _zoom);
        var (_, y) = layout.ToOffsets(position, _viewportWidthDip, _viewportHeightDip);
        var scrollable = Math.Max(0, layout.ExtentHeightDip(_zoom) - _viewportHeightDip);
        return scrollable > 0 ? Math.Clamp(y / scrollable, 0, 1) * _track : 0;
    }

    [Fact]
    public void Positions_map_mark_tops_to_the_track_with_the_same_mapping_as_the_knob()
    {
        var layout = Layout(10);
        var marks = new[]
        {
            M(0, 10, 60), // near the start: the view is clamped to the top
            M(3, 100, 300), // fits the window: centred
            M(5, 20, 780), // taller than the window: top aligned
            M(9, 700, 760), // near the end: the view is clamped at the last offset
        };

        var positions = RailTickLayout.Positions(marks, layout, _windowPt, _track);

        Assert.Equal(4, positions.Count);
        for (var i = 0; i < marks.Length; i++)
        {
            Assert.InRange(positions[i], KnobTop(layout, marks[i]) - 1e-6, KnobTop(layout, marks[i]) + 1e-6);
        }

        Assert.Equal(0, positions[0], 9);
        Assert.Equal(_track, positions[3], 9); // clamped to the end of the track, where the knob can go

        // Centred mark: by hand, the target is the mark's centre minus half the window.
        var expected = (layout.PageTopPt(3) + 200 - (_windowPt / 2)) / (layout.TotalHeightPt - _windowPt) * _track;
        Assert.Equal(expected, positions[1], 6);
        var tall = (layout.PageTopPt(5) + 20) / (layout.TotalHeightPt - _windowPt) * _track;
        Assert.Equal(tall, positions[2], 6);
    }

    [Fact]
    public void Ticks_closer_than_one_dip_merge()
    {
        var layout = Layout(10);
        var scale = _track / (layout.TotalHeightPt - _windowPt); // DIP of track per point of document
        var marks = new[]
        {
            M(4, 100, 150),
            M(4, 100 + (0.4 / scale), 150 + (0.4 / scale)), // 0.4 DIP lower on the track: merged
            M(4, 100 + (0.9 / scale), 150 + (0.9 / scale)), // 0.9 DIP below the first: merged as well
            M(4, 100 + (3 / scale), 150 + (3 / scale)), // 3 DIP: its own tick
        };

        var positions = RailTickLayout.Positions(marks, layout, _windowPt, _track);

        Assert.Equal(2, positions.Count);
        Assert.True(positions[1] - positions[0] >= 1);
    }

    [Fact]
    public void Positions_are_sorted_whatever_the_order_of_the_marks()
    {
        var layout = Layout(10);
        var marks = new[] { M(8, 100, 200), M(2, 100, 200), M(5, 100, 200) };

        var positions = RailTickLayout.Positions(marks, layout, _windowPt, _track);

        Assert.Equal(3, positions.Count);
        Assert.True(positions[0] < positions[1] && positions[1] < positions[2]);
    }

    [Fact]
    public void One_page_document_and_empty_marks_give_empty_or_single_tick_without_division_by_zero()
    {
        var one = Layout(1);
        Assert.Empty(RailTickLayout.Positions([], one, _windowPt, _track));
        Assert.Empty(RailTickLayout.Positions([], Layout(10), _windowPt, _track));

        // The whole page fits the window: nothing scrolls, the knob stays at the top, and so do the ticks (merged).
        var positions = RailTickLayout.Positions([M(0, 100, 200), M(0, 600, 700)], one, 2000, _track);
        Assert.Equal([0.0], positions);

        // A window exactly as tall as the document, a zero-length track, no window size yet.
        Assert.Equal([0.0], RailTickLayout.Positions([M(0, 100, 200)], one, one.TotalHeightPt, _track));
        Assert.Equal([0.0], RailTickLayout.Positions([M(0, 100, 200)], one, 0, 0));
        var noWindow = RailTickLayout.Positions([M(0, 100, 200)], one, 0, _track);
        Assert.Single(noWindow);
        Assert.True(double.IsFinite(noWindow[0]));

        // Marks on pages outside the document are ignored; non-finite inputs give nothing.
        Assert.Empty(RailTickLayout.Positions([M(3, 100, 200), M(-1, 0, 10)], one, _windowPt, _track));
        Assert.Empty(RailTickLayout.Positions([M(0, 100, 200)], one, _windowPt, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => RailTickLayout.Positions([M(0, 1, 2)], one, _windowPt, _track, 0));
    }

    [Fact]
    public void Ten_thousand_marks_produce_at_most_one_tick_per_dip()
    {
        var layout = Layout(500);
        var random = new Random(7);
        var marks = Enumerable.Range(0, 10_000)
            .Select(_ =>
            {
                var top = random.NextDouble() * 700;
                return M(random.Next(500), top, top + 50 + (random.NextDouble() * 40));
            })
            .ToArray();

        var positions = RailTickLayout.Positions(marks, layout, _windowPt, _track);

        Assert.InRange(positions.Count, 1, (int)_track + 1);
        for (var i = 1; i < positions.Count; i++)
        {
            Assert.True(positions[i] - positions[i - 1] >= 1, $"ticks {i - 1} and {i} are closer than one DIP");
        }

        Assert.All(positions, p => Assert.InRange(p, 0, _track));
    }

    [Fact]
    public void Mark_target_centres_a_mark_that_fits_and_top_aligns_one_that_does_not()
    {
        var layout = Layout(3);
        Assert.Equal(layout.PageTopPt(1) + 150 - 250, MarkTarget.TopPt(M(1, 100, 200), layout, 500), 9);
        Assert.Equal(layout.PageTopPt(1) + 100, MarkTarget.TopPt(M(1, 100, 700), layout, 500), 9);
        Assert.Equal(0, MarkTarget.TopPt(M(0, 10, 20), layout, 500), 9); // clamped to the document
        Assert.Equal(layout.PageTopPt(2) + 100, MarkTarget.TopPt(M(2, 100, 200), layout, 0), 9); // no window yet
    }
}
