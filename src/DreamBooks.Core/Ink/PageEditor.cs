using DreamBooks.Core.Commands;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Ink;

/// <summary>Applies ink edits to a page through the undo manager (FR-27, FR-63).</summary>
public sealed class PageEditor
{
    private readonly UndoManager _undo;

    public PageEditor(Page page, UndoManager undo)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(undo);
        Page = page;
        _undo = undo;
    }

    public Page Page { get; }

    /// <summary>
    /// Clips the stroke to the page and adds the surviving pieces as one undo step.
    /// Returns false, executing nothing, if no piece survives.
    /// </summary>
    public bool CommitStroke(IReadOnlyList<InkPoint> points, InkTool tool, InkColor color, double baseWidthPt)
    {
        var pieces = StrokeClipper.Clip(points, Page.WidthPt, Page.HeightPt);
        if (pieces.Count == 0)
        {
            return false;
        }

        var strokes = pieces
            .Select(piece => new InkStroke(Guid.NewGuid(), tool, color, baseWidthPt, piece))
            .ToArray();
        _undo.Execute(new AddStrokesCommand(Page, strokes));
        return true;
    }
}
