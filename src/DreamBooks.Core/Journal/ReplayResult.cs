namespace DreamBooks.Core.Journal;

/// <summary>
/// Outcome of a replay. <see cref="Applied"/> counts add and erase entries that changed the
/// document; <see cref="Skipped"/> counts entries that could not be applied plus, inside applied entries,
/// erased Ids that no longer existed and added strokes already on the page. View entries count as neither.
/// </summary>
public sealed record ReplayResult(int Applied, int Skipped, ViewEntry? LastView);
