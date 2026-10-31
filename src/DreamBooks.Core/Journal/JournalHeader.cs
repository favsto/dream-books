using DreamBooks.Core.Documents;

namespace DreamBooks.Core.Journal;

/// <summary>First line of a journal: format version, full document path and the file fingerprint it was written for.</summary>
public sealed record JournalHeader(int FormatVersion, string Path, DocumentFingerprint Fingerprint);
