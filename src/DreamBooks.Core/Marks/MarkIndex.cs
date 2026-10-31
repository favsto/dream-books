namespace DreamBooks.Core.Marks;

/// <summary>
/// Document-wide index of recognised marks, filled page by page by a background scanner and read
/// by the UI. The page table is copy-on-write: writers publish a new table of immutable per-page
/// arrays (each sorted by top) under a lock, readers grab the current reference and work on it
/// without locking. <see cref="Changed"/> is raised outside the lock. Lookups binary-search the
/// page and then walk to the nearest scanned page that has marks; they never wrap around.
/// </summary>
public sealed class MarkIndex
{
    // Marks closer than this to the current position count as "here" when going backwards.
    private const double _previousToleranceInPoints = 1.0;

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

    public void ClearPage(int pageIndex)
    {
        lock (_gate)
        {
            CheckPage(pageIndex);
            if (_pages[pageIndex] is null)
            {
                return;
            }

            Publish(pageIndex, null);
        }

        Changed?.Invoke(this, new MarkIndexChangedEventArgs(pageIndex));
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

    /// <summary>First mark strictly after (page, y); false at the end of the document.</summary>
    public bool TryNext(int pageIndex, double yPt, out Mark mark)
    {
        var pages = Pages;
        for (var p = Math.Max(pageIndex, 0); p < pages.Length; p++)
        {
            var marks = pages[p];
            if (marks is null || marks.Length == 0)
            {
                continue;
            }

            var i = p == pageIndex ? FirstIndexWhere(marks, m => m.TopPt > yPt) : 0;
            if (i < marks.Length)
            {
                mark = marks[i];
                return true;
            }
        }

        mark = null!;
        return false;
    }

    /// <summary>
    /// Last mark whose top is more than 1 pt above (page, y), so calling again with the returned
    /// mark's top walks back one mark at a time; false at the start of the document.
    /// </summary>
    public bool TryPrevious(int pageIndex, double yPt, out Mark mark)
    {
        var pages = Pages;
        var limit = yPt - _previousToleranceInPoints;
        for (var p = Math.Min(pageIndex, pages.Length - 1); p >= 0; p--)
        {
            var marks = pages[p];
            if (marks is null || marks.Length == 0)
            {
                continue;
            }

            var i = p == pageIndex ? FirstIndexWhere(marks, m => m.TopPt >= limit) - 1 : marks.Length - 1;
            if (i >= 0)
            {
                mark = marks[i];
                return true;
            }
        }

        mark = null!;
        return false;
    }

    public void Reset(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        lock (_gate)
        {
            _pages = new Mark[]?[pageCount];
        }
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

    // Binary search over a sorted array for the first element satisfying a monotone predicate.
    private static int FirstIndexWhere(Mark[] marks, Func<Mark, bool> predicate)
    {
        int lo = 0, hi = marks.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (predicate(marks[mid]))
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }
}
