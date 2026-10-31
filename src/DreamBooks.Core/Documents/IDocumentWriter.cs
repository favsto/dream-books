namespace DreamBooks.Core.Documents;

/// <summary>
/// Produces a verified copy of a document with the given stroke changes applied (FR-80, FR-82,
/// NFR-10). The copy is a new temporary file next to <c>destinationPath</c>; the caller replaces
/// the destination with it (or deletes it). Neither the source nor the destination is modified.
/// </summary>
public interface IDocumentWriter
{
    /// <summary>
    /// Writes <paramref name="sourcePath"/> with <paramref name="changes"/> applied to a new temporary
    /// file in the folder of <paramref name="destinationPath"/>, verifies it and returns its path.
    /// On any failure or cancellation no temporary file is left behind.
    /// </summary>
    Task<string> WriteVerifiedCopyAsync(
        string sourcePath, string destinationPath, IReadOnlyList<PageChange> changes, CancellationToken ct = default);
}
