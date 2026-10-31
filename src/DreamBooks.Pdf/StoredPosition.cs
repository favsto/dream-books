using DreamBooks.Core.Layout;

namespace DreamBooks.Pdf;

/// <summary>A remembered view position and the UTC time it was stored.</summary>
public readonly record struct StoredPosition(ViewPosition Position, DateTime SavedUtc);
