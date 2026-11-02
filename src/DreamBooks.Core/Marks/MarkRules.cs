namespace DreamBooks.Core.Marks;

/// <summary>
/// Tunable constants of the mark rule (spec section 4). Tolerant on purpose: a few false marks beat missed ones.
/// Tuned on 1 Nov 2026 on Fausto's real pen strokes (an A4 book): his margin lines are 40-95 pt long, 32-48 pt from
/// the page edge, within 8 degrees of vertical, and some are retraced up and down several times.
/// </summary>
public static class MarkRules
{
    /// <summary>
    /// Maximum average deviation from the vertical, in degrees: the stroke's horizontal travel may be at most
    /// tan(this) times its vertical travel (sums of |dx| and |dy| on the resampled stroke). A straight line tilted up to
    /// this angle, a slightly wavy, curved or retraced line and a square bracket pass; zig-zags, scribbles, loops and
    /// horizontal strokes, which move sideways about as much as down, do not.
    /// </summary>
    public const double MaxDeviationFromVerticalDegrees = 25;

    /// <summary>
    /// Spacing of the points the travel is measured on, as a fraction of the stroke's vertical extent, so the
    /// digitizer's sample-to-sample jitter does not count and the result does not depend on the sampling density.
    /// </summary>
    public const double PathSampleSpacingFraction = 0.05;

    /// <summary>Minimum vertical extent, in text lines.</summary>
    public const double MinExtentLines = 2;

    /// <summary>
    /// Line height used until the text layer supplies a real one (3b-2): a book's body text line (about 14-15 pt in
    /// Fausto's book, whose three-line margin lines start at 40 pt).
    /// </summary>
    public const double DefaultLineHeightPt = 15;

    /// <summary>Width of the outer margin zone on each side, as a fraction of the page width.</summary>
    public const double MarginZoneFraction = 0.18;

    /// <summary>Largest vertical gap between two lines on one side that still merges them, in text lines.</summary>
    public const double MergeGapLines = 1.5;

    /// <summary>Margin note window above and below the mark, in text lines.</summary>
    public const double NoteWindowLines = 1;

    /// <summary>How far beyond the margin zone a margin note may reach, as a fraction of the page width.</summary>
    public const double NoteReachFraction = 0.25;

    /// <summary>
    /// Notes on the rail (FR-101): the largest vertical gap between strokes that are not margin lines that still keeps
    /// them in one note, in text lines (horizontal position does not matter).
    /// </summary>
    public const double NoteGapLines = 4;

    /// <summary>A note smaller than this in both directions (a dot, a tap of the pen) is ignored, in text lines.</summary>
    public const double NoteMinSizeLines = 0.5;

    /// <summary>
    /// How far, in text lines, a short, mostly vertical stroke may lie beside a margin line's strokes and still be a pass
    /// of the same line (drawn again over it, or continued): it joins the mark instead of becoming a note.
    /// </summary>
    public const double PassReachLines = 1;

    /// <summary>
    /// Highlights as marks (FR-100, phase 3c): the smallest inked height of a highlighter cluster (strokes that touch
    /// or overlap vertically) that makes it a mark: one text line. Inclusive.
    /// </summary>
    public const double HighlightMinHeightPt = DefaultLineHeightPt;

    /// <summary>The smallest inked width of a highlighter cluster that makes it a mark (about three short words). Inclusive.</summary>
    public const double HighlightMinWidthPt = 60;

    /// <summary>
    /// A highlight mark whose horizontal centre lies in this middle band of the page width (inclusive) is on
    /// <see cref="MarkSide.Centre"/>; outside it, Left or Right by its centre.
    /// </summary>
    public const double HighlightCentreBandFraction = 0.36;
}
