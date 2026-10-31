using System.Diagnostics;
using System.Security.Cryptography;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;
using static DreamBooks.Pdf.Tests.TestPdfFactory;

namespace DreamBooks.Pdf.Tests;

public sealed class PdfRendererStrokesTests : IDisposable
{
    private readonly string _dir;

    public PdfRendererStrokesTests()
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

    [Fact]
    public async Task LoadStrokes_returns_the_strokes_written_by_StrokeAnnotations() // FR-83
    {
        InkStroke a = Stroke(Guid.NewGuid(), 300);
        InkStroke b = Stroke(Guid.NewGuid(), 400);
        string path = await AuthorAsync(new PageSpec(595, 842), [a, b]);

        await using var renderer = await PdfRenderer.OpenAsync(path);
        PageStrokes loaded = await renderer.LoadStrokesAsync(0);

        Assert.Equal(0, loaded.UnreadableCount);
        Assert.Equal([a.Id, b.Id], loaded.Strokes.Select(s => s.Id).ToArray());
        Assert.Equal(StrokeJson.Serialize(a), StrokeJson.Serialize(loaded.Strokes[0]));
        Assert.Equal(path, renderer.Path);
    }

    [Fact]
    public async Task LoadStrokes_reports_unreadable_count() // FR-83
    {
        string path = await AuthorAsync(
            new PageSpec(595, 842), [Stroke(Guid.NewGuid(), 300), Stroke(Guid.NewGuid(), 400)], breakPayloadOf: 1);

        await using var renderer = await PdfRenderer.OpenAsync(path);
        PageStrokes loaded = await renderer.LoadStrokesAsync(0);

        Assert.Single(loaded.Strokes);
        Assert.Equal(1, loaded.UnreadableCount);
    }

    [Fact]
    public async Task LoadStrokes_returns_each_id_once_and_the_first_occurrence_wins() // ruling 5
    {
        Guid id = Guid.NewGuid();
        InkStroke first = Stroke(id, 300);
        InkStroke second = Stroke(id, 500);
        string path = await AuthorAsync(new PageSpec(595, 842), [first, second]);

        await using var renderer = await PdfRenderer.OpenAsync(path);
        PageStrokes loaded = await renderer.LoadStrokesAsync(0);

        InkStroke only = Assert.Single(loaded.Strokes);
        Assert.Equal(StrokeJson.Serialize(first), StrokeJson.Serialize(only));

        // Both are hidden from the bitmap.
        RenderedPage page = await renderer.RenderPageAsync(0, 595, 842);
        Assert.True(IsWhite(page, 300, 300));
        Assert.True(IsWhite(page, 300, 500));
    }

    [Fact]
    public async Task Render_does_not_draw_app_strokes_but_draws_foreign_annotations() // FR-26
    {
        string path = await AuthorAsync(
            new PageSpec(595, 842, WithForeignSquareAnnotation: true), [Stroke(Guid.NewGuid(), 300)]);

        await using var renderer = await PdfRenderer.OpenAsync(path);
        RenderedPage page = await renderer.RenderPageAsync(0, 595, 842);

        Assert.True(IsWhite(page, 300, 300), "the app stroke is drawn in the bitmap");
        Assert.True(HasGreen(page, 140, 640, 220, 700), "the foreign annotation is not drawn");
    }

    [Fact]
    public async Task Render_draws_unreadable_annotations_as_pictures() // FR-83
    {
        string path = await AuthorAsync(
            new PageSpec(595, 842), [Stroke(Guid.NewGuid(), 300), Stroke(Guid.NewGuid(), 400)], breakPayloadOf: 1);

        await using var renderer = await PdfRenderer.OpenAsync(path);
        RenderedPage page = await renderer.RenderPageAsync(0, 595, 842);

        Assert.True(IsWhite(page, 300, 300), "the readable stroke is drawn");
        Assert.True(IsDark(page, 300, 400), "the unreadable annotation is not drawn");
    }

    [Fact]
    public async Task LoadStrokes_after_render_still_returns_the_strokes() // ruling 6
    {
        InkStroke a = Stroke(Guid.NewGuid(), 300);
        string path = await AuthorAsync(new PageSpec(595, 842), [a]);

        await using var renderer = await PdfRenderer.OpenAsync(path);
        await renderer.RenderPageAsync(0, 595, 842);
        await renderer.RenderPageAsync(0, 300, 420);
        PageStrokes first = await renderer.LoadStrokesAsync(0);
        PageStrokes second = await renderer.LoadStrokesAsync(0);

        Assert.Equal(a.Id, Assert.Single(first.Strokes).Id);
        Assert.Equal(a.Id, Assert.Single(second.Strokes).Id);
    }

    [Fact]
    public async Task Render_after_LoadStrokes_still_hides_them() // ruling 6
    {
        string path = await AuthorAsync(new PageSpec(595, 842), [Stroke(Guid.NewGuid(), 300)]);

        await using var renderer = await PdfRenderer.OpenAsync(path);
        await renderer.LoadStrokesAsync(0);
        RenderedPage page = await renderer.RenderPageAsync(0, 595, 842);

        Assert.True(IsWhite(page, 300, 300));
    }

    [Fact]
    public async Task Rendering_twice_after_loading_does_not_change_the_file_on_disk() // NFR-10
    {
        string path = await AuthorAsync(new PageSpec(595, 842), [Stroke(Guid.NewGuid(), 300)]);
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path));

        await using (var renderer = await PdfRenderer.OpenAsync(path))
        {
            await renderer.LoadStrokesAsync(0);
            await renderer.RenderPageAsync(0, 595, 842);
            await renderer.RenderPageAsync(0, 595, 842);
        }

        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task LoadStrokes_runs_on_the_PDFium_thread() // NFR-04
    {
        string path = await AuthorAsync(new PageSpec(595, 842), [Stroke(Guid.NewGuid(), 300)]);
        await using var renderer = await PdfRenderer.OpenAsync(path);

        // StrokeAnnotations.Read throws unless it runs on the PDFium thread.
        PageStrokes loaded = await Task.Run(() => renderer.LoadStrokesAsync(0));

        Assert.Single(loaded.Strokes);
        Assert.False(PdfiumThread.IsPdfiumThread);
    }

    [Fact]
    public async Task LoadStrokes_for_page_out_of_range_throws_ArgumentOutOfRange()
    {
        await using var renderer = await PdfRenderer.OpenAsync(Pdf(new PageSpec(595, 842)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => renderer.LoadStrokesAsync(1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => renderer.LoadStrokesAsync(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.GetPageDimensions(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.GetPageDimensions(-1));
    }

    [Fact]
    public async Task Members_throw_ObjectDisposed_after_DisposeAsync()
    {
        PdfRenderer renderer = await PdfRenderer.OpenAsync(Pdf(new PageSpec(595, 842)));
        await renderer.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.LoadStrokesAsync(0));
        Assert.Throws<ObjectDisposedException>(() => renderer.GetPageDimensions(0));
    }

    [Fact]
    public async Task GetPageDimensions_matches_the_displayed_page_for_rotation_and_crop() // FR-26
    {
        var rotated = new PageSpec(700, 900, 90, OriginX: 100, OriginY: 200, CropBox: new PageBox(130, 240, 730, 1060));
        string path = Pdf(rotated, new PageSpec(595, 842));

        await using var renderer = await PdfRenderer.OpenAsync(path);

        Assert.Equal(new PageDimensions(820, 600), renderer.GetPageDimensions(0));
        Assert.Equal(new PageDimensions(595, 842), renderer.GetPageDimensions(1));
        Assert.Equal(2, ((IPageSource)renderer).PageCount);
    }

    [Fact]
    public async Task GetPageSize_is_the_float_cast_of_GetPageDimensions_for_fractional_boxes() // FR-26
    {
        string path = Pdf([
            new PageSpec(595.3, 841.9, 90),
            new PageSpec(700.7, 900.1, 0, OriginX: 10.3, OriginY: 20.9, CropBox: new PageBox(33.37, 41.13, 610.91, 820.29)),
            new PageSpec(612.45, 792.55, 270, CropBox: new PageBox(0.1, 0.2, 500.33, 700.77)),
            new PageSpec(595, 842),
            .. Enumerable.Range(0, 40).Select(n => new PageSpec(500.37 + (n * 7.13), 700.91 + (n * 3.77), (n % 4) * 90, OriginX: 10.3 + (n * 0.37), OriginY: 20.9 + (n * 0.11)))]);

        await using var renderer = await PdfRenderer.OpenAsync(path);

        for (int i = 0; i < renderer.PageCount; i++)
        {
            PageDimensions d = renderer.GetPageDimensions(i);
            Assert.Equal(new PageSize((float)d.WidthPt, (float)d.HeightPt), renderer.GetPageSize(i));
        }

        Assert.Equal(new PageSize(595, 842), renderer.GetPageSize(3));
    }

    [Fact]
    public async Task LoadStrokes_after_dispose_throws_ObjectDisposed()
    {
        string path = await AuthorAsync(new PageSpec(595, 842), [Stroke(Guid.NewGuid(), 300)]);
        PdfRenderer renderer = await PdfRenderer.OpenAsync(path);
        await renderer.LoadStrokesAsync(0);
        await renderer.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.LoadStrokesAsync(0));
    }

    [Fact]
    public async Task Opening_a_300_page_document_stays_fast()
    {
        string path = Path.Combine(_dir, "many.pdf");
        Write(path, Enumerable.Repeat((595.0, 842.0), 300).ToArray());

        var watch = Stopwatch.StartNew();
        await using var renderer = await PdfRenderer.OpenAsync(path);
        watch.Stop();

        Assert.Equal(300, renderer.PageCount);
        Assert.Equal(new PageDimensions(595, 842), renderer.GetPageDimensions(299));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"opening took {watch.Elapsed}");
    }

    private static InkStroke Stroke(Guid id, double y) =>
        new(id, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 6, Line(200, 400, y, 40));

    private static InkPoint[] Line(double x0, double x1, double y, int count)
    {
        var points = new InkPoint[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = new InkPoint(x0 + ((x1 - x0) * i / (count - 1)), y, null, null, null, null, i * 8L);
        }

        return points;
    }

    private string Pdf(params PageSpec[] specs)
    {
        string path = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        Write(path, specs);
        return path;
    }

    /// <summary>Writes the strokes onto page 0 and saves a full rewrite; returns the saved path.</summary>
    private async Task<string> AuthorAsync(PageSpec spec, InkStroke[] strokes, int? breakPayloadOf = null)
    {
        string source = Pdf(spec);
        string saved = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(source);
                nint page = document.LoadPage(0);
                try
                {
                    var geometry = PageGeometry.From(page);
                    foreach (InkStroke stroke in strokes)
                    {
                        StrokeAnnotations.Write(page, geometry, stroke);
                    }

                    if (breakPayloadOf is int stroked)
                    {
                        int index = PdfiumNative.FPDFPage_GetAnnotCount(page) - strokes.Length + stroked;
                        nint annot = PdfiumNative.FPDFPage_GetAnnot(page, index);
                        try
                        {
                            string newer = "{\"v\":2,\"id\":\"" + Guid.NewGuid().ToString("N") + "\"}";
                            Assert.True(PdfiumAnnotations.SetString(annot, StrokeAnnotations.PayloadKey, newer));
                        }
                        finally
                        {
                            PdfiumNative.FPDFPage_CloseAnnot(annot);
                        }
                    }

                    document.SaveFullRewrite(saved);
                }
                finally
                {
                    document.ClosePage(page);
                }
            },
            CancellationToken.None);
        return saved;
    }

    private static bool IsDark(RenderedPage page, int x, int y)
    {
        int i = (y * page.Stride) + (x * 4);
        return page.Bgra[i] < 80 && page.Bgra[i + 1] < 80 && page.Bgra[i + 2] < 80;
    }

    private static bool IsWhite(RenderedPage page, int x, int y)
    {
        int i = (y * page.Stride) + (x * 4);
        return page.Bgra[i] > 240 && page.Bgra[i + 1] > 240 && page.Bgra[i + 2] > 240;
    }

    private static bool HasGreen(RenderedPage page, int left, int top, int right, int bottom)
    {
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int i = (y * page.Stride) + (x * 4);
                if (page.Bgra[i + 1] > 120 && page.Bgra[i] < 80 && page.Bgra[i + 2] < 80)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
