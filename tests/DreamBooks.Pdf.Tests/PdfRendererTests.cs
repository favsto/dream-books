using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf.Tests;

public sealed class PdfRendererTests : IDisposable
{
    private readonly string _dir;

    public PdfRendererTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DreamBooks.Pdf.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

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

    private string WriteSample(string name = "sample.pdf")
    {
        string path = Path.Combine(_dir, name);
        TestPdfFactory.Write(path, (595, 842), (842, 595));
        return path;
    }

    [Fact]
    public async Task Opens_and_reports_page_count_and_sizes()
    {
        await using var renderer = await PdfRenderer.OpenAsync(WriteSample());

        Assert.Equal(2, renderer.PageCount);
        Assert.Equal(new PageSize(595, 842), renderer.GetPageSize(0));
        Assert.Equal(new PageSize(842, 595), renderer.GetPageSize(1));
    }

    [Fact]
    public async Task Missing_file_throws_FileNotFound()
    {
        var ex = await Assert.ThrowsAsync<PdfOpenException>(
            () => PdfRenderer.OpenAsync(Path.Combine(_dir, "missing.pdf")));

        Assert.Equal(PdfOpenError.FileNotFound, ex.Error);
    }

    [Fact]
    public async Task Garbage_file_throws_InvalidFormat() // NFR-11
    {
        string path = Path.Combine(_dir, "garbage.pdf");
        TestPdfFactory.WriteGarbage(path);

        var ex = await Assert.ThrowsAsync<PdfOpenException>(() => PdfRenderer.OpenAsync(path));

        Assert.Equal(PdfOpenError.InvalidFormat, ex.Error);
    }

    [Fact]
    public async Task Render_returns_requested_size_and_stride()
    {
        await using var renderer = await PdfRenderer.OpenAsync(WriteSample());

        RenderedPage page = await renderer.RenderPageAsync(0, 120, 170);

        Assert.Equal(120, page.PixelWidth);
        Assert.Equal(170, page.PixelHeight);
        Assert.True(page.Stride >= 480);
        Assert.Equal(page.Stride * 170, page.Bgra.Length);
    }

    [Fact]
    public async Task Render_draws_content()
    {
        // Page 595x842 pt rendered at 1 px per pt: the square covers pixels [0,100) x [0,100).
        await using var renderer = await PdfRenderer.OpenAsync(WriteSample());

        RenderedPage page = await renderer.RenderPageAsync(0, 595, 842);

        (byte b, byte g, byte r) inside = Pixel(page, 50, 50);
        (byte b, byte g, byte r) corner = Pixel(page, 594, 841);
        Assert.True(inside.b < 32 && inside.g < 32 && inside.r < 32, $"Expected dark pixel, got {inside}");
        Assert.True(corner.b > 223 && corner.g > 223 && corner.r > 223, $"Expected white pixel, got {corner}");
    }

    [Fact]
    public async Task Render_runs_off_the_calling_thread()
    {
        await using var renderer = await PdfRenderer.OpenAsync(WriteSample());
        int callerThread = Environment.CurrentManagedThreadId;

        await renderer.RenderPageAsync(0, 10, 10);
        bool onPdfiumThread = await PdfiumThread.RunAsync(() => PdfiumThread.IsPdfiumThread, CancellationToken.None);

        Assert.True(onPdfiumThread);
        Assert.NotEqual(0, PdfiumThread.ThreadId);
        Assert.NotEqual(callerThread, PdfiumThread.ThreadId);
        Assert.False(PdfiumThread.IsPdfiumThread);
    }

    [Fact]
    public async Task Cancelled_render_throws_OperationCanceled()
    {
        await using var renderer = await PdfRenderer.OpenAsync(WriteSample());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => renderer.RenderPageAsync(0, 10, 10, cts.Token));
    }

    [Fact]
    public async Task Path_with_non_ascii_characters_opens()
    {
        await using var renderer = await PdfRenderer.OpenAsync(WriteSample("pàgina è.pdf"));

        Assert.Equal(2, renderer.PageCount);
    }

    [Fact]
    public async Task Disposed_renderer_throws_ObjectDisposed()
    {
        var renderer = await PdfRenderer.OpenAsync(WriteSample());
        await renderer.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.RenderPageAsync(0, 10, 10));
        Assert.Throws<ObjectDisposedException>(() => renderer.GetPageSize(0));
        await renderer.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task Invalid_render_arguments_throw()
    {
        await using var renderer = await PdfRenderer.OpenAsync(WriteSample());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => renderer.RenderPageAsync(2, 10, 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => renderer.RenderPageAsync(0, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.GetPageSize(-1));
    }

    private static (byte b, byte g, byte r) Pixel(RenderedPage page, int x, int y)
    {
        int i = (y * page.Stride) + (x * 4);
        return (page.Bgra[i], page.Bgra[i + 1], page.Bgra[i + 2]);
    }
}
