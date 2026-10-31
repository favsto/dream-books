using DreamBooks.Core.Model;

namespace DreamBooks.Core.Marks;

/// <summary>Turns the ink strokes of one page into marks (margin lines, FR-100, FR-104).</summary>
public sealed class MarkRecognizer(IPageLineMetrics? metrics = null)
{
    private readonly IPageLineMetrics _metrics = metrics ?? new FixedLineMetrics();

    public IReadOnlyList<Mark> Recognize(int pageIndex, double pageWidthPt, IReadOnlyList<InkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ValidateWidth(pageWidthPt);
        var line = ValidLineHeight(pageIndex);

        var lines = new List<(MarkSide Side, double Top, double Bottom, Guid Id)>();
        foreach (var s in strokes)
        {
            if (TryMarginLine(s, pageWidthPt, line, out var side, out var top, out var bottom))
            {
                lines.Add((side, top, bottom, s.Id));
            }
        }

        var marks = new List<Mark>();
        foreach (var group in lines.GroupBy(l => l.Side))
        {
            var ids = new List<Guid>();
            double top = 0, bottom = 0;
            foreach (var l in group.OrderBy(l => l.Top))
            {
                if (ids.Count > 0 && l.Top - bottom <= MarkRules.MergeGapLines * line)
                {
                    bottom = Math.Max(bottom, l.Bottom);
                    ids.Add(l.Id);
                    continue;
                }

                if (ids.Count > 0)
                {
                    marks.Add(new Mark(pageIndex, group.Key, top, bottom, ids));
                }

                ids = [l.Id];
                top = l.Top;
                bottom = l.Bottom;
            }

            marks.Add(new Mark(pageIndex, group.Key, top, bottom, ids));
        }

        return marks.OrderBy(m => m.TopPt).ThenBy(m => m.Side).ToList();
    }

    /// <summary>
    /// The pen strokes that are not margin lines and sit near the mark: its margin note. Highlighter strokes
    /// are not notes (the 3c highlighter source handles them); strokes without points are ignored.
    /// An invalid page width or line height throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public IReadOnlyList<InkStroke> MarginNotes(Mark mark, double pageWidthPt, IReadOnlyList<InkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentNullException.ThrowIfNull(strokes);
        ValidateWidth(pageWidthPt);
        var line = ValidLineHeight(mark.PageIndex);
        var windowTop = mark.TopPt - (MarkRules.NoteWindowLines * line);
        var windowBottom = mark.BottomPt + (MarkRules.NoteWindowLines * line);
        var reach = (MarkRules.MarginZoneFraction + MarkRules.NoteReachFraction) * pageWidthPt;

        var notes = new List<InkStroke>();
        foreach (var s in strokes)
        {
            if (s.Tool != InkTool.Pen || s.Points.Count == 0 || TryMarginLine(s, pageWidthPt, line, out _, out _, out _))
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
        double path = 0, sumX = 0;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (var i = 0; i < pts.Count; i++)
        {
            sumX += pts[i].X;
            minY = Math.Min(minY, pts[i].Y);
            maxY = Math.Max(maxY, pts[i].Y);
            if (i > 0)
            {
                var ddx = pts[i].X - pts[i - 1].X;
                var ddy = pts[i].Y - pts[i - 1].Y;
                path += Math.Sqrt((ddx * ddx) + (ddy * ddy));
            }
        }

        var dx = pts[^1].X - pts[0].X;
        var dy = pts[^1].Y - pts[0].Y;
        var dist = Math.Sqrt((dx * dx) + (dy * dy));
        if (dist <= 0 || path > MarkRules.MaxPathToDistanceRatio * dist)
        {
            return false;
        }

        var angle = Math.Atan2(Math.Abs(dx), Math.Abs(dy)) * 180 / Math.PI;
        if (angle > MarkRules.MaxDeviationFromVerticalDegrees || maxY - minY < MarkRules.MinExtentLines * line)
        {
            return false;
        }

        var meanX = sumX / pts.Count;
        var zone = MarkRules.MarginZoneFraction * pageWidthPt;
        if (meanX <= zone)
        {
            side = MarkSide.Left;
        }
        else if (meanX >= pageWidthPt - zone)
        {
            side = MarkSide.Right;
        }
        else
        {
            return false;
        }

        top = minY;
        bottom = maxY;
        return true;
    }
}
