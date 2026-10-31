using DreamBooks.Core.Journal;

namespace DreamBooks.Pdf;

/// <summary>An open journal file for one document. Append is thread-safe and never waits for the disk sync.</summary>
public interface IJournalSession : IJournalSink, IAsyncDisposable
{
    string FilePath { get; }

    /// <summary>Atomically replaces the journal content with the new header only; later appends go to the new file.</summary>
    void Truncate(JournalHeader newHeader) => Truncate(newHeader, []);

    /// <summary>
    /// Replaces the journal content with the new header followed by <paramref name="lines"/> (e.g. the changes not yet
    /// in the saved file) through a temp file: afterwards the journal holds either the old content (the call throws)
    /// or the new content (success, also when the replace removed the old file without moving the new one in, which
    /// is then moved in). Never a mix of the two. Later appends go to the journal.
    /// </summary>
    void Truncate(JournalHeader newHeader, IReadOnlyList<string> lines);

    /// <summary>Removes the journal and its folder. Idempotent; the session cannot be appended to afterwards.</summary>
    void Delete();
}
