namespace DreamBooks.Core.Marks;

/// <summary>Tunable constants of the mark rule (spec section 4). Tolerant on purpose: a few false marks beat missed ones.</summary>
public static class MarkRules
{
    /// <summary>Maximum angle between the stroke's end-to-end line and the vertical, in degrees.</summary>
    public const double MaxDeviationFromVerticalDegrees = 20;

    /// <summary>Maximum path length as a multiple of the end-to-end distance (rejects zig-zags and scribbles).</summary>
    public const double MaxPathToDistanceRatio = 1.3;

    /// <summary>Minimum vertical extent, in text lines.</summary>
    public const double MinExtentLines = 3;

    /// <summary>Line height used until the text layer supplies a real one (3b-2).</summary>
    public const double DefaultLineHeightPt = 36;

    /// <summary>Width of the outer margin zone on each side, as a fraction of the page width.</summary>
    public const double MarginZoneFraction = 0.18;

    /// <summary>Largest vertical gap between two lines on one side that still merges them, in text lines.</summary>
    public const double MergeGapLines = 1.5;

    /// <summary>Margin note window above and below the mark, in text lines.</summary>
    public const double NoteWindowLines = 1;

    /// <summary>How far beyond the margin zone a margin note may reach, as a fraction of the page width.</summary>
    public const double NoteReachFraction = 0.25;
}
