namespace DreamBooks.Core.Marks;

/// <summary>
/// Document-wide index of the recognised marks and notes (FR-100, FR-101), filled page by page by a background
/// scanner and the live tracker, and read by the UI. The page table is copy-on-write: writers publish a new table of
/// immutable per-page entries (marks and notes, each sorted by top) under a lock, readers grab the current reference
/// and work on it without locking, so a page's marks and notes are always read together. <see cref="Changed"/> is
/// raised outside the lock. A page is "scanned" once it has authoritative marks and notes (possibly none);
/// previous/next walks live in the marks view model.
/// </summary>
public sealed class MarkIndex
{
    private static readonly Mark[] _noMarks = [];
    private static readonly Note[] _noNotes = [];

    private readonly object _gate = new();
    private PageEntry?[] _pages;

    public MarkIndex(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        _pages = new PageEntry?[pageCount];
    }

    public event EventHandler<MarkIndexChangedEventArgs>? Changed;

    public int ScannedCount => Pages.Count(p => p is not null);

    public int TotalMarkCount => Pages.Sum(p => p?.Marks.Length ?? 0);

    public int TotalNoteCount => Pages.Sum(p => p?.Notes.Length ?? 0);

    private PageEntry?[] Pages
    {
        get
        {
            lock (_gate)
            {
                return _pages;
            }
        }
    }

    /// <summary>
    /// Replaces the page's marks and notes (its whole recognition: <paramref name="notes"/> null means none). Equal
    /// marks and notes (in any order) change nothing and raise nothing.
    /// </summary>
    public void SetPage(int pageIndex, IReadOnlyList<Mark> marks, IReadOnlyList<Note>? notes = null)
    {
        var entry = PageEntry.Create(marks, notes);
        lock (_gate)
        {
            CheckPage(pageIndex);
            if (_pages[pageIndex] is { } current && current.SameAs(entry))
            {
                return;
            }

            Publish(pageIndex, entry);
        }

        Changed?.Invoke(this, new MarkIndexChangedEventArgs(pageIndex));
    }

    /// <summary>
    /// Sets the page only if nothing set it yet (it is not scanned), atomically: the background scan uses it
    /// so that marks and notes it computed from strokes loaded earlier never overwrite a live update made meanwhile.
    /// Returns false, changing nothing, when the page is already scanned.
    /// </summary>
    public bool TrySetPageIfUnscanned(int pageIndex, IReadOnlyList<Mark> marks, IReadOnlyList<Note>? notes = null)
    {
        var entry = PageEntry.Create(marks, notes);
        lock (_gate)
        {
            CheckPage(pageIndex);
            if (_pages[pageIndex] is not null)
            {
                return false;
            }

            Publish(pageIndex, entry);
        }

        Changed?.Invoke(this, new MarkIndexChangedEventArgs(pageIndex));
        return true;
    }

    public bool IsScanned(int pageIndex)
    {
        var pages = Pages;
        return pageIndex >= 0 && pageIndex < pages.Length && pages[pageIndex] is not null;
    }

    public IReadOnlyList<Mark> AllMarks() => Pages.SelectMany(p => p?.Marks ?? _noMarks).ToArray();

    public IReadOnlyList<Mark> MarksOnPage(int pageIndex)
    {
        var pages = Pages;
        return pageIndex >= 0 && pageIndex < pages.Length ? pages[pageIndex]?.Marks ?? _noMarks : _noMarks;
    }

    /// <summary>Every note, by page then top.</summary>
    public IReadOnlyList<Note> AllNotes() => Pages.SelectMany(p => p?.Notes ?? _noNotes).ToArray();

    public IReadOnlyList<Note> NotesOnPage(int pageIndex)
    {
        var pages = Pages;
        return pageIndex >= 0 && pageIndex < pages.Length ? pages[pageIndex]?.Notes ?? _noNotes : _noNotes;
    }

    // Must be called with the lock held.
    private void Publish(int pageIndex, PageEntry entry)
    {
        var copy = (PageEntry?[])_pages.Clone();
        copy[pageIndex] = entry;
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

    /// <summary>One page's recognition, immutable: both lists sorted by top, then bottom.</summary>
    private sealed class PageEntry(Mark[] marks, Note[] notes)
    {
        public Mark[] Marks { get; } = marks;

        public Note[] Notes { get; } = notes;

        public static PageEntry Create(IReadOnlyList<Mark> marks, IReadOnlyList<Note>? notes)
        {
            ArgumentNullException.ThrowIfNull(marks);
            return new PageEntry(
                marks.OrderBy(m => m.TopPt).ThenBy(m => m.BottomPt).ToArray(),
                notes is null ? _noNotes : notes.OrderBy(n => n.TopPt).ThenBy(n => n.BottomPt).ToArray());
        }

        public bool SameAs(PageEntry other) =>
            Marks.AsSpan().SequenceEqual(other.Marks) && Notes.AsSpan().SequenceEqual(other.Notes);
    }
}
