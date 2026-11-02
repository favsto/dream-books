using System.IO;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Journal;
using DreamBooks.Pdf;

namespace DreamBooks.App.Services;

/// <summary>
/// The journal of one open document (FR-84): a <see cref="JournalRecorder"/> on the session whose sink opens the
/// journal file lazily at the first line, so a document opened and closed without changes leaves no file.
/// Saving rewrites it as a fresh header plus the catch-up (<see cref="JournalCatchUp"/>: what changed while the
/// save ran), Save as moves it to the new path, Don't save deletes it. A disabled journal records nothing and never
/// touches the file on disk (unreadable or refused journals).
/// </summary>
/// <remarks>
/// UI thread only, like the session's undo history it listens to. Commands also run on the UI thread, so none
/// can append between the catch-up being computed and written in <see cref="OnSaved"/>, which is synchronous.
/// When a rewrite fails, the next command rewrites the whole journal from the session's state instead of
/// appending (resync), so nothing unsaved is ever missing from a journal that is written at all.
/// </remarks>
public sealed class DocumentJournal : IAsyncDisposable
{
    private readonly RecoveryStore _store;
    private readonly Func<string, DocumentFingerprint, IJournalSession> _begin;
    private readonly DocumentSession _session;
    private readonly List<IJournalSession> _retired = [];
    private JournalRecorder? _recorder;
    private IJournalSession? _journal;
    private bool _enabled;

    // The file this journal is for and the fingerprint its header carries when (re)written.
    private string _keyPath;
    private DocumentFingerprint _keyFingerprint;

    // True when a journal file of this document is known to exist (found at open, or created by us).
    private bool _fileOnDisk;

    // True after a failed rewrite: the next command rewrites header + catch-up instead of appending.
    private bool _resyncPending;
    private bool _closed;

    internal DocumentJournal(
        RecoveryStore store,
        Func<string, DocumentFingerprint, IJournalSession> begin,
        DocumentSession session,
        bool enabled,
        bool existing)
    {
        _store = store;
        _begin = begin;
        _session = session;
        _enabled = enabled;
        _keyPath = session.Path;
        _keyFingerprint = session.OpenFingerprint;
        if (!enabled)
        {
            return;
        }

        _recorder = new JournalRecorder(session, new Sink(this));
        if (existing)
        {
            // After a replay the journal already holds the document's unsaved changes: keep appending to it.
            _fileOnDisk = true;
            try
            {
                EnsureBegun();
            }
#pragma warning disable CA1031 // Retried at the next append; the user is warned through FaultChanged.
            catch (Exception)
#pragma warning restore CA1031
            {
                SetFaulted(true);
            }
        }
    }

    /// <summary>Raised when <see cref="IsFaulted"/> changes.</summary>
    public event EventHandler? FaultChanged;

    /// <summary>False when this document's journal is left alone (nothing recorded, nothing deleted).</summary>
    public bool IsEnabled => _enabled;

    /// <summary>True while the last journal operation failed; the next successful write clears it.</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>
    /// Records where the reader is, but only when there is something to restore (unsaved changes) or a journal
    /// already exists: a view change alone never creates a journal file.
    /// </summary>
    public void RecordView(int pageIndex, double zoom, double scrollX, double scrollY, bool? fitWidth = null)
    {
        if (!_enabled || _recorder is null || _resyncPending || (!_session.IsDirty && !_fileOnDisk))
        {
            return;
        }

        _recorder.RecordView(pageIndex, zoom, scrollX, scrollY, fitWidth);
    }

    /// <summary>
    /// A successful save (FR-84), called after the session was marked saved. A plain Save atomically rewrites the
    /// journal as a header for the saved file followed by the catch-up; Save as writes that to the new path's
    /// journal (only if the catch-up is not empty: otherwise the new journal is created at the next change) and
    /// then deletes the old path's journal. <paramref name="savedFingerprint"/> null (the saved file could not be
    /// fingerprinted): a plain Save keeps the journal untouched, since a header with a wrong fingerprint is worse
    /// than an old one (the restore then asks); a Save as keeps the old journal and stops journaling. With
    /// <paramref name="newPathAvailable"/> false (another process holds the new file) the old journal is deleted and
    /// journaling stops, never touching that process's journal. Throws <see cref="IOException"/> when the journal
    /// could not be updated; the warning then shows and the next command resyncs.
    /// </summary>
    public void OnSaved(DocumentSavedEventArgs saved, DocumentFingerprint? savedFingerprint, bool newPathAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (!_enabled)
        {
            return;
        }

        if (savedFingerprint is not { } fingerprint)
        {
            if (saved.IsSaveAs)
            {
                Disable();
            }

            return;
        }

        var lines = CatchUpLines();
        if (!saved.IsSaveAs)
        {
            _keyPath = _session.Path;
            _keyFingerprint = fingerprint;
            if (_journal is null && !_fileOnDisk && lines.Count == 0)
            {
                return; // nothing was ever journalled and nothing is unsaved
            }

            Rewrite(lines);
            return;
        }

        _keyPath = saved.NewPath;
        _keyFingerprint = fingerprint;
        Exception? failure = null;
        IJournalSession? fresh = null;
        if (newPathAvailable)
        {
            try
            {
                // A journal left from an earlier life of the overwritten file must never be appended to.
                _store.Delete(saved.NewPath);
                if (lines.Count > 0)
                {
                    fresh = _begin(_keyPath, fingerprint);
                    fresh.Truncate(Header(), lines);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failure = ex;
                if (fresh is not null)
                {
                    TryDelete(fresh);
                    fresh = null;
                }
            }
        }

        var old = _journal;
        _journal = fresh;
        if (failure is null)
        {
            // The new journal (or the file itself, when nothing is unsaved) holds everything: drop the old one.
            try
            {
                if (old is not null)
                {
                    old.Delete();
                }
                else if (_fileOnDisk)
                {
                    _store.Delete(saved.OldPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
                if (old is not null)
                {
                    _retired.Add(old); // its handle is closed on dispose, not leaked
                }
            }
        }
        else if (old is not null)
        {
            _retired.Add(old); // still holds the changes (for the old file): kept on disk, closed on dispose
        }

        _fileOnDisk = fresh is not null;
        if (!newPathAvailable)
        {
            Disable();
        }
        else if (failure is not null && fresh is null)
        {
            _resyncPending = true;
        }

        if (failure is not null)
        {
            SetFaulted(true);
            throw new IOException("The recovery journal could not be moved to the new file.", failure);
        }
    }

    /// <summary>
    /// Makes the next command rewrite the whole journal (header + everything unsaved) instead of appending, for a
    /// fresh journal attached to a session that already has unsaved changes.
    /// </summary>
    public void RequestResync()
    {
        if (_enabled)
        {
            _resyncPending = true;
        }
    }

    /// <summary>
    /// The user dropped the changes ("Don't save", or closing with nothing unsaved): deletes the journal and stops
    /// recording. A disabled journal is left alone. Throws <see cref="IOException"/> if the file stays.
    /// </summary>
    public void Discard()
    {
        if (!_enabled)
        {
            return;
        }

        Disable();
        var journal = _journal;
        _journal = null;
        if (journal is not null)
        {
            journal.Delete();
        }
        else if (_fileOnDisk)
        {
            _store.Delete(_keyPath);
        }

        _fileOnDisk = false;
    }

    /// <summary>Stops recording and closes the files, keeping them on disk. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        _closed = true;
        Disable();
        var toClose = _retired.ToList();
        _retired.Clear();
        if (_journal is { } journal)
        {
            _journal = null;
            toClose.Add(journal);
        }

        foreach (var session in toClose)
        {
            await session.DisposeAsync();
        }
    }

    private void Disable()
    {
        _enabled = false;
        _recorder?.Dispose();
        _recorder = null;
    }

    private JournalHeader Header() =>
        new(JournalCodec.CurrentFormatVersion, Path.GetFullPath(_keyPath), _keyFingerprint);

    private List<string> CatchUpLines() =>
        JournalCatchUp.Build(_session.CaptureSaveSnapshot()).Select(e => JournalCodec.SerializeEntry(e)).ToList();

    /// <summary>Header + catch-up in one atomic replace; on failure the old content stays and a resync is pending.</summary>
    private void Rewrite(IReadOnlyList<string> lines)
    {
        try
        {
            EnsureBegun();
            _journal!.Truncate(Header(), lines);
            _resyncPending = false;
        }
        catch (Exception)
        {
            _resyncPending = true;
            SetFaulted(true);
            throw;
        }
    }

    private void EnsureBegun()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_journal is null)
        {
            _journal = _begin(_keyPath, _keyFingerprint);
            _fileOnDisk = true;
        }
    }

    /// <summary>
    /// Once the journal at the current path holds everything, deletes the old-path journals kept after a failed
    /// Save-as write, so they are never restored into the old file. False if one could not be deleted (it stays on
    /// disk, is closed on dispose, and the warning shows).
    /// </summary>
    private bool DeleteRetired()
    {
        var ok = true;
        foreach (var retired in _retired.ToList())
        {
            try
            {
                retired.Delete();
                _retired.Remove(retired);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }

        return ok;
    }

    private static void TryDelete(IJournalSession session)
    {
        try
        {
            session.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Header-only at most: harmless, and replaced by the next resync.
        }
    }

    private void SetFaulted(bool faulted)
    {
        if (IsFaulted == faulted)
        {
            return;
        }

        IsFaulted = faulted;
        FaultChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The recorder's sink: opens the journal at the first line, resyncs after a failed rewrite, tracks faults.</summary>
    private sealed class Sink(DocumentJournal owner) : IJournalSink
    {
        public void Append(string line)
        {
            try
            {
                owner.EnsureBegun();
                if (owner._resyncPending)
                {
                    try
                    {
                        // The session already includes this command: the rewrite covers it, so the line is not appended.
                        owner._journal!.Truncate(owner.Header(), owner.CatchUpLines());
                        owner._resyncPending = false;
                    }
                    catch (Exception)
                    {
                        // The rewrite keeps failing (e.g. the file is held by an antivirus): the old content is intact,
                        // so append to it; replayed, old content + these lines is still the whole story. Stays faulted
                        // and retries the rewrite at the next command.
                        owner._journal!.Append(line);
                        owner.SetFaulted(true);
                        return;
                    }

                    if (!owner.DeleteRetired())
                    {
                        owner.SetFaulted(true);
                        return;
                    }
                }
                else
                {
                    owner._journal!.Append(line);
                }
            }
            catch (Exception)
            {
                // The line is lost (or only partly written): the next command rewrites the whole journal from the
                // session instead of appending after a hole. The warning stays until that rewrite succeeds.
                if (owner._enabled)
                {
                    owner._resyncPending = true;
                }

                owner.SetFaulted(true);
                throw; // the recorder counts the failure and keeps going
            }

            owner.SetFaulted(false); // reached only by a plain append with no resync pending, or a successful resync
        }
    }
}
