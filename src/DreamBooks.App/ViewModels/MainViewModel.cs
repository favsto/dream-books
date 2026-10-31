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
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;
using DreamBooks.Pdf;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// Window state over one <see cref="DocumentSession"/>: the current page, the document's undo history
/// (shared by all its pages, FR-62), the eraser gesture, the notices, and saving: Save, Save as, the dirty
/// mark and the unsaved-changes question on close or on opening another document (FR-80, FR-85, FR-89).
/// Strokes live in the session, so they survive page changes (FR-83).
/// </summary>
/// <remarks>
/// At most one save runs at a time (<see cref="IsSaving"/>); while it runs, drawing and erasing go on, but
/// page navigation, opening and saving again are disabled. Strokes drawn during a save stay unsaved.
/// </remarks>
public sealed class MainViewModel : ObservableObject
{
    private readonly IFileDialogService _dialogs;

    // Stands in for the session's history while no document is open: never executed into, so always empty.
    private readonly UndoManager _noDocumentUndo = new();
    private DocumentSession? _session;
    private OpenedDocument? _document;
    private IViewerHandle? _viewer;
    private PageEditor? _editor;
    private bool _isSaving;
    private Task<SaveOutcome>? _runningSave;
    private PageSize _pageSize;
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
    private RestoredView? _viewToRestore;
    private bool _listenerFailed;

    /// <param name="dialogs">The dialogs.</param>
    /// <param name="recovery">
    /// Where journals and document locks live (FR-84); null disables crash recovery and the single-writer lock
    /// (tests that do not exercise them).
    /// </param>
    public MainViewModel(IFileDialogService dialogs, RecoveryStore? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        _dialogs = dialogs;
        Recovery = recovery is null ? null : new RecoveryService(recovery, dialogs);
        Tools = new ToolsViewModel();
        ZoomVm = new ZoomViewModel();
        OpenDocument = OpenedDocument.OpenPdfAsync;
        SaveService = new DocumentSaveService(new PdfDocumentSaver(), dialogs);

        OpenPdfCommand = new AsyncRelayCommand(OpenPdfAsync, () => !IsSaving);
        GoToPageCommand = new AsyncRelayCommand<int>(GoToPageAsync, _ => !IsSaving);
        PreviousPageCommand = new AsyncRelayCommand(() => GoToPageAsync(PageNumber - 1), () => !IsSaving);
        NextPageCommand = new AsyncRelayCommand(() => GoToPageAsync(PageNumber + 1), () => !IsSaving);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => Session is not null && Undo.CanUndo);
        RedoCommand = new AsyncRelayCommand(RedoAsync, () => Session is not null && Undo.CanRedo);
        SaveCommand = new AsyncRelayCommand(() => SaveCoreAsync(saveAs: false), CanSave);
        SaveAsCommand = new AsyncRelayCommand(() => SaveCoreAsync(saveAs: true), CanSave);
    }

    /// <summary>Raised after a different page (or document) became current.</summary>
    public event EventHandler? PageChanged;

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

    /// <summary>The open document, or null.</summary>
    public DocumentSession? Session
    {
        get => _session;
        private set => SetProperty(ref _session, value);
    }

    /// <summary>The current document's undo history; an empty, inert one when no document is open.</summary>
    public UndoManager Undo => Session?.Undo ?? _noDocumentUndo;

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
                OpenPdfCommand.NotifyCanExecuteChanged();
                GoToPageCommand.NotifyCanExecuteChanged();
                PreviousPageCommand.NotifyCanExecuteChanged();
                NextPageCommand.NotifyCanExecuteChanged();
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

    public PageSize PageSize
    {
        get => _pageSize;
        private set => SetProperty(ref _pageSize, value);
    }

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

    /// <summary>
    /// Zoom and scroll offsets to apply once after a restore from the journal (FR-04); null for a document opened
    /// without one. The zoom is already applied to <see cref="ZoomVm"/>; the view applies the scroll offsets.
    /// </summary>
    public RestoredView? ViewToRestore
    {
        get => _viewToRestore;
        private set => SetProperty(ref _viewToRestore, value);
    }

    /// <summary>Crash recovery over the recovery store, or null when the view model has none.</summary>
    internal RecoveryService? Recovery { get; }

    /// <summary>The eraser gesture in progress, or null; its <see cref="EraserSession.Strokes"/> is the live preview.</summary>
    public EraserSession? ActiveErase
    {
        get => _activeErase;
        private set => SetProperty(ref _activeErase, value);
    }

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
                RaiseDiscardRequested();
                await CloseJournalAsync(userDiscarded: true);
                ReleaseLock();
                return true;
            case UnsavedChanges.Resolved:
                await CloseJournalAsync(userDiscarded: false);
                ReleaseLock();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The view settled on a position (debounced by the view): journalled so a crash restarts there (FR-04),
    /// only while there are unsaved changes or a journal already exists.
    /// </summary>
    public void ReportViewState(int pageIndex, double zoom, double scrollX, double scrollY)
    {
        // JSON has no NaN or infinity: a non-finite value would fail the journal write.
        if (pageIndex < 0 || !double.IsFinite(zoom) || zoom <= 0)
        {
            return;
        }

        static double Offset(double value) => double.IsFinite(value) && value > 0 ? value : 0;
        _journal?.RecordView(pageIndex, zoom, Offset(scrollX), Offset(scrollY));
    }

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
    /// Starts an eraser gesture with the selected mode (FR-31). Zoom stays locked until it ends. No-op
    /// without a page or while a gesture is already live.
    /// </summary>
    public void BeginErase()
    {
        if (ActiveErase is not null || Editor is not { } editor)
        {
            return;
        }

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
        try
        {
            opened = await OpenDocument(path, ct);
            if (opened.Source.PageCount == 0)
            {
                throw new PdfOpenException(PdfOpenError.InvalidFormat);
            }

            // Everything below fails before anything is swapped, so the current document stays (NFR-11).
            _ = opened.Source.GetPageDimensions(0);
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

            var firstPage = restore?.LastView is { } view ? Math.Clamp(view.PageIndex, 0, session.PageCount - 1) : 0;
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
        Swap(opened, session, journal, newLock ?? _lock);
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

                if (restore.LastView is { } view)
                {
                    RestoreView(view);
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

    /// <summary>Applies the journalled zoom (clamped; ignored if not finite) and publishes the scroll offsets.</summary>
    private void RestoreView(ViewEntry view)
    {
        if (double.IsFinite(view.Zoom) && view.Zoom > 0)
        {
            ZoomVm.Zoom = view.Zoom; // clamped to 25%..800% by the zoom model
        }

        static double Offset(double value) => double.IsFinite(value) && value > 0 ? value : 0;
        ViewToRestore = new RestoredView(ZoomVm.Zoom, Offset(view.ScrollX), Offset(view.ScrollY));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes a ready session current in one synchronous step: no half-switched state is observable.</summary>
    private void Swap(OpenedDocument opened, DocumentSession session, DocumentJournal? journal, DocumentLock? documentLock)
    {
        CancelErase();
        if (Session is { } previous)
        {
            previous.CurrentPageChanged -= OnCurrentPageChanged;
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
        AttachJournal(journal);
        ViewToRestore = null;
        Session = session;
        session.CurrentPageChanged += OnCurrentPageChanged;
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

    private async Task GoToPageAsync(int pageNumber)
    {
        // Disabled while saving: a page load would queue behind the write on the PDFium thread, and a plain
        // Save has the file released for a moment.
        if (IsSaving || Session is not { } session || pageNumber < 1 || pageNumber > PageCount || pageNumber == PageNumber)
        {
            return;
        }

        CancelErase();
        try
        {
            // No confirmation and no undo clearing: the page's strokes stay in the session.
            await session.GoToPageAsync(pageNumber - 1);
        }
        catch (Exception)
        {
            // The session changes nothing until the page is loaded, so the current page stays intact.
            ReportFailure(session);
        }
    }

    private async Task UndoAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        try
        {
            await session.UndoAsync();
        }
        catch (Exception)
        {
            ReportFailure(session); // the page of the change could not be loaded
        }
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
        }
    }

    private void OnCurrentPageChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, Session))
        {
            ShowCurrentPage(); // also after undo or redo navigated to another page
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

    private void ShowCurrentPage()
    {
        CancelErase();
        var session = Session;
        var editor = session?.CurrentEditor;
        Editor = editor;

        // The page was built from the source's single-source dimensions, so the view's page frame and the
        // strokes use the same numbers (FR-26).
        PageSize = editor is null ? default : new PageSize(editor.Page.WidthPt, editor.Page.HeightPt);
        PageNumber = session is { CurrentPageIndex: >= 0 } ? session.CurrentPageIndex + 1 : 0;
        UpdateNotice();
        PageChanged?.Invoke(this, EventArgs.Empty);
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
}
