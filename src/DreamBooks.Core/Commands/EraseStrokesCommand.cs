using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Commands;

/// <summary>Replaces the page strokes with <c>after</c>; undo restores <c>before</c> in its original order.</summary>
public sealed class EraseStrokesCommand : IUndoableCommand
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
    }

    public string Description => "Erase";

    public void Do() => _page.SetStrokes(_after);

    public void Undo() => _page.SetStrokes(_before);
}
