using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Commands;

/// <summary>Appends strokes on top of the page (z-order). Undo restores the exact previous list.</summary>
public sealed class AddStrokesCommand : IUndoableCommand
{
    private readonly Page _page;
    private readonly InkStroke[] _strokes;
    private IReadOnlyList<InkStroke>? _previous;

    public AddStrokesCommand(Page page, IReadOnlyList<InkStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(strokes);
        _page = page;
        _strokes = strokes.ToArray();
    }

    public string Description => "Add stroke";

    public void Do()
    {
        var previous = _page.Strokes;
        _page.SetStrokes([.. previous, .. _strokes]);
        _previous = previous;
    }

    public void Undo()
    {
        if (_previous is null)
        {
            return;
        }

        _page.SetStrokes(_previous);
    }
}
