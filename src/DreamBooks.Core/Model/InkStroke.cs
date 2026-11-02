namespace DreamBooks.Core.Model;

public sealed class InkStroke
{
    public InkStroke(Guid id, InkTool tool, InkColor color, double baseWidthPt, IReadOnlyList<InkPoint> points, ChiselTip? chisel = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("A stroke needs at least one point.", nameof(points));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseWidthPt, 0);

        Id = id;
        Tool = tool;
        Color = color;
        BaseWidthPt = baseWidthPt;
        Points = points.ToArray();
        Chisel = chisel;
    }

    public Guid Id { get; }

    public InkTool Tool { get; }

    public InkColor Color { get; }

    public double BaseWidthPt { get; }

    public IReadOnlyList<InkPoint> Points { get; }

    /// <summary>Chisel nib (FR-46); null for round tips and pen strokes.</summary>
    public ChiselTip? Chisel { get; }

    public InkStroke WithPoints(IReadOnlyList<InkPoint> points)
        => new(Guid.NewGuid(), Tool, Color, BaseWidthPt, points, Chisel);
}
