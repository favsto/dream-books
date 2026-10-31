using DreamBooks.Core.Layout;

namespace DreamBooks.Core.Marks;

/// <summary>
/// Where the marks' ticks go on the scroll rail (FR-101, UI-07), in DIPs along the track the knob's top travels.
/// </summary>
/// <remarks>
/// A tick uses exactly the knob's mapping for the view a jump to its mark shows: the knob's top is at the scroll
/// offset over the scrollable range (extent minus viewport), clamped to [0, 1], times the track length. In points
/// that is <see cref="MarkTarget.TopPt"/> over (document height minus window height). So when the view is at a mark
/// the knob sits on its tick, and a mark beyond the last scrollable offset (near the end of the document, where the
/// view is clamped) gets its tick at the end of the track, where the knob stops, never past it. When nothing
/// scrolls (the document fits the window) the knob stays at the top and so do the ticks. Ticks closer than
/// <c>mergeDip</c> to the first tick of their run merge into it, so thousands of marks give at most one tick per DIP.
/// </remarks>
public static class RailTickLayout
{
    /// <param name="marks">The marks, in any order; those on pages outside the layout are ignored.</param>
    /// <param name="layout">The document's page frames.</param>
    /// <param name="windowHeightPt">The window's height in document points (the viewport height at the current zoom).</param>
    /// <param name="trackLengthDip">How far the knob's top can travel; nothing is placed when it is not finite.</param>
    /// <param name="mergeDip">Ticks closer than this merge; positive.</param>
    /// <returns>Ascending offsets in [0, <paramref name="trackLengthDip"/>], at least <paramref name="mergeDip"/> apart.</returns>
    public static IReadOnlyList<double> Positions(
        IReadOnlyList<Mark> marks, DocumentLayout layout, double windowHeightPt, double trackLengthDip, double mergeDip = 1)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(layout);
        if (!double.IsFinite(mergeDip) || mergeDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mergeDip), mergeDip, "The merge distance must be positive.");
        }

        if (marks.Count == 0 || !double.IsFinite(trackLengthDip))
        {
            return [];
        }

        var track = Math.Max(0, trackLengthDip);
        var window = double.IsFinite(windowHeightPt) ? Math.Max(0, windowHeightPt) : 0;
        var scrollablePt = layout.TotalHeightPt - window;
        var raw = new List<double>(marks.Count);
        foreach (var mark in marks)
        {
            if (mark.PageIndex < 0 || mark.PageIndex >= layout.PageCount)
            {
                continue;
            }

            var fraction = scrollablePt > 0 ? Math.Clamp(MarkTarget.TopPt(mark, layout, window) / scrollablePt, 0, 1) : 0;
            raw.Add(fraction * track);
        }

        raw.Sort();
        var merged = new List<double>();
        foreach (var position in raw)
        {
            if (merged.Count == 0 || position - merged[^1] >= mergeDip)
            {
                merged.Add(position);
            }
        }

        return merged;
    }
}
