using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;
using DreamBooks.Core.Search;

namespace DreamBooks.Core.Tests.Marks;

/// <summary>
/// The search results' ticks on the scroll rail (FR-105, UI-07): the knob's own mapping and the marks' jump-target rule,
/// with a hit's vertical extent (from its first line's top to its last line's bottom), merged within one DIP.
/// </summary>
public class RailTickLayoutSearchTests
{
    private const double _zoom = 1.5;
    private const double _viewportWidthDip = 1000;
    private const double _viewportHeightDip = 800;
    private const double _track = 500;

    private static readonly double _windowPt = _viewportHeightDip / (_zoom * Units.DipPerPt);

    private static DocumentLayout Layout(int pages) =>
        new(Enumerable.Repeat(new PageDimensions(612, 792), pages).ToArray());

    private static SearchHit H(int page, double top, double bottom, double left = 72) =>
        new(page, [new SearchRect(left, top, left + 40, bottom)]);

    /// <summary>Where the knob's top is for the view a jump to an extent shows (as ScrollRail maps it).</summary>
    private static double KnobTop(DocumentLayout layout, int page, double top, double bottom)
    {
        var topPt = MarkTarget.TopPt(page, top, bottom, layout, _windowPt);
        var at = layout.PageAt(topPt);
        var position = new ViewPosition(at, topPt - layout.PageTopPt(at), layout.ColumnWidthPt / 2, _zoom);
        var (_, y) = layout.ToOffsets(position, _viewportWidthDip, _viewportHeightDip);
        var scrollable = Math.Max(0, layout.ExtentHeightDip(_zoom) - _viewportHeightDip);
        return scrollable > 0 ? Math.Clamp(y / scrollable, 0, 1) * _track : 0;
    }

    [Fact]
    public void Search_ticks_use_the_same_mapping_as_the_knob_and_marks()
    {
        var layout = Layout(10);
        var hits = new[]
        {
            H(0, 10, 22), // near the start: clamped to the top
            H(3, 100, 112),
            new SearchHit(5, [new SearchRect(400, 300, 560, 312), new SearchRect(72, 314, 140, 326)]), // two lines
            H(9, 740, 752), // near the end: clamped at the last offset
        };

        var positions = RailTickLayout.SearchPositions(hits, layout, _windowPt, _track);

        Assert.Equal(4, positions.Count);
        Assert.Equal(0, positions[0], 9);
        Assert.InRange(positions[1], KnobTop(layout, 3, 100, 112) - 1e-6, KnobTop(layout, 3, 100, 112) + 1e-6);
        Assert.InRange(positions[2], KnobTop(layout, 5, 300, 326) - 1e-6, KnobTop(layout, 5, 300, 326) + 1e-6);
        Assert.Equal(_track, positions[3], 9);

        // Exactly where a mark over the same extent puts its tick.
        var mark = new Mark(3, MarkSide.Left, 100, 112, [Guid.NewGuid()]);
        Assert.Equal(RailTickLayout.Positions([mark], layout, _windowPt, _track)[0], positions[1], 9);

        // Pages outside the layout and hits without a rectangle are ignored; no track, nothing.
        Assert.Empty(RailTickLayout.SearchPositions([H(12, 10, 20), new SearchHit(2, [])], layout, _windowPt, _track));
        Assert.Empty(RailTickLayout.SearchPositions(hits, layout, _windowPt, double.NaN));
        Assert.Empty(RailTickLayout.SearchPositions([], layout, _windowPt, _track));
    }

    [Fact]
    public void Ticks_merge_within_one_dip()
    {
        var layout = Layout(30);
        var pitch = _track / (layout.TotalHeightPt - _windowPt); // DIP of track per point of document
        var close = 0.5 / pitch; // half a DIP apart on the track
        var hits = new[] { H(10, 300, 312), H(10, 300 + close, 312 + close, left: 200), H(20, 300, 312) };

        var positions = RailTickLayout.SearchPositions(hits, layout, _windowPt, _track);

        Assert.Equal(2, positions.Count);
        Assert.True(positions[1] - positions[0] >= 1);
    }

    [Fact]
    public void Thousands_of_hits_give_at_most_one_tick_per_dip()
    {
        var layout = Layout(300);
        var hits = Enumerable.Range(0, 300)
            .SelectMany(page => Enumerable.Range(0, 30).Select(line => H(page, 60 + (line * 22), 72 + (line * 22))))
            .ToArray();

        var positions = RailTickLayout.SearchPositions(hits, layout, _windowPt, _track);

        Assert.InRange(positions.Count, 2, (int)_track + 1);
        for (var i = 1; i < positions.Count; i++)
        {
            Assert.True(positions[i] - positions[i - 1] >= 1);
        }
    }
}
