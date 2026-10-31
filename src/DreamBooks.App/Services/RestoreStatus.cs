namespace DreamBooks.App.Services;

/// <summary>What happened to a document's journal when the document was opened (FR-84, FR-86).</summary>
public enum RestoreStatus
{
    /// <summary>No usable journal (none, or a damaged one that will be moved aside): journaling starts fresh.</summary>
    None,

    /// <summary>The journal was replayed (possibly with nothing to apply); appends continue in it.</summary>
    Replayed,

    /// <summary>The journal could not be read (I/O or permission error): kept untouched, journaling off.</summary>
    Unreadable,

    /// <summary>The journal was written for another version of the file and the user cancelled: kept, journaling off.</summary>
    MismatchCancelled,

    /// <summary>The journal was written for another version of the file and the user discarded it.</summary>
    Discarded,

    /// <summary>The journal's header names another document: kept untouched, nothing applied, journaling off.</summary>
    ForeignJournal,

    /// <summary>The user discarded a mismatched journal but it could not be deleted: kept, journaling off.</summary>
    DiscardFailed,

    /// <summary>
    /// Replaying failed: the session is partly replayed and must be thrown away; the journal is kept and
    /// journaling is off for the fresh session.
    /// </summary>
    ReplayFailed,
}
