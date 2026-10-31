namespace DreamBooks.Core.Marks;

/// <summary>Uses the same line height on every page.</summary>
public sealed class FixedLineMetrics(double lineHeightPt = MarkRules.DefaultLineHeightPt) : IPageLineMetrics
{
    public double LineHeightPt(int pageIndex) => lineHeightPt;
}
