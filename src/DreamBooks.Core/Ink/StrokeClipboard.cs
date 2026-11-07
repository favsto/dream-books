using DreamBooks.Core.Model;

namespace DreamBooks.Core.Ink;

/// <summary>The app's own copy buffer for strokes (FR-51); page-point coordinates, no system clipboard.</summary>
public sealed class StrokeClipboard
{
    private IReadOnlyList<InkStroke> _strokes = [];

    public event EventHandler? Changed;

    public bool IsEmpty => _strokes.Count == 0;

    public IReadOnlyList<InkStroke> Strokes => _strokes;

    public void Set(IReadOnlyList<InkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        _strokes = strokes.ToArray();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
