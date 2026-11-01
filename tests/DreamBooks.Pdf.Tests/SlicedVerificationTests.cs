using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// The save's fidelity check runs on the single PDFium thread in short slices, so page renders and stroke loads asked
/// meanwhile (a jump to another page during a save) run between them instead of waiting seconds behind one long job
/// (NFR-03, NFR-04, NFR-10). The sliced check reports exactly what the single-job one does.
/// </summary>
public sealed class SlicedVerificationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DreamBooks.Pdf.Tests", Guid.NewGuid().ToString("N"));

    public SlicedVerificationTests() => Directory.CreateDirectory(_dir);

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
    public async Task The_sliced_comparison_reports_exactly_what_the_single_job_comparison_reports()
    {
        var (source, saved) = await SourceAndSavedCopyAsync(pages: 30);

        FidelityReport whole = await PdfiumThread.RunAsync(() => PdfFidelity.Compare(source, saved), CancellationToken.None);
        FidelityReport sliced = await PdfFidelity.CompareAsync(source, saved, TimeSpan.Zero, afterSlice: null, CancellationToken.None);

        Assert.NotEmpty(whole.Differences); // the added stroke: one more annotation on page 4
        Assert.Equal(whole.Differences, sliced.Differences);
        Assert.Equal(whole.Summary, sliced.Summary);
        Assert.Equal(whole.IsFaithful, sliced.IsFaithful);
    }

    [Fact]
    public async Task Other_PDFium_work_runs_between_the_slices_of_a_comparison()
    {
        var (source, saved) = await SourceAndSavedCopyAsync(pages: 30);
        var order = new List<string>();
        Task? probe = null;

        await PdfFidelity.CompareAsync(
            source,
            saved,
            TimeSpan.Zero, // one page per slice
            afterSlice: () =>
            {
                order.Add("slice");
                probe ??= PdfiumThread.RunAsync(() => order.Add("probe"), CancellationToken.None); // e.g. a page render
            },
            CancellationToken.None);
        await probe!;

        Assert.True(order.Count(o => o == "slice") >= 30);
        Assert.InRange(order.IndexOf("probe"), 1, order.Count - 2); // after the first slice, long before the last
    }

    [Fact]
    public async Task A_save_lets_other_PDFium_work_run_while_it_verifies_the_packed_copy()
    {
        var source = Path.Combine(_dir, "book.pdf");
        TestPdfFactory.WriteWithText(source, "Lorem ipsum dolor sit amet", Enumerable.Repeat((612.0, 792.0), 40).ToArray());
        var order = new List<string>();
        Task? probe = null;
        var saver = new PdfDocumentSaver
        {
            FidelitySlice = TimeSpan.Zero,
            AfterPackedWritten = _ => probe = PdfiumThread.RunAsync(() => order.Add("probe"), CancellationToken.None),
            AfterFidelitySlice = () => order.Add("slice"),
        };

        var temp = await saver.WriteVerifiedCopyAsync(source, source, [new PageChange(3, [], [Stroke()])]);
        await probe!;
        File.Delete(temp);

        Assert.True(order.Count(o => o == "slice") >= 40);
        Assert.InRange(order.IndexOf("probe"), 0, 2); // served at once, not after the whole check
    }

    [Fact]
    public async Task Cancelling_a_sliced_comparison_closes_both_documents()
    {
        var (source, saved) = await SourceAndSavedCopyAsync(pages: 30);
        using var cts = new CancellationTokenSource();
        var slices = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfFidelity.CompareAsync(
            source,
            saved,
            TimeSpan.Zero,
            afterSlice: () =>
            {
                if (++slices == 3)
                {
                    cts.Cancel();
                }
            },
            cts.Token));

        await PdfiumThread.RunAsync(() => { }, CancellationToken.None); // the closing job has run
        File.Delete(saved); // PDFium no longer holds the file
        Assert.InRange(slices, 3, 4);
    }

    private async Task<(string Source, string Saved)> SourceAndSavedCopyAsync(int pages)
    {
        var source = Path.Combine(_dir, "source.pdf");
        TestPdfFactory.WriteWithText(source, "Lorem ipsum dolor sit amet", Enumerable.Repeat((612.0, 792.0), pages).ToArray());
        var saved = await new PdfDocumentSaver(packObjects: false)
            .WriteVerifiedCopyAsync(source, source, [new PageChange(3, [], [Stroke()])]);
        return (source, saved);
    }

    private static InkStroke Stroke() =>
        new(
            Guid.NewGuid(),
            InkTool.Pen,
            InkColor.FromRgb(0, 0, 0),
            1.5,
            [new InkPoint(100, 100, 0.5f, null, null, null, 0), new InkPoint(200, 150, 0.5f, null, null, null, 8)]);
}
