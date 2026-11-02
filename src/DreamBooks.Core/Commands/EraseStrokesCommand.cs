using System.Collections.ObjectModel;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Commands;

/// <summary>
/// Replaces the page strokes with <c>after</c>; undo restores <c>before</c> in its original order. Used by the
/// eraser and by highlighter straightening (FR-44); the journal records it as a replacement either way.
/// </summary>
public sealed class EraseStrokesCommand : IPageCommand
{
    private readonly Page _page;
    private readonly InkStroke[] _before;
    private readonly InkStroke[] _after;

    /// <param name="description">The undo step's description; straightening a highlighter (FR-44) reuses this replacement.</param>
    public EraseStrokesCommand(Page page, IReadOnlyList<InkStroke> before, IReadOnlyList<InkStroke> after, string description = "Erase")
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(description);
        _page = page;
        Description = description;
        _before = before.ToArray();
        _after = after.ToArray();
        Before = new ReadOnlyCollection<InkStroke>(_before);
        After = new ReadOnlyCollection<InkStroke>(_after);
    }

    public string Description { get; }

    public int PageIndex => _page.Index;

    /// <summary>The page strokes before the erase, in z-order.</summary>
    public IReadOnlyList<InkStroke> Before { get; }

    /// <summary>The page strokes after the erase, in z-order.</summary>
    public IReadOnlyList<InkStroke> After { get; }

    public void Do() => _page.SetStrokes(_after);

    public void Undo() => _page.SetStrokes(_before);
}
