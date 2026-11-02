using DreamBooks.Core.Commands;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Ink;

/// <summary>
/// One eraser gesture. Works on a copy of the page strokes and commits one undo step on <see cref="Complete"/>.
/// The page is not modified before then (FR-33, FR-63).
/// </summary>
public sealed class EraserSession
{
    private readonly Page _page;
    private readonly UndoManager _undo;
    private readonly EraserMode _mode;
    private readonly InkStroke[] _before;
    private List<Entry> _entries;
    private IReadOnlyList<InkStroke> _strokes;
    private PointD? _last;
    private bool _changed;
    private bool _finished;

    internal EraserSession(Page page, UndoManager undo, EraserMode mode)
    {
        _page = page;
        _undo = undo;
        _mode = mode;
        _before = page.Strokes.ToArray();
        _entries = _before.Select(s => new Entry(s, EraserGeometry.Bounds.Of(s))).ToList();
        _strokes = _before;
    }

    /// <summary>The working copy of the page strokes, for the view to draw.</summary>
    public IReadOnlyList<InkStroke> Strokes => _strokes;

    /// <summary>
    /// Sweeps the eraser from the previous center (or from this point, on the first call) to this one.
    /// Returns true if <see cref="Strokes"/> changed.
    /// </summary>
    public bool MoveTo(PointD centerPt, double radiusPt)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The eraser session is finished.");
        }

        var from = _last ?? centerPt;
        _last = centerPt;
        var changed = false;
        var result = new List<Entry>(_entries.Count);
        foreach (var entry in _entries)
        {
            if (_mode == EraserMode.WholeStroke)
            {
                if (EraserGeometry.HitsStroke(entry.Stroke, entry.Bounds, from, centerPt, radiusPt))
                {
                    changed = true;
                }
                else
                {
                    result.Add(entry);
                }

                continue;
            }

            var pieces = EraserGeometry.ErasePartial(entry.Stroke, entry.Bounds, from, centerPt, radiusPt);
            if (pieces.Count == 1 && ReferenceEquals(pieces[0], entry.Stroke))
            {
                result.Add(entry);
                continue;
            }

            changed = true;
            result.AddRange(pieces.Select(p => new Entry(p, EraserGeometry.Bounds.Of(p))));
        }

        if (changed)
        {
            _entries = result;
            _strokes = result.Select(e => e.Stroke).ToArray();
            _changed = true;
        }

        return changed;
    }

    /// <summary>Commits the gesture as one undo step. Returns false, executing nothing, if nothing was erased.</summary>
    public bool Complete()
    {
        _finished = true;
        if (!_changed)
        {
            return false;
        }

        _undo.Execute(new EraseStrokesCommand(_page, _before, _strokes));
        return true;
    }

    public void Cancel() => _finished = true;

    private readonly record struct Entry(InkStroke Stroke, EraserGeometry.Bounds Bounds);
}
