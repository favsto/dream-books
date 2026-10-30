namespace DreamBooks.Core.Model;

public sealed class Page
{
    private IReadOnlyList<InkStroke> _strokes = [];

    public Page(int index, double widthPt, double heightPt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(widthPt, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heightPt, 0);
        Index = index;
        WidthPt = widthPt;
        HeightPt = heightPt;
    }

    public event EventHandler? StrokesChanged;

    public int Index { get; }

    public double WidthPt { get; }

    public double HeightPt { get; }

    public IReadOnlyList<InkStroke> Strokes => _strokes;

    internal void SetStrokes(IReadOnlyList<InkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        _strokes = strokes.ToArray();
        StrokesChanged?.Invoke(this, EventArgs.Empty);
    }
}
