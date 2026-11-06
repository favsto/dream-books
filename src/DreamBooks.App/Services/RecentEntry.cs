namespace DreamBooks.App.Services;

/// <summary>
/// A recent document as the start screen sees it. <see cref="Exists"/> is null until the file has been checked (the check
/// runs off the UI thread, so a disconnected network drive never freezes the window).
/// </summary>
internal sealed record RecentEntry(string Path, string FileName, DateTime LastOpenedUtc, int Page, int PageCount, bool? Exists);
