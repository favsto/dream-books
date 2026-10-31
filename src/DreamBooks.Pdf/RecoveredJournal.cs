using DreamBooks.Core.Journal;

namespace DreamBooks.Pdf;

/// <summary>
/// A journal found on disk. <c>Contents.Header</c> null with <paramref name="Unreadable"/> false means the file is
/// genuinely damaged. <paramref name="Unreadable"/> true means a transient I/O or permission error: the content is
/// unknown and the app must never delete such a journal.
/// </summary>
public sealed record RecoveredJournal(string FilePath, JournalContents Contents, DateTime LastWriteUtc, bool Unreadable = false);
