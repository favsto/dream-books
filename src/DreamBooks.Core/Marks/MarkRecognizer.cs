using DreamBooks.Core.Model;

namespace DreamBooks.Core.Marks;

/// <summary>
/// Turns the ink strokes of one page into marks (margin lines and line-sized highlights, FR-100, FR-104) and notes
/// (every other stroke, grouped, FR-101).
/// </summary>
public sealed class MarkRecognizer(IPageLineMetrics? metrics = null)
{
    private readonly IPageLineMetrics _metrics = metrics ?? new FixedLineMetrics();

    /// <summary>The page's marks, ordered by top (then side).</summary>
    public IReadOnlyList<Mark> Recognize(int pageIndex, double pageWidthPt, IReadOnlyList<InkStroke> strokes)
        => RecognizePage(pageIndex, pageWidthPt, strokes).Marks;

    /// <summary>
    /// The page's marks and notes in one pass over its strokes (the scan and the live tracker read a page once for
    /// both). A short, mostly vertical pen stroke along a margin line (a second pass over it, or its continuation;
    /// see <see cref="MarkRules.PassReachLines"/>) joins that line's mark. Highlighter strokes are never margin lines:
    /// a cluster of them large enough to cover a text line is a mark of its own (see <see cref="HighlightMarks"/>).
    /// Every other stroke, pen or highlighter, belongs to a note: they are taken from the top
    /// and a gap of more than <see cref="MarkRules.NoteGapLines"/> lines starts a new note; a note smaller than
    /// <see cref="MarkRules.NoteMinSizeLines"/> in both directions (a dot, a tap) is dropped. Notes are ordered by top,
    /// their stroke ids by top (then by z-order). An invalid page width or line height throws
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public (IReadOnlyList<Mark> Marks, IReadOnlyList<Note> Notes) RecognizePage(
        int pageIndex, double pageWidthPt, IReadOnlyList<InkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ValidateWidth(pageWidthPt);
        var line = ValidLineHeight(pageIndex);

        var lines = new List<LineGroup>();
        var others = new List<InkStroke>();
        var highlights = new List<InkStroke>();
        foreach (var s in strokes)
        {
            if (s.Tool == InkTool.Highlighter)
            {
                highlights.Add(s);
            }
            else if (TryMarginLine(s, pageWidthPt, line, out var side, out var top, out var bottom))
            {
                var box = Bounds(s);
                lines.Add(new LineGroup(side, top, bottom, box.MinX, box.MaxX, [s.Id]));
            }
            else
            {
                others.Add(s);
            }
        }

        var groups = Merge(lines, line);
        if (groups.Count > 0)
        {
            others = AbsorbPasses(groups, others, pageWidthPt, line);
            groups = Merge(groups, line); // a pass may close the gap between two marks
        }

        var marks = groups
            .Select(g => new Mark(pageIndex, g.Side, g.Top, g.Bottom, g.Ids))
            .Concat(HighlightMarks(pageIndex, pageWidthPt, highlights, out var smallHighlights))
            .OrderBy(m => m.TopPt)
            .ThenBy(m => m.Side)
            .ToList();
        if (smallHighlights.Count > 0)
        {
            var noteIds = smallHighlights.Concat(others.Select(s => s.Id)).ToHashSet();
            others = strokes.Where(s => noteIds.Contains(s.Id)).ToList(); // in the page's z-order, as before
        }

        return (marks, GroupNotes(pageIndex, line, others));
    }

    /// <summary>
    /// Highlights as marks (FR-100): highlighter strokes whose inked extents touch or overlap vertically form a
    /// cluster; a cluster at least <see cref="MarkRules.HighlightMinHeightPt"/> tall and
    /// <see cref="MarkRules.HighlightMinWidthPt"/> wide (inclusive) is a mark, on <see cref="MarkSide.Centre"/> when its
    /// horizontal centre lies in the middle <see cref="MarkRules.HighlightCentreBandFraction"/> of the page, else on
    /// the side of its centre. The strokes of smaller clusters are returned in <paramref name="small"/> for the notes.
    /// </summary>
    private static List<Mark> HighlightMarks(int pageIndex, double pageWidthPt, List<InkStroke> highlights, out List<Guid> small)
    {
        var marks = new List<Mark>();
        small = [];
        if (highlights.Count == 0)
        {
            return marks;
        }

        var boxes = highlights
            .Select((s, z) => (Stroke: s, Z: z, Box: InkBounds(s)))
            .OrderBy(b => b.Box.MinY)
            .ThenBy(b => b.Z)
            .ToList();
        var ids = new List<Guid>();
        double minX = 0, maxX = 0, top = 0, bottom = 0;
        foreach (var (stroke, _, box) in boxes)
        {
            if (ids.Count > 0 && box.MinY > bottom)
            {
                Close(small);
                ids = [];
            }

            if (ids.Count == 0)
            {
                (minX, maxX, top, bottom) = (box.MinX, box.MaxX, box.MinY, box.MaxY);
            }
            else
            {
                minX = Math.Min(minX, box.MinX);
                maxX = Math.Max(maxX, box.MaxX);
                bottom = Math.Max(bottom, box.MaxY);
            }

            ids.Add(stroke.Id);
        }

        Close(small);
        return marks;

        void Close(List<Guid> rest)
        {
            if (bottom - top >= MarkRules.HighlightMinHeightPt && maxX - minX >= MarkRules.HighlightMinWidthPt)
            {
                marks.Add(new Mark(pageIndex, HighlightSide((minX + maxX) / 2, pageWidthPt), top, bottom, ids));
            }
            else
            {
                rest.AddRange(ids);
            }
        }
    }

    private static MarkSide HighlightSide(double centreX, double pageWidthPt)
    {
        var halfBand = MarkRules.HighlightCentreBandFraction / 2 * pageWidthPt;
        var middle = pageWidthPt / 2;
        if (Math.Abs(centreX - middle) <= halfBand)
        {
            return MarkSide.Centre;
        }

        return centreX < middle ? MarkSide.Left : MarkSide.Right;
    }

    /// <summary>
    /// The inked box of a highlighter stroke: its points grown by the tip, half the width on every side for a round
    /// tip, the chisel nib's half-extents (its segment at the fixed angle plus its rounded thickness) otherwise.
    /// </summary>
    private static (double MinX, double MaxX, double MinY, double MaxY) InkBounds(InkStroke s)
    {
        var (minX, maxX, minY, maxY) = Bounds(s);
        double growX, growY;
        if (s.Chisel is { } chisel)
        {
            var theta = chisel.AngleDeg * Math.PI / 180;
            var segment = (1 - chisel.Ratio) * s.BaseWidthPt / 2;
            var round = chisel.Ratio * s.BaseWidthPt / 2;
            growX = (segment * Math.Abs(Math.Cos(theta))) + round;
            growY = (segment * Math.Abs(Math.Sin(theta))) + round;
        }
        else
        {
            growX = growY = s.BaseWidthPt / 2;
        }

        return (minX - growX, maxX + growX, minY - growY, maxY + growY);
    }

    /// <summary>Lines (or groups) on one side whose vertical gap is at most <see cref="MarkRules.MergeGapLines"/> merge.</summary>
    private static List<LineGroup> Merge(List<LineGroup> items, double line)
    {
        var merged = new List<LineGroup>();
        foreach (var side in items.GroupBy(l => l.Side))
        {
            LineGroup? current = null;
            foreach (var l in side.OrderBy(l => l.Top))
            {
                if (current is not null && l.Top - current.Bottom <= MarkRules.MergeGapLines * line)
                {
                    current.Add(l.Top, l.Bottom, l.MinX, l.MaxX, l.Ids);
                    continue;
                }

                current = new LineGroup(l.Side, l.Top, l.Bottom, l.MinX, l.MaxX, [.. l.Ids]);
                merged.Add(current);
            }
        }

        return merged;
    }

    /// <summary>
    /// Strokes that are passes of a margin line, too short to be one by themselves (one line drawn in several
    /// strokes): at least <see cref="MarkRules.NoteMinSizeLines"/> tall, mostly vertical, in the same margin zone as
    /// a mark, within <see cref="MarkRules.PassReachLines"/> of its strokes horizontally and overlapping its extent or
    /// within <see cref="MarkRules.MergeGapLines"/> of it vertically. They join that mark (and may extend it) instead of
    /// becoming a note. Returns the strokes left for the notes, in their order.
    /// </summary>
    private static List<InkStroke> AbsorbPasses(List<LineGroup> groups, List<InkStroke> strokes, double pageWidthPt, double line)
    {
        var absorbed = new HashSet<Guid>();
        var gap = MarkRules.MergeGapLines * line;
        var reach = MarkRules.PassReachLines * line;
        foreach (var (s, box) in strokes.Select(s => (s, Bounds(s))).OrderBy(x => x.Item2.MinY))
        {
            var height = box.MaxY - box.MinY;
            if (s.Tool != InkTool.Pen || s.Points.Count < 2 || height < MarkRules.NoteMinSizeLines * line
                || !IsMostlyVertical(s.Points, height)
                || !TrySide(s.Points.Average(p => p.X), pageWidthPt, out var side))
            {
                continue;
            }

            var group = groups.FirstOrDefault(g =>
                g.Side == side
                && box.MinY - g.Bottom <= gap
                && g.Top - box.MaxY <= gap
                && Math.Max(box.MinX - g.MaxX, g.MinX - box.MaxX) <= reach);
            if (group is not null)
            {
                group.Add(box.MinY, box.MaxY, box.MinX, box.MaxX, [s.Id]);
                absorbed.Add(s.Id);
            }
        }

        return absorbed.Count == 0 ? strokes : strokes.Where(s => !absorbed.Contains(s.Id)).ToList();
    }

    private static List<Note> GroupNotes(int pageIndex, double line, List<InkStroke> strokes)
    {
        var boxes = strokes
            .Select((s, z) => (Stroke: s, Z: z, Box: Bounds(s)))
            .OrderBy(b => b.Box.MinY)
            .ThenBy(b => b.Z)
            .ToList();
        var notes = new List<Note>();
        var ids = new List<Guid>();
        double minX = 0, maxX = 0, top = 0, bottom = 0;
        foreach (var (stroke, _, box) in boxes)
        {
            if (ids.Count > 0 && box.MinY - bottom > MarkRules.NoteGapLines * line)
            {
                AddNote();
                ids = [];
            }

            if (ids.Count == 0)
            {
                (minX, maxX, top, bottom) = (box.MinX, box.MaxX, box.MinY, box.MaxY);
            }
            else
            {
                minX = Math.Min(minX, box.MinX);
                maxX = Math.Max(maxX, box.MaxX);
                bottom = Math.Max(bottom, box.MaxY);
            }

            ids.Add(stroke.Id);
        }

        if (ids.Count > 0)
        {
            AddNote();
        }

        return notes;

        void AddNote()
        {
            var min = MarkRules.NoteMinSizeLines * line;
            if (maxX - minX >= min || bottom - top >= min)
            {
                notes.Add(new Note(pageIndex, top, bottom, ids));
            }
        }
    }

    private static (double MinX, double MaxX, double MinY, double MaxY) Bounds(InkStroke s)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in s.Points)
        {
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        return (minX, maxX, minY, maxY);
    }

    /// <summary>
    /// The pen strokes that are not margin lines (nor passes of this mark's line) and sit near the mark: its margin note. Highlighter strokes
    /// are never margin notes, and a <see cref="MarkSide.Centre"/> mark (a highlight in the middle of the page) has none;
    /// strokes without points are ignored.
    /// An invalid page width or line height throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public IReadOnlyList<InkStroke> MarginNotes(Mark mark, double pageWidthPt, IReadOnlyList<InkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentNullException.ThrowIfNull(strokes);
        ValidateWidth(pageWidthPt);
        var line = ValidLineHeight(mark.PageIndex);
        if (mark.Side == MarkSide.Centre)
        {
            return []; // a highlight in the middle of the page has no margin to write in
        }

        var windowTop = mark.TopPt - (MarkRules.NoteWindowLines * line);
        var windowBottom = mark.BottomPt + (MarkRules.NoteWindowLines * line);
        var reach = (MarkRules.MarginZoneFraction + MarkRules.NoteReachFraction) * pageWidthPt;

        var own = mark.StrokeIds.ToHashSet();
        var notes = new List<InkStroke>();
        foreach (var s in strokes)
        {
            if (s.Tool != InkTool.Pen || s.Points.Count == 0 || own.Contains(s.Id) || TryMarginLine(s, pageWidthPt, line, out _, out _, out _))
            {
                continue;
            }

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in s.Points)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }

            var inWindow = minY >= windowTop && maxY <= windowBottom;
            var inReach = mark.Side == MarkSide.Left ? maxX <= reach : minX >= pageWidthPt - reach;
            if (inWindow && inReach)
            {
                notes.Add(s);
            }
        }

        return notes;
    }

    private static void ValidateWidth(double pageWidthPt)
    {
        if (!double.IsFinite(pageWidthPt) || pageWidthPt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageWidthPt), "The page width must be finite and positive.");
        }
    }

    /// <summary>A metrics provider returning a non-finite or non-positive height is a bug: throw, do not guess.</summary>
    private double ValidLineHeight(int pageIndex)
    {
        var line = _metrics.LineHeightPt(pageIndex);
        if (!double.IsFinite(line) || line <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "The line height must be finite and positive.");
        }

        return line;
    }

    private static bool TryMarginLine(
        InkStroke s, double pageWidthPt, double line, out MarkSide side, out double top, out double bottom)
    {
        side = MarkSide.Left;
        top = bottom = 0;
        if (s.Tool != InkTool.Pen || s.Points.Count < 2)
        {
            return false;
        }

        var pts = s.Points;
        double sumX = 0;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (var i = 0; i < pts.Count; i++)
        {
            sumX += pts[i].X;
            minY = Math.Min(minY, pts[i].Y);
            maxY = Math.Max(maxY, pts[i].Y);
        }

        var height = maxY - minY;
        if (height <= 0 || height < MarkRules.MinExtentLines * line)
        {
            return false;
        }

        if (!IsMostlyVertical(pts, height) || !TrySide(sumX / pts.Count, pageWidthPt, out side))
        {
            return false;
        }

        top = minY;
        bottom = maxY;
        return true;
    }

    /// <summary>
    /// Mostly vertical movement: a straight, tilted, slightly wavy or retraced line, a bracket. Zig-zags, scribbles,
    /// loops and horizontal strokes move sideways about as much as down, or more.
    /// </summary>
    private static bool IsMostlyVertical(IReadOnlyList<InkPoint> pts, double height)
    {
        var (across, along) = ResampledTravel(pts, MarkRules.PathSampleSpacingFraction * height);
        return across <= Math.Tan(MarkRules.MaxDeviationFromVerticalDegrees * Math.PI / 180) * along;
    }

    /// <summary>The outer margin zone a mean x falls in, if any.</summary>
    private static bool TrySide(double meanX, double pageWidthPt, out MarkSide side)
    {
        var zone = MarkRules.MarginZoneFraction * pageWidthPt;
        side = meanX <= zone ? MarkSide.Left : MarkSide.Right;
        return meanX <= zone || meanX >= pageWidthPt - zone;
    }

    /// <summary>
    /// The horizontal and vertical travel (sums of |dx| and |dy|) over the points kept at least
    /// <paramref name="spacing"/> apart (the first, each one that far from the last kept, and the last): the shape of the
    /// stroke without the digitizer's sample-to-sample jitter, so the result does not depend on the sampling density
    /// (see <see cref="MarkRules.PathSampleSpacingFraction"/>).
    /// </summary>
    private static (double Across, double Along) ResampledTravel(IReadOnlyList<InkPoint> pts, double spacing)
    {
        double across = 0, along = 0;
        var kept = pts[0];
        for (var i = 1; i < pts.Count; i++)
        {
            var ddx = Math.Abs(pts[i].X - kept.X);
            var ddy = Math.Abs(pts[i].Y - kept.Y);
            if (Math.Sqrt((ddx * ddx) + (ddy * ddy)) >= spacing || i == pts.Count - 1)
            {
                across += ddx;
                along += ddy;
                kept = pts[i];
            }
        }

        return (across, along);
    }

    /// <summary>A mark being built: its side, extent, horizontal span and strokes.</summary>
    private sealed class LineGroup(MarkSide side, double top, double bottom, double minX, double maxX, List<Guid> ids)
    {
        public MarkSide Side { get; } = side;

        public double Top { get; private set; } = top;

        public double Bottom { get; private set; } = bottom;

        public double MinX { get; private set; } = minX;

        public double MaxX { get; private set; } = maxX;

        public List<Guid> Ids { get; } = ids;

        public void Add(double top, double bottom, double minX, double maxX, IEnumerable<Guid> ids)
        {
            Top = Math.Min(Top, top);
            Bottom = Math.Max(Bottom, bottom);
            MinX = Math.Min(MinX, minX);
            MaxX = Math.Max(MaxX, maxX);
            Ids.AddRange(ids);
        }
    }
}
