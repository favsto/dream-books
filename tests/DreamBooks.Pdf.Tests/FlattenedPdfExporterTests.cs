using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// The flattened export (FR-87): a new PDF with the strokes merged into the page content. The round trip is the test:
/// export, reopen, compare strokes (none left as annotations), page count, text and the picture of the page.
/// </summary>
public sealed class FlattenedPdfExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DreamBooks.Pdf.Tests", "flatten-" + Guid.NewGuid().ToString("N"));

    public FlattenedPdfExporterTests() => Directory.CreateDirectory(_dir);

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

    private static Task EditAsync(string path, int pageIndex, Action<nint> edit)
    {
        var rewritten = path + ".fixture";
        return PdfiumThread.RunAsync(
            () =>
            {
                using (var document = PdfiumDocument.Open(path))
                {
                    var page = document.LoadPage(pageIndex);
                    try
                    {
                        edit(page);
                    }
                    finally
                    {
                        document.ClosePage(page);
                    }

                    document.SaveFullRewrite(rewritten);
                }

                File.Move(rewritten, path, overwrite: true);
            },
            CancellationToken.None);
    }

    private static Task AddStrokeInFileAsync(string path, int pageIndex, InkStroke stroke) =>
        EditAsync(path, pageIndex, page => StrokeAnnotations.Write(page, PageGeometry.From(page), stroke));

    private string NewPdf(string name, int pages = 3, string? text = null)
    {
        var path = Path.Combine(_dir, name);
        var sizes = Enumerable.Range(0, pages).Select(_ => (612.0, 792.0)).ToArray();
        if (text is null)
        {
            TestPdfFactory.Write(path, sizes);
        }
        else
        {
            TestPdfFactory.WriteWithText(path, text, sizes);
        }

        return path;
    }

    private static async Task<int> DarkPixelsAsync(string path, int pageIndex)
    {
        await using var renderer = await PdfRenderer.OpenAsync(path);
        var page = await renderer.RenderPageAsync(pageIndex, 612, 792);
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

    private static Task<bool> HasAppAnnotationAsync(string path, int pageIndex) => PdfiumThread.RunAsync(
        () =>
        {
            using var document = PdfiumDocument.Open(path);
            var page = document.LoadPage(pageIndex);
            try
            {
                return StrokeAnnotations.HasAppAnnotation(page);
            }
            finally
            {
                document.ClosePage(page);
            }
        },
        CancellationToken.None);

    private static byte[] Bytes(string path) => File.ReadAllBytes(path);

    /// <summary>Files in the folder that are not PDFs: temporary copies a failed export left behind.</summary>
    private IEnumerable<string> LeftOvers() =>
        Directory.GetFiles(_dir).Where(f => !f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task The_export_has_no_stroke_annotations_left_the_same_pages_and_the_strokes_in_the_picture() // FR-87
    {
        var source = NewPdf("source.pdf", pages: 3);
        await AddStrokeInFileAsync(source, 0, Stroke(200));
        Assert.True(await HasAppAnnotationAsync(source, 0));
        var baseline = await DarkPixelsAsync(NewPdf("plain.pdf", pages: 3), 1); // what an untouched page of these PDFs draws
        var destination = Path.Combine(_dir, "flat.pdf");

        var result = await new FlattenedPdfExporter().ExportAsync(source, destination, [], CancellationToken.None);

        Assert.Equal(destination, result);
        Assert.True(File.Exists(destination));
        Assert.False(await HasAppAnnotationAsync(destination, 0)); // merged into the page: not an annotation any more
        await using (var renderer = await PdfRenderer.OpenAsync(destination))
        {
            Assert.Equal(3, renderer.PageCount);
        }

        Assert.True(await DarkPixelsAsync(destination, 0) > baseline + 100, "the stroke is in the page picture");
        Assert.Equal(baseline, await DarkPixelsAsync(destination, 1)); // the other pages are as they were
    }

    [Fact]
    public async Task Strokes_not_yet_saved_in_the_session_are_in_the_export_too()
    {
        var source = NewPdf("source.pdf", pages: 2);
        var unsaved = Stroke(300);
        var destination = Path.Combine(_dir, "flat.pdf");
        var baseline = await DarkPixelsAsync(source, 1);

        await new FlattenedPdfExporter().ExportAsync(source, destination, [new PageChange(1, [], [unsaved])], CancellationToken.None);

        Assert.False(await HasAppAnnotationAsync(destination, 1));
        Assert.True(await DarkPixelsAsync(destination, 1) > baseline + 100);
    }

    [Fact]
    public async Task A_stroke_erased_in_the_session_is_not_in_the_export()
    {
        var source = NewPdf("source.pdf", pages: 1);
        var baseline = await DarkPixelsAsync(NewPdf("plain.pdf", pages: 1), 0);
        var erased = Stroke(200);
        await AddStrokeInFileAsync(source, 0, erased);
        var destination = Path.Combine(_dir, "flat.pdf");

        await new FlattenedPdfExporter().ExportAsync(source, destination, [new PageChange(0, [erased.Id], [])], CancellationToken.None);

        Assert.Equal(baseline, await DarkPixelsAsync(destination, 0));
    }

    [Fact]
    public async Task The_page_text_survives_the_flattening()
    {
        var source = NewPdf("source.pdf", pages: 1, text: "Dream Books flatten test");
        var destination = Path.Combine(_dir, "flat.pdf");

        await new FlattenedPdfExporter().ExportAsync(source, destination, [new PageChange(0, [], [Stroke(500)])], CancellationToken.None);

        await using var renderer = await PdfRenderer.OpenAsync(destination);
        var found = await renderer.SearchPageAsync(0, "flatten", 10, CancellationToken.None);
        Assert.NotEmpty(found.Hits);
    }

    [Fact]
    public async Task The_source_file_is_never_touched_and_nothing_is_left_behind()
    {
        var source = NewPdf("source.pdf", pages: 2);
        await AddStrokeInFileAsync(source, 0, Stroke(200));
        var before = Bytes(source);
        var stamp = File.GetLastWriteTimeUtc(source);
        var destination = Path.Combine(_dir, "flat.pdf");

        await new FlattenedPdfExporter().ExportAsync(source, destination, [new PageChange(1, [], [Stroke(100)])], CancellationToken.None);

        Assert.Equal(before, Bytes(source));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(source));
        Assert.Empty(LeftOvers());
    }

    [Fact]
    public async Task An_existing_destination_is_replaced_by_the_export()
    {
        var source = NewPdf("source.pdf", pages: 1);
        var destination = Path.Combine(_dir, "flat.pdf");
        File.WriteAllText(destination, "an older file");

        await new FlattenedPdfExporter().ExportAsync(source, destination, [new PageChange(0, [], [Stroke(200)])], CancellationToken.None);

        await using var renderer = await PdfRenderer.OpenAsync(destination);
        Assert.Equal(1, renderer.PageCount);
    }

    [Fact]
    public async Task An_encrypted_or_restricted_source_is_refused_and_no_file_is_made()
    {
        var source = Path.Combine(_dir, "locked.pdf");
        TestPdfFactory.WriteEncryptedRestricted(source, (612, 792));
        var destination = Path.Combine(_dir, "flat.pdf");

        await Assert.ThrowsAsync<PdfSaveRefusedException>(
            () => new FlattenedPdfExporter().ExportAsync(source, destination, [], CancellationToken.None));

        Assert.False(File.Exists(destination));
        Assert.Empty(LeftOvers());
    }

    [Fact]
    public async Task A_cancelled_export_leaves_no_file_and_an_existing_destination_untouched()
    {
        var source = NewPdf("source.pdf", pages: 3);
        var destination = Path.Combine(_dir, "flat.pdf");
        File.WriteAllText(destination, "keep me");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FlattenedPdfExporter().ExportAsync(source, destination, [new PageChange(0, [], [Stroke(100)])], cts.Token));

        Assert.Equal("keep me", File.ReadAllText(destination));
        Assert.Empty(LeftOvers());
    }

    [Fact]
    public async Task A_destination_in_a_missing_folder_fails_with_an_IO_error_and_leaves_nothing()
    {
        var source = NewPdf("source.pdf", pages: 1);
        var destination = Path.Combine(_dir, "no-such-folder", "flat.pdf");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => new FlattenedPdfExporter().ExportAsync(source, destination, [], CancellationToken.None));

        Assert.Empty(LeftOvers());
    }
}
