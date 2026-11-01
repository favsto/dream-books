using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Journal;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;
using DreamBooks.Pdf;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// Window state over one <see cref="DocumentSession"/>: the multi-page view state (<see cref="Viewport"/>), the
/// current page, the document's undo history (shared by all its pages, FR-62), the eraser gesture (on any loaded
/// page), the notices, the remembered position (FR-04), and saving: Save, Save as, the dirty mark and the
/// unsaved-changes question on close or on opening another document (FR-80, FR-85, FR-89). Strokes live in the
/// session, so they survive page changes (FR-83).
/// </summary>
/// <remarks>
/// At most one save runs at a time (<see cref="IsSaving"/>); while it runs, drawing, erasing, undo and page
/// navigation go on, but opening and saving again are disabled. Strokes drawn during a save stay unsaved. A page
/// whose strokes are loaded during the save takes its baseline from the file as it was, which is what the save
/// writes for a page it did not capture; during the brief release of a plain Save such a load fails and is
/// reported like any failed page load (nothing is applied).
/// </remarks>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>Delay after the last movement before the position is journalled and remembered (FR-04).</summary>
    public static readonly TimeSpan ViewSettleDelay = TimeSpan.FromMilliseconds(500);

    private readonly IFileDialogService _dialogs;
    private readonly ViewPositionStore? _positions;
    private readonly IDelayScheduler _scheduler;

    // Stands in for the session's history while no document is open: never executed into, so always empty.
    private readonly UndoManager _noDocumentUndo = new();
    private IDisposable? _pendingSettle;
    private Task _positionWrites = Task.CompletedTask;
    private bool _positionsPruned;
    private PageEditor? _activeErasePage;

    // Pages whose stroke load failure was already told to the user (once per page and document).
    private readonly HashSet<int> _reportedLoadFailures = [];
    private DocumentSession? _session;
    private OpenedDocument? _document;
    private IViewerHandle? _viewer;
    private PageEditor? _editor;
    private bool _isSaving;
    private Task<SaveOutcome>? _runningSave;
    private int _pageNumber;
    private int _pageCount;
    private string? _errorMessage;
    private string? _notice;
    private bool _hasUnreadableAnnotations;
    private bool _noticeShown;
    private EraserSession? _activeErase;
    private bool _completingErase;
    private DocumentJournal? _journal;
    private DocumentLock? _lock;
    private bool _startupRecoveryDone;
    private string? _journalWarning;
    private bool _listenerFailed;

    // Marks (FR-100..FR-103): a new index per document, live tracking of loaded pages, and a background scan over
    // the viewer's current renderer, rebuilt when a save swaps it. The scan is paused while a stroke or eraser
    // gesture is live and while a save runs (the two pauses nest).
    private readonly MarkRecognizer _markRecognizer = new();
    private MarkIndex? _markIndex;
    private MarkTracker? _markTracker;
    private MarkScanner? _markScanner;
    private DocumentSession? _markSession;
    private bool _scanPausedForInk;
    private bool _scanPausedForSave;

    /// <param name="dialogs">The dialogs.</param>
    /// <param name="recovery">
    /// Where journals and document locks live (FR-84); null disables crash recovery and the single-writer lock
    /// (tests that do not exercise them).
    /// </param>
    /// <param name="positions">Where the last position of each document is remembered (FR-04); null disables it.</param>
    /// <param name="scheduler">Debounce timers; the UI thread's dispatcher when null.</param>
    public MainViewModel(
        IFileDialogService dialogs,
        RecoveryStore? recovery = null,
        ViewPositionStore? positions = null,
        IDelayScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        _dialogs = dialogs;
        _positions = positions;
        _scheduler = scheduler ?? new DispatcherDelayScheduler();
        Recovery = recovery is null ? null : new RecoveryService(recovery, dialogs);
        Tools = new ToolsViewModel();
        ZoomVm = new ZoomViewModel();
        Viewport = new DocumentViewportViewModel(ZoomVm, _scheduler) { PageNavigator = NavigateToPageAsync };
        Viewport.PositionChanged += OnViewportPositionChanged;
        Marks = new MarksViewModel(Viewport, _scheduler) { InkSource = LoadMarkInkAsync };
        ZoomVm.PropertyChanged += OnZoomPropertyChanged;
        OpenDocument = OpenedDocument.OpenPdfAsync;
        MarkSourceFactory = static opened => opened.Viewer?.Current is { } renderer ? new MarkPageSource(renderer) : null;
        SaveService = new DocumentSaveService(new PdfDocumentSaver(), dialogs);

        // Navigation stays available while a save runs (the earlier lock is gone): pages that cannot be loaded
        // meanwhile show their placeholder, and the commands may overlap.
        OpenPdfCommand = new AsyncRelayCommand(OpenPdfAsync, () => !IsSaving);
        GoToPageCommand = new AsyncRelayCommand<int>(GoToPageAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        PreviousPageCommand = new AsyncRelayCommand(
            () => Viewport.StepPageAsync(-1), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        NextPageCommand = new AsyncRelayCommand(
            () => Viewport.StepPageAsync(1), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => Session is not null && Undo.CanUndo);
        RedoCommand = new AsyncRelayCommand(RedoAsync, () => Session is not null && Undo.CanRedo);
        SaveCommand = new AsyncRelayCommand(() => SaveCoreAsync(saveAs: false), CanSave);
        SaveAsCommand = new AsyncRelayCommand(() => SaveCoreAsync(saveAs: true), CanSave);
    }

    /// <summary>Raised after a successful Save or Save as (the session is already marked saved).</summary>
    public event EventHandler<DocumentSavedEventArgs>? Saved;

    /// <summary>
    /// Raised when the user answered "Don't save" for <see cref="Session"/>, while it is still the current
    /// session: on close, or just before another document replaces it (only once that one opened).
    /// </summary>
    public event EventHandler? DiscardRequested;

    /// <summary>
    /// Raised when the strokes to draw change because of the eraser: a gesture started, its working copy
    /// changed, or it ended (then the view goes back to the page strokes).
    /// </summary>
    public event EventHandler? ErasePreviewChanged;

    public ToolsViewModel Tools { get; }

    public ZoomViewModel ZoomVm { get; }

    /// <summary>The multi-page view state: layout, position, current page, history, fit width.</summary>
    public DocumentViewportViewModel Viewport { get; }

    /// <summary>The marks of the open document: list, previous/next mark (FR-101..FR-103).</summary>
    public MarksViewModel Marks { get; }

    /// <summary>The open document, or null.</summary>
    public DocumentSession? Session
    {
        get => _session;
        private set => SetProperty(ref _session, value);
    }

    /// <summary>The current document's undo history; an empty, inert one when no document is open.</summary>
    public UndoManager Undo => Session?.Undo ?? _noDocumentUndo;

    /// <summary>
    /// The editor of the current page (the page under the viewport centre), or null while that page's strokes are not
    /// loaded. Every loaded page has its own editor (<see cref="EnsureEditorAsync"/>); this one serves the panel.
    /// </summary>
    public PageEditor? Editor
    {
        get => _editor;
        private set => SetProperty(ref _editor, value);
    }

    /// <summary>
    /// Draws the page bitmaps: the viewer's current renderer, replaced by each save; null without a document,
    /// with a test page source, or while a Save has the file released.
    /// </summary>
    public PdfRenderer? Renderer => _viewer?.Current;

    /// <summary>True while a Save or Save as runs (FR-89).</summary>
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                SetScanPause(ref _scanPausedForSave, value);
                OpenPdfCommand.NotifyCanExecuteChanged();
                SaveCommand.NotifyCanExecuteChanged();
                SaveAsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True when the document has changes not yet in the file.</summary>
    public bool IsDirty => Session?.IsDirty ?? false;

    /// <summary>"file name – app name", with a "*" after the file name while there are unsaved changes.</summary>
    public string WindowTitle => Session is not { } session
        ? Strings.AppTitle
        : string.Format(
            CultureInfo.CurrentCulture,
            session.IsDirty ? Strings.WindowTitleDirtyFormat : Strings.WindowTitleFormat,
            System.IO.Path.GetFileName(session.Path),
            Strings.AppTitle);

    /// <summary>Current page, 1-based; 0 when no document is open.</summary>
    public int PageNumber
    {
        get => _pageNumber;
        private set => SetProperty(ref _pageNumber, value);
    }

    public int PageCount
    {
        get => _pageCount;
        private set => SetProperty(ref _pageCount, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>
    /// A one-line notice for the user, set once per document the first time a loaded page holds
    /// annotations the app cannot edit; null otherwise.
    /// </summary>
    public string? Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    /// <summary>True when a loaded page of the document holds annotations the app cannot read.</summary>
    public bool HasUnreadableAnnotations
    {
        get => _hasUnreadableAnnotations;
        private set => SetProperty(ref _hasUnreadableAnnotations, value);
    }

    /// <summary>
    /// "Unsaved changes are not being recorded for recovery": shown while the journal cannot be written (or a
    /// save listener failed), cleared as soon as a journal write succeeds again; null otherwise.
    /// </summary>
    public string? JournalWarning
    {
        get => _journalWarning;
        private set => SetProperty(ref _journalWarning, value);
    }

    /// <summary>Crash recovery over the recovery store, or null when the view model has none.</summary>
    internal RecoveryService? Recovery { get; }

    /// <summary>The eraser gesture in progress, or null; its <see cref="EraserSession.Strokes"/> is the live preview.</summary>
    public EraserSession? ActiveErase
    {
        get => _activeErase;
        private set => SetProperty(ref _activeErase, value);
    }

    /// <summary>The editor of the page the live eraser gesture belongs to, or null (one gesture at a time).</summary>
    public PageEditor? ActiveErasePage
    {
        get => _activeErasePage;
        private set => SetProperty(ref _activeErasePage, value);
    }

    /// <summary>Test hook: completes when the queued position-store writes are done.</summary>
    internal Task PositionWritesForTest => _positionWrites;

    public IAsyncRelayCommand OpenPdfCommand { get; }

    public IAsyncRelayCommand<int> GoToPageCommand { get; }

    public IAsyncRelayCommand PreviousPageCommand { get; }

    public IAsyncRelayCommand NextPageCommand { get; }

    /// <summary>Undoes the last change of the document, first showing its page.</summary>
    public IAsyncRelayCommand UndoCommand { get; }

    /// <summary>Redoes the next change of the document, first showing its page.</summary>
    public IAsyncRelayCommand RedoCommand { get; }

    /// <summary>Writes the open document to its file (Ctrl+S).</summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>Writes the open document to a file the user picks, which becomes the document (Ctrl+Shift+S).</summary>
    public IAsyncRelayCommand SaveAsCommand { get; }

    /// <summary>Test seam: opens the pages (and, for a real PDF, the renderer) for a path.</summary>
    internal Func<string, CancellationToken, Task<OpenedDocument>> OpenDocument { get; set; }

    /// <summary>Test seam: the saving pipeline.</summary>
    internal DocumentSaveService SaveService { get; set; }

    /// <summary>
    /// Test seam: the mark scan's source for a document's current renderer, asked at open and after every renderer
    /// swap; null (no renderer: released during a save, or a test source) means no scan for now.
    /// </summary>
    internal Func<OpenedDocument, IMarkPageSource?> MarkSourceFactory { get; set; }

    /// <summary>Test seam: the scan's pause between pages; the scanner's default when null.</summary>
    internal TimeSpan? MarkScanIdleGap { get; set; }

    /// <summary>Test hook: the mark index of the open document.</summary>
    internal MarkIndex? MarkIndexForTest => _markIndex;

    /// <summary>Test hook: the scan running over the current renderer, if any.</summary>
    internal MarkScanner? MarkScannerForTest => _markScanner;

    /// <summary>Test seam: builds the live tracker of a document's marks.</summary>
    internal Func<DocumentSession, MarkRecognizer, MarkIndex, MarkTracker> MarkTrackerFactory { get; set; } =
        static (session, recognizer, index) => new MarkTracker(session, recognizer, index);

    /// <summary>Test hook: how many times the scan was recentred.</summary>
    internal int MarkRecentresForTest { get; private set; }

    /// <summary>
    /// Whether the window may close (FR-85): waits for a running save, then, with unsaved changes, asks
    /// Save / Don't save / Cancel. Save closes only if the save succeeded; Don't save raises
    /// <see cref="DiscardRequested"/>. Never throws.
    /// </summary>
    public async Task<bool> CanCloseAsync()
    {
        if (_runningSave is { } running)
        {
            await running; // never faults
        }

        switch (await ResolveUnsavedChangesAsync())
        {
            case UnsavedChanges.Discard:
                await PersistViewAsync();
                RaiseDiscardRequested();
                await CloseJournalAsync(userDiscarded: true);
                ReleaseLock();
                StopMarks();
                return true;
            case UnsavedChanges.Resolved:
                await PersistViewAsync();
                await CloseJournalAsync(userDiscarded: false);
                ReleaseLock();
                StopMarks();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Test hook: the journal of the open document (e.g. to write an entry of an older format).</summary>
    internal DocumentJournal? JournalForTest => _journal;

    /// <summary>Test hook: the viewer whose renderer a save releases and reopens.</summary>
    internal IViewerHandle? ViewerForTest => _viewer;

    /// <summary>
    /// Startup recovery (FR-86), once per run: reopens the document of the most recent journal through the normal
    /// open path (lock, restore, notices). If that file is gone, says so and offers to discard the journal; the
    /// other journals stay until their documents are opened. Never throws except on cancellation.
    /// </summary>
    public async Task TryRestoreLastSessionAsync(CancellationToken ct = default)
    {
        if (Recovery is not { } recovery || _startupRecoveryDone || Session is not null)
        {
            return;
        }

        _startupRecoveryDone = true;
        RecoveredJournal? latest;
        try
        {
            latest = await Task.Run(recovery.MostRecent, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        if (latest?.Contents.Header is not { } header)
        {
            return;
        }

        if (File.Exists(header.Path))
        {
            await OpenPathAsync(header.Path, quietIfInUse: true, ct);
            return;
        }

        ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.ErrorJournalFileMissing, header.Path);
        if (!_dialogs.AskDiscardOrphanJournal(header.Path))
        {
            return;
        }

        try
        {
            recovery.Store.Delete(header.Path);
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Strings.ErrorJournalDeleteFailed;
        }
    }

    /// <summary>Test hook: what a crash leaves behind: the journal and the lock are released, nothing is deleted.</summary>
    internal async Task SimulateCrashForTestAsync()
    {
        if (_journal is { } journal)
        {
            _journal = null;
            await journal.DisposeAsync();
        }

        ReleaseLock();
    }

    /// <summary>
    /// Loads a page's strokes (once) and returns its editor, e.g. for a page entering the view. When the page is the
    /// current one, <see cref="Editor"/> follows. Throws <see cref="OperationCanceledException"/> without a document,
    /// or when the document was closed or replaced meanwhile; a failed load propagates.
    /// </summary>
    public async Task<PageEditor> EnsureEditorAsync(int pageIndex, CancellationToken ct = default)
    {
        if (Session is not { } session)
        {
            throw new OperationCanceledException("No document is open.");
        }

        PageEditor editor;
        try
        {
            editor = await session.EnsureEditorAsync(pageIndex, ct);
        }
        catch (ObjectDisposedException ex)
        {
            throw new OperationCanceledException("The document was closed.", ex);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // NFR-11: say so, never with the exception text (NFR-12), once per page. Not during a save: a plain Save
            // releases the file for a moment, and the view tries the load again when the renderer comes back.
            if (ReferenceEquals(session, Session) && !IsSaving && _reportedLoadFailures.Add(pageIndex))
            {
                ReportFailure(session);
            }

            throw;
        }

        if (!ReferenceEquals(session, Session))
        {
            throw new OperationCanceledException("The document was replaced.");
        }

        RefreshCurrentEditor();
        UpdateNotice();
        return editor;
    }

    /// <summary>
    /// Starts an eraser gesture with the selected mode on <paramref name="editor"/>'s page (FR-31), which need not
    /// be the current page. Zoom stays locked until it ends. No-op while a gesture is live (on any page) or for an
    /// editor that is not this document's.
    /// </summary>
    public void BeginErase(PageEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (ActiveErase is not null
            || Session is not { } session
            || !session.TryGetEditor(editor.Page.Index, out var own)
            || !ReferenceEquals(own, editor))
        {
            return;
        }

        ActiveErasePage = editor;
        ActiveErase = editor.BeginErase(Tools.EraserMode);
        ZoomVm.IsLocked = true;
        ErasePreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the eraser to a page point; the circle has the selected diameter (FR-32).</summary>
    public void EraseAt(PointD centerPt)
    {
        if (ActiveErase is { } session && session.MoveTo(centerPt, Units.MmToPt(Tools.EraserSizeMm) / 2))
        {
            ErasePreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Ends the gesture: one undo step if anything was erased, none otherwise (FR-33, FR-63).</summary>
    public void EndErase()
    {
        if (ActiveErase is not { } session)
        {
            return;
        }

        // Complete first, then clear: the page already holds the working copy when the view switches back
        // to it, so nothing erased reappears for a frame.
        _completingErase = true;
        try
        {
            session.Complete();
        }
        finally
        {
            _completingErase = false;
            FinishErase();
        }
    }

    /// <summary>Drops the gesture without touching the page (lost capture, page change, undo mid-gesture).</summary>
    public void CancelErase()
    {
        if (ActiveErase is not { } session)
        {
            return;
        }

        session.Cancel();
        FinishErase();
    }

    private void FinishErase()
    {
        ActiveErase = null;
        ActiveErasePage = null;
        ZoomVm.IsLocked = false;
        ErasePreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenPdfAsync(CancellationToken ct)
    {
        var path = _dialogs.PickPdf();
        if (path is null)
        {
            return;
        }

        await OpenPathAsync(path, quietIfInUse: false, ct);
    }

    /// <summary>
    /// Opens a document: takes its single-writer lock (refusing a document open in another process), resolves the
    /// current document's unsaved changes, opens and restores from the journal (FR-84, FR-86), then swaps.
    /// Any failure before the swap leaves the current document as it was (NFR-11).
    /// </summary>
    private async Task OpenPathAsync(string path, bool quietIfInUse, CancellationToken ct)
    {
        // 1. Single writer: refuse before asking anything. Reopening the current document keeps its lock.
        var sameDocument = Session is { } current && SamePath(current.Path, path);
        DocumentLock? newLock = null;
        var journalUnavailable = false;
        if (Recovery is { } lockStore && !(sameDocument && _lock is not null))
        {
            try
            {
                newLock = DocumentLock.TryAcquire(lockStore.Store.RootDirectory, path);
                if (newLock is null)
                {
                    if (!quietIfInUse)
                    {
                        ErrorMessage = Strings.ErrorDocumentInUse;
                    }

                    return;
                }
            }
#pragma warning disable CA1031 // The recovery folder is unusable: open anyway, without a journal, and say so.
            catch (Exception)
#pragma warning restore CA1031
            {
                journalUnavailable = true;
            }
        }

        var unsaved = await ResolveUnsavedChangesAsync();
        if (unsaved == UnsavedChanges.Stay)
        {
            newLock?.Dispose();
            return;
        }

        var previousJournalEnabled = _journal?.IsEnabled ?? false;
        if (sameDocument)
        {
            // The journal of the document being replaced is the one the new session would restore from: close it
            // first, as the user decided (saved, or Don't save).
            await CloseJournalAsync(userDiscarded: unsaved == UnsavedChanges.Discard);
        }

        OpenedDocument? opened = null;
        DocumentSession? session = null;
        RestoreResult? restore = null;
        DocumentJournal? journal = null;
        DocumentLayout layout;
        RestoredPosition? restored;
        int firstPage;
        try
        {
            opened = await OpenDocument(path, ct);
            if (opened.Source.PageCount == 0)
            {
                throw new PdfOpenException(PdfOpenError.InvalidFormat);
            }

            // Everything below fails before anything is swapped, so the current document stays (NFR-11).
            // Every page size is read now, so the layout is exact from the first frame.
            layout = BuildLayout(opened.Source);
            var fingerprint = await DocumentFingerprint.ComputeAsync(opened.Path, ct);
            session = new DocumentSession(opened.Source, opened.Path, fingerprint);
            if (Recovery is { } recovery)
            {
                if (journalUnavailable)
                {
                    journal = recovery.Disabled(session);
                }
                else
                {
                    restore = await recovery.TryRestoreAsync(session, ct);
                    journal = restore.Journal;
                    if (restore.Status == RestoreStatus.ReplayFailed)
                    {
                        // The session is partly replayed: start again from the file, the journal kept for later.
                        session.Dispose();
                        session = new DocumentSession(opened.Source, opened.Path, fingerprint);
                        journal = recovery.Disabled(session);
                    }
                }
            }

            // FR-04: the journal's view (unsaved changes) wins over the remembered position of the saved file.
            restored = restore is { Status: RestoreStatus.Replayed, LastView: { } view }
                ? FromJournal(view, layout)
                : sameDocument ? new RestoredPosition(Viewport.Position, ZoomVm.IsFitWidth)
                : await LoadPositionAsync(opened.Path, ct);
            firstPage = restored is { } start ? Math.Clamp(start.Position.PageIndex, 0, session.PageCount - 1) : 0;
            await session.GoToPageAsync(firstPage, ct);
        }
        catch (OperationCanceledException)
        {
            await ReleaseQuietlyAsync(session, opened, journal, newLock);
            ReattachAfterFailedReopen(sameDocument, previousJournalEnabled);
            throw;
        }
        catch (PdfOpenException ex)
        {
            await ReleaseQuietlyAsync(session, opened, journal, newLock);
            ReattachAfterFailedReopen(sameDocument, previousJournalEnabled);
            ErrorMessage = MessageFor(ex.Error);
            return;
        }
        catch (Exception)
        {
            // Never include the exception text: it may carry file paths or document content (NFR-12).
            await ReleaseQuietlyAsync(session, opened, journal, newLock);
            ReattachAfterFailedReopen(sameDocument, previousJournalEnabled);
            ErrorMessage = Strings.ErrorUnknown;
            return;
        }

        if (Session is not null)
        {
            await PersistViewAsync(); // the document being replaced remembers where it was
            if (unsaved == UnsavedChanges.Discard)
            {
                // Only now that the new document is ready: a failed open keeps the current changes.
                RaiseDiscardRequested();
            }

            if (!sameDocument)
            {
                await CloseJournalAsync(userDiscarded: unsaved == UnsavedChanges.Discard);
                ReleaseLock();
            }
        }

        var previousSession = Session;
        var previousDocument = _document;
        Swap(opened, session, journal, newLock ?? _lock, layout, restored, firstPage);
        ShowRestoreOutcome(restore, journalUnavailable);
        if (opened.Viewer is { Info.CanBeRewritten: false })
        {
            // Owner-password or restricted PDFs open without a password but are never rewritten (FR-82): say so now,
            // not at the first Ctrl+S.
            Notice = Notice is null ? Strings.NoticeNotSavable : Notice + " " + Strings.NoticeNotSavable;
        }
        previousSession?.Dispose();
        if (previousDocument is not null)
        {
            await previousDocument.Lifetime.DisposeAsync();
        }

        // The view's current page is the one at the centre, which can differ from the top page loaded above: load it
        // too, so the session, PageNumber, Editor and the indicator agree from the start.
        if (ReferenceEquals(Session, session) && Viewport.CurrentPage != session.CurrentPageIndex)
        {
            await NavigateToPageAsync(Viewport.CurrentPage);
        }

        await PrunePositionsOnceAsync();
    }

    private static DocumentLayout BuildLayout(IPageSource source)
    {
        var pages = new PageDimensions[source.PageCount];
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i] = source.GetPageDimensions(i);
        }

        return new DocumentLayout(pages);
    }

    /// <summary>The remembered position of a document (FR-04), read off the UI thread; null when none or unreadable.</summary>
    private async Task<RestoredPosition?> LoadPositionAsync(string path, CancellationToken ct)
    {
        if (_positions is not { } store)
        {
            return null;
        }

        var stored = await Task.Run(() => store.TryLoad(path), ct);
        return stored is { } found ? new RestoredPosition(found.Position, found.FitWidth) : null;
    }

    /// <summary>
    /// A journalled view as a position. Entries of the multi-page view (with a fit flag) are (page of the top edge,
    /// zoom, column x, y inside the page) in points. Entries of the single-page view (no flag) hold the current page
    /// and the viewport's DIP offsets inside it: the vertical offset is turned into page points at that zoom and the
    /// column is centred (best effort; the viewport clamps the result).
    /// </summary>
    private RestoredPosition FromJournal(ViewEntry view, DocumentLayout layout)
    {
        if (view.FitWidth is { } fit)
        {
            return new RestoredPosition(new ViewPosition(view.PageIndex, view.ScrollY, view.ScrollX, view.Zoom), fit);
        }

        var zoom = double.IsFinite(view.Zoom) && view.Zoom > 0
            ? Math.Clamp(view.Zoom, LayoutConstants.MinZoom, LayoutConstants.MaxZoom)
            : ZoomVm.Zoom;
        var y = double.IsFinite(view.ScrollY) && view.ScrollY > 0 ? view.ScrollY / (zoom * Units.DipPerPt) : 0;
        return new RestoredPosition(new ViewPosition(view.PageIndex, y, layout.ColumnWidthPt / 2, zoom), FitWidth: false);
    }

    /// <summary>Once per run, after the first successful open: old and excess remembered positions go.</summary>
    private async Task PrunePositionsOnceAsync()
    {
        if (_positionsPruned || _positions is not { } store)
        {
            return;
        }

        _positionsPruned = true;
        try
        {
            await Task.Run(() => store.Prune());
        }
#pragma warning disable CA1031 // Pruning is housekeeping: a failure changes nothing for the user.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void OnViewportPositionChanged(object? sender, EventArgs e)
    {
        if (Session is null)
        {
            return;
        }

        _pendingSettle?.Dispose();
        _pendingSettle = _scheduler.Schedule(ViewSettleDelay, () =>
        {
            _pendingSettle = null;
            QueueViewPersistence();
        });
    }

    /// <summary>
    /// The view settled (or the document is closing): the journal gets the position (page of the top edge, zoom,
    /// column x, y inside the page, in points; only while it records, FR-84) and the position store remembers it
    /// (FR-04). Never throws.
    /// </summary>
    private void QueueViewPersistence()
    {
        _pendingSettle?.Dispose();
        _pendingSettle = null;
        if (Session is not { } session || Viewport.Layout is null)
        {
            return;
        }

        var position = Viewport.Position;
        if (!double.IsFinite(position.YPt) || !double.IsFinite(position.ColumnXPt)
            || !double.IsFinite(position.Zoom) || position.Zoom <= 0)
        {
            return;
        }

        var fit = ZoomVm.IsFitWidth;
        try
        {
            _journal?.RecordView(position.PageIndex, position.Zoom, position.ColumnXPt, position.YPt, fit);
        }
#pragma warning disable CA1031 // A journal failure is reported through FaultChanged, never thrown from a timer.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        if (_positions is { } store)
        {
            // The store reports its own failures (ViewPositionStore.Failed, no document content) and never throws;
            // the chain still guards so that no write can end as an unobserved faulted task.
            var path = session.Path;
            _positionWrites = _positionWrites.ContinueWith(
                _ => SaveQuietly(store, path, position, fit),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private static void SaveQuietly(ViewPositionStore store, string path, ViewPosition position, bool fitWidth)
    {
        try
        {
            store.Save(path, position, fitWidth);
        }
#pragma warning disable CA1031 // Best effort: remembering the position must never fault the write chain.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Writes the current position now (close, document swap) and waits for the store. Never throws.</summary>
    private async Task PersistViewAsync()
    {
        QueueViewPersistence();
        try
        {
            await _positionWrites;
        }
#pragma warning disable CA1031 // Remembering the position is best effort; closing must go on.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Reopening the current document failed after its journal was closed: the document stays open, so it gets a
    /// fresh journal for its future changes.
    /// </summary>
    private void ReattachAfterFailedReopen(bool sameDocument, bool previousJournalEnabled)
    {
        if (!sameDocument || _journal is not null || Recovery is not { } recovery || Session is not { } session)
        {
            return;
        }

        // A journal that was disabled (unreadable, refused mismatch, failed discard) stays disabled: an enabled one
        // would append under someone else's header.
        if (!previousJournalEnabled)
        {
            AttachJournal(recovery.Disabled(session));
            return;
        }

        var journal = recovery.Attach(session);
        if (session.IsDirty)
        {
            journal.RequestResync(); // the first change writes everything still unsaved, not just itself
        }

        AttachJournal(journal);
    }

    private void AttachJournal(DocumentJournal? journal)
    {
        if (_journal is { } previous)
        {
            previous.FaultChanged -= OnJournalFaultChanged;
        }

        _journal = journal;
        if (journal is not null)
        {
            journal.FaultChanged += OnJournalFaultChanged;
        }

        UpdateJournalWarning();
    }

    private void OnJournalFaultChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _journal))
        {
            UpdateJournalWarning();
        }
    }

    /// <summary>
    /// A journal write failure shows <see cref="Strings.WarnJournalFault"/> until a write succeeds again; a failed
    /// save or discard listener shows <see cref="Strings.WarnJournalListenerFailed"/>, which stays for the document.
    /// </summary>
    private void UpdateJournalWarning() =>
        JournalWarning = _journal is { IsFaulted: true } ? Strings.WarnJournalFault
            : _listenerFailed ? Strings.WarnJournalListenerFailed
            : null;

    private void ReportListenerFailure()
    {
        _listenerFailed = true;
        UpdateJournalWarning();
    }

    /// <summary>
    /// The current document is being closed or replaced (FR-84): with the changes dropped or nothing unsaved the
    /// journal is deleted; it is kept when the only complete copy of the document is the adopted temp file.
    /// Failures are told to the user, never thrown.
    /// </summary>
    private async Task CloseJournalAsync(bool userDiscarded)
    {
        var journal = _journal;
        if (journal is null)
        {
            return;
        }

        AttachJournal(null);
        try
        {
            if (Session is { } session && SaveService.AdoptedCopyFolder(session) is { } folder)
            {
                var message = string.Format(CultureInfo.CurrentCulture, Strings.NoticeCopyKept, folder);
                Notice = message;
                ShowMessageQuietly(message);
            }
            else if (userDiscarded || Session is not { IsDirty: true })
            {
                try
                {
                    journal.Discard();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ErrorMessage = Strings.ErrorJournalDeleteFailed;
                    ShowMessageQuietly(Strings.ErrorJournalDeleteFailed);
                }
            }
        }
        finally
        {
            try
            {
                await journal.DisposeAsync();
            }
#pragma warning disable CA1031 // A failed final flush must not keep the window open; the file stays on disk.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    private void ReleaseLock()
    {
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>Tells the "Don't save" listeners, each isolated; a failing one shows the journal warning.</summary>
    private void RaiseDiscardRequested()
    {
        if (DiscardRequested is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // A listener failing does not stop the close; the user is told.
            catch (Exception)
#pragma warning restore CA1031
            {
                ReportListenerFailure();
            }
        }
    }

    private void ShowRestoreOutcome(RestoreResult? restore, bool journalUnavailable)
    {
        if (journalUnavailable)
        {
            Notice = Strings.NoticeJournalDisabled;
            return;
        }

        switch (restore?.Status)
        {
            case RestoreStatus.Replayed:
                if (restore.Applied > 0 || restore.Skipped > 0)
                {
                    Notice = restore.Skipped > 0
                        ? string.Format(CultureInfo.CurrentCulture, Strings.NoticeRestoredSkipped, restore.Applied, restore.Skipped)
                        : Strings.NoticeRestored;
                }

                break;
            case RestoreStatus.Unreadable:
                ErrorMessage = Strings.ErrorJournalUnreadable;
                Notice = Strings.NoticeJournalDisabled;
                break;
            case RestoreStatus.MismatchCancelled:
            case RestoreStatus.ForeignJournal:
                Notice = Strings.NoticeJournalDisabled;
                break;
            case RestoreStatus.DiscardFailed:
                ErrorMessage = Strings.ErrorJournalDeleteFailed;
                Notice = Strings.NoticeJournalDisabled;
                break;
            case RestoreStatus.ReplayFailed:
                ErrorMessage = Strings.ErrorJournalRestoreFailed;
                Notice = Strings.NoticeJournalDisabled;
                break;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes a ready session current in one synchronous step: no half-switched state is observable.</summary>
    private void Swap(
        OpenedDocument opened,
        DocumentSession session,
        DocumentJournal? journal,
        DocumentLock? documentLock,
        DocumentLayout layout,
        RestoredPosition? restored,
        int firstPage)
    {
        CancelErase();
        _pendingSettle?.Dispose();
        _pendingSettle = null;
        StopMarks(); // before anything of the previous document goes: nothing scans or tracks into the new index
        if (Session is { } previous)
        {
            previous.CurrentPageChanged -= OnCurrentPageChanged;
            previous.RevealRequested -= OnRevealRequested;
            previous.Undo.StateChanged -= OnUndoStateChanged;
            previous.DirtyChanged -= OnDirtyChanged;
        }

        if (_viewer is { } previousViewer)
        {
            previousViewer.CurrentChanged -= OnViewerChanged;
        }

        _document = opened;
        _viewer = opened.Viewer;
        _lock = documentLock;
        _listenerFailed = false;
        _reportedLoadFailures.Clear();
        AttachJournal(journal);
        Session = session;
        Viewport.Attach(session, layout, restored?.Position, firstPage, restored?.FitWidth ?? false);
        session.CurrentPageChanged += OnCurrentPageChanged;
        session.RevealRequested += OnRevealRequested;
        session.Undo.StateChanged += OnUndoStateChanged;
        session.DirtyChanged += OnDirtyChanged;
        if (_viewer is { } viewer)
        {
            viewer.CurrentChanged += OnViewerChanged;
        }

        OnPropertyChanged(nameof(Undo));
        OnPropertyChanged(nameof(Renderer));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        PageCount = session.PageCount;
        ErrorMessage = null;
        _noticeShown = false;
        Notice = null;
        ShowCurrentPage();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        StartMarks(session);
    }

    /// <summary>
    /// A new document: a new index, the tracker for its loaded pages (it recognises those already loaded at once),
    /// the list, then the scan over the current renderer around the current page.
    /// </summary>
    /// <remarks>
    /// Runs at the end of <see cref="Swap"/>, after the session is current: a failure here (the tracker recognises the
    /// loaded pages at once) must not leave the swap half done, so marks are simply off for this document.
    /// </remarks>
    private void StartMarks(DocumentSession session)
    {
        var index = new MarkIndex(session.PageCount);
        try
        {
            _markTracker = MarkTrackerFactory(session, _markRecognizer, index);
        }
#pragma warning disable CA1031 // Marks are an aid: the document stays open without them.
        catch (Exception)
#pragma warning restore CA1031
        {
            _markTracker = null;
            return;
        }

        _markIndex = index;
        _markSession = session;
        session.PageStrokesChanged += OnMarkPageStrokesChanged;
        Marks.Attach(index);
        StartScan();
    }

    // Any thread (the session raises it where the edit or the load happened): the rows of that page redraw their ink.
    private void OnMarkPageStrokesChanged(object? sender, PageStrokesChangedEventArgs e) => Marks.NotifyInkChanged(e.PageIndex);

    /// <summary>
    /// The ink a marks-list row draws (FR-103): the mark's strokes and its margin notes (<see cref="MarkRecognizer.MarginNotes"/>).
    /// A page loaded in the session gives its live strokes; any other page its stored strokes, through the scan's own
    /// source (the renderer's stroke cache, on the PDFium thread), so nothing is added to the session and no page is
    /// kept for it. The selection runs on the thread pool. Null when there is no document, the page is gone or the
    /// file is released by a save (the row keeps its placeholder and asks again when it is shown again).
    /// </summary>
    internal async Task<MarkInk?> LoadMarkInkAsync(Mark mark, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (Session is not { } session || _document is not { } document
            || mark.PageIndex < 0 || mark.PageIndex >= session.PageCount)
        {
            return null;
        }

        var curve = Tools.Curve;
        var smoothing = Tools.Smoothing;
        IReadOnlyList<InkStroke>? strokes;
        double widthPt;
        if (session.IsPageLoaded(mark.PageIndex))
        {
            var page = await session.EnsurePageAsync(mark.PageIndex, ct).ConfigureAwait(false); // loaded: no load
            strokes = page.Strokes; // an immutable snapshot
            widthPt = page.WidthPt;
        }
        else
        {
            IMarkPageSource? source;
            try
            {
                source = MarkSourceFactory(document);
            }
#pragma warning disable CA1031 // A row without its ink keeps its placeholder.
            catch (Exception)
#pragma warning restore CA1031
            {
                source = null;
            }

            if (source is null || mark.PageIndex >= source.PageCount)
            {
                return null;
            }

            widthPt = source.GetPageDimensions(mark.PageIndex).WidthPt;
            strokes = (await source.TryLoadStrokesForScanAsync(mark.PageIndex, ct).ConfigureAwait(false))?.Strokes;
        }

        if (strokes is null)
        {
            return null;
        }

        var recognizer = _markRecognizer;
        return await Task.Run(
            () =>
            {
                var ids = mark.StrokeIds.ToHashSet();
                var own = strokes.Where(s => ids.Contains(s.Id));
                var notes = recognizer.MarginNotes(mark, widthPt, strokes);
                return new MarkInk(mark, widthPt, own.Concat(notes).ToArray(), curve, smoothing);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A scan over the document's current renderer, into the current index: pages already scanned (by an earlier scan
    /// over a replaced renderer, or tracked live) are skipped, so a renderer swap loses no marks and loads no page twice.
    /// No scan while there is no renderer (a plain Save released it); the reopened one starts the next.
    /// </summary>
    private void StartScan()
    {
        if (_markIndex is not { } index || _document is not { } document || Session is not { } session)
        {
            return;
        }

        IMarkPageSource? source;
        try
        {
            source = MarkSourceFactory(document);
        }
#pragma warning disable CA1031 // Marks are an aid: without a source there is simply no scan.
        catch (Exception)
#pragma warning restore CA1031
        {
            source = null;
        }

        // A source of another page count would not match the index (a save never changes it): no scan then.
        if (source is null || source.PageCount != session.PageCount)
        {
            Marks.SetScanner(null);
            return;
        }

        var scanner = new MarkScanner(source, _markRecognizer, index, isBusy: null, MarkScanIdleGap);
        if (_scanPausedForInk)
        {
            scanner.Pause();
        }

        if (_scanPausedForSave)
        {
            scanner.Pause();
        }

        _markScanner = scanner;
        scanner.Start(Math.Max(session.CurrentPageIndex, 0));
        Marks.SetScanner(scanner);
    }

    private void StopScan()
    {
        var scanner = _markScanner;
        _markScanner = null;
        scanner?.Dispose();
        Marks.SetScanner(null);
    }

    /// <summary>The document goes (close or swap): the scan and the tracker stop before the index is dropped.</summary>
    private void StopMarks()
    {
        StopScan();
        if (_markSession is { } markSession)
        {
            markSession.PageStrokesChanged -= OnMarkPageStrokesChanged;
            _markSession = null;
        }

        _markTracker?.Dispose();
        _markTracker = null;
        _markIndex = null;
        Marks.Attach(null);
    }

    /// <summary>Takes one pause on the scan when <paramref name="paused"/> turns on, gives it back when it turns off.</summary>
    private void SetScanPause(ref bool flag, bool paused)
    {
        if (flag == paused)
        {
            return;
        }

        flag = paused;
        if (paused)
        {
            _markScanner?.Pause();
        }
        else
        {
            _markScanner?.Resume();
        }
    }

    /// <summary>The zoom is locked exactly while a stroke or an eraser gesture is live: the scan waits meanwhile.</summary>
    private void OnZoomPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ZoomViewModel.IsLocked))
        {
            SetScanPause(ref _scanPausedForInk, ZoomVm.IsLocked);
        }
    }

    private static async Task ReleaseQuietlyAsync(
        DocumentSession? session, OpenedDocument? opened, DocumentJournal? journal, DocumentLock? documentLock)
    {
        if (journal is not null)
        {
            try
            {
                await journal.DisposeAsync(); // closes the file; the journal stays on disk for the next attempt
            }
#pragma warning disable CA1031 // Best effort, like the rest of the release.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        documentLock?.Dispose();
        session?.Dispose();
        if (opened is null)
        {
            return;
        }

        try
        {
            await opened.Lifetime.DisposeAsync();
        }
        catch (Exception)
        {
            // Best effort: the original failure is what the user needs to hear about.
        }
    }

    /// <summary>Go to page (1-based, clamped): the viewport scrolls at once and records Back; the page then loads.</summary>
    private Task GoToPageAsync(int pageNumber) => Viewport.GoToPageAsync(pageNumber);

    /// <summary>
    /// The viewport jumped to a page: it becomes current with its editor once its strokes are loaded. Works while a
    /// save runs (the load may wait for the PDFium thread, or fail during a plain Save's release: then the error line
    /// says so and the page stays without an editor).
    /// </summary>
    private async Task NavigateToPageAsync(int pageIndex)
    {
        if (Session is not { } session)
        {
            return;
        }

        CancelErase(); // a jump scrolls the page under the eraser away
        try
        {
            // No confirmation and no undo clearing: the page's strokes stay in the session.
            await session.GoToPageAsync(pageIndex);
        }
        catch (Exception)
        {
            ReportFailure(session);
            if (ReferenceEquals(session, Session) && pageIndex == Viewport.CurrentPage)
            {
                // The viewport is on that page now: the session agrees, the page simply has no editor (no strokes
                // loaded), like any page not loaded yet. A later jump elsewhere is left alone.
                session.SetCurrentPageFromView(pageIndex);
            }
        }

        // A centre report or an undo may have changed the current page or its editor meanwhile without telling.
        RefreshCurrentEditor();
    }

    private async Task UndoAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        try
        {
            // Loads the page of the change if needed, then raises RevealRequested (OnRevealRequested).
            await session.UndoAsync();
        }
        catch (Exception)
        {
            ReportFailure(session); // the page of the change could not be loaded
        }

        RefreshCurrentEditor();
    }

    private async Task RedoAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        try
        {
            await session.RedoAsync();
        }
        catch (Exception)
        {
            ReportFailure(session);
        }

        RefreshCurrentEditor();
    }

    /// <summary>An undo or redo changed a page: scroll to it only when none of it is in view (FR-62).</summary>
    private void OnRevealRequested(object? sender, RevealRequestedEventArgs e)
    {
        if (!ReferenceEquals(sender, Session) || Viewport.VisiblePages.Contains(e.PageIndex))
        {
            return;
        }

        Viewport.ScrollToPage(e.PageIndex, recordHistory: false);
    }

    private void ReportFailure(DocumentSession session)
    {
        // A failure of a session replaced meanwhile is no longer the user's concern.
        if (ReferenceEquals(session, Session))
        {
            ErrorMessage = Strings.ErrorPageChangeFailed; // never the exception text (NFR-12)
        }
    }

    private bool CanSave() => Session is not null && _viewer is not null && !IsSaving;

    /// <summary>
    /// Runs one save; a request while another runs is ignored (never two at once). Shows the outcome's
    /// message, if any, in <see cref="ErrorMessage"/> and as a popup; a clean save clears the error line.
    /// </summary>
    private async Task<SaveOutcome> SaveCoreAsync(bool saveAs)
    {
        if (IsSaving || Session is not { } session || _viewer is not { } viewer)
        {
            return SaveOutcome.Cancelled;
        }

        var completion = new TaskCompletionSource<SaveOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runningSave = completion.Task;
        IsSaving = true;

        // The save counts as running until its popup is closed and every Saved listener has run: a close request
        // awaits the running-save task, so it can neither stack a question on the popup nor close the window
        // before the journal heard of the save. On every exit, the task completes and IsSaving clears, last.
        var result = new SaveResult(SaveOutcome.Failed, Strings.ErrorSaveFailedAfterRelease);
        try
        {
            try
            {
                result = await SaveService.SaveAsync(session, viewer, saveAs, CancellationToken.None);
            }
#pragma warning disable CA1031 // The service reports every failure itself; this is a last line of defence.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Unknown state: the neutral message, never "the file was not touched".
            }

            if (ReferenceEquals(session, Session))
            {
                if (result.Message is { } message)
                {
                    ErrorMessage = message;
                    ShowMessageQuietly(message);
                }
                else if (result.Outcome == SaveOutcome.Saved)
                {
                    ErrorMessage = null;
                }
            }

            if (result.Outcome == SaveOutcome.Saved)
            {
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(WindowTitle)); // Save as changed the file name
            }

            if (result.Saved is { } saved)
            {
                // Without Saved (the file is saved but the session's state is not) the journal stays as it is.
                await UpdateJournalAfterSaveAsync(saved);
                RaiseSaved(saved);
            }
        }
        finally
        {
            _runningSave = null;
            completion.SetResult(result.Outcome);
            IsSaving = false;
        }

        return result.Outcome; // what happened to the file, whatever the listeners did
    }

    /// <summary>A popup that fails must not turn a save outcome into an exception.</summary>
    private void ShowMessageQuietly(string message)
    {
        try
        {
            _dialogs.ShowMessage(message);
        }
#pragma warning disable CA1031 // The message is also in ErrorMessage.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Tells every <see cref="Saved"/> listener, each isolated: one failing listener stops neither the others nor the caller.</summary>
    private void RaiseSaved(DocumentSavedEventArgs saved)
    {
        if (Saved is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<DocumentSavedEventArgs>>())
        {
            try
            {
                handler(this, saved);
            }
#pragma warning disable CA1031 // A listener failing does not undo the save; the user is told.
            catch (Exception)
#pragma warning restore CA1031
            {
                ReportListenerFailure();
            }
        }
    }

    /// <summary>
    /// A successful save (FR-84): Save rewrites the journal as a header for the saved file plus the changes made
    /// while the save ran; Save as moves the single-writer lock to the new file and the journal with it (or stops
    /// journaling if another process holds that file). Never throws.
    /// </summary>
    /// <remarks>
    /// Only the fingerprint is awaited; everything after it is synchronous on the UI thread, where commands also
    /// run, so the catch-up the journal computes includes every command executed until then and none can append
    /// in between (commands executed during the await went to the old journal, which the rewrite replaces).
    /// </remarks>
    private async Task UpdateJournalAfterSaveAsync(DocumentSavedEventArgs saved)
    {
        if (_journal is null || !ReferenceEquals(saved.Session, Session))
        {
            return;
        }

        // The service's fingerprint may have failed (the session then keeps the old one): never trust it for a header.
        DocumentFingerprint? fingerprint = null;
        try
        {
            fingerprint = await DocumentFingerprint.ComputeAsync(saved.NewPath, CancellationToken.None);
        }
#pragma warning disable CA1031 // Without a fingerprint the journal is kept as it is (see DocumentJournal.OnSaved).
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        if (_journal is not { } journal || !ReferenceEquals(saved.Session, Session))
        {
            return;
        }

        var newPathAvailable = true;
        if (saved.IsSaveAs && Recovery is { } recovery)
        {
            DocumentLock? newLock = null;
            try
            {
                newLock = DocumentLock.TryAcquire(recovery.Store.RootDirectory, saved.NewPath);
            }
#pragma warning disable CA1031 // Treated like a lock held elsewhere: journaling stops for the new file.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            ReleaseLock(); // the old file is no longer this session's
            _lock = newLock;
            newPathAvailable = newLock is not null;
        }

        try
        {
            journal.OnSaved(saved, fingerprint, newPathAvailable);
        }
#pragma warning disable CA1031 // The file is saved; the journal's failure is reported (FaultChanged), not thrown.
        catch (Exception)
#pragma warning restore CA1031
        {
            UpdateJournalWarning();
        }

        if (!journal.IsEnabled)
        {
            Notice = Strings.NoticeJournalDisabled;
        }
    }

    /// <summary>
    /// With unsaved changes, asks Save / Don't save / Cancel. After a successful save it asks again if strokes
    /// drawn during the save left the document dirty, so nothing is closed unasked.
    /// </summary>
    private async Task<UnsavedChanges> ResolveUnsavedChangesAsync()
    {
        while (Session is { IsDirty: true })
        {
            switch (_dialogs.AskSaveChanges())
            {
                case SaveChangesAnswer.Save:
                    if (await SaveCoreAsync(saveAs: false) != SaveOutcome.Saved)
                    {
                        return UnsavedChanges.Stay;
                    }

                    break;
                case SaveChangesAnswer.DontSave:
                    return UnsavedChanges.Discard;
                default:
                    return UnsavedChanges.Stay;
            }
        }

        return UnsavedChanges.Resolved;
    }

    private void OnDirtyChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, Session))
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    private void OnViewerChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _viewer))
        {
            OnPropertyChanged(nameof(Renderer));

            // The scan reads through one renderer: it follows the swap (stopped while released, resumed over the
            // reopened one with the same index, so the pages already scanned stay and are not loaded again).
            StopScan();
            StartScan();
        }
    }

    private void OnCurrentPageChanged(object? sender, EventArgs e)
    {
        if (sender is DocumentSession session && ReferenceEquals(session, Session))
        {
            ShowCurrentPage(); // also after undo or redo navigated to another page

            // Only settled page changes and jumps reach here (the viewport debounces scrolling): the scan goes on
            // around the page being read.
            if (_markScanner is { } scanner)
            {
                scanner.Recentre(Math.Max(session.CurrentPageIndex, 0));
                MarkRecentresForTest++;
            }
        }
    }

    private void OnUndoStateChanged(object? sender, EventArgs e)
    {
        if (!_completingErase)
        {
            // Undo or redo while the eraser is down: the gesture's snapshot is stale, so drop it
            // rather than overwrite the page with it on EndErase.
            CancelErase();
        }

        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The current page may have got its editor without a page change (a load for the view, an undo): the session
    /// raises nothing then, so <see cref="Editor"/> is re-read here.
    /// </summary>
    private void RefreshCurrentEditor()
    {
        if (Session is { } session && !ReferenceEquals(session.CurrentEditor, Editor))
        {
            ShowCurrentPage();
        }
    }

    /// <summary>
    /// Publishes the current page. A live eraser gesture is not touched: it belongs to its own page, which may stay
    /// in view while the centre moves on.
    /// </summary>
    private void ShowCurrentPage()
    {
        var session = Session;
        var editor = session?.CurrentEditor;
        Editor = editor;

        PageNumber = session is { CurrentPageIndex: >= 0 } ? session.CurrentPageIndex + 1 : 0;
        UpdateNotice();
    }

    private void UpdateNotice()
    {
        var unreadable = Session?.UnreadableAnnotationCount ?? 0;
        HasUnreadableAnnotations = unreadable > 0;
        if (unreadable > 0 && !_noticeShown)
        {
            _noticeShown = true;
            Notice = Strings.NoticeUnreadableAnnotations;
        }
    }

    private static string MessageFor(PdfOpenError error) => error switch
    {
        PdfOpenError.FileNotFound => Strings.ErrorFileNotFound,
        PdfOpenError.InvalidFormat => Strings.ErrorInvalidFormat,
        PdfOpenError.PasswordRequired => Strings.ErrorPassword,
        PdfOpenError.UnsupportedSecurity => Strings.ErrorUnsupported,
        _ => Strings.ErrorUnknown,
    };

    private enum UnsavedChanges
    {
        /// <summary>Nothing unsaved (any more): go ahead.</summary>
        Resolved,

        /// <summary>The user chose to drop the changes.</summary>
        Discard,

        /// <summary>Cancelled, or the save did not succeed: keep the document.</summary>
        Stay,
    }

    /// <summary>A position to show at open, and whether it was taken in fit-width mode (FR-04, FR-07).</summary>
    private readonly record struct RestoredPosition(ViewPosition Position, bool FitWidth);
}
