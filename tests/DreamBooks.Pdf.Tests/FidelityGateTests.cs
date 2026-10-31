using DreamBooks.Pdf.Interop;
using Xunit.Abstractions;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// The phase 2 fidelity gate (FR-82, NFR-10): a full rewrite of Fausto's real documents must
/// keep pages, text, links, annotations, page objects, outline, metadata and appearance.
/// The originals are never touched: each one is copied to a temporary folder first, and the
/// output lists only counts and difference categories, never document text (NFR-12).
/// </summary>
public sealed class FidelityGateTests(ITestOutputHelper output) : IDisposable
{
    private const string _realDocumentsFolder = @"C:\Users\Favsto\workspace\temp";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "DreamBooks.Pdf.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Synthetic_document_is_faithful_to_itself()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "self.pdf");
        TestPdfFactory.WriteWithText(path, "self", (595, 842), (300, 300));

        FidelityReport report = await PdfiumThread.RunAsync(
            () => PdfFidelity.Compare(path, path), CancellationToken.None);

        Assert.True(report.IsFaithful, string.Join("; ", report.Differences));
        Assert.NotEmpty(report.Summary);
    }

    [Fact]
    public async Task Different_documents_are_reported_as_different()
    {
        Directory.CreateDirectory(_dir);
        string a = Path.Combine(_dir, "a.pdf");
        string b = Path.Combine(_dir, "b.pdf");
        TestPdfFactory.WriteWithText(a, "first", (595, 842), (300, 300));
        TestPdfFactory.WriteWithText(b, "other", (595, 842));

        FidelityReport report = await PdfiumThread.RunAsync(
            () => PdfFidelity.Compare(a, b), CancellationToken.None);

        Assert.False(report.IsFaithful);
        Assert.Contains(report.Differences, d => d.StartsWith("page-count", StringComparison.Ordinal));
        Assert.Contains(report.Differences, d => d.StartsWith("page 1: text", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Differences, d => d.Contains("first", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fidelity_gate_on_real_documents() // FR-82, NFR-10
    {
        string[] originals = Directory.Exists(_realDocumentsFolder)
            ? Directory.GetFiles(_realDocumentsFolder, "*.pdf")
            : [];
        if (originals.Length == 0)
        {
            // xUnit 2 has no runtime skip: report it and pass.
            output.WriteLine($"SKIPPED: no PDFs in {_realDocumentsFolder}.");
            return;
        }

        Directory.CreateDirectory(_dir);
        Array.Sort(originals, StringComparer.Ordinal);
        var unfaithful = new List<string>();
        for (int i = 0; i < originals.Length; i++)
        {
            string copy = Path.Combine(_dir, $"original-{i + 1}.pdf");
            string rewritten = Path.Combine(_dir, $"rewritten-{i + 1}.pdf");
            File.Copy(originals[i], copy);

            await PdfFullRewrite.RewriteAsync(copy, rewritten);
            FidelityReport report = await PdfiumThread.RunAsync(
                () => PdfFidelity.Compare(copy, rewritten), CancellationToken.None);

            // File names are private: identify files by their index (ordinal name order) only.
            output.WriteLine($"=== file {i + 1}");
            output.WriteLine($"size before {new FileInfo(copy).Length} bytes, after {new FileInfo(rewritten).Length} bytes");
            foreach (string line in report.Summary)
            {
                output.WriteLine($"  info: {line}");
            }

            output.WriteLine(report.IsFaithful ? "  FAITHFUL" : $"  {report.Differences.Count} DIFFERENCES:");
            foreach (string difference in report.Differences)
            {
                output.WriteLine($"  diff: {difference}");
            }

            if (!report.IsFaithful)
            {
                unfaithful.Add($"file {i + 1}: {report.Differences.Count} differences");
            }
        }

        Assert.True(unfaithful.Count == 0, string.Join("; ", unfaithful));
    }
}
