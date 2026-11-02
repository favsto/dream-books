using DreamBooks.Core.Layout;

namespace DreamBooks.Pdf;

/// <summary>
/// A remembered view position, the UTC time it was stored, and whether the zoom followed the window width (fit-width
/// mode: the zoom is recomputed for the current window on restore). Files without the flag read as not fit.
/// </summary>
public readonly record struct StoredPosition(ViewPosition Position, DateTime SavedUtc, bool FitWidth = false);
