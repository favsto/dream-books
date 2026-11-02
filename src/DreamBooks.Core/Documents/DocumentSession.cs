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
/// Mutation and events belong to one thread (the UI thread), except <see cref="PageStrokesChanged"/>, which
/// documents its own threading. <see cref="EnsurePageAsync"/> and
/// <see cref="EnsureEditorAsync"/> may be awaited concurrently: callers share one load per page, which runs
/// without the callers' tokens so that one caller cancelling does not cancel the load for the others; a failed
/// load is not cached. Every loaded page can have an editor, so several visible pages can be edited at once;
/// all editors share <see cref="Undo"/> (FR-26, FR-62).
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

    /// <summary>
    /// Raised when <see cref="CurrentPageIndex"/> changes, or when <see cref="GoToPageAsync"/> gives the current
    /// page its editor.
    /// </summary>
    public event EventHandler? CurrentPageChanged;

    /// <summary>
    /// Raised after an undo or redo changed a page: the view brings that page into sight if it is not visible.
    /// Undo and redo never change <see cref="CurrentPageIndex"/> themselves.
    /// </summary>
    public event EventHandler<RevealRequestedEventArgs>? RevealRequested;

    public event EventHandler? DirtyChanged;

    /// <summary>
    /// Raised when a loaded page's strokes change (stroke added, erased, undone, redone) and once when a page
    /// finishes loading. Unlike the other events it may be raised off the UI thread: an edit raises it on the
    /// thread that changed the page (synchronously, inside the command); a load raises it on the context of the
    /// call that started the load (the UI thread for the app's loads, a pool thread otherwise), never while the
    /// session's lock is held. Handlers must be thread-safe and cheap. An exception from a handler is swallowed
    /// (on both paths) and the remaining handlers still run, so a faulty subscriber can never break the edit,
    /// its undo step or dirty tracking. Unloaded pages are not listened to. Nothing is raised once
    /// <see cref="Dispose"/> has returned, with one threading limit: the disposed check and the invocation are
    /// not atomic, so a notification raised on another thread (a load completing on a pool thread) that passed
    /// the check just before <see cref="Dispose"/> ran may still reach its handlers after it returned.
    /// </summary>
    public event EventHandler<PageStrokesChangedEventArgs>? PageStrokesChanged;

    public string Path { get; private set; }

    /// <summary>Fingerprint of the file as opened or last saved; changes only through <see cref="MarkSaved"/>.</summary>
    public DocumentFingerprint OpenFingerprint { get; private set; }

    public int PageCount { get; }

    /// <summary>The document's own undo history (capacity 500), created and owned by the session.</summary>
    public UndoManager Undo { get; }

    /// <summary>
    /// Index of the page the view considers current, or -1 before the first <see cref="GoToPageAsync"/> or
    /// <see cref="SetCurrentPageFromView"/>.
    /// </summary>
    public int CurrentPageIndex { get; private set; } = -1;

    /// <summary>The editor of the current page, or null while that page has no editor yet.</summary>
    public PageEditor? CurrentEditor => TryGetEditor(CurrentPageIndex, out var editor) ? editor : null;

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
        var started = false;
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
                started = true;
                if (!pending.IsCompleted)
                {
                    _loads[pageIndex] = pending;
                }
            }

            load = pending;
        }

        if (started)
        {
            // Outside the lock; for a load that already completed this raises before returning.
            _ = RaiseWhenLoadedAsync(load, pageIndex);
        }

        return (await load.WaitAsync(ct)).Page;
    }

    /// <summary>The editor of an already loaded page, without loading anything.</summary>
    public bool TryGetEditor(int pageIndex, out PageEditor? editor)
    {
        lock (_gate)
        {
            return _editors.TryGetValue(pageIndex, out editor);
        }
    }

    /// <summary>
    /// Returns the page's editor, loading its strokes the first time only. One editor per page, the same
    /// instance <see cref="GoToPageAsync"/> uses; all editors share <see cref="Undo"/>.
    /// </summary>
    public async Task<PageEditor> EnsureEditorAsync(int pageIndex, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidatePageIndex(pageIndex);
        if (TryGetEditor(pageIndex, out var existing))
        {
            return existing!;
        }

        var page = await EnsurePageAsync(pageIndex, ct);
        lock (_gate)
        {
            // Concurrent callers awaited the same load: the first one back creates the editor.
            if (!_editors.TryGetValue(pageIndex, out var editor))
            {
                editor = new PageEditor(page, Undo);
                _editors[pageIndex] = editor;
            }

            return editor;
        }
    }

    /// <summary>Shows a page, loading it if needed. Never clears the undo history.</summary>
    public async Task GoToPageAsync(int pageIndex, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidatePageIndex(pageIndex); // before taking a ticket: an invalid call must not supersede a valid one

        var ticket = ++_navigation;
        var alreadyShown = pageIndex == CurrentPageIndex && CurrentEditor is not null;
        await EnsureEditorAsync(pageIndex, ct);
        if (ticket != _navigation || _disposed)
        {
            return; // A later navigation superseded this one, or the session was closed.
        }

        if (alreadyShown)
        {
            return;
        }

        CurrentPageIndex = pageIndex;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Records the page the view considers current (e.g. the one at the viewport centre) without loading its
    /// strokes and without touching the undo history. Supersedes a <see cref="GoToPageAsync"/> in flight only
    /// when the current page actually changes.
    /// Ignored once the session is disposed.
    /// </summary>
    public void SetCurrentPageFromView(int pageIndex)
    {
        if (_disposed)
        {
            return;
        }

        ValidatePageIndex(pageIndex);
        if (pageIndex == CurrentPageIndex)
        {
            return; // Nothing changes, so an explicit GoToPageAsync in flight stays valid.
        }

        ++_navigation;
        CurrentPageIndex = pageIndex;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Undoes the last command, loading its page if needed, then raises <see cref="RevealRequested"/> for that
    /// page. The current page does not change.
    /// </summary>
    public Task<bool> UndoAsync(CancellationToken ct = default) => ApplyAsync(redo: false, ct);

    /// <summary>
    /// Redoes the next command, loading its page if needed, then raises <see cref="RevealRequested"/> for that
    /// page. The current page does not change.
    /// </summary>
    public Task<bool> RedoAsync(CancellationToken ct = default) => ApplyAsync(redo: true, ct);

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

    /// <summary>
    /// Stops listening to the undo history and to the loaded pages; no event is raised afterwards. Idempotent.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var loaded in _pages.Values)
            {
                loaded.Page.StrokesChanged -= OnPageStrokesChanged;
            }
        }

        Undo.CommandApplied -= OnCommandApplied;
    }

    private void ValidatePageIndex(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
    }

    private async Task<bool> ApplyAsync(bool redo, CancellationToken ct)
    {
        while (true)
        {
            if ((redo ? Undo.PeekRedo() : Undo.PeekUndo()) is not { } command)
            {
                return false;
            }

            var pageIndex = command is IPageCommand { PageIndex: var index } && index >= 0 && index < PageCount
                ? index
                : -1;
            if (pageIndex >= 0 && !_disposed && !TryGetEditor(pageIndex, out _))
            {
                await EnsureEditorAsync(pageIndex, ct);
                continue; // The history may have changed while the page loaded: look again.
            }

            if (!(redo ? Undo.Redo() : Undo.Undo()))
            {
                return false;
            }

            if (pageIndex >= 0 && !_disposed)
            {
                RevealRequested?.Invoke(this, new RevealRequestedEventArgs(pageIndex));
            }

            return true;
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
                if (!_disposed)
                {
                    // Only loaded pages are listened to; Dispose (under the same lock) unsubscribes them.
                    loaded.Page.StrokesChanged += OnPageStrokesChanged;
                }
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

    private async Task RaiseWhenLoadedAsync(Task<LoadedPage> load, int pageIndex)
    {
        try
        {
            await load;
        }
        catch (Exception)
        {
            return; // A failed load is reported to its callers, who await it; nothing changed.
        }

        RaisePageStrokesChanged(pageIndex);
    }

    private void OnPageStrokesChanged(object? sender, EventArgs e)
    {
        if (sender is Page page)
        {
            RaisePageStrokesChanged(page.Index);
        }
    }

    private void RaisePageStrokesChanged(int pageIndex)
    {
        bool disposed;
        lock (_gate)
        {
            disposed = _disposed;
        }

        if (disposed || PageStrokesChanged is not { } handlers)
        {
            return;
        }

        // Each handler on its own, and none may throw out of here: on the edit path this runs inside
        // command.Do(), and an exception would keep the command out of the undo history and the page out of
        // dirty tracking and the save, although the page already changed. On the load path nobody awaits it.
        var args = new PageStrokesChangedEventArgs(pageIndex);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<PageStrokesChangedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception)
            {
                // Derived data (marks) may go stale for this page; the document itself stays consistent.
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
