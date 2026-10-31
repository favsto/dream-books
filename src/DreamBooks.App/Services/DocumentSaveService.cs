using System.Globalization;
using System.IO;
using DreamBooks.App.Resources;
using DreamBooks.Core.Documents;
using DreamBooks.Pdf;

namespace DreamBooks.App.Services;

/// <summary>
/// The saving pipeline (FR-80, FR-89, NFR-10): checks, verified copy, release, replace, reopen, mark saved.
/// The original (or the Save as target) is only ever replaced by a verified copy; on every failure the
/// session stays dirty, the viewer is reopened if it was released, and the user gets an Italian message that
/// never contains exception text (NFR-12). The only path ever shown is the folder of a kept verified copy.
/// </summary>
/// <remarks>
/// Call from the UI thread: continuations come back to it, where the session and the viewer are used.
/// One save at a time is the caller's job (<c>MainViewModel.IsSaving</c>).
/// </remarks>
public sealed class DocumentSaveService
{
    private readonly IDocumentWriter _writer;
    private readonly IFileDialogService _dialogs;
    private readonly Func<string, string, Task> _replace;

    // Ruling 16: after a replace that left the original missing, the verified copy stands in for it as the
    // source of later saves of that session, until one succeeds. The session's baseline is not advanced, so
    // the next write re-applies every change since the original baseline (safe by ruling 14).
    private DocumentSession? _adoptedSession;
    private string? _adoptedPath;

    /// <param name="writer">Produces the verified temporary copy.</param>
    /// <param name="dialogs">Questions and the Save as file picker.</param>
    /// <param name="replace">Test seam: puts the temp copy in the target's place (default: <see cref="FileReplacer"/> on a background thread).</param>
    public DocumentSaveService(IDocumentWriter writer, IFileDialogService dialogs, Func<string, string, Task>? replace = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(dialogs);
        _writer = writer;
        _dialogs = dialogs;
        _replace = replace ?? ReplaceOnBackgroundThreadAsync;
    }

    /// <summary>
    /// Saves <paramref name="session"/> to its file, or (<paramref name="saveAs"/>) to a file the user picks.
    /// Never throws: every outcome comes back as a <see cref="SaveResult"/>.
    /// </summary>
    public async Task<SaveResult> SaveAsync(DocumentSession session, IViewerHandle viewer, bool saveAs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(viewer);
        var documentPath = session.Path;

        // After a replace that lost the original (ruling 16), the verified copy is the document's content.
        var adopted = AdoptedCopyOf(session);
        var source = adopted ?? documentPath;

        // 1. Encrypted or restricted documents are never rewritten (nothing is asked or written).
        if (!viewer.Info.CanBeRewritten)
        {
            return new SaveResult(SaveOutcome.Refused, Strings.ErrorSaveEncrypted);
        }

        // 2. A full rewrite invalidates digital signatures.
        if (viewer.Info.SignatureCount > 0 && !_dialogs.ConfirmSignatureInvalidation())
        {
            return Cancelled();
        }

        string tempPath;
        string target;
        SaveSnapshot snapshot;
        try
        {
            // 3-4. The target; writing over the document's own file (Save, or Save as onto it) first checks that
            // no other program changed it since it was opened. With an adopted copy the original is gone, so
            // there is nothing to compare.
            var overwriteAccepted = adopted is not null;
            while (true)
            {
                if (saveAs)
                {
                    if (_dialogs.PickSavePdf(Path.GetFileName(documentPath)) is not { } picked)
                    {
                        return Cancelled();
                    }

                    target = Path.GetFullPath(picked);
                }
                else
                {
                    target = documentPath;
                }

                if (overwriteAccepted || !SamePath(documentPath, target) || await IsUnchangedAsync(session, ct))
                {
                    break;
                }

                switch (_dialogs.AskFileChanged())
                {
                    case FileChangedAnswer.SaveAs:
                        saveAs = true; // pick (again)
                        break;
                    case FileChangedAnswer.OverwriteAnyway:
                        overwriteAccepted = true;
                        break;
                    default:
                        return Cancelled();
                }

                if (overwriteAccepted)
                {
                    break;
                }
            }

            // 5. One snapshot for the whole save (ruling 7): strokes drawn from now on stay unsaved. The writer
            // reads the opened file (or the adopted copy); its temp copy sits next to the target.
            snapshot = session.CaptureSaveSnapshot();
            tempPath = await _writer.WriteVerifiedCopyAsync(source, target, snapshot.Changes, ct);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (PdfSaveRefusedException)
        {
            return new SaveResult(SaveOutcome.Refused, Strings.ErrorSaveEncrypted);
        }
        catch (SaveVerificationException)
        {
            return Failed(Strings.ErrorSaveVerification);
        }
#pragma warning disable CA1031 // Any failure means "not saved"; its text may hold paths, so it is never shown.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Failed(Strings.ErrorSaveFailed); // nothing was released or replaced: the file is untouched
        }

        // From here the verified copy exists: no cancellation, finish or clean up.
        var isSaveAs = !SamePath(documentPath, target);
        var released = false;
        try
        {
            // 6. The viewer's PDFium document locks the file it shows: release it before replacing that file.
            // For Save as the old file stays shown until the new one is in place.
            if (!isSaveAs)
            {
                await viewer.ReleaseAsync();
                released = true;
            }

            // 7. Replace (or, for a missing target, move into place).
            await _replace(tempPath, target);
        }
        catch (ReplaceFailedException ex)
        {
            return await OnReplaceFailedAsync(ex, session, viewer, source, released, isSaveAs);
        }
#pragma warning disable CA1031 // See above.
        catch (Exception)
#pragma warning restore CA1031
        {
            DeleteStrayCopy(tempPath, source, target);
            await ReopenQuietlyAsync(viewer, source, released);
            return Failed(Strings.ErrorSaveFailedAfterRelease); // state unknown: never claim "untouched"
        }

        // 8. The file IS saved now. A viewer that cannot reopen does not turn that into a failure.
        string? notice = null;
        try
        {
            await viewer.ReopenAsync(target, CancellationToken.None);
        }
#pragma warning disable CA1031 // The save succeeded; the user is told the view could not follow.
        catch (Exception)
#pragma warning restore CA1031
        {
            notice = Strings.ErrorReopenAfterSave;
        }

        // The adopted copy's content is now in the saved file.
        if (adopted is not null)
        {
            DeleteAdoptedCopy();
        }

        // 9. Rebaseline to the snapshot and re-key to the saved file.
        DocumentFingerprint? fingerprint = null;
        try
        {
            fingerprint = await DocumentFingerprint.ComputeAsync(target, CancellationToken.None);
        }
#pragma warning disable CA1031 // Keeping the old fingerprint only makes the next Save ask "file changed".
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        try
        {
            session.MarkSaved(snapshot, isSaveAs ? target : null, fingerprint);
        }
#pragma warning disable CA1031 // The file is saved; only the app's bookkeeping failed. No journal event then.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new SaveResult(SaveOutcome.Saved, Strings.ErrorSavedStateNotUpdated);
        }

        // 10. What the journal needs (Task 13).
        return new SaveResult(
            SaveOutcome.Saved, notice, new DocumentSavedEventArgs(session, snapshot, documentPath, session.Path, isSaveAs));
    }


    /// <summary>
    /// The replace failed after every retry; the verified copy is kept (it may be the only complete one).
    /// When the document's own file went missing, the copy is adopted: the viewer shows it and later saves of
    /// this session read it. Otherwise the viewer goes back to what it showed.
    /// </summary>
    private async Task<SaveResult> OnReplaceFailedAsync(
        ReplaceFailedException ex, DocumentSession session, IViewerHandle viewer, string source, bool released, bool isSaveAs)
    {
        var copyExists = ex.TempExists && File.Exists(ex.TempPath);
        var folder = Path.GetDirectoryName(ex.TempPath);
        if (!isSaveAs && copyExists && !ex.TargetExists)
        {
            if (_adoptedPath is { } previous && !SamePath(previous, ex.TempPath))
            {
                DeleteAdoptedCopy(); // superseded: the new copy holds everything it held
            }

            _adoptedSession = session;
            _adoptedPath = ex.TempPath;
            try
            {
                await viewer.ReopenAsync(ex.TempPath, CancellationToken.None);
            }
#pragma warning disable CA1031 // The view keeps its last bitmap; saving still works from the copy.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            return Failed(string.Format(CultureInfo.CurrentCulture, Strings.ErrorSaveTargetMissing, folder));
        }

        await ReopenQuietlyAsync(viewer, source, released);
        if (copyExists)
        {
            return Failed(string.Format(CultureInfo.CurrentCulture, Strings.ErrorSaveReplaceFailed, folder));
        }

        // Without a copy: the target is untouched if it is still there; otherwise nobody can say.
        return Failed(ex.TargetExists ? Strings.ErrorSaveFailed : Strings.ErrorSaveFailedAfterRelease);
    }

    /// <summary>
    /// True when the session's own file went missing and the only complete copy is the verified temp file the
    /// session now reads from: its journal must then survive a "Don't save".
    /// </summary>
    public bool HasAdoptedCopy(DocumentSession session) => AdoptedCopyOf(session) is not null;

    /// <summary>The folder holding the session's adopted copy, or null without one.</summary>
    public string? AdoptedCopyFolder(DocumentSession session) =>
        AdoptedCopyOf(session) is { } path ? Path.GetDirectoryName(path) : null;

    private string? AdoptedCopyOf(DocumentSession session) =>
        ReferenceEquals(session, _adoptedSession) && _adoptedPath is { } path && File.Exists(path) ? path : null;

    private void DeleteAdoptedCopy()
    {
        var path = _adoptedPath;
        _adoptedSession = null;
        _adoptedPath = null;
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover .drbk-tmp is harmless once its content is saved.
        }
    }

    /// <summary>The production replacement: <see cref="FileReplacer.Replace"/> (with retries) off the UI thread.</summary>
    internal static Task ReplaceOnBackgroundThreadAsync(string tempPath, string targetPath) =>
        Task.Run(() => FileReplacer.Replace(tempPath, targetPath));

    private static SaveResult Cancelled() => new(SaveOutcome.Cancelled);

    private static SaveResult Failed(string message) => new(SaveOutcome.Failed, message);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsUnchangedAsync(DocumentSession session, CancellationToken ct)
    {
        // A file that cannot even be fingerprinted (missing, locked) fails the save in the catch-all.
        var current = await DocumentFingerprint.ComputeAsync(session.Path, ct);
        return current == session.OpenFingerprint;
    }

    /// <summary>Deletes the copy only while a complete file is still in place, so it is never the only one.</summary>
    private static void DeleteStrayCopy(string tempPath, string source, string target)
    {
        try
        {
            if (File.Exists(tempPath) && (File.Exists(target) || File.Exists(source)))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover .drbk-tmp is harmless compared to the reported failure.
        }
    }

    private static async Task ReopenQuietlyAsync(IViewerHandle viewer, string source, bool released)
    {
        if (!released)
        {
            return;
        }

        try
        {
            await viewer.ReopenAsync(source, CancellationToken.None);
        }
#pragma warning disable CA1031 // The save failure is what the user hears about; the view keeps its bitmap.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
