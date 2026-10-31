using DreamBooks.Core.Documents;

namespace DreamBooks.Core.Marks;

/// <summary>
/// Keeps the <see cref="MarkIndex"/> in step with the pages loaded in a <see cref="DocumentSession"/>: every
/// stroke change on a loaded page (draw, erase, undo, redo) and every page that finishes loading re-recognises
/// that page from its in-memory strokes and replaces its marks (FR-100, FR-101, FR-104). Pages loaded before
/// the tracker was created are recognised by the constructor. Nothing is loaded from the PDF.
/// </summary>
/// <remarks>
/// <see cref="DocumentSession.PageStrokesChanged"/> may arrive on several threads. The tracker never blocks:
/// changed pages go into a set, and whichever thread finds nobody else working drains it, always reading the
/// page's current strokes, so the last write for a page reflects its latest strokes and an older result never
/// overwrites a newer one. Normally that is the UI thread, synchronously inside the edit (recognising one
/// page is cheap, pure CPU). Failures (an index reset to fewer pages, a recogniser error) are ignored for
/// that page and never reach the edit that raised the event.
/// </remarks>
public sealed class MarkTracker : IDisposable
{
    private readonly DocumentSession _session;
    private readonly MarkRecognizer _recognizer;
    private readonly MarkIndex _index;
    private readonly object _gate = new();
    private readonly HashSet<int> _dirty = [];
    private bool _draining;
    private bool _disposed;

    public MarkTracker(DocumentSession session, MarkRecognizer recognizer, MarkIndex index)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(index);
        _session = session;
        _recognizer = recognizer;
        _index = index;

        // Subscribe first, then pick up the pages already loaded, so that no change falls in between.
        _session.PageStrokesChanged += OnPageStrokesChanged;
        Enqueue(Enumerable.Range(0, session.PageCount).Where(session.IsPageLoaded).ToArray());
    }

    /// <summary>Stops listening to the session. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dirty.Clear();
        }

        _session.PageStrokesChanged -= OnPageStrokesChanged;
    }

    private void OnPageStrokesChanged(object? sender, PageStrokesChangedEventArgs e) => Enqueue([e.PageIndex]);

    private void Enqueue(int[] pages)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _dirty.UnionWith(pages);
            if (_draining || _dirty.Count == 0)
            {
                return; // the thread already draining will pick these pages up
            }

            _draining = true;
        }

        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            int page;
            lock (_gate)
            {
                if (_disposed || _dirty.Count == 0)
                {
                    _draining = false;
                    return;
                }

                page = _dirty.First();
                _dirty.Remove(page);
            }

            try
            {
                Update(page);
            }
            catch (Exception)
            {
                // E.g. the index was reset to fewer pages: this page keeps what it had.
            }
        }
    }

    private void Update(int pageIndex)
    {
        if (!_session.IsPageLoaded(pageIndex))
        {
            return;
        }

        // A loaded page is never unloaded, so this completes synchronously without touching the source.
        var ensure = _session.EnsurePageAsync(pageIndex);
        if (!ensure.IsCompletedSuccessfully)
        {
            return;
        }

        var page = ensure.Result;
        var marks = _recognizer.Recognize(pageIndex, page.WidthPt, page.Strokes);
        _index.SetPage(pageIndex, marks);
    }
}
