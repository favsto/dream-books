namespace DreamBooks.Core.Journal;

/// <summary>
/// A parsed journal. <see cref="HadTornTail"/> means the last line was unparseable (a write cut by a crash)
/// and was dropped; <see cref="SkippedCount"/> counts other unparseable lines, and every entry line when the
/// header is missing or has an unsupported format version (entries are then empty).
/// </summary>
public sealed record JournalContents(JournalHeader? Header, IReadOnlyList<JournalEntry> Entries, bool HadTornTail, int SkippedCount)
{
    /// <summary>True when there is a header this build can replay.</summary>
    public bool IsSupportedVersion => Header?.FormatVersion == JournalCodec.CurrentFormatVersion;
}
