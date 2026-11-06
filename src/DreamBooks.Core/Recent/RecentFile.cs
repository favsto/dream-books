namespace DreamBooks.Core.Recent;

/// <summary>
/// A document the reader opened lately (FR-03): where it is, when it was last opened, and where the reader stopped
/// (<see cref="Page"/> is zero-based, within <see cref="PageCount"/>).
/// </summary>
public sealed record RecentFile(string Path, DateTime LastOpenedUtc, int Page, int PageCount);
