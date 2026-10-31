using System.Diagnostics;
using System.Globalization;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;
using Xunit.Abstractions;
using static DreamBooks.Pdf.Tests.TestPdfFactory;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// The scan's page source: a cheap annotation pre-check on the PDFium thread, then the renderer's stroke
/// loader only for pages that hold the app's annotations (FR-100, NFR-02). Real documents are only read as
/// copies and only counts and times are reported (NFR-12).
/// </summary>
public sealed class MarkPageSourceTests : IDisposable
{
    private const string _realDocumentsFolder = @"C:\Users\Favsto\workspace\temp";
    private const string _fixturesVariable = "DREAMBOOKS_FIXTURES";
    private const int _textAnnotSubtype = 1; // FPDF_ANNOT_TEXT

    private readonly string _dir;
    private readonly ITestOutputHelper _output;

    public MarkPageSourceTests(ITestOutputHelper output)
    {
        _output = output;
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
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Page_without_annotations_returns_null_without_loading_strokes()
    {
        string path = Pdf(new PageSpec(595, 842), new PageSpec(595, 842));
        await using var renderer = await PdfRenderer.OpenAsync(path);
        var source = new MarkPageSource(renderer);

        Assert.Null(await source.TryLoadStrokesForScanAsync(0, CancellationToken.None));
        Assert.Null(await source.TryLoadStrokesForScanAsync(1, CancellationToken.None));
        Assert.Equal(0, renderer.FirstTouchReadCount);
    }

    [Fact]
    public async Task Page_with_only_foreign_annotations_returns_null()
    {
        string source = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        WriteWithFeatures(source, new Features { LinkCount = 2, SquareAnnotationCount = 1 }, (595, 842));
        string path = await EditAsync(source, page =>
        {
            // A text note and an ink annotation from another app (no private key).
            AddForeignAnnotation(page, _textAnnotSubtype);
            AddForeignAnnotation(page, PdfiumNative.FPDF_ANNOT_INK);
        });

        await using var renderer = await PdfRenderer.OpenAsync(path);
        var markSource = new MarkPageSource(renderer);

        Assert.Null(await markSource.TryLoadStrokesForScanAsync(0, CancellationToken.None));
        Assert.Equal(0, renderer.FirstTouchReadCount);
    }

    [Fact]
    public async Task Page_with_our_strokes_returns_them_identical_to_LoadStrokesAsync()
    {
        InkStroke a = Stroke(300);
        InkStroke b = Stroke(400);
        string path = await AuthorAsync(2, new Dictionary<int, InkStroke[]> { [1] = [a, b] });

        await using var renderer = await PdfRenderer.OpenAsync(path);
        var source = new MarkPageSource(renderer);

        PageStrokes? scanned = await source.TryLoadStrokesForScanAsync(1, CancellationToken.None);
        PageStrokes loaded = await renderer.LoadStrokesAsync(1);

        Assert.NotNull(scanned);
        Assert.Same(loaded, scanned); // the first-touch cache, not a second parse
        Assert.Equal([a.Id, b.Id], scanned.Strokes.Select(s => s.Id).ToArray());
        Assert.Equal(StrokeJson.Serialize(a), StrokeJson.Serialize(scanned.Strokes[0]));
        Assert.Equal(0, scanned.UnreadableCount);
        Assert.Equal(1, renderer.FirstTouchReadCount);
    }

    [Fact]
    public async Task Page_rendered_before_the_scan_still_returns_its_strokes()
    {
        // A render hides the app annotations from the in-memory page, so the pre-check must use the cache.
        InkStroke a = Stroke(300);
        string path = await AuthorAsync(1, new Dictionary<int, InkStroke[]> { [0] = [a] });

        await using var renderer = await PdfRenderer.OpenAsync(path);
        await renderer.RenderPageAsync(0, 200, 280);
        var source = new MarkPageSource(renderer);

        PageStrokes? scanned = await source.TryLoadStrokesForScanAsync(0, CancellationToken.None);

        Assert.NotNull(scanned);
        Assert.Equal(a.Id, Assert.Single(scanned.Strokes).Id);
        Assert.Equal(1, renderer.FirstTouchReadCount);
    }

    [Fact]
    public async Task Damaged_annotation_json_returns_a_page_with_unreadable_count_not_an_exception()
    {
        string path = await AuthorAsync(
            1, new Dictionary<int, InkStroke[]> { [0] = [Stroke(300), Stroke(400)] }, damageSecondOnPage: 0);

        await using var renderer = await PdfRenderer.OpenAsync(path);
        var source = new MarkPageSource(renderer);

        PageStrokes? scanned = await source.TryLoadStrokesForScanAsync(0, CancellationToken.None);

        Assert.NotNull(scanned);
        Assert.Single(scanned.Strokes);
        Assert.Equal(1, scanned.UnreadableCount);
    }

    [Fact]
    public async Task Page_with_only_a_damaged_annotation_is_returned_with_its_unreadable_count()
    {
        string path = await AuthorAsync(
            1, new Dictionary<int, InkStroke[]> { [0] = [Stroke(300), Stroke(400)] }, damageSecondOnPage: 0, damageAll: true);

        await using var renderer = await PdfRenderer.OpenAsync(path);
        var source = new MarkPageSource(renderer);

        PageStrokes? scanned = await source.TryLoadStrokesForScanAsync(0, CancellationToken.None);

        Assert.NotNull(scanned);
        Assert.Empty(scanned.Strokes);
        Assert.Equal(2, scanned.UnreadableCount);
    }

    [Fact]
    public async Task Cancelled_token_returns_promptly()
    {
        string path = await AuthorAsync(1, new Dictionary<int, InkStroke[]> { [0] = [Stroke(300)] });
        await using var renderer = await PdfRenderer.OpenAsync(path);
        var source = new MarkPageSource(renderer);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.TryLoadStrokesForScanAsync(0, cts.Token));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"cancellation took {watch.Elapsed}");
        Assert.Equal(0, renderer.FirstTouchReadCount);

        // Nothing was left half-done: the page still scans normally afterwards.
        PageStrokes? scanned = await source.TryLoadStrokesForScanAsync(0, CancellationToken.None);
        Assert.Single(scanned!.Strokes);
    }

    [Fact]
    public async Task Page_count_and_dimensions_come_from_the_renderer()
    {
        string path = Pdf(new PageSpec(595, 842), new PageSpec(700, 900, 90));
        await using var renderer = await PdfRenderer.OpenAsync(path);
        var source = new MarkPageSource(renderer);

        Assert.Equal(2, source.PageCount);
        Assert.Equal(renderer.GetPageDimensions(1), source.GetPageDimensions(1));
    }

    [Fact]
    public async Task Scan_of_a_300_page_generated_document_with_strokes_on_twenty_pages_finds_exactly_those_twenty()
    {
        int[] marked = Enumerable.Range(0, 20).Select(i => (i * 15) + 7).ToArray();
        string path = await AuthorAsync(300, marked.ToDictionary(p => p, _ => new[] { Stroke(300) }));

        await using var renderer = await PdfRenderer.OpenAsync(path);
        var source = new MarkPageSource(renderer);

        var found = new List<int>();
        var watch = Stopwatch.StartNew();
        for (int page = 0; page < source.PageCount; page++)
        {
            if (await source.TryLoadStrokesForScanAsync(page, CancellationToken.None) is { } strokes)
            {
                Assert.Single(strokes.Strokes);
                found.Add(page);
            }
        }

        watch.Stop();

        Assert.Equal(marked, found.ToArray());
        Assert.Equal(20, renderer.FirstTouchReadCount);
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"pages=300 total_ms={watch.Elapsed.TotalMilliseconds:0.0} per_page_ms={watch.Elapsed.TotalMilliseconds / 300:0.000}"));

        // Pre-check only (every page now cached or empty): time the 280 pages without annotations again.
        await using var fresh = await PdfRenderer.OpenAsync(path);
        var freshSource = new MarkPageSource(fresh);
        var empty = Enumerable.Range(0, 300).Except(marked).ToArray();
        watch.Restart();
        foreach (int page in empty)
        {
            Assert.Null(await freshSource.TryLoadStrokesForScanAsync(page, CancellationToken.None));
        }

        watch.Stop();
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"precheck_pages={empty.Length} total_ms={watch.Elapsed.TotalMilliseconds:0.0} per_page_ms={watch.Elapsed.TotalMilliseconds / empty.Length:0.000}"));
    }

    [Fact]
    public async Task Scan_of_every_page_of_a_real_book_copy_completes()
    {
        string? copy = CopyLargestRealDocument();
        if (copy is null)
        {
            return;
        }

        await using var renderer = await PdfRenderer.OpenAsync(copy);
        var source = new MarkPageSource(renderer);
        int withStrokes = 0;
        var watch = Stopwatch.StartNew();
        for (int page = 0; page < source.PageCount; page++)
        {
            if (await source.TryLoadStrokesForScanAsync(page, CancellationToken.None) is not null)
            {
                withStrokes++;
            }
        }

        watch.Stop();
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"pages={source.PageCount} with_app_annotations={withStrokes} total_ms={watch.Elapsed.TotalMilliseconds:0.0} "
            + $"per_page_ms={watch.Elapsed.TotalMilliseconds / Math.Max(source.PageCount, 1):0.000}"));
    }

    private static InkStroke Stroke(double y) =>
        new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 6, Line(200, 400, y, 40));

    private static InkPoint[] Line(double x0, double x1, double y, int count)
    {
        var points = new InkPoint[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = new InkPoint(x0 + ((x1 - x0) * i / (count - 1)), y, null, null, null, null, i * 8L);
        }

        return points;
    }

    private static void AddForeignAnnotation(nint page, int subtype)
    {
        nint annot = PdfiumNative.FPDFPage_CreateAnnot(page, subtype);
        Assert.NotEqual(0, annot);
        try
        {
            Assert.True(PdfiumAnnotations.SetString(annot, "T", "Someone else"));
            Assert.True(PdfiumAnnotations.SetString(annot, "Contents", "A note"));
        }
        finally
        {
            PdfiumNative.FPDFPage_CloseAnnot(annot);
        }
    }

    private string Pdf(params PageSpec[] specs)
    {
        string path = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        Write(path, specs);
        return path;
    }

    /// <summary>Applies <paramref name="edit"/> to page 0 and saves a full rewrite; returns the saved path.</summary>
    private async Task<string> EditAsync(string source, Action<nint> edit)
    {
        string saved = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(source);
                nint page = document.LoadPage(0);
                try
                {
                    edit(page);
                }
                finally
                {
                    document.ClosePage(page);
                }

                document.SaveFullRewrite(saved);
            },
            CancellationToken.None);
        return saved;
    }

    /// <summary>
    /// Writes a document of <paramref name="pageCount"/> A4 pages with the given strokes on the given pages and
    /// saves a full rewrite. With <paramref name="damageSecondOnPage"/>, the second app annotation on that page
    /// (or every one, with <paramref name="damageAll"/>) gets a payload that is not JSON.
    /// </summary>
    private async Task<string> AuthorAsync(
        int pageCount, Dictionary<int, InkStroke[]> strokesByPage, int? damageSecondOnPage = null, bool damageAll = false)
    {
        string source = Pdf(Enumerable.Repeat(new PageSpec(595, 842), pageCount).ToArray());
        string saved = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(source);
                foreach ((int index, InkStroke[] strokes) in strokesByPage)
                {
                    nint page = document.LoadPage(index);
                    try
                    {
                        var geometry = PageGeometry.From(page);
                        foreach (InkStroke stroke in strokes)
                        {
                            StrokeAnnotations.Write(page, geometry, stroke);
                        }

                        if (damageSecondOnPage == index)
                        {
                            int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
                            for (int i = damageAll ? 0 : 1; i < count; i++)
                            {
                                nint annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
                                try
                                {
                                    Assert.True(PdfiumAnnotations.SetString(annot, StrokeAnnotations.PayloadKey, "{not json"));
                                }
                                finally
                                {
                                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                                }

                                if (!damageAll)
                                {
                                    break;
                                }
                            }
                        }
                    }
                    finally
                    {
                        document.ClosePage(page);
                    }
                }

                document.SaveFullRewrite(saved);
            },
            CancellationToken.None);
        return saved;
    }

    /// <summary>A copy of the largest real PDF, or null when none is available.</summary>
    private string? CopyLargestRealDocument()
    {
        string folder = Environment.GetEnvironmentVariable(_fixturesVariable) is { Length: > 0 } env
            ? env
            : _realDocumentsFolder;
        FileInfo? largest = Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles("*.pdf").OrderByDescending(f => f.Length).FirstOrDefault()
            : null;
        if (largest is null)
        {
            // xUnit 2 has no runtime skip: report it and pass.
            _output.WriteLine("SKIPPED: no real PDF available.");
            return null;
        }

        string copy = Path.Combine(_dir, "real-copy.pdf");
        largest.CopyTo(copy);
        return copy;
    }
}
