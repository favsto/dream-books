namespace DreamBooks.Core.Rendering;

/// <summary>
/// A bitmap held by <see cref="RenderScheduler{TBitmap}"/>. <see cref="Generation"/> is the scheduler
/// generation of the page when the bitmap was rendered: when it differs from
/// <see cref="RenderScheduler{TBitmap}.GetGeneration(int)"/> the bitmap is stale (rendered for an old
/// zoom, DPI or renderer) and may only be shown stretched as a preview until it is replaced.
/// </summary>
public sealed record CachedBitmap<TBitmap>(
    TBitmap Bitmap,
    RenderQuality Quality,
    int PixelWidth,
    int PixelHeight,
    long Generation);
