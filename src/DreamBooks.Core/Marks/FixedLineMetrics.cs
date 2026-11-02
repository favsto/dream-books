namespace DreamBooks.Core.Marks;

/// <summary>Uses the same line height on every page. The height must be finite and positive.</summary>
public sealed class FixedLineMetrics : IPageLineMetrics
{
    private readonly double _lineHeightPt;

    public FixedLineMetrics(double lineHeightPt = MarkRules.DefaultLineHeightPt)
    {
        if (!double.IsFinite(lineHeightPt) || lineHeightPt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineHeightPt));
        }

        _lineHeightPt = lineHeightPt;
    }

    public double LineHeightPt(int pageIndex) => _lineHeightPt;
}
