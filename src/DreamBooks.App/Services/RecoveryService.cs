using System.IO;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Journal;
using DreamBooks.Pdf;

namespace DreamBooks.App.Services;

/// <summary>
/// Crash recovery over a <see cref="RecoveryStore"/> (FR-84, FR-86): finds a document's journal when it is opened,
/// replays it or asks when it was written for another version of the file, and hands out the
/// <see cref="DocumentJournal"/> that records the document's changes from then on.
/// </summary>
/// <remarks>
/// Rules: an unreadable journal (I/O or permission error) is never deleted or appended to; a damaged one (no
/// header) is ignored and moved aside by the store at the first change; a journal with a different fingerprint
/// is applied only after "apply anyway", and then keeps its old header, so a second crash asks again.
/// </remarks>
public sealed class RecoveryService
{
    private readonly IFileDialogService _dialogs;

    public RecoveryService(RecoveryStore store, IFileDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dialogs);
        Store = store;
        _dialogs = dialogs;
        BeginJournal = store.Begin;
    }

    public RecoveryStore Store { get; }

    /// <summary>Test seam: opens a document's journal file (the store's <see cref="RecoveryStore.Begin"/>).</summary>
    internal Func<string, DocumentFingerprint, IJournalSession> BeginJournal { get; set; }

    /// <summary>A fresh journal for the session: nothing on disk until the first change.</summary>
    public DocumentJournal Attach(DocumentSession session) => Create(session, enabled: true, existing: false);

    /// <summary>A journal that records nothing and leaves any file on disk alone.</summary>
    public DocumentJournal Disabled(DocumentSession session) => Create(session, enabled: false, existing: false);

    /// <summary>
    /// The most recently written journal with a header this build can replay and a usable full document path, or null.
    /// </summary>
    public RecoveredJournal? MostRecent() =>
        Store.FindAll().FirstOrDefault(j =>
            !j.Unreadable && j.Contents.IsSupportedVersion
            && FullPathOrNull(j.Contents.Header!.Path) is { } path
            && string.Equals(path, j.Contents.Header.Path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The normalised full path, or null for a path Windows cannot use (blank, invalid characters).</summary>
    internal static string? FullPathOrNull(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Looks for the session's journal and applies it if it belongs to the file as opened (or the user says
    /// so); returns the journal to use from now on. Call it on a session no recorder listens to, before
    /// anything else changes it. On <see cref="RestoreStatus.ReplayFailed"/> the session is partly replayed:
    /// throw it away and use <see cref="Disabled"/> on a fresh one. Cancellation throws.
    /// </summary>
    public async Task<RestoreResult> TryRestoreAsync(DocumentSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Off the UI thread: a long session's journal can be a few megabytes.
        var found = await Task.Run(() => Store.Find(session.Path), ct);
        if (found is null)
        {
            return new RestoreResult(RestoreStatus.None, Attach(session));
        }

        if (found.Unreadable)
        {
            return new RestoreResult(RestoreStatus.Unreadable, Disabled(session));
        }

        if (found.Contents.Header is not { } header || !found.Contents.IsSupportedVersion)
        {
            return new RestoreResult(RestoreStatus.None, Attach(session));
        }

        if (FullPathOrNull(header.Path) is not { } headerPath
            || !string.Equals(headerPath, Path.GetFullPath(session.Path), StringComparison.OrdinalIgnoreCase))
        {
            // Not written for this document (a copied recovery folder, a key collision, an unusable path): never
            // replayed or deleted.
            return new RestoreResult(RestoreStatus.ForeignJournal, Disabled(session));
        }

        if (header.Fingerprint != session.OpenFingerprint)
        {
            switch (_dialogs.AskJournalMismatch())
            {
                case JournalMismatchAnswer.ApplyAnyway:
                    break;
                case JournalMismatchAnswer.Discard:
                    try
                    {
                        Store.Delete(session.Path);
                        return new RestoreResult(RestoreStatus.Discarded, Attach(session));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return new RestoreResult(RestoreStatus.DiscardFailed, Disabled(session));
                    }

                default:
                    return new RestoreResult(RestoreStatus.MismatchCancelled, Disabled(session));
            }
        }

        ReplayResult replay;
        try
        {
            replay = await new JournalReplayer().ReplayAsync(session, found.Contents, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A page that cannot be loaded must not lose the journal: it is kept for later.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new RestoreResult(RestoreStatus.ReplayFailed, null);
        }

        // Attached only now, so the replay itself is not journalled again.
        var journal = Create(session, enabled: true, existing: true);
        return new RestoreResult(RestoreStatus.Replayed, journal, replay.Applied, replay.Skipped, replay.LastView);
    }

    private DocumentJournal Create(DocumentSession session, bool enabled, bool existing) =>
        new(Store, (path, fingerprint) => BeginJournal(path, fingerprint), session, enabled, existing);
}
