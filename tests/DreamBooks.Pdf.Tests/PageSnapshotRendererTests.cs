using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf.Tests;

/// <summary>One page as the reader sees it, strokes included, saved or not, rendered at a resolution (FR-88).</summary>
public sealed class PageSnapshotRendererTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DreamBooks.Pdf.Tests", "snapshot-" + Guid.NewGuid().ToString("N"));

    public PageSnapshotRendererTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static InkStroke Stroke(double y)
    {
        var points = new InkPoint[30];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new InkPoint(100 + (i * 10), y + (i % 3), 0.5f, null, null, null, i * 8L);
        }

        return new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 4, points);
    }

    private string NewPdf(params (double w, double h)[] pages)
    {
        var path = Path.Combine(_dir, "source.pdf");
        TestPdfFactory.Write(path, pages);
        return path;
    }

    private static int Dark(RenderedPage page)
    {
        var count = 0;
        for (var i = 0; i < page.Bgra.Length; i += 4)
        {
            if (Math.Min(page.Bgra[i], Math.Min(page.Bgra[i + 1], page.Bgra[i + 2])) < 128)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public async Task The_size_is_the_page_in_points_at_the_given_dpi()
    {
        var source = NewPdf((612, 792), (300, 400));

        var first = await new PageSnapshotRenderer().RenderAsync(source, 0, [], 144, CancellationToken.None);
        var second = await new PageSnapshotRenderer().RenderAsync(source, 1, [], 72, CancellationToken.None);

        Assert.Equal((1224, 1584), (first.PixelWidth, first.PixelHeight)); // 612 x 792 pt at 2 px per pt
        Assert.Equal((300, 400), (second.PixelWidth, second.PixelHeight));
    }

    [Fact]
    public async Task Strokes_added_in_the_session_are_drawn_and_the_other_pages_changes_are_ignored()
    {
        var source = NewPdf((612, 792), (612, 792));
        var baseline = Dark(await new PageSnapshotRenderer().RenderAsync(source, 0, [], 72, CancellationToken.None));

        var drawn = await new PageSnapshotRenderer().RenderAsync(
            source, 0, [new PageChange(0, [], [Stroke(300)]), new PageChange(1, [], [Stroke(100)])], 72, CancellationToken.None);

        Assert.True(Dark(drawn) > baseline + 100);
    }

    [Fact]
    public async Task Strokes_already_in_the_file_are_drawn_and_a_session_erase_removes_them()
    {
        var source = NewPdf((612, 792));
        var baseline = Dark(await new PageSnapshotRenderer().RenderAsync(source, 0, [], 72, CancellationToken.None));
        var saved = Stroke(300);
        await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(source);
                var page = document.LoadPage(0);
                try
                {
                    StrokeAnnotations.Write(page, PageGeometry.From(page), saved);
                }
                finally
                {
                    document.ClosePage(page);
                }

                document.SaveFullRewrite(source + ".new");
            },
            CancellationToken.None);
        File.Move(source + ".new", source, overwrite: true);

        var withSaved = await new PageSnapshotRenderer().RenderAsync(source, 0, [], 72, CancellationToken.None);
        var erased = await new PageSnapshotRenderer().RenderAsync(source, 0, [new PageChange(0, [saved.Id], [])], 72, CancellationToken.None);

        Assert.True(Dark(withSaved) > baseline + 100);
        Assert.Equal(baseline, Dark(erased));
    }

    [Fact]
    public async Task The_source_file_is_never_written()
    {
        var source = NewPdf((612, 792));
        var before = File.ReadAllBytes(source);
        var stamp = File.GetLastWriteTimeUtc(source);

        await new PageSnapshotRenderer().RenderAsync(source, 0, [new PageChange(0, [], [Stroke(300)])], 72, CancellationToken.None);

        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(source));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_huge_page_is_scaled_down_to_a_bitmap_that_fits()
    {
        var source = NewPdf((14400, 14400)); // the largest page PDF allows: 200 inch square

        var page = await new PageSnapshotRenderer().RenderAsync(source, 0, [], 200, CancellationToken.None);

        Assert.True((long)page.PixelWidth * page.PixelHeight * 4 <= PageSnapshotRenderer.MaxBytes);
        Assert.Equal(page.PixelWidth, page.PixelHeight);
    }

    [Fact]
    public async Task A_bad_page_index_or_dpi_is_refused_and_a_cancelled_render_throws()
    {
        var source = NewPdf((612, 792));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new PageSnapshotRenderer().RenderAsync(source, 5, [], 72, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new PageSnapshotRenderer().RenderAsync(source, -1, [], 72, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new PageSnapshotRenderer().RenderAsync(source, 0, [], 0, CancellationToken.None));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PageSnapshotRenderer().RenderAsync(source, 0, [], 72, cts.Token));
    }
}
