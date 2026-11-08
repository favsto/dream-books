using System.Diagnostics;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;
using Xunit.Abstractions;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// The cost of a lot of ink on a long real document (NFR-06, FR-89): 2,000 strokes on a copy of the largest real PDF
/// of the fixtures folder (the e-book; skipped when there is none), saved, saved again with one new stroke, and read
/// back. Only counts, times, sizes and memory are reported (NFR-12); the real file is only ever read as a copy.
/// </summary>
[Collection(LargeDocumentCollection.Name)]
public sealed class LargeDocumentTests : IDisposable
{
    private const string _realDocumentsFolder = @"C:\Users\Favsto\workspace\temp";
    private const string _fixturesVariable = "DREAMBOOKS_FIXTURES";
    private const int _strokeCount = 2000;
    private const int _pointsPerStroke = 80;

    private readonly string _dir;
    private readonly ITestOutputHelper _output;

    public LargeDocumentTests(ITestOutputHelper output)
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
    public async Task Two_thousand_strokes_on_the_real_ebook_save_reload_and_stay_within_the_memory_budget() // NFR-06, FR-89
    {
        string folder = Environment.GetEnvironmentVariable(_fixturesVariable) is { Length: > 0 } custom ? custom : _realDocumentsFolder;
        string? original = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.pdf").OrderByDescending(p => new FileInfo(p).Length).FirstOrDefault()
            : null;
        if (original is null)
        {
            _output.WriteLine($"SKIPPED: no real PDF in the fixtures folder (set {_fixturesVariable}).");
            return;
        }

        string path = Path.Combine(_dir, "book.pdf");
        File.Copy(original, path);
        int pages = await PdfiumThread.RunAsync(() =>
        {
            using var document = PdfiumDocument.Open(path);
            return document.PageCount;
        }, CancellationToken.None);

        var changes = Enumerable.Range(0, pages)
            .Select(page => new PageChange(
                page,
                [],
                Enumerable.Range(0, _strokeCount).Where(i => i % pages == page).Select(i => Stroke(i)).ToArray()))
            .Where(c => c.Added.Count > 0)
            .ToArray();
        var saver = new PdfDocumentSaver();

        long privateBefore = PrivateBytes();
        var watch = Stopwatch.StartNew();
        string temp = await saver.WriteVerifiedCopyAsync(path, path, changes);
        long firstSaveMs = watch.ElapsedMilliseconds;
        long privatePeakAfter = PrivateBytes();
        FileReplacer.Replace(temp, path);
        long sizeAfterFirst = new FileInfo(path).Length;

        watch.Restart();
        temp = await saver.WriteVerifiedCopyAsync(path, path, [new PageChange(0, [], [Stroke(_strokeCount + 1)])]);
        long secondSaveMs = watch.ElapsedMilliseconds;
        FileReplacer.Replace(temp, path);

        watch.Restart();
        int found = await PdfiumThread.RunAsync(
            () =>
            {
                // One open for every page, as the app does: opening a 30 MB document per page would measure the open.
                using var document = PdfiumDocument.Open(path);
                int count = 0;
                for (int pageIndex = 0; pageIndex < pages; pageIndex++)
                {
                    nint page = document.LoadPage(pageIndex);
                    try
                    {
                        count += StrokeAnnotations.Read(page, PageGeometry.From(page)).Strokes.Count;
                    }
                    finally
                    {
                        document.ClosePage(page);
                    }
                }

                return count;
            },
            CancellationToken.None);

        long readAllMs = watch.ElapsedMilliseconds;

        _output.WriteLine(
            $"pages {pages}, original {new FileInfo(original).Length / 1024} KB, with {_strokeCount} strokes {sizeAfterFirst / 1024} KB; "
            + $"first save {firstSaveMs} ms, save of one more stroke {secondSaveMs} ms, read every page's strokes {readAllMs} ms; "
            + $"private bytes delta after the first save {(privatePeakAfter - privateBefore) / (1024 * 1024)} MB");

        Assert.Equal(_strokeCount + 1, found);

        // Budgets with room for a slower disk or CPU (measured 19 s, 6 s and 1 s on the dev Surface): they catch a
        // change that makes a long book several times slower, not run-to-run noise. The save is verified page by page
        // on purpose (NFR-10) and runs in the background (FR-89).
        Assert.True(firstSaveMs < 90_000, $"the first save took {firstSaveMs} ms");
        Assert.True(secondSaveMs < 40_000, $"the save of one more stroke took {secondSaveMs} ms");
        Assert.True(readAllMs < 10_000, $"reading every page's strokes took {readAllMs} ms");
        Assert.True(
            (privatePeakAfter - privateBefore) < 600L * 1024 * 1024,
            $"the save grew the process by {(privatePeakAfter - privateBefore) / (1024 * 1024)} MB (NFR-06: under 600 MB in all)");
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static InkStroke Stroke(int seed)
    {
        var points = new InkPoint[_pointsPerStroke];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new InkPoint(60 + (i * 4), 100 + (seed % 500) + (i % 7), 0.2f + ((i % 10) * 0.07f), null, null, null, i * 8L);
        }

        return new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1.5, points);
    }
}

