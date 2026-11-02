using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>
/// Committed highlighter strokes, under the pen ink and above the search highlights (FR-40): the constant-width
/// outline for round tips (FR-42) or the chisel outline (FR-46), the same shapes the saved annotation carries, filled
/// with the stroke colour and its alpha. Not hit-testable, no automation peer; erase and undo update it through the
/// same per-stroke bookkeeping as the pen layer.
/// </summary>
/// <remarks>
/// The fill stays translucent also with Windows transparency effects off (NFR-32): it is a highlight colour, not a
/// transparency effect, and an opaque highlight would hide the text it marks.
/// </remarks>
public sealed class HighlighterLayer : RetainedStrokeLayer
{
    protected override bool Draws(InkStroke stroke) => stroke.Tool == InkTool.Highlighter;

    protected override IReadOnlyList<IReadOnlyList<PointD>> BuildFigures(InkStroke stroke) =>
        [HighlighterGeometry.Outline(stroke)];
}
