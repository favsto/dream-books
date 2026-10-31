using System.Collections.ObjectModel;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Commands;

/// <summary>Appends strokes on top of the page (z-order). Undo restores the exact previous list.</summary>
public sealed class AddStrokesCommand : IPageCommand
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
        Strokes = new ReadOnlyCollection<InkStroke>(_strokes);
    }

    public string Description => "Add stroke";

    public int PageIndex => _page.Index;

    /// <summary>The strokes this command adds, in order.</summary>
    public IReadOnlyList<InkStroke> Strokes { get; }

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
