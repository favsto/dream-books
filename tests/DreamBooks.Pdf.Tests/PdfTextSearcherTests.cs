using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;
using DreamBooks.Core.Search;
using DreamBooks.Pdf.Interop;
using Xunit.Abstractions;
using static DreamBooks.Pdf.Tests.TextPdfFactory;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// Text search of one page on the PDFium thread (FR-13): accent- and case-insensitive matching, rectangles in
/// displayed page points, one job per page with every handle closed. Only counts and times are ever printed,
/// never page text or queries (NFR-12).
/// </summary>
public sealed class PdfTextSearcherTests : IDisposable
{
    private const string _realDocumentsFolder = @"C:\Users\Favsto\workspace\temp";
    private const string _fixturesVariable = "DREAMBOOKS_FIXTURES";
    private const double _w = 600;
    private const double _h = 800;

    private readonly string _dir;
    private readonly ITestOutputHelper _output;

    public PdfTextSearcherTests(ITestOutputHelper output)
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
    public async Task Finds_a_word_and_returns_rectangles_in_displayed_page_points()
    {
        await using var renderer = await OpenAsync(Page(new TextLine(100, 700, "il sole nasce")));

        SearchPageResult result = await renderer.SearchPageAsync(0, Q("sole"), 100, CancellationToken.None);

        Assert.True(result.HasTextLayer);
        Assert.False(result.Truncated);
        SearchHit hit = Assert.Single(result.Hits);
        Assert.Equal(0, hit.PageIndex);
        SearchRect rect = Assert.Single(hit.Rects);
        double left = 100 + Width("il ");
        double baseline = _h - 700;
        Assert.InRange(rect.LeftPt, left - 1.5, left + 1.5);
        Assert.InRange(rect.RightPt, left + Width("sole") - 1.5, left + Width("sole") + 1.5);
        Assert.InRange(rect.TopPt, baseline - 12.5, baseline - 5);
        Assert.InRange(rect.BottomPt, baseline - 1, baseline + 4);
    }

    [Fact]
    public async Task Match_is_case_and_accent_insensitive()
    {
        await using var renderer = await OpenAsync(Page(
            new TextLine(100, 700, "Perch\u00e9 \u00c8 COS\u00cc"),
            new TextLine(100, 600, "citt\u00e0 e fine")));

        Assert.Single((await renderer.SearchPageAsync(0, Q("perche"), 100, CancellationToken.None)).Hits);
        Assert.Single((await renderer.SearchPageAsync(0, Q("PERCHÉ è così"), 100, CancellationToken.None)).Hits);
        Assert.Single((await renderer.SearchPageAsync(0, Q("Citta"), 100, CancellationToken.None)).Hits);
        Assert.Empty((await renderer.SearchPageAsync(0, Q("perchex"), 100, CancellationToken.None)).Hits);
    }

    [Fact]
    public async Task A_ligature_glyph_matches_its_letters_and_highlights_the_glyph()
    {
        await using var renderer = await OpenAsync(Page(new TextLine(100, 700, "la \uFB01ne e il \uFB02usso")));

        SearchHit fine = Assert.Single((await renderer.SearchPageAsync(0, Q("fine"), 100, CancellationToken.None)).Hits);
        SearchHit flusso = Assert.Single((await renderer.SearchPageAsync(0, Q("flusso"), 100, CancellationToken.None)).Hits);
        SearchHit inside = Assert.Single((await renderer.SearchPageAsync(0, Q("ine"), 100, CancellationToken.None)).Hits);

        double left = 100 + Width("la ");
        Assert.InRange(Assert.Single(fine.Rects).LeftPt, left - 1.5, left + 1.5);
        Assert.Single(flusso.Rects);

        // Starting inside the ligature highlights the whole glyph: the hit begins where "fine" begins.
        Assert.Equal(Assert.Single(fine.Rects).LeftPt, Assert.Single(inside.Rects).LeftPt, 3);
    }

    [Fact]
    public async Task Two_occurrences_on_one_line_give_two_hits()
    {
        await using var renderer = await OpenAsync(Page(new TextLine(100, 700, "rosa e rosa")));

        SearchPageResult result = await renderer.SearchPageAsync(0, Q("Rosa"), 100, CancellationToken.None);

        Assert.Equal(2, result.Hits.Count);
        SearchRect first = Assert.Single(result.Hits[0].Rects);
        SearchRect second = Assert.Single(result.Hits[1].Rects);
        Assert.True(first.RightPt <= second.LeftPt, "hits are in reading order and do not overlap");
        Assert.Equal(first.TopPt, second.TopPt, 3);
        Assert.InRange(second.LeftPt, 100 + Width("rosa e ") - 1.5, 100 + Width("rosa e ") + 1.5);
    }

    [Fact]
    public async Task A_query_spanning_a_line_break_gives_one_hit_with_two_rects()
    {
        await using var renderer = await OpenAsync(Page(
            new TextLine(100, 700, "la fine del"),
            new TextLine(100, 686, "mondo intero")));

        SearchPageResult result = await renderer.SearchPageAsync(0, Q("del  mondo"), 100, CancellationToken.None);

        SearchHit hit = Assert.Single(result.Hits);
        Assert.Equal(2, hit.Rects.Count);
        SearchRect line1 = hit.Rects[0];
        SearchRect line2 = hit.Rects[1];
        Assert.True(line1.BottomPt <= line2.TopPt + 3, "the first rect is on the upper line");
        Assert.InRange(line1.LeftPt, 100 + Width("la fine ") - 1.5, 100 + Width("la fine ") + 1.5);
        Assert.InRange(line1.RightPt, 100 + Width("la fine del") - 1.5, 100 + Width("la fine del") + 1.5);
        Assert.InRange(line2.LeftPt, 99, 101.5);
        Assert.InRange(line2.RightPt, 100 + Width("mondo") - 1.5, 100 + Width("mondo") + 1.5);
    }

    [Fact]
    public async Task Two_text_objects_on_one_line_give_one_rect_per_hit()
    {
        await using var renderer = await OpenAsync(Page(
            new TextLine(100, 700, "buon"),
            new TextLine(100 + Width("buon "), 700, "giorno")));

        SearchHit hit = Assert.Single((await renderer.SearchPageAsync(0, Q("buon giorno"), 100, CancellationToken.None)).Hits);

        SearchRect rect = Assert.Single(hit.Rects);
        Assert.InRange(rect.LeftPt, 99, 101.5);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Rects_are_correct_on_a_page_with_Rotate_90_and_a_cropbox_offset(int rotate)
    {
        var crop = new TestPdfFactory.PageBox(50, 60, 550, 760);
        TextLine[] lines = [new TextLine(150, 600, "il sole nasce"), new TextLine(150, 586, "e poi il sole tramonta")];
        string path = NewPath();
        TextPdfFactory.Write(path,
            new TextPageSpec(_w, _h, lines, CropBox: crop),
            new TextPageSpec(_w, _h, lines, Rotate: rotate, CropBox: crop));
        await using var renderer = await PdfRenderer.OpenAsync(path);

        SearchPageResult upright = await renderer.SearchPageAsync(0, Q("il sole"), 100, CancellationToken.None);
        SearchPageResult rotated = await renderer.SearchPageAsync(1, Q("il sole"), 100, CancellationToken.None);

        // Upright with the CropBox offset: displayed x = user x - 50, displayed y = 760 - user y.
        Assert.Equal(2, upright.Hits.Count);
        SearchRect u = Assert.Single(upright.Hits[0].Rects);
        Assert.InRange(u.LeftPt, 100 - 1.5, 100 + 1.5);
        Assert.InRange(u.BottomPt, 160 - 1, 160 + 4);

        double width = crop.Right - crop.Left;
        double height = crop.Top - crop.Bottom;
        SearchRect[] expected = upright.Hits.SelectMany(h => h.Rects).Select(r => rotate switch
        {
            90 => new SearchRect(height - r.BottomPt, r.LeftPt, height - r.TopPt, r.RightPt),
            180 => new SearchRect(width - r.RightPt, height - r.BottomPt, width - r.LeftPt, height - r.TopPt),
            _ => new SearchRect(r.TopPt, width - r.RightPt, r.BottomPt, width - r.LeftPt),
        }).OrderBy(r => r.LeftPt).ThenBy(r => r.TopPt).ToArray();

        // PDFium orders the characters of a rotated page its own way: compare positions, not order
        // (the search index orders hits by position).
        SearchRect[] actual = rotated.Hits.SelectMany(h => h.Rects).OrderBy(r => r.LeftPt).ThenBy(r => r.TopPt).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        Assert.All(rotated.Hits, h => Assert.Equal(1, h.PageIndex));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].LeftPt, actual[i].LeftPt, 3);
            Assert.Equal(expected[i].TopPt, actual[i].TopPt, 3);
            Assert.Equal(expected[i].RightPt, actual[i].RightPt, 3);
            Assert.Equal(expected[i].BottomPt, actual[i].BottomPt, 3);
        }

        // Every rect lies inside the displayed page.
        PageDimensions dims = renderer.GetPageDimensions(1);
        Assert.All(actual, r =>
        {
            Assert.InRange(r.LeftPt, 0, dims.WidthPt);
            Assert.InRange(r.RightPt, r.LeftPt, dims.WidthPt);
            Assert.InRange(r.TopPt, 0, dims.HeightPt);
            Assert.InRange(r.BottomPt, r.TopPt, dims.HeightPt);
        });
    }

    [Fact]
    public async Task A_page_without_text_reports_no_text_layer()
    {
        string path = NewPath();
        TextPdfFactory.Write(path, new TextPageSpec(_w, _h, []), new TextPageSpec(_w, _h, [new TextLine(100, 700, "testo")]));
        await using var renderer = await PdfRenderer.OpenAsync(path);

        SearchPageResult empty = await renderer.SearchPageAsync(0, Q("testo"), 100, CancellationToken.None);
        SearchPageResult text = await renderer.SearchPageAsync(1, Q("testo"), 100, CancellationToken.None);

        Assert.Equal(new SearchPageResult(false, [], false), empty);
        Assert.True(text.HasTextLayer);
        Assert.Single(text.Hits);
    }

    [Fact]
    public async Task An_empty_query_finds_nothing_but_still_reports_the_text_layer()
    {
        await using var renderer = await OpenAsync(Page(new TextLine(100, 700, "testo")));

        Assert.Equal(new SearchPageResult(true, [], false), await renderer.SearchPageAsync(0, string.Empty, 100, CancellationToken.None));
        Assert.Equal(new SearchPageResult(true, [], false), await renderer.SearchPageAsync(0, "   ", 100, CancellationToken.None));
    }

    [Fact]
    public async Task maxHits_truncates_and_sets_Truncated()
    {
        await using var renderer = await OpenAsync(Page(new TextLine(100, 700, "a a a a a")));

        SearchPageResult three = await renderer.SearchPageAsync(0, Q("a"), 3, CancellationToken.None);
        SearchPageResult five = await renderer.SearchPageAsync(0, Q("a"), 5, CancellationToken.None);
        SearchPageResult none = await renderer.SearchPageAsync(0, Q("a"), 0, CancellationToken.None);

        Assert.Equal(3, three.Hits.Count);
        Assert.True(three.Truncated);
        Assert.Equal(five.Hits.Take(3), three.Hits);
        Assert.Equal(5, five.Hits.Count);
        Assert.False(five.Truncated);
        Assert.Empty(none.Hits);
        Assert.True(none.Truncated);
    }

    [Fact]
    public async Task Arguments_are_validated()
    {
        await using var renderer = await OpenAsync(Page(new TextLine(100, 700, "testo")));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => renderer.SearchPageAsync(1, "a", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => renderer.SearchPageAsync(-1, "a", 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => renderer.SearchPageAsync(0, "a", -1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => renderer.SearchPageAsync(0, null!, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Search_after_dispose_throws_ObjectDisposedException()
    {
        PdfRenderer renderer = await OpenAsync(Page(new TextLine(100, 700, "testo")));
        await renderer.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.SearchPageAsync(0, "testo", 1, CancellationToken.None));
    }

    [Fact]
    public async Task Cancelled_token_returns_promptly_and_closes_handles()
    {
        string path = Page(new TextLine(100, 700, "a a a a a"), new TextLine(100, 680, "a a a a a"));
        await using var renderer = await PdfRenderer.OpenAsync(path);

        // A token cancelled before the job starts: the job never runs.
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var watch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => renderer.SearchPageAsync(0, Q("a"), 100, cancelled.Token));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"cancellation took {watch.Elapsed}");
        }

        // Cancelled while the page and its text page are open: both are closed on the way out.
        (int before, int during, int after, int cancelledRuns) = await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(path);
                int start = PdfTextSearcher.OpenHandleCount;
                int peak = 0;
                int runs = 0;
                for (int i = 0; i < 500; i++)
                {
                    using var cts = new CancellationTokenSource();
                    try
                    {
                        PdfTextSearcher.Search(document.Handle, 0, Q("a"), 100, cts.Token, afterOpen: () =>
                        {
                            peak = Math.Max(peak, PdfTextSearcher.OpenHandleCount);
                            cts.Cancel();
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        runs++;
                    }
                }

                return (start, peak, PdfTextSearcher.OpenHandleCount, runs);
            },
            CancellationToken.None);

        Assert.Equal(500, cancelledRuns);
        Assert.Equal(before + 2, during); // the page and its text page
        Assert.Equal(before, after);

        // An exception thrown inside the job closes them too.
        (int beforeThrow, int afterThrow) = await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(path);
                int start = PdfTextSearcher.OpenHandleCount;
                Assert.Throws<InvalidOperationException>(() => PdfTextSearcher.Search(
                    document.Handle, 0, Q("a"), 100, CancellationToken.None, afterOpen: () => throw new InvalidOperationException()));
                return (start, PdfTextSearcher.OpenHandleCount);
            },
            CancellationToken.None);
        Assert.Equal(beforeThrow, afterThrow);

        // PDFium is not exhausted: a normal search still works.
        Assert.Equal(10, (await renderer.SearchPageAsync(0, Q("a"), 100, CancellationToken.None)).Hits.Count);
    }

    [Fact]
    public async Task Search_leaves_the_stroke_cache_untouched_and_strokes_still_load()
    {
        InkStroke stroke = Stroke(300);
        string source = Page(new TextLine(100, 700, "testo con inchiostro"));
        string inked = await new PdfDocumentSaver(packObjects: false).WriteVerifiedCopyAsync(
            source, source, [new PageChange(0, [], [stroke])]);
        await using var renderer = await PdfRenderer.OpenAsync(inked);

        Assert.Single((await renderer.SearchPageAsync(0, Q("testo"), 100, CancellationToken.None)).Hits);

        Assert.Equal(0, renderer.FirstTouchReadCount);
        Assert.Equal(0, await renderer.CachedStrokePageCountAsync());
        Assert.Equal(stroke.Id, Assert.Single((await renderer.LoadStrokesAsync(0)).Strokes).Id);

        // After a first touch (which hides the app annotations in memory) the search still finds the text.
        Assert.Single((await renderer.SearchPageAsync(0, Q("testo"), 100, CancellationToken.None)).Hits);
        Assert.Equal(stroke.Id, Assert.Single((await renderer.LoadStrokesAsync(0)).Strokes).Id);
    }

    [Fact]
    public async Task Search_does_not_change_the_saved_file()
    {
        // A three-page document with strokes on page 1, saved once.
        string source = NewPath();
        TextPdfFactory.Write(source,
            new TextPageSpec(_w, _h, [new TextLine(100, 700, "prima pagina")]),
            new TextPageSpec(_w, _h, [new TextLine(100, 700, "seconda pagina")], Rotate: 90),
            new TextPageSpec(_w, _h, []));
        InkStroke kept = Stroke(300);
        string first = await new PdfDocumentSaver(packObjects: false).WriteVerifiedCopyAsync(
            source, source, [new PageChange(1, [], [kept])]);
        FileReplacer.Replace(first, source);
        byte[] hashBefore = SHA256.HashData(await File.ReadAllBytesAsync(source));

        // Search every page (before and after a render touched page 1), then save a change through the real saver.
        InkStroke added = Stroke(400);
        string saved;
        await using (var renderer = await PdfRenderer.OpenAsync(source))
        {
            for (int page = 0; page < renderer.PageCount; page++)
            {
                await renderer.SearchPageAsync(page, Q("pagina"), 100, CancellationToken.None);
            }

            await renderer.RenderPageAsync(1, 100, 140);
            for (int page = 0; page < renderer.PageCount; page++)
            {
                await renderer.SearchPageAsync(page, Q("pagina"), 100, CancellationToken.None);
            }

            Assert.Equal(kept.Id, Assert.Single((await renderer.LoadStrokesAsync(1)).Strokes).Id);
            saved = await new PdfDocumentSaver().WriteVerifiedCopyAsync(
                source, source, [new PageChange(0, [], [added])]);
        }

        Assert.Equal(hashBefore, SHA256.HashData(await File.ReadAllBytesAsync(source)));

        await using (var reopened = await PdfRenderer.OpenAsync(saved))
        {
            Assert.Equal(StrokeJson.Serialize(added), StrokeJson.Serialize(Assert.Single((await reopened.LoadStrokesAsync(0)).Strokes)));
            Assert.Equal(StrokeJson.Serialize(kept), StrokeJson.Serialize(Assert.Single((await reopened.LoadStrokesAsync(1)).Strokes)));
            Assert.Empty((await reopened.LoadStrokesAsync(2)).Strokes);
            Assert.Single((await reopened.SearchPageAsync(1, Q("seconda"), 100, CancellationToken.None)).Hits);
        }

        FidelityReport report = await PdfiumThread.RunAsync(() => PdfFidelity.Compare(source, saved), CancellationToken.None);
        Assert.DoesNotContain(report.Differences, d => !d.Contains("annotations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Searching_a_300_page_generated_document_finds_exactly_the_planted_words()
    {
        int[] planted = Enumerable.Range(0, 30).Select(i => (i * 10) + 3).ToArray();
        string path = NewPath();
        TextPdfFactory.Write(path, Enumerable.Range(0, 300).Select(p => new TextPageSpec(_w, _h,
        [
            new TextLine(100, 700, "riga di testo normale"),
            new TextLine(100, 686, planted.Contains(p) ? "qui c'\u00e8 la Citt\u00e0 segreta" : "niente da vedere qui"),
        ])).ToArray());
        await using var renderer = await PdfRenderer.OpenAsync(path);

        var found = new List<int>();
        var watch = Stopwatch.StartNew();
        for (int page = 0; page < renderer.PageCount; page++)
        {
            if ((await renderer.SearchPageAsync(page, Q("citta segreta"), 100, CancellationToken.None)).Hits.Count > 0)
            {
                found.Add(page);
            }
        }

        watch.Stop();
        Assert.Equal(planted, found.ToArray());
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"pages=300 hits={found.Count} total_ms={watch.Elapsed.TotalMilliseconds:0} per_page_ms={watch.Elapsed.TotalMilliseconds / 300:0.000}"));
    }

    [Fact]
    public async Task Searching_a_common_word_over_a_real_book_copy_completes()
    {
        string? copy = CopyLargestRealDocument();
        if (copy is null)
        {
            return;
        }

        await using var renderer = await PdfRenderer.OpenAsync(copy);
        foreach (string query in new[] { "che", "e" })
        {
            int hits = 0, pagesWithText = 0, truncatedPages = 0;
            double slowestMs = 0;
            var watch = Stopwatch.StartNew();
            for (int page = 0; page < renderer.PageCount; page++)
            {
                long start = watch.ElapsedTicks;
                SearchPageResult result = await renderer.SearchPageAsync(page, Q(query), 1000, CancellationToken.None);
                slowestMs = Math.Max(slowestMs, (watch.ElapsedTicks - start) * 1000.0 / Stopwatch.Frequency);
                hits += result.Hits.Count;
                pagesWithText += result.HasTextLayer ? 1 : 0;
                truncatedPages += result.Truncated ? 1 : 0;
            }

            watch.Stop();

            // The query itself is not printed (NFR-12): only its length.
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"query_length={query.Length} pages={renderer.PageCount} pages_with_text={pagesWithText} hits={hits} "
                + $"truncated_pages={truncatedPages} total_ms={watch.Elapsed.TotalMilliseconds:0} "
                + $"per_page_ms={watch.Elapsed.TotalMilliseconds / Math.Max(renderer.PageCount, 1):0.000} slowest_page_ms={slowestMs:0.0}"));
        }

        Assert.Equal(0, renderer.FirstTouchReadCount);
    }

    private static string Q(string query) => TextFolding.FoldQuery(query);

    private static InkStroke Stroke(double y)
    {
        var points = new InkPoint[20];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new InkPoint(200 + (i * 10), y, null, null, null, null, i * 8L);
        }

        return new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 6, points);
    }

    private string NewPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");

    private string Page(params TextLine[] lines)
    {
        string path = NewPath();
        TextPdfFactory.Write(path, new TextPageSpec(_w, _h, lines));
        return path;
    }

    private async Task<PdfRenderer> OpenAsync(string path) => await PdfRenderer.OpenAsync(path);

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
