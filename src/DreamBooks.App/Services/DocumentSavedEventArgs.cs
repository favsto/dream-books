using DreamBooks.Core.Documents;

namespace DreamBooks.App.Services;

/// <summary>
/// A successful save: the session (already marked saved, so its <see cref="DocumentSession.Path"/> and
/// <see cref="DocumentSession.OpenFingerprint"/> describe the new file), the snapshot that was written, and the
/// paths before and after. <see cref="IsSaveAs"/> is true only when the session moved to another file.
/// </summary>
public sealed class DocumentSavedEventArgs(
    DocumentSession session, SaveSnapshot snapshot, string oldPath, string newPath, bool isSaveAs) : EventArgs
{
    public DocumentSession Session { get; } = session;

    public SaveSnapshot Snapshot { get; } = snapshot;

    public string OldPath { get; } = oldPath;

    public string NewPath { get; } = newPath;

    public bool IsSaveAs { get; } = isSaveAs;
}
