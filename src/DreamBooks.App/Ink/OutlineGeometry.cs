using System.Windows;
using System.Windows.Media;
using DreamBooks.Core.Geometry;

namespace DreamBooks.App.Ink;

/// <summary>Turns outline figures from Core into a frozen, non-zero filled geometry.</summary>
internal static class OutlineGeometry
{
    public static StreamGeometry ToStreamGeometry(IReadOnlyList<IReadOnlyList<PointD>> figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geometry.Open())
        {
            foreach (var figure in figures)
            {
                if (figure.Count < 3)
                {
                    continue;
                }

                ctx.BeginFigure(new Point(figure[0].X, figure[0].Y), isFilled: true, isClosed: true);
                var rest = new Point[figure.Count - 1];
                for (var i = 1; i < figure.Count; i++)
                {
                    rest[i - 1] = new Point(figure[i].X, figure[i].Y);
                }

                ctx.PolyLineTo(rest, isStroked: false, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
