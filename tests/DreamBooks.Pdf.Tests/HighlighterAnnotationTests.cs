using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;
using static DreamBooks.Pdf.Tests.TestPdfFactory;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// Highlighter strokes saved through the real saver (FR-40, FR-42, FR-46, NFR-10): written transparent
/// (route A of <see cref="HighlighterWriteRoute"/>: /CA from the colour alpha and an appearance that selects
/// the /GS state first), placed under the pen annotations of the page, and read back as highlighters.
/// Pen strokes keep exactly the bytes they had before highlighters existed.
/// </summary>
public sealed partial class HighlighterAnnotationTests : IDisposable
{
    private const string _sampleText = "Dream Books highlighter test";

    // SHA-256 of the appearance stream of PinnedPen() as written before phase 3c (regression pin).
    private const string _pinnedPenAppearanceSha256 = "D59CEFC1E2AB9E9A75F0044CC388C181C74A26A419469885D06961798E4372E0";

    // The payload of PinnedPen() as written before phase 3c.
    private const string _pinnedPenPayload =
        """{"v":1,"id":"0f1e2d3c4b5a69788796a5b4c3d2e1f0","tool":"pen","color":"#FF1428A0","w":2.5,"pts":["""
        + """{"x":120,"y":650,"p":0.3,"t":0},{"x":135.5,"y":651.25,"p":0.35000002,"t":8},"""
        + """{"x":151,"y":652.5,"p":0.4,"t":16},{"x":166.5,"y":653.75,"p":0.45000002,"t":24},"""
        + """{"x":182,"y":650,"p":0.5,"t":32},{"x":197.5,"y":651.25,"p":0.55,"t":40},"""
        + """{"x":213,"y":652.5,"p":0.6,"t":48},{"x":228.5,"y":653.75,"p":0.65,"t":56},"""
        + """{"x":244,"y":650,"p":0.70000005,"t":64},{"x":259.5,"y":651.25,"p":0.75,"t":72},"""
        + """{"x":275,"y":652.5,"p":0.8,"t":80},{"x":290.5,"y":653.75,"p":0.85,"t":88}]}""";

    private static readonly InkColor _yellow = new(89, 0xFF, 0xE1, 0x4D);
    private static readonly InkColor _green = new(89, 0x7E, 0xE0, 0x7E);

    private readonly string _dir;

    public HighlighterAnnotationTests()
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
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Saved_highlighters_are_read_back_with_tool_colour_alpha_chisel_and_points() // FR-40, FR-46, FR-81
    {
        string source = SamplePdf();
        InkStroke pen = Pen(500);
        InkStroke round = RoundHighlighter(300);
        InkStroke chisel = ChiselHighlighter(400);

        string saved = await SaveAsync(source, new PageChange(0, [], [pen, round, chisel]));
        StrokeReadResult read = await ReadPageAsync(saved);

        Assert.Equal(2, read.ForeignCount); // the link and the square of the sample
        Assert.Empty(read.UnreadableAnnotationIndexes);
        InkStroke roundBack = Assert.Single(read.Strokes, s => s.Id == round.Id);
        InkStroke chiselBack = Assert.Single(read.Strokes, s => s.Id == chisel.Id);
        InkStroke penBack = Assert.Single(read.Strokes, s => s.Id == pen.Id);

        Assert.Equal(InkTool.Highlighter, roundBack.Tool);
        Assert.Equal(_yellow, roundBack.Color);
        Assert.Null(roundBack.Chisel);
        Assert.Equal(InkTool.Highlighter, chiselBack.Tool);
        Assert.Equal(_green, chiselBack.Color);
        Assert.Equal(new ChiselTip(45, 0.25), chiselBack.Chisel);
        Assert.Equal(InkTool.Pen, penBack.Tool);

        Assert.Equal(StrokeJson.Serialize(round), StrokeJson.Serialize(roundBack));
        Assert.Equal(StrokeJson.Serialize(chisel), StrokeJson.Serialize(chiselBack));
        Assert.Equal(StrokeJson.Serialize(pen), StrokeJson.Serialize(penBack));
    }

    [Fact]
    public async Task Highlighter_annotations_come_before_pen_annotations_on_the_page() // FR-40, spec 5
    {
        string source = SamplePdf();
        InkStroke pen = Pen(500);
        InkStroke round = RoundHighlighter(300);
        InkStroke chisel = ChiselHighlighter(400);

        string saved = await SaveAsync(source, new PageChange(0, [], [pen, round, chisel]));

        Assert.Equal([round.Id, chisel.Id, pen.Id], (await ReadPageAsync(saved)).Strokes.Select(s => s.Id));
    }

    [Fact]
    public async Task Highlighters_added_to_a_page_with_saved_pen_strokes_go_under_them_and_the_pens_are_unchanged() // FR-40, NFR-10
    {
        string source = SamplePdf();
        InkStroke firstPen = Pen(500);
        InkStroke secondPen = Pen(600);
        string withPens = await SaveAsync(source, new PageChange(0, [], [firstPen, secondPen]));
        IReadOnlyList<AppAnnotation> before = await ReadAppAnnotationsAsync(withPens);

        InkStroke round = RoundHighlighter(300);
        string saved = await SaveAsync(withPens, new PageChange(0, [], [round]));

        Assert.Equal([round.Id, firstPen.Id, secondPen.Id], (await ReadPageAsync(saved)).Strokes.Select(s => s.Id));
        IReadOnlyList<AppAnnotation> after = await ReadAppAnnotationsAsync(saved);
        Assert.Equal(before.Select(a => (a.Payload, a.Appearance)), after.Skip(1).Select(a => (a.Payload, a.Appearance)));
    }

    [Fact]
    public async Task Highlighter_annotation_carries_CA_and_its_appearance_selects_GS_first() // FR-40
    {
        string source = SamplePdf();

        string saved = await SaveAsync(
            source, new PageChange(0, [], [Pen(500), RoundHighlighter(300), ChiselHighlighter(400)]));

        IReadOnlyList<AppAnnotation> annotations = await ReadAppAnnotationsAsync(saved);
        Assert.Equal(3, annotations.Count);
        foreach (AppAnnotation highlighter in annotations.Take(2))
        {
            Assert.NotNull(highlighter.Ca);
            Assert.InRange(highlighter.Ca.Value, 0.345, 0.355);
            Assert.StartsWith("/GS gs\n", highlighter.Appearance, StringComparison.Ordinal);
            Assert.EndsWith("f", highlighter.Appearance.TrimEnd(), StringComparison.Ordinal);
        }

        Assert.Contains("1 0.8824 0.302 rg", annotations[0].Appearance, StringComparison.Ordinal);
        AppAnnotation pen = annotations[2];
        Assert.Equal(1f, pen.Ca); // opaque, as before phase 3c
        Assert.DoesNotContain(" gs", pen.Appearance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Highlighter_appearances_follow_the_constant_width_and_chisel_outlines() // FR-42, FR-46
    {
        string source = SamplePdf();
        InkStroke round = RoundHighlighter(300);
        InkStroke chisel = ChiselHighlighter(400);

        string saved = await SaveAsync(source, new PageChange(0, [], [round, chisel]));

        IReadOnlyList<AppAnnotation> annotations = await ReadAppAnnotationsAsync(saved);
        // Round tip, 16 pt wide, along y = 300 (model, y down): the band spans 292..308, i.e. user 534..550.
        (double minY, double maxY) = VerticalExtent(annotations[0].Appearance);
        Assert.Equal(842 - 308, minY, 0.05);
        Assert.Equal(842 - 292, maxY, 0.05);

        // Chisel at 45 degrees, 22 pt long, along a horizontal line: 22 * (0.25 + 0.75 sin 45) high.
        (double cMinY, double cMaxY) = VerticalExtent(annotations[1].Appearance);
        double expected = 22 * (0.25 + (0.75 * Math.Sin(Math.PI / 4)));
        Assert.Equal(expected, cMaxY - cMinY, 0.05);
    }

    [Fact]
    public async Task Saving_highlighters_keeps_page_content_and_foreign_annotations() // FR-82, NFR-10
    {
        string source = SamplePdf();

        string saved = await SaveAsync(
            source, new PageChange(0, [], [Pen(500), RoundHighlighter(300), ChiselHighlighter(400)]));

        FidelityReport report = await PdfiumThread.RunAsync(
            () => PdfFidelity.Compare(source, saved), CancellationToken.None);

        // The new ink annotations are expected, and the comparison render includes annotations.
        string[] unexpected = [.. report.Differences.Where(d =>
            !d.Contains("annotations Ink", StringComparison.Ordinal)
            && !d.Contains("pixels differ", StringComparison.Ordinal))];
        Assert.Empty(unexpected);
    }

    [Fact]
    public async Task A_page_with_only_pen_strokes_is_written_exactly_as_before() // NFR-10 regression
    {
        string source = SamplePdf();
        InkStroke pen = PinnedPen();

        string saved = await SaveAsync(source, new PageChange(0, [], [pen]));

        AppAnnotation written = Assert.Single(await ReadAppAnnotationsAsync(saved));
        Assert.Equal(_pinnedPenPayload, written.Payload);
        Assert.Equal(_pinnedPenAppearanceSha256, Sha256(written.Appearance));
        Assert.Equal(1f, written.Ca);
    }

    private static InkStroke PinnedPen()
    {
        var points = new InkPoint[12];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new InkPoint(120 + (i * 15.5), 650 + (i % 4 * 1.25), 0.3f + (i * 0.05f), null, null, null, i * 8L);
        }

        return new InkStroke(
            Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"), InkTool.Pen, InkColor.FromRgb(20, 40, 160), 2.5, points);
    }

    private static InkStroke Pen(double y)
    {
        var points = new InkPoint[20];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new InkPoint(100 + (i * 10), y + (i % 3), 0.5f, null, null, null, i * 8L);
        }

        return new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2, points);
    }

    /// <summary>A straight-line result: the two end points of a horizontal pass.</summary>
    private static InkStroke RoundHighlighter(double y) => new(
        Guid.NewGuid(),
        InkTool.Highlighter,
        _yellow,
        16,
        [new InkPoint(100, y, 0.4f, null, null, null, 0), new InkPoint(300, y, 0.9f, null, null, null, 400)]);

    private static InkStroke ChiselHighlighter(double y) => new(
        Guid.NewGuid(),
        InkTool.Highlighter,
        _green,
        22,
        [new InkPoint(100, y, null, null, null, null, 0), new InkPoint(300, y, null, null, null, null, 400)],
        new ChiselTip(45, 0.25));

    private string SamplePdf()
    {
        string path = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        WriteWithFeatures(path, new Features { Text = _sampleText, LinkCount = 1, SquareAnnotationCount = 1 }, (595, 842));
        return path;
    }

    private static async Task<string> SaveAsync(string source, PageChange change)
    {
        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source, [change]);
        string saved = Path.ChangeExtension(temp, ".saved.pdf");
        File.Move(temp, saved);
        return saved;
    }

    private static Task<StrokeReadResult> ReadPageAsync(string path) => PdfiumThread.RunAsync(
        () => OnPage(path, page => StrokeAnnotations.Read(page, PageGeometry.From(page))),
        CancellationToken.None);

    /// <summary>The app annotations of page 1 in annotation order, with the raw facts the writer set.</summary>
    private static Task<IReadOnlyList<AppAnnotation>> ReadAppAnnotationsAsync(string path) => PdfiumThread.RunAsync(
        () => OnPage<IReadOnlyList<AppAnnotation>>(path, page =>
        {
            var result = new List<AppAnnotation>();
            int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                nint annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
                try
                {
                    string? payload = PdfiumAnnotations.GetString(annot, StrokeAnnotations.PayloadKey);
                    if (payload is null)
                    {
                        continue;
                    }

                    float? ca = NativeGetNumberValue(annot, "CA", out float value) != 0 ? value : null;
                    result.Add(new AppAnnotation(payload, PdfiumAnnotations.GetAppearance(annot), ca));
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                }
            }

            return result;
        }),
        CancellationToken.None);

    private static T OnPage<T>(string path, Func<nint, T> work)
    {
        using var document = PdfiumDocument.Open(path);
        nint page = document.LoadPage(0);
        try
        {
            return work(page);
        }
        finally
        {
            document.ClosePage(page);
        }
    }

    private static (double Min, double Max) VerticalExtent(string appearance)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (Match m in VertexPattern().Matches(appearance))
        {
            double y = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            min = Math.Min(min, y);
            max = Math.Max(max, y);
        }

        return (min, max);
    }

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // PdfiumNative's pointer parameters need unsafe code, which the test project does not enable.
    [DllImport(PdfiumNative.Library, EntryPoint = "FPDFAnnot_GetNumberValue")]
    private static extern int NativeGetNumberValue(
        nint annotation, [MarshalAs(UnmanagedType.LPStr)] string key, out float value);

    [GeneratedRegex(@"(-?[0-9.]+) (-?[0-9.]+) [ml]\n")]
    private static partial Regex VertexPattern();

    private sealed record AppAnnotation(string Payload, string Appearance, float? Ca);
}
