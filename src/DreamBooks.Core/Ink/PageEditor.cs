using DreamBooks.Core.Commands;
using DreamBooks.Core.Geometry;
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

    /// <summary>The settings used by <see cref="CommitHighlighterStroke"/> (FR-41..FR-46).</summary>
    public HighlighterSettings HighlighterSettings { get; private set; } = HighlighterSettings.Default;

    public void SetHighlighterSettings(HighlighterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        HighlighterSettings = settings;
    }

    /// <summary>
    /// Clips the stroke to the page and adds the surviving pieces as one undo step.
    /// Returns false, executing nothing, if no piece survives.
    /// </summary>
    public bool CommitStroke(IReadOnlyList<InkPoint> points, InkTool tool, InkColor color, double baseWidthPt)
        => Add(points, tool, color, baseWidthPt, chisel: null) is not null;

    /// <summary>
    /// Commits a highlighter stroke with <see cref="HighlighterSettings"/>: constant width whatever the pressure,
    /// translucent colour, chisel nib if chosen (FR-41, FR-42, FR-46). The drawn stroke is one undo step; with
    /// straight-line mode on, the pieces that qualify are then replaced by their straight version as a second
    /// step, so undo first restores the drawn stroke (FR-44). Returns false, executing nothing, if no piece survives.
    /// </summary>
    public bool CommitHighlighterStroke(IReadOnlyList<InkPoint> points)
    {
        var settings = HighlighterSettings;
        var added = Add(points, InkTool.Highlighter, settings.StrokeColor, settings.WidthPt, settings.Tip);
        if (added is null)
        {
            return false;
        }

        if (settings.StraightLine)
        {
            Straighten(added);
        }

        return true;
    }

    /// <summary>
    /// Removes every stroke of the page as one undo step ("Clear all on this page", UI-02): a replacement with no
    /// strokes, so undo restores them in their order and the journal records it like an erase. Returns false,
    /// executing nothing, when the page has no strokes.
    /// </summary>
    public bool ClearPage()
    {
        var before = Page.Strokes;
        if (before.Count == 0)
        {
            return false;
        }

        _undo.Execute(new EraseStrokesCommand(Page, before, [], "Clear page"));
        return true;
    }

    /// <summary>How far a pasted copy lands from its original, in page points (FR-51).</summary>
    public const double PasteOffsetPt = 12;

    /// <summary>The page strokes with these ids, in z-order.</summary>
    public IReadOnlyList<InkStroke> Strokes(IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return Page.Strokes.Where(s => ids.Contains(s.Id)).ToArray();
    }

    /// <summary>Moves the strokes as one undo step, keeping them on the page. Returns the new ids (FR-51).</summary>
    public IReadOnlyList<Guid> MoveStrokes(IReadOnlyCollection<Guid> ids, double dx, double dy)
        => Replace(ids, "Move", selected => StrokeTransform.Translate(selected, dx, dy, Page.WidthPt, Page.HeightPt));

    /// <summary>Resizes the strokes about <paramref name="anchor"/> as one undo step. Returns the new ids (FR-51).</summary>
    public IReadOnlyList<Guid> ScaleStrokes(IReadOnlyCollection<Guid> ids, double factor, PointD anchor)
        => Replace(ids, "Resize", selected => StrokeTransform.Scale(selected, factor, anchor, Page.WidthPt, Page.HeightPt));

    /// <summary>Recolours the strokes as one undo step. Returns the new ids (FR-51).</summary>
    public IReadOnlyList<Guid> RecolorStrokes(IReadOnlyCollection<Guid> ids, InkColor color)
        => Replace(ids, "Recolour", selected => StrokeTransform.Recolor(selected, color));

    /// <summary>Deletes the strokes as one undo step; nothing remains selected (FR-51).</summary>
    public IReadOnlyList<Guid> DeleteStrokes(IReadOnlyCollection<Guid> ids)
    {
        var selected = Strokes(ids);
        if (selected.Count == 0)
        {
            return [];
        }

        var before = Page.Strokes;
        var after = before.Where(s => !ids.Contains(s.Id)).ToArray();
        _undo.Execute(new EraseStrokesCommand(Page, before, after, "Delete selection"));
        return [];
    }

    /// <summary>
    /// Adds copies of the clipboard strokes, <see cref="PasteOffsetPt"/> from the originals and inside the page, as
    /// one undo step. Returns the new ids; an empty clipboard does nothing (FR-51).
    /// </summary>
    public IReadOnlyList<Guid> PasteStrokes(StrokeClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (clipboard.IsEmpty)
        {
            return [];
        }

        // Copies from a bigger page (or document) are shrunk to fit, so nothing is written outside this page.
        var source = clipboard.Strokes;
        if (SelectionGeometry.Bounds(source) is { } bounds)
        {
            var fit = Math.Min(
                bounds.Width > Page.WidthPt ? Page.WidthPt / bounds.Width : 1,
                bounds.Height > Page.HeightPt ? Page.HeightPt / bounds.Height : 1);
            if (fit < 1)
            {
                source = StrokeTransform.Scale(source, fit, new PointD(0, 0), Page.WidthPt, Page.HeightPt);
            }
        }

        var copies = StrokeTransform.Translate(source, PasteOffsetPt, PasteOffsetPt, Page.WidthPt, Page.HeightPt);
        _undo.Execute(new AddStrokesCommand(Page, copies));
        return copies.Select(s => s.Id).ToArray();
    }

    /// <summary>Starts an eraser gesture; nothing changes until <see cref="EraserSession.Complete"/> (FR-31).</summary>
    public EraserSession BeginErase(EraserMode mode) => new(Page, _undo, mode);

    private IReadOnlyList<Guid> Replace(
        IReadOnlyCollection<Guid> ids,
        string description,
        Func<IReadOnlyList<InkStroke>, IReadOnlyList<InkStroke>> transform)
    {
        var selected = Strokes(ids);
        if (selected.Count == 0)
        {
            return [];
        }

        var replaced = transform(selected);
        var replacements = new Dictionary<Guid, InkStroke>();
        for (var i = 0; i < selected.Count; i++)
        {
            replacements[selected[i].Id] = replaced[i];
        }

        var before = Page.Strokes;
        var after = before.Select(s => replacements.GetValueOrDefault(s.Id, s)).ToArray();
        _undo.Execute(new EraseStrokesCommand(Page, before, after, description));
        return replaced.Select(s => s.Id).ToArray();
    }

    private InkStroke[]? Add(IReadOnlyList<InkPoint> points, InkTool tool, InkColor color, double baseWidthPt, ChiselTip? chisel)
    {
        var pieces = StrokeClipper.Clip(points, Page.WidthPt, Page.HeightPt);
        if (pieces.Count == 0)
        {
            return null;
        }

        var strokes = pieces
            .Select(piece => new InkStroke(Guid.NewGuid(), tool, color, baseWidthPt, piece, chisel))
            .ToArray();
        _undo.Execute(new AddStrokesCommand(Page, strokes));
        return strokes;
    }

    private void Straighten(InkStroke[] added)
    {
        var replacements = new Dictionary<Guid, InkStroke>();
        foreach (var stroke in added)
        {
            if (HighlighterGeometry.StraightenIfNearHorizontal(stroke.Points) is { } straight
                && !straight.SequenceEqual(stroke.Points))
            {
                replacements[stroke.Id] = stroke.WithPoints(straight);
            }
        }

        if (replacements.Count == 0)
        {
            return;
        }

        var before = Page.Strokes;
        var after = before.Select(s => replacements.GetValueOrDefault(s.Id, s)).ToArray();
        _undo.Execute(new EraseStrokesCommand(Page, before, after, "Straighten stroke"));
    }
}
