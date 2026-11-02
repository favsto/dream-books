namespace DreamBooks.Core.Rendering;

/// <summary>Raised by <see cref="RenderScheduler{TBitmap}"/> when a page bitmap entered the cache.</summary>
public sealed class RenderedEventArgs<TBitmap>(int pageIndex, CachedBitmap<TBitmap> bitmap) : EventArgs
{
    public int PageIndex { get; } = pageIndex;

    public CachedBitmap<TBitmap> Bitmap { get; } = bitmap;

    public RenderQuality Quality => Bitmap.Quality;

    public long Generation => Bitmap.Generation;
}
