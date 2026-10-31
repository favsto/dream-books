using DreamBooks.Pdf.Interop;
using static DreamBooks.Pdf.Tests.TestPdfFactory;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// Pins every check of <see cref="PdfFidelity.Compare"/> on synthetic fixtures (FR-82, NFR-10):
/// Fausto's real documents have no outline, links or annotations, so only these tests prove
/// that those checks see real values and that a full rewrite keeps them.
/// </summary>
public sealed class PdfFidelityTests : IDisposable
{
    private const int _linkSubtype = 2;
    private const int _squareSubtype = 5;

    private static readonly string[] _metadataTags = ["Title", "Author", "Subject", "Keywords", "Creator", "Producer"];

    private readonly string _dir;

    public PdfFidelityTests()
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

    private static Dictionary<string, string> FullInfo() => new()
    {
        ["Title"] = "A title",
        ["Author"] = "An author",
        ["Subject"] = "A subject",
        ["Keywords"] = "key words",
        ["Creator"] = "A creator",
        ["Producer"] = "A producer",
    };

    // 3 top-level entries, the second with 2 children, one of which has 1 child: 6 entries.
    private static OutlineItem[] NestedOutline() =>
    [
        new("One"),
        new("Two", new OutlineItem("Two.1", new OutlineItem("Two.1.a")), new OutlineItem("Two.2")),
        new("Three"),
    ];

    private static Features RichFeatures() => new()
    {
        Text = "rich fixture",
        Info = FullInfo(),
        Outline = NestedOutline(),
        LinkCount = 2,
        SquareAnnotationCount = 1,
    };

    private string WriteFixture(string name, Features features)
    {
        string path = Path.Combine(_dir, name);
        WriteWithFeatures(path, features, (595, 842), (300, 400));
        return path;
    }

    private static Task<FidelityReport> CompareAsync(string a, string b) =>
        PdfiumThread.RunAsync(() => PdfFidelity.Compare(a, b), CancellationToken.None);

    [Fact]
    public async Task Rich_fixture_rewrite_is_faithful_and_keeps_outline_links_annotations_and_metadata()
    {
        string source = WriteFixture("rich.pdf", RichFeatures());
        string target = Path.Combine(_dir, "rich-rewritten.pdf");

        await PdfFullRewrite.RewriteAsync(source, target);
        FidelityReport report = await CompareAsync(source, target);

        Assert.True(report.IsFaithful, string.Join("; ", report.Differences));
        Assert.Contains("outline entries 6 vs 6", report.Summary);
        Assert.Contains("annotations by subtype {Link=2, Square=1} vs {Link=2, Square=1}", report.Summary);
        Assert.Contains("metadata fields present: Title, Author, Subject, Keywords, Creator, Producer", report.Summary);

        // The rewritten file itself, read directly (not only "equal to the source").
        (int outline, SortedDictionary<int, int> annotations, Dictionary<string, string> metadata) =
            await PdfiumThread.RunAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(target);
                    nint page = document.LoadPage(0);
                    try
                    {
                        return (
                            PdfFidelity.CountOutlineEntries(document.Handle),
                            PdfFidelity.CountAnnotations(page),
                            _metadataTags.ToDictionary(t => t, t => PdfFidelity.ReadMetaText(document.Handle, t)));
                    }
                    finally
                    {
                        document.ClosePage(page);
                    }
                },
                CancellationToken.None);

        Assert.Equal(6, outline);
        Assert.Equal(2, annotations.GetValueOrDefault(_linkSubtype));
        Assert.Equal(1, annotations.GetValueOrDefault(_squareSubtype));
        Assert.Equal(2, annotations.Count);
        Assert.Equal(FullInfo(), metadata);
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Author")]
    [InlineData("Subject")]
    [InlineData("Keywords")]
    [InlineData("Creator")]
    [InlineData("Producer")]
    public async Task Metadata_field_difference_is_reported(string tag)
    {
        Dictionary<string, string> changed = FullInfo();
        changed[tag] = "Changed value!";
        string a = WriteFixture("a.pdf", new Features { Info = FullInfo() });
        string b = WriteFixture("b.pdf", new Features { Info = changed });

        FidelityReport report = await CompareAsync(a, b);

        string difference = Assert.Single(report.Differences);
        Assert.StartsWith($"metadata {tag} differs (length ", difference);
        Assert.DoesNotContain("Changed", difference);
    }

    [Fact]
    public async Task Outline_entry_difference_is_reported()
    {
        OutlineItem[] smaller = [new("One"), new("Two", new OutlineItem("Two.1")), new("Three")];
        string a = WriteFixture("a.pdf", new Features { Outline = NestedOutline() });
        string b = WriteFixture("b.pdf", new Features { Outline = smaller });

        FidelityReport report = await CompareAsync(a, b);

        Assert.Equal("outline-entries 6 vs 4", Assert.Single(report.Differences));
    }

    [Fact]
    public async Task Cyclic_outline_terminates_and_counts_each_entry_once()
    {
        string path = WriteFixture("cyclic.pdf", new Features { Outline = NestedOutline(), CyclicOutline = true });

        FidelityReport report = await CompareAsync(path, path).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(report.IsFaithful, string.Join("; ", report.Differences));
        Assert.Contains("outline entries 6 vs 6", report.Summary);
    }

    [Fact]
    public async Task Link_annotation_difference_is_reported()
    {
        string a = WriteFixture("a.pdf", new Features { LinkCount = 2 });
        string b = WriteFixture("b.pdf", new Features { LinkCount = 1 });

        FidelityReport report = await CompareAsync(a, b);

        Assert.Equal("page 1: link annotations 2 vs 1", Assert.Single(report.Differences));
    }

    [Fact]
    public async Task Other_annotation_difference_is_reported_by_subtype()
    {
        string a = WriteFixture("a.pdf", new Features { SquareAnnotationCount = 1 });
        string b = WriteFixture("b.pdf", new Features());

        FidelityReport report = await CompareAsync(a, b);

        Assert.Contains("page 1: annotations Square 1 vs 0", report.Differences);
        Assert.DoesNotContain(report.Differences, d => d.Contains("link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rendered_difference_above_threshold_is_reported()
    {
        string a = WriteFixture("a.pdf", new Features { DrawSquare = true });
        string b = WriteFixture("b.pdf", new Features { DrawSquare = false });

        FidelityReport report = await CompareAsync(a, b);

        Assert.Contains(report.Differences, d => d.StartsWith("page 1: pixels differ", StringComparison.Ordinal));
        Assert.Contains(report.Differences, d => d.StartsWith("page 2: pixels differ", StringComparison.Ordinal));
    }

    [Fact]
    public void Mean_absolute_difference_is_per_channel_and_ignores_stride_padding()
    {
        // 2x1 pixels, stride 12: 4 bytes of padding that differ and must be ignored.
        byte[] zero = new byte[12];
        byte[] other = [10, 20, 30, 40, 0, 0, 0, 0, 255, 255, 255, 255];
        var a = new RenderedPage(2, 1, 12, zero);
        var b = new RenderedPage(2, 1, 12, other);

        double[] means = PdfFidelity.MeanAbsoluteDifferencePerChannel(a, b);

        Assert.Equal([5.0, 10.0, 15.0, 20.0], means);
        Assert.Equal([0.0, 0.0, 0.0, 0.0], PdfFidelity.MeanAbsoluteDifferencePerChannel(b, b));
    }

    [Fact]
    public void Mean_absolute_difference_rejects_different_sizes()
    {
        var a = new RenderedPage(1, 1, 4, new byte[4]);
        var b = new RenderedPage(2, 1, 8, new byte[8]);

        Assert.Throws<ArgumentException>(() => PdfFidelity.MeanAbsoluteDifferencePerChannel(a, b));
    }
}
