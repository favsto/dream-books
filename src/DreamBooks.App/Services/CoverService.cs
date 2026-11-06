using System.IO;
using System.Windows.Media.Imaging;
using DreamBooks.Pdf;

namespace DreamBooks.App.Services;

/// <summary>
/// The first-page pictures of the recent documents (FR-03). A cover is stored when its document is opened (the page-1
/// bitmap the thumbnail path already rendered, without ink, so a save never makes it stale) and read back from the
/// cover cache; a document that was never opened has no cover and none is rendered for it in the background. Reading and
/// writing run on the thread pool; pictures are frozen, so any thread may hand them to the UI.
/// </summary>
internal sealed class CoverService
{
    private readonly CoverStore _store;

    public CoverService(CoverStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>A cover was stored for this document: cards showing it can load it again.</summary>
    public event EventHandler<string>? Stored;

    /// <summary>The stored cover, or null when there is none or it is damaged. Never throws.</summary>
    public Task<BitmapSource?> GetAsync(string documentPath, CancellationToken ct) =>
        Task.Run(() => Decode(_store.TryLoad(documentPath)), ct);

    /// <summary>Keeps <paramref name="firstPage"/> as the document's cover. Never throws.</summary>
    public async Task StoreAsync(string documentPath, BitmapSource firstPage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firstPage);
        if (!firstPage.IsFrozen && firstPage.CanFreeze)
        {
            firstPage.Freeze();
        }

        await Task.Run(() => _store.Save(documentPath, Encode(firstPage)), ct);
        Stored?.Invoke(this, documentPath);
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource? Decode(byte[]? png)
    {
        if (png is null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException)
        {
            return null; // a damaged cover is no cover
        }
    }
}
