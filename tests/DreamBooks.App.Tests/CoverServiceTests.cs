using System.Windows.Media;
using System.Windows.Media.Imaging;
using DreamBooks.App.Services;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>The first-page pictures of the recent documents: stored when a document is opened, never rendered for files that were not (FR-03).</summary>
public sealed class CoverServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-cover-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static BitmapSource Page(byte grey)
    {
        var pixels = new byte[10 * 14];
        Array.Fill(pixels, grey);
        var bitmap = BitmapSource.Create(10, 14, 96, 96, PixelFormats.Gray8, null, pixels, 10);
        bitmap.Freeze();
        return bitmap;
    }

    [Fact]
    public async Task Store_then_get_round_trips_the_picture()
    {
        var service = new CoverService(new CoverStore(_dir));
        await service.StoreAsync(@"C:\a.pdf", Page(90), CancellationToken.None);

        var cover = await service.GetAsync(@"C:\a.pdf", CancellationToken.None);

        Assert.NotNull(cover);
        Assert.Equal((10, 14), (cover!.PixelWidth, cover.PixelHeight));
        var pixel = new byte[4];
        new FormatConvertedBitmap(cover, PixelFormats.Bgra32, null, 0).CopyPixels(new System.Windows.Int32Rect(5, 5, 1, 1), pixel, 4, 0);
        Assert.InRange(pixel[0], 85, 95);
        Assert.True(cover.IsFrozen); // usable from any thread
    }

    [Fact]
    public async Task A_cover_that_was_never_stored_is_null_and_nothing_is_rendered_for_it()
    {
        var service = new CoverService(new CoverStore(_dir));

        Assert.Null(await service.GetAsync(@"C:\never-opened.pdf", CancellationToken.None));
    }

    [Fact]
    public async Task A_damaged_cover_file_is_null_and_does_not_throw()
    {
        var store = new CoverStore(_dir);
        store.Save(@"C:\a.pdf", [1, 2, 3, 4]); // not a PNG
        var service = new CoverService(store);

        Assert.Null(await service.GetAsync(@"C:\a.pdf", CancellationToken.None));
    }

    [Fact]
    public async Task A_dropped_cover_is_deleted_and_nothing_remains_of_the_page()
    {
        var store = new CoverStore(_dir);
        var service = new CoverService(store);
        await service.StoreAsync(@"C:\a.pdf", Page(90), CancellationToken.None);
        await service.StoreAsync(@"C:\b.pdf", Page(90), CancellationToken.None);

        await service.DeleteAsync(@"C:\a.pdf");

        Assert.Null(store.TryLoad(@"C:\a.pdf"));
        Assert.NotNull(store.TryLoad(@"C:\b.pdf"));
    }

    [Fact]
    public async Task Storing_raises_Stored_with_the_path()
    {
        var service = new CoverService(new CoverStore(_dir));
        string? stored = null;
        service.Stored += (_, path) => stored = path;

        await service.StoreAsync(@"C:\a.pdf", Page(1), CancellationToken.None);

        Assert.Equal(@"C:\a.pdf", stored);
    }
}
