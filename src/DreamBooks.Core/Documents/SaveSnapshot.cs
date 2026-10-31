namespace DreamBooks.Core.Documents;

/// <summary>
/// What a save writes, captured before the asynchronous write starts: the net changes and, for every page
/// that had a difference, its stroke Id sequence at that moment. <see cref="DocumentSession.MarkSaved"/>
/// rebaselines to these sequences, so edits made while the save runs stay unsaved changes.
/// </summary>
public sealed record SaveSnapshot(
    IReadOnlyList<PageChange> Changes,
    IReadOnlyDictionary<int, IReadOnlyList<Guid>> Sequences);
