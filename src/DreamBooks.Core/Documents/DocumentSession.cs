using DreamBooks.Core.Commands;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Documents;

/// <summary>
/// The whole-document model: pages loaded lazily from an <see cref="IPageSource"/>, one undo history
/// for the document, and dirty tracking by stroke Id against what was loaded or last saved
/// (FR-62, FR-83, NFR-02).
/// </summary>
/// <remarks>
/// Mutation and events belong to one thread (the UI thread). <see cref="EnsurePageAsync"/> may be awaited
/// concurrently: callers share one load per page, which runs without the callers' tokens so that one
/// caller cancelling does not cancel the load for the others; a failed load is not cached.
/// </remarks>
public sealed class DocumentSession : IDisposable
{
    private readonly IPageSource _source;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, LoadedPage> _pages = [];
    private readonly Dictionary<int, Task<LoadedPage>> _loads = [];
    private readonly Dictionary<int, PageEditor> _editors = [];
    private int _dirtyPages;
    private bool _isDirty;
    private int _navigation;
    private bool _disposed;

    public DocumentSession(IPageSource source, string path, DocumentFingerprint openFingerprint)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _source = source;
        Path = path;
        OpenFingerprint = openFingerprint;
        PageCount = source.PageCount;
        Undo = new UndoManager();
        Undo.CommandApplied += OnCommandApplied;
    }

    public event EventHandler? CurrentPageChanged;

    public event EventHandler? DirtyChanged;

    public string Path { get; private set; }

    /// <summary>Fingerprint of the file as opened or last saved; changes only through <see cref="MarkSaved"/>.</summary>
    public DocumentFingerprint OpenFingerprint { get; private set; }

    public int PageCount { get; }

    /// <summary>The document's own undo history (capacity 500), created and owned by the session.</summary>
    public UndoManager Undo { get; }

    /// <summary>Index of the page shown, or -1 before the first <see cref="GoToPageAsync"/>.</summary>
    public int CurrentPageIndex { get; private set; } = -1;

    public PageEditor? CurrentEditor { get; private set; }

    /// <summary>True when some loaded page's stroke Id sequence differs from its loaded or last saved one.</summary>
    public bool IsDirty => _isDirty;

    /// <summary>Stored strokes that could not be read, summed over the loaded pages.</summary>
    public int UnreadableAnnotationCount
    {
        get
        {
            lock (_gate)
            {
                var total = 0;
                foreach (var loaded in _pages.Values)
                {
                    total += loaded.UnreadableCount;
                }

                return total;
            }
        }
    }

    public bool IsPageLoaded(int pageIndex)
    {
        lock (_gate)
        {
            return _pages.ContainsKey(pageIndex);
        }
    }

    /// <summary>Returns the page, loading its strokes from the source the first time only.</summary>
    public async Task<Page> EnsurePageAsync(int pageIndex, CancellationToken ct = default)
    {
        ValidatePageIndex(pageIndex);

        Task<LoadedPage> load;
        lock (_gate)
        {
            if (_pages.TryGetValue(pageIndex, out var loaded))
            {
                return loaded.Page;
            }

            if (!_loads.TryGetValue(pageIndex, out var pending))
            {
                // The lock is held until the task is registered, so the load's own cleanup
                // (which takes the lock) always runs after the registration.
                pending = LoadAsync(pageIndex);
                if (!pending.IsCompleted)
                {
                    _loads[pageIndex] = pending;
                }
            }

            load = pending;
        }

        return (await load.WaitAsync(ct)).Page;
    }

    /// <summary>Shows a page, loading it if needed. Never clears the undo history.</summary>
    public async Task GoToPageAsync(int pageIndex, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidatePageIndex(pageIndex); // before taking a ticket: an invalid call must not supersede a valid one

        var ticket = ++_navigation;
        var page = await EnsurePageAsync(pageIndex, ct);
        if (ticket != _navigation || _disposed)
        {
            return; // A later navigation superseded this one, or the session was closed.
        }

        if (pageIndex == CurrentPageIndex && CurrentEditor is not null)
        {
            return;
        }

        if (!_editors.TryGetValue(pageIndex, out var editor))
        {
            editor = new PageEditor(page, Undo);
            _editors[pageIndex] = editor;
        }

        CurrentPageIndex = pageIndex;
        CurrentEditor = editor;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undoes the last command, first showing the page it changed.</summary>
    public async Task<bool> UndoAsync(CancellationToken ct = default)
    {
        if (Undo.PeekUndo() is not { } command)
        {
            return false;
        }

        await ShowCommandPageAsync(command, ct);
        return Undo.Undo();
    }

    /// <summary>Redoes the next command, first showing the page it changes.</summary>
    public async Task<bool> RedoAsync(CancellationToken ct = default)
    {
        if (Undo.PeekRedo() is not { } command)
        {
            return false;
        }

        await ShowCommandPageAsync(command, ct);
        return Undo.Redo();
    }

    /// <summary>
    /// The net change per loaded page against its baseline, ordered by page index. Removed Ids are always
    /// Ids that were loaded (or saved) in this session; unloaded pages never appear.
    /// Same result as <c>CaptureSaveSnapshot().Changes</c>.
    /// </summary>
    public IReadOnlyList<PageChange> GetChanges() => CaptureSaveSnapshot().Changes;

    /// <summary>
    /// Captures what a save must write: the changes and the current Id sequence of every page that differs.
    /// Pass it to <see cref="MarkSaved"/> once the write succeeded.
    /// </summary>
    public SaveSnapshot CaptureSaveSnapshot()
    {
        lock (_gate)
        {
            var changes = new List<PageChange>();
            var sequences = new Dictionary<int, IReadOnlyList<Guid>>();
            foreach (var index in _pages.Keys.Order())
            {
                var loaded = _pages[index];
                if (!loaded.Differs)
                {
                    continue;
                }

                var strokes = loaded.Page.Strokes;
                var sequence = strokes.Select(s => s.Id).ToArray();
                var baseline = loaded.Baseline.ToHashSet();
                var current = sequence.ToHashSet();
                changes.Add(new PageChange(
                    index,
                    loaded.Baseline.Where(id => !current.Contains(id)).ToArray(),
                    strokes.Where(s => !baseline.Contains(s.Id)).ToArray()));
                sequences[index] = Array.AsReadOnly(sequence);
            }

            return new SaveSnapshot(changes.AsReadOnly(), sequences.AsReadOnly());
        }
    }

    /// <summary>
    /// Records a successful save of <paramref name="snapshot"/>: each page in it takes the snapshot's sequence as
    /// its baseline, other pages keep theirs, and the dirty state is recomputed, so edits made while the save ran
    /// stay unsaved. A new path and fingerprint re-key the session (Save as).
    /// </summary>
    public void MarkSaved(SaveSnapshot snapshot, string? newPath = null, DocumentFingerprint? newFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (newPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        }

        lock (_gate)
        {
            foreach (var (index, sequence) in snapshot.Sequences)
            {
                if (_pages.TryGetValue(index, out var loaded))
                {
                    loaded.SetBaseline(sequence);
                }
            }

            foreach (var loaded in _pages.Values)
            {
                Refresh(loaded);
            }
        }

        if (newPath is not null)
        {
            Path = newPath;
        }

        if (newFingerprint is { } fingerprint)
        {
            OpenFingerprint = fingerprint;
        }

        UpdateDirty();
    }

    /// <summary>Stops listening to the undo history; no event is raised afterwards. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Undo.CommandApplied -= OnCommandApplied;
    }

    private void ValidatePageIndex(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
    }

    private async Task ShowCommandPageAsync(IUndoableCommand command, CancellationToken ct)
    {
        if (command is IPageCommand pageCommand
            && pageCommand.PageIndex != CurrentPageIndex
            && pageCommand.PageIndex >= 0
            && pageCommand.PageIndex < PageCount)
        {
            await GoToPageAsync(pageCommand.PageIndex, ct);
        }
    }

    private async Task<LoadedPage> LoadAsync(int pageIndex)
    {
        try
        {
            var dimensions = _source.GetPageDimensions(pageIndex);
            PageStrokes strokes;
            try
            {
                strokes = await _source.LoadStrokesAsync(pageIndex, CancellationToken.None);
            }
            catch (OperationCanceledException ex)
            {
                // The shared load gets no caller token, so this was not a user cancellation.
                throw new InvalidOperationException("The page source cancelled loading the page strokes.", ex);
            }

            var loaded = LoadedPage.Create(pageIndex, dimensions, strokes);
            lock (_gate)
            {
                _pages[pageIndex] = loaded;
            }

            return loaded;
        }
        finally
        {
            lock (_gate)
            {
                _loads.Remove(pageIndex);
            }
        }
    }

    private void OnCommandApplied(object? sender, CommandAppliedEventArgs e)
    {
        lock (_gate)
        {
            if (e.Command is IPageCommand pageCommand)
            {
                if (_pages.TryGetValue(pageCommand.PageIndex, out var loaded))
                {
                    Refresh(loaded);
                }
            }
            else
            {
                foreach (var loaded in _pages.Values)
                {
                    Refresh(loaded);
                }
            }
        }

        UpdateDirty();
    }

    private void Refresh(LoadedPage loaded)
    {
        var differs = loaded.ComputeDiffers();
        if (differs != loaded.Differs)
        {
            loaded.Differs = differs;
            _dirtyPages += differs ? 1 : -1;
        }
    }

    private void UpdateDirty()
    {
        bool dirty;
        lock (_gate)
        {
            dirty = _dirtyPages > 0;
        }

        if (dirty == _isDirty || _disposed)
        {
            return;
        }

        _isDirty = dirty;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class LoadedPage(Page page, Guid[] baseline, int unreadableCount)
    {
        public Page Page { get; } = page;

        public Guid[] Baseline { get; private set; } = baseline;

        public int UnreadableCount { get; } = unreadableCount;

        public bool Differs { get; set; }

        public static LoadedPage Create(int pageIndex, PageDimensions dimensions, PageStrokes loaded)
        {
            var page = new Page(pageIndex, dimensions.WidthPt, dimensions.HeightPt);
            var seen = new HashSet<Guid>();
            var unique = loaded.Strokes.Where(s => seen.Add(s.Id)).ToArray();
            page.SetStrokes(unique);
            return new LoadedPage(page, unique.Select(s => s.Id).ToArray(), loaded.UnreadableCount);
        }

        public bool ComputeDiffers()
        {
            var strokes = Page.Strokes;
            if (strokes.Count != Baseline.Length)
            {
                return true;
            }

            for (var i = 0; i < strokes.Count; i++)
            {
                if (strokes[i].Id != Baseline[i])
                {
                    return true;
                }
            }

            return false;
        }

        public void SetBaseline(IReadOnlyList<Guid> sequence) => Baseline = sequence.ToArray();
    }
}
