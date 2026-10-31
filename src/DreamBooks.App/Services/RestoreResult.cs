using DreamBooks.Core.Journal;

namespace DreamBooks.App.Services;

/// <summary>
/// The outcome of <see cref="RecoveryService.TryRestoreAsync"/>: what happened, the journal to use from now on
/// (null only for <see cref="RestoreStatus.ReplayFailed"/>), the replay counts and the last view recorded.
/// </summary>
public sealed record RestoreResult(
    RestoreStatus Status, DocumentJournal? Journal, int Applied = 0, int Skipped = 0, ViewEntry? LastView = null)
{
    /// <summary>True when the replay changed the document.</summary>
    public bool Restored => Applied > 0;
}
