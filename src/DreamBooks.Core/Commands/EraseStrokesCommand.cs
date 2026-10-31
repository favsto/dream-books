using System.Collections.ObjectModel;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Commands;

/// <summary>Replaces the page strokes with <c>after</c>; undo restores <c>before</c> in its original order.</summary>
public sealed class EraseStrokesCommand : IPageCommand
{
    private readonly Page _page;
    private readonly InkStroke[] _before;
    private readonly InkStroke[] _after;

    public EraseStrokesCommand(Page page, IReadOnlyList<InkStroke> before, IReadOnlyList<InkStroke> after)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        _page = page;
        _before = before.ToArray();
        _after = after.ToArray();
        Before = new ReadOnlyCollection<InkStroke>(_before);
        After = new ReadOnlyCollection<InkStroke>(_after);
    }

    public string Description => "Erase";

    public int PageIndex => _page.Index;

    /// <summary>The page strokes before the erase, in z-order.</summary>
    public IReadOnlyList<InkStroke> Before { get; }

    /// <summary>The page strokes after the erase, in z-order.</summary>
    public IReadOnlyList<InkStroke> After { get; }

    public void Do() => _page.SetStrokes(_after);

    public void Undo() => _page.SetStrokes(_before);
}
