namespace DreamBooks.Core.Marks;

/// <summary>
/// Document-wide index of recognised marks, filled page by page by a background scanner and read
/// by the UI. The page table is copy-on-write: writers publish a new table of immutable per-page
/// arrays (each sorted by top) under a lock, readers grab the current reference and work on it
/// without locking. <see cref="Changed"/> is raised outside the lock. A page is "scanned" once it has
/// authoritative marks (possibly none); previous/next walks live in the marks view model.
/// </summary>
public sealed class MarkIndex
{
    private static readonly Mark[] _empty = [];

    private readonly object _gate = new();
    private Mark[]?[] _pages;

    public MarkIndex(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        _pages = new Mark[]?[pageCount];
    }

    public event EventHandler<MarkIndexChangedEventArgs>? Changed;

    public int ScannedCount => Pages.Count(p => p is not null);

    public int TotalMarkCount => Pages.Sum(p => p?.Length ?? 0);

    private Mark[]?[] Pages
    {
        get
        {
            lock (_gate)
            {
                return _pages;
            }
        }
    }

    public void SetPage(int pageIndex, IReadOnlyList<Mark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        var sorted = marks.OrderBy(m => m.TopPt).ThenBy(m => m.BottomPt).ToArray();
        lock (_gate)
        {
            CheckPage(pageIndex);
            var current = _pages[pageIndex];
            if (current is not null && current.AsSpan().SequenceEqual(sorted))
            {
                return;
            }

            Publish(pageIndex, sorted);
        }

        Changed?.Invoke(this, new MarkIndexChangedEventArgs(pageIndex));
    }

    /// <summary>
    /// Sets the page only if nothing set it yet (it is not scanned), atomically: the background scan uses it
    /// so that marks it computed from strokes loaded earlier never overwrite a live update made meanwhile.
    /// Returns false, changing nothing, when the page is already scanned.
    /// </summary>
    public bool TrySetPageIfUnscanned(int pageIndex, IReadOnlyList<Mark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        var sorted = marks.OrderBy(m => m.TopPt).ThenBy(m => m.BottomPt).ToArray();
        lock (_gate)
        {
            CheckPage(pageIndex);
            if (_pages[pageIndex] is not null)
            {
                return false;
            }

            Publish(pageIndex, sorted);
        }

        Changed?.Invoke(this, new MarkIndexChangedEventArgs(pageIndex));
        return true;
    }

    public bool IsScanned(int pageIndex)
    {
        var pages = Pages;
        return pageIndex >= 0 && pageIndex < pages.Length && pages[pageIndex] is not null;
    }

    public IReadOnlyList<Mark> AllMarks() => Pages.SelectMany(p => p ?? _empty).ToArray();

    public IReadOnlyList<Mark> MarksOnPage(int pageIndex)
    {
        var pages = Pages;
        return pageIndex >= 0 && pageIndex < pages.Length ? pages[pageIndex] ?? _empty : _empty;
    }

    // Must be called with the lock held.
    private void Publish(int pageIndex, Mark[]? marks)
    {
        var copy = (Mark[]?[])_pages.Clone();
        copy[pageIndex] = marks;
        _pages = copy;
    }

    // Must be called with the lock held.
    private void CheckPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _pages.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }
    }
}
