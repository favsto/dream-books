using System.Globalization;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;
using Xunit.Abstractions;
using static DreamBooks.Pdf.Tests.TestPdfFactory;

namespace DreamBooks.Pdf.Tests;

public sealed class StrokeAnnotationsTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _output;

    public StrokeAnnotationsTests(ITestOutputHelper output)
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
    }

    [Fact]
    public async Task Written_stroke_is_read_back_identical() // FR-81, FR-83
    {
        InkStroke first = new(
            Guid.NewGuid(),
            InkTool.Pen,
            new InkColor(255, 10, 20, 200),
            2.75,
            [
                new InkPoint(150.125, 300.5, 0.25f, 12.5f, -7.25f, 90f, 1_000),
                new InkPoint(180.0625, 310.75, 0.5f, null, null, null, 1_016),
                new InkPoint(220.1, 305.3, null, 3f, 4f, null, 1_033),
                new InkPoint(260.7, 295.9, 1f, null, null, 359.5f, 1_049),
            ]);
        InkStroke second = new(
            Guid.NewGuid(),
            InkTool.Highlighter,
            new InkColor(0x80, 0xFF, 0xEE, 0x00),
            12,
            Line(new PointD(150, 400), new PointD(400, 420), 40, pressure: null));
        InkStroke third = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1.5,
            Line(new PointD(300, 500), new PointD(320, 650), 250, pressure: 0.7f)); // > 100 points

        string saved = await WriteAndSaveAsync(Pdf(new PageSpec(595, 842)), first, second, third);
        StrokeReadResult result = await ReadAsync(saved);

        Assert.Empty(result.UnreadableAnnotationIndexes);
        Assert.Equal(0, result.ForeignCount);
        Assert.Equal(3, result.Strokes.Count);
        AssertSameStroke(first, result.Strokes[0]);
        AssertSameStroke(second, result.Strokes[1]);
        AssertSameStroke(third, result.Strokes[2]);
    }

    [Fact]
    public async Task Annotation_has_ink_subtype_print_flag_name_and_appearance() // FR-81
    {
        InkStroke stroke = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(200, 30, 40), 4,
            Line(new PointD(150, 300), new PointD(450, 340), 250, pressure: 0.6f));

        string saved = await WriteAndSaveAsync(Pdf(new PageSpec(595, 842)), stroke);

        var facts = await OnPageAsync(saved, (page, _) =>
        {
            Assert.Equal(1, PdfiumNative.FPDFPage_GetAnnotCount(page));
            nint annot = PdfiumNative.FPDFPage_GetAnnot(page, 0);
            try
            {
                Assert.NotEqual(0, PdfiumNative.FPDFAnnot_GetRect(annot, out PdfiumNative.FS_RECTF rect));
                // FPDFAnnot_GetColor refuses annotations that have an appearance stream, so /C is
                // checked for presence; the colour itself is checked in the appearance below.
                return new
                {
                    Subtype = PdfiumNative.FPDFAnnot_GetSubtype(annot),
                    Flags = PdfiumNative.FPDFAnnot_GetFlags(annot),
                    Name = PdfiumAnnotations.GetString(annot, "NM"),
                    Title = PdfiumAnnotations.GetString(annot, "T"),
                    Date = PdfiumAnnotations.GetString(annot, "M"),
                    Payload = PdfiumAnnotations.GetString(annot, StrokeAnnotations.PayloadKey),
                    Appearance = PdfiumAnnotations.GetAppearance(annot),
                    InkLists = PdfiumNative.FPDFAnnot_GetInkListCount(annot),
                    InkPoints = PdfiumAnnotations.GetInkPath(annot, 0),
                    Rect = rect,
                    HasColor = PdfiumNative.FPDFAnnot_HasKey(annot, "C") != 0,
                    HasBorder = PdfiumNative.FPDFAnnot_HasKey(annot, "Border") != 0,
                };
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        });

        Assert.Equal(PdfiumNative.FPDF_ANNOT_INK, facts.Subtype);
        Assert.Equal(PdfiumNative.FPDF_ANNOT_FLAG_PRINT, facts.Flags);
        Assert.Equal(stroke.Id.ToString("N", CultureInfo.InvariantCulture), facts.Name);
        Assert.Equal("Dream Books", facts.Title);
        Assert.Matches(@"^D:\d{14}Z$", facts.Date);
        Assert.Equal(StrokeJson.Serialize(stroke), facts.Payload);
        Assert.True(facts.HasColor, "/C is missing");
        Assert.True(facts.HasBorder, "/Border is missing");

        // The centreline is simplified to at most ~100 points and keeps both ends.
        Assert.Equal(1u, facts.InkLists);
        Assert.InRange(facts.InkPoints.Length, 2, 100);
        Assert.Equal(150, facts.InkPoints[0].X, 1e-3);
        Assert.Equal(842 - 300, facts.InkPoints[0].Y, 1e-3);
        Assert.Equal(450, facts.InkPoints[^1].X, 1e-3);
        Assert.Equal(842 - 340, facts.InkPoints[^1].Y, 1e-3);

        // Appearance: a filled path in the stroke colour, every vertex inside /Rect.
        Assert.False(string.IsNullOrEmpty(facts.Appearance));
        Assert.Contains("0.7843 0.1176 0.1569 rg", facts.Appearance, StringComparison.Ordinal);
        Assert.EndsWith("f", facts.Appearance.TrimEnd(), StringComparison.Ordinal);
        var vertices = AppearanceVertices(facts.Appearance);
        Assert.True(vertices.Count > 10);
        foreach (PointD v in vertices)
        {
            Assert.InRange(v.X, facts.Rect.Left, facts.Rect.Right);
            Assert.InRange(v.Y, facts.Rect.Bottom, facts.Rect.Top);
        }
    }

    [Fact]
    public async Task Rendering_with_annotations_shows_the_stroke_and_without_does_not() // FR-81
    {
        InkStroke stroke = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 6,
            Line(new PointD(200, 300), new PointD(400, 300), 50, pressure: null));

        string saved = await WriteAndSaveAsync(Pdf(new PageSpec(595, 842)), stroke);

        RenderedPage with = await RenderAsync(saved, withAnnotations: true);
        RenderedPage without = await RenderAsync(saved, withAnnotations: false);

        Assert.True(IsDark(with, 300, 300), "the stroke centre is not dark when annotations are rendered");
        Assert.True(IsWhite(without, 300, 300), "the stroke centre is not white without annotations");
        Assert.True(IsWhite(with, 300, 320), "the area next to the stroke is not white");
    }

    [Fact]
    public async Task Pressure_width_is_visible_in_the_render() // FR-22, FR-81
    {
        const int count = 200;
        var points = new InkPoint[count];
        for (int i = 0; i < count; i++)
        {
            double t = i / (double)(count - 1);
            points[i] = new InkPoint(100 + (400 * t), 400, (float)(0.1 + (0.9 * t)), null, null, null, i * 4L);
        }

        InkStroke stroke = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 8, points);
        string saved = await WriteAndSaveAsync(Pdf(new PageSpec(595, 842)), stroke);

        RenderedPage page = await RenderAsync(saved, withAnnotations: true, scale: 2);

        int thin = DarkRunLength(page, column: 130 * 2, aroundRow: 400 * 2);
        int thick = DarkRunLength(page, column: 470 * 2, aroundRow: 400 * 2);

        Assert.True(thin > 0, "the thin start is not drawn");
        Assert.True(thick >= thin * 2.5, $"thin start {thin} px vs thick end {thick} px");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Stroke_survives_rotation_and_offset_box_at_the_same_model_coordinates(int rotate) // FR-26, FR-81
    {
        // MediaBox [100 200 800 1100], CropBox [130 240 730 1060]: visible 600 x 820 before rotation.
        var spec = new PageSpec(700, 900, rotate, OriginX: 100, OriginY: 200, CropBox: new PageBox(130, 240, 730, 1060));
        InkStroke stroke = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 4,
        [
            new InkPoint(150.123456789, 255.987654321, 0.8f, null, null, null, 0),
            new InkPoint(250.5, 255.25, 0.8f, null, null, null, 10),
            new InkPoint(350.0000001, 254.75, 0.8f, null, null, null, 20),
        ]);

        string saved = await WriteAndSaveAsync(Pdf(spec), stroke);
        StrokeReadResult result = await ReadAsync(saved);

        InkStroke back = Assert.Single(result.Strokes);
        Assert.Equal(stroke.Points.Count, back.Points.Count);
        for (int i = 0; i < stroke.Points.Count; i++)
        {
            Assert.Equal(stroke.Points[i].X, back.Points[i].X, 1e-6);
            Assert.Equal(stroke.Points[i].Y, back.Points[i].Y, 1e-6);
        }

        // Rendered at 1 px per point, the model frame is the pixel frame: the horizontal stroke
        // shows along model y = 255, and nothing is drawn where a transposed stroke would be.
        RenderedPage page = await RenderAsync(saved, withAnnotations: true);
        Assert.True(IsDark(page, 180, 255), "no ink at model (180, 255)");
        Assert.True(IsDark(page, 250, 255), "no ink at model (250, 255)");
        Assert.True(IsDark(page, 320, 255), "no ink at model (320, 255)");
        Assert.True(IsWhite(page, 255, 180), "unexpected ink at model (255, 180)");
        Assert.True(IsWhite(page, 255, 330), "unexpected ink at model (255, 330)");
        Assert.True(IsWhite(page, 250, 275), "unexpected ink at model (250, 275)");
    }

    [Fact]
    public async Task Foreign_annotation_is_counted_and_never_returned_as_a_stroke() // FR-81
    {
        InkStroke stroke = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2,
            Line(new PointD(300, 600), new PointD(400, 650), 20, pressure: 0.5f));

        string saved = await WriteAndSaveAsync(Pdf(new PageSpec(595, 842, WithForeignSquareAnnotation: true)), stroke);
        StrokeReadResult result = await ReadAsync(saved);

        Assert.Equal(1, result.ForeignCount);
        Assert.Empty(result.UnreadableAnnotationIndexes);
        Assert.Equal(stroke.Id, Assert.Single(result.Strokes).Id);
    }

    [Fact]
    public async Task Newer_schema_annotation_is_reported_unreadable_and_kept() // FR-83
    {
        string payload = "{\"v\":2,\"id\":\"" + Guid.NewGuid().ToString("N") + "\",\"future\":true}";
        await AssertUnreadablePayloadIsKeptAsync(payload);
    }

    [Fact]
    public async Task Corrupt_payload_is_reported_unreadable_and_kept() // FR-83
    {
        await AssertUnreadablePayloadIsKeptAsync("{\"v\":1,\"id\":\"broken");
    }

    [Fact]
    public async Task RemoveById_removes_only_that_annotation_and_keeps_foreign_ones() // FR-81
    {
        InkStroke a = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2,
            Line(new PointD(300, 500), new PointD(400, 500), 10, pressure: null));
        InkStroke b = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 255), 2,
            Line(new PointD(300, 550), new PointD(400, 550), 10, pressure: null));
        InkStroke c = new(Guid.NewGuid(), InkTool.Highlighter, InkColor.FromRgb(255, 255, 0), 10,
            Line(new PointD(300, 600), new PointD(400, 600), 10, pressure: null));

        string source = Pdf(new PageSpec(595, 842, WithForeignSquareAnnotation: true));
        string saved = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        var outcome = await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(source);
                nint page = document.LoadPage(0);
                try
                {
                    var geometry = PageGeometry.From(page);
                    StrokeAnnotations.Write(page, geometry, a);
                    StrokeAnnotations.Write(page, geometry, b);
                    StrokeAnnotations.Write(page, geometry, c);
                    int removed = StrokeAnnotations.RemoveById(page, b.Id);
                    int removedAgain = StrokeAnnotations.RemoveById(page, b.Id);
                    int removedUnknown = StrokeAnnotations.RemoveById(page, Guid.NewGuid());
                    int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
                    document.SaveFullRewrite(saved);
                    return (removed, removedAgain, removedUnknown, count);
                }
                finally
                {
                    document.ClosePage(page);
                }
            },
            CancellationToken.None);

        Assert.Equal(1, outcome.removed);
        Assert.Equal(0, outcome.removedAgain);
        Assert.Equal(0, outcome.removedUnknown);
        Assert.Equal(3, outcome.count); // foreign square + a + c

        StrokeReadResult result = await ReadAsync(saved);
        Assert.Equal(1, result.ForeignCount);
        Assert.Equal([a.Id, c.Id], result.Strokes.Select(s => s.Id));
    }

    [Fact]
    public async Task RemoveById_removes_every_annotation_with_that_id() // FR-81, FR-63
    {
        InkStroke a = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2,
            Line(new PointD(300, 500), new PointD(400, 500), 10, pressure: null));
        InkStroke keep = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 255), 2,
            Line(new PointD(300, 550), new PointD(400, 550), 10, pressure: null));
        InkStroke aAgain = new(a.Id, InkTool.Pen, InkColor.FromRgb(255, 0, 0), 3,
            Line(new PointD(300, 600), new PointD(400, 600), 10, pressure: null));

        string source = Pdf(new PageSpec(595, 842, WithForeignSquareAnnotation: true));
        var outcome = await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(source);
                nint page = document.LoadPage(0);
                try
                {
                    var geometry = PageGeometry.From(page);
                    StrokeAnnotations.Write(page, geometry, a);
                    StrokeAnnotations.Write(page, geometry, keep);
                    StrokeAnnotations.Write(page, geometry, aAgain);
                    int removed = StrokeAnnotations.RemoveById(page, a.Id);
                    return (removed, read: StrokeAnnotations.Read(page, geometry));
                }
                finally
                {
                    document.ClosePage(page);
                }
            },
            CancellationToken.None);

        Assert.Equal(2, outcome.removed);
        Assert.Equal(1, outcome.read.ForeignCount);
        Assert.Equal(keep.Id, Assert.Single(outcome.read.Strokes).Id);
    }

    [Fact]
    public async Task RemoveById_does_not_remove_an_annotation_whose_NM_matches_but_payload_differs() // FR-81
    {
        InkStroke target = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2,
            Line(new PointD(300, 500), new PointD(400, 500), 10, pressure: null));
        InkStroke namedLikeTarget = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 255), 2,
            Line(new PointD(300, 550), new PointD(400, 550), 10, pressure: null));
        string targetName = target.Id.ToString("N", CultureInfo.InvariantCulture);

        string source = Pdf(new PageSpec(595, 842));
        var outcome = await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(source);
                nint page = document.LoadPage(0);
                try
                {
                    var geometry = PageGeometry.From(page);
                    // The look-alike comes first, so a first-match search would hit it.
                    StrokeAnnotations.Write(page, geometry, namedLikeTarget);
                    StrokeAnnotations.Write(page, geometry, target);
                    nint annot = PdfiumNative.FPDFPage_GetAnnot(page, 0);
                    try
                    {
                        Assert.True(PdfiumAnnotations.SetString(annot, "NM", targetName));
                    }
                    finally
                    {
                        PdfiumNative.FPDFPage_CloseAnnot(annot);
                    }

                    // A payload match exists: only it goes, the /NM look-alike stays.
                    int first = StrokeAnnotations.RemoveById(page, target.Id);
                    Guid[] afterFirst = [.. StrokeAnnotations.Read(page, geometry).Strokes.Select(s => s.Id)];

                    // No payload match any more: matching is by payload Id only, nothing goes.
                    int second = StrokeAnnotations.RemoveById(page, target.Id);
                    int left = StrokeAnnotations.Read(page, geometry).Strokes.Count;
                    return (first, afterFirst, second, left);
                }
                finally
                {
                    document.ClosePage(page);
                }
            },
            CancellationToken.None);

        Assert.Equal(1, outcome.first);
        Assert.Equal([namedLikeTarget.Id], outcome.afterFirst);
        Assert.Equal(0, outcome.second);
        Assert.Equal(1, outcome.left);
    }

    [Fact]
    public async Task RemoveByIds_removes_every_match_and_keeps_everything_else() // FR-81, FR-63
    {
        InkStroke a = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2,
            Line(new PointD(300, 500), new PointD(400, 500), 10, pressure: null));
        InkStroke aAgain = new(a.Id, InkTool.Pen, InkColor.FromRgb(255, 0, 0), 3,
            Line(new PointD(300, 520), new PointD(400, 520), 10, pressure: null));
        InkStroke b = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 255), 2,
            Line(new PointD(300, 550), new PointD(400, 550), 10, pressure: null));
        InkStroke keep = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 255, 0), 2,
            Line(new PointD(300, 600), new PointD(400, 600), 10, pressure: null));
        InkStroke unreadable = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2,
            Line(new PointD(300, 650), new PointD(400, 650), 10, pressure: null));
        string future = "{\"v\":2,\"id\":\"" + unreadable.Id.ToString("N") + "\"}";

        // 0 foreign square, 1 a, 2 b, 3 unreadable (v2 payload with its own Id), 4 keep, 5 a again.
        string source = Pdf(new PageSpec(595, 842, WithForeignSquareAnnotation: true));
        var outcome = await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(source);
                nint page = document.LoadPage(0);
                try
                {
                    var geometry = PageGeometry.From(page);
                    foreach (InkStroke s in new[] { a, b, unreadable, keep, aAgain })
                    {
                        StrokeAnnotations.Write(page, geometry, s);
                    }

                    nint annot = PdfiumNative.FPDFPage_GetAnnot(page, 3);
                    try
                    {
                        Assert.True(PdfiumAnnotations.SetString(annot, StrokeAnnotations.PayloadKey, future));
                    }
                    finally
                    {
                        PdfiumNative.FPDFPage_CloseAnnot(annot);
                    }

                    int none = StrokeAnnotations.RemoveByIds(page, new HashSet<Guid>());
                    int removed = StrokeAnnotations.RemoveByIds(
                        page, new HashSet<Guid> { a.Id, b.Id, unreadable.Id, Guid.NewGuid() });
                    return (none, removed, read: StrokeAnnotations.Read(page, geometry),
                        count: PdfiumNative.FPDFPage_GetAnnotCount(page));
                }
                finally
                {
                    document.ClosePage(page);
                }
            },
            CancellationToken.None);

        Assert.Equal(0, outcome.none);
        Assert.Equal(3, outcome.removed); // a twice, b once; the unreadable and the unknown Id are not
        Assert.Equal(3, outcome.count);
        Assert.Equal(1, outcome.read.ForeignCount);
        Assert.Single(outcome.read.UnreadableAnnotationIndexes);
        Assert.Equal(keep.Id, Assert.Single(outcome.read.Strokes).Id);
    }

    [Fact]
    public async Task RemoveByIds_on_a_1500_stroke_page_parses_each_payload_once() // FR-81, NFR-04
    {
        const int strokeCount = 1_500;
        var strokes = new InkStroke[strokeCount];
        for (int i = 0; i < strokeCount; i++)
        {
            double y = 50 + (i % 700);
            strokes[i] = new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2,
                Line(new PointD(100, y), new PointD(400, y + 5), 50, pressure: 0.5f));
        }

        string saved = await WriteAndSaveAsync(Pdf(new PageSpec(595, 842)), strokes);
        var doomed = new HashSet<Guid>(strokes.Where((_, i) => i % 3 == 0).Select(s => s.Id));
        Assert.Equal(500, doomed.Count);

        var outcome = await OnPageAsync(saved, (page, geometry) =>
        {
            long parsesBefore = StrokeAnnotations.PayloadParseCount;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int removed = StrokeAnnotations.RemoveByIds(page, doomed);
            watch.Stop();
            long parses = StrokeAnnotations.PayloadParseCount - parsesBefore;
            return (removed, parses, watch.Elapsed, left: StrokeAnnotations.Read(page, geometry).Strokes.Count);
        });

        _output.WriteLine($"RemoveByIds 500 of 1500: {outcome.Elapsed.TotalMilliseconds:0} ms, {outcome.parses} payload parses");
        Assert.Equal(500, outcome.removed);
        Assert.Equal(1_000, outcome.left);
        Assert.InRange(outcome.parses, 0, strokeCount + 2);
        Assert.True(outcome.Elapsed < TimeSpan.FromSeconds(5), $"took {outcome.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task Dot_and_one_point_stroke_round_trip() // FR-20, FR-81
    {
        InkStroke dot = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 6,
        [
            new InkPoint(200, 300, 0.7f, null, null, null, 5),
            new InkPoint(200, 300, 0.7f, null, null, null, 9),
        ]);
        InkStroke single = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 6,
            [new InkPoint(400, 300, null, null, null, null, 0)]);

        string saved = await WriteAndSaveAsync(Pdf(new PageSpec(595, 842)), dot, single);
        StrokeReadResult result = await ReadAsync(saved);

        Assert.Equal(2, result.Strokes.Count);
        AssertSameStroke(dot, result.Strokes[0]);
        AssertSameStroke(single, result.Strokes[1]);

        int[] inkPointCounts = await OnPageAsync(saved, (page, _) =>
            Enumerable.Range(0, 2).Select(i =>
            {
                nint annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
                try
                {
                    return PdfiumAnnotations.GetInkPath(annot, 0).Length;
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                }
            }).ToArray());
        Assert.Equal(1, inkPointCounts[1]);

        RenderedPage page = await RenderAsync(saved, withAnnotations: true);
        Assert.True(IsDark(page, 200, 300), "the dot is not drawn");
        Assert.True(IsDark(page, 400, 300), "the one-point stroke is not drawn");
    }

    [Fact]
    public async Task Existing_indirect_Annots_array_is_preserved() // FR-81, FR-82
    {
        InkStroke stroke = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2,
            Line(new PointD(300, 600), new PointD(400, 650), 20, pressure: 0.5f));
        var spec = new PageSpec(595, 842, WithForeignSquareAnnotation: true, ExistingAnnotsAsIndirectArray: true);

        string saved = await WriteAndSaveAsync(Pdf(spec), stroke);
        StrokeReadResult result = await ReadAsync(saved);

        Assert.Equal(1, result.ForeignCount);
        Assert.Equal(stroke.Id, Assert.Single(result.Strokes).Id);
        int count = await OnPageAsync(saved, (page, _) => PdfiumNative.FPDFPage_GetAnnotCount(page));
        Assert.Equal(2, count);
    }

    private async Task AssertUnreadablePayloadIsKeptAsync(string payload)
    {
        InkStroke good = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2,
            Line(new PointD(300, 500), new PointD(400, 500), 10, pressure: null));
        InkStroke other = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2,
            Line(new PointD(300, 600), new PointD(400, 600), 10, pressure: null));

        // Annotation 0: foreign square; 1: the stroke whose payload gets replaced; 2: a good stroke.
        string source = Pdf(new PageSpec(595, 842, WithForeignSquareAnnotation: true));
        string first = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(source);
                nint page = document.LoadPage(0);
                try
                {
                    var geometry = PageGeometry.From(page);
                    StrokeAnnotations.Write(page, geometry, other);
                    StrokeAnnotations.Write(page, geometry, good);
                    nint annot = PdfiumNative.FPDFPage_GetAnnot(page, 1);
                    try
                    {
                        Assert.True(PdfiumAnnotations.SetString(annot, StrokeAnnotations.PayloadKey, payload));
                    }
                    finally
                    {
                        PdfiumNative.FPDFPage_CloseAnnot(annot);
                    }

                    document.SaveFullRewrite(first);
                }
                finally
                {
                    document.ClosePage(page);
                }
            },
            CancellationToken.None);

        // Read, then try to remove it by its /NM and remove all app strokes; save again.
        string second = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        var outcome = await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(first);
                nint page = document.LoadPage(0);
                try
                {
                    var geometry = PageGeometry.From(page);
                    StrokeReadResult read = StrokeAnnotations.Read(page, geometry);
                    int countAfterRead = PdfiumNative.FPDFPage_GetAnnotCount(page);
                    int removedById = StrokeAnnotations.RemoveById(page, other.Id);
                    int removedAll = StrokeAnnotations.RemoveAllApp(page);
                    document.SaveFullRewrite(second);
                    return (read, countAfterRead, removedById, removedAll);
                }
                finally
                {
                    document.ClosePage(page);
                }
            },
            CancellationToken.None);

        Assert.Equal([1], outcome.read.UnreadableAnnotationIndexes);
        Assert.Equal(1, outcome.read.ForeignCount);
        Assert.Equal(good.Id, Assert.Single(outcome.read.Strokes).Id);
        Assert.Equal(3, outcome.countAfterRead);
        Assert.Equal(0, outcome.removedById);
        Assert.Equal(1, outcome.removedAll);

        // After saving: the foreign square and the unreadable annotation, payload untouched.
        var kept = await OnPageAsync(second, (page, geometry) =>
        {
            StrokeReadResult read = StrokeAnnotations.Read(page, geometry);
            nint annot = PdfiumNative.FPDFPage_GetAnnot(page, 1);
            try
            {
                return (read, PdfiumNative.FPDFPage_GetAnnotCount(page),
                    PdfiumAnnotations.GetString(annot, StrokeAnnotations.PayloadKey),
                    PdfiumAnnotations.GetAppearance(annot));
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        });

        Assert.Equal(2, kept.Item2);
        Assert.Empty(kept.read.Strokes);
        Assert.Equal([1], kept.read.UnreadableAnnotationIndexes);
        Assert.Equal(payload, kept.Item3);
        Assert.False(string.IsNullOrEmpty(kept.Item4));
    }

    private static void AssertSameStroke(InkStroke expected, InkStroke actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Tool, actual.Tool);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.BaseWidthPt, actual.BaseWidthPt);
        Assert.Equal(expected.Points, actual.Points);
    }

    private static InkPoint[] Line(PointD from, PointD to, int count, float? pressure)
    {
        var points = new InkPoint[count];
        for (int i = 0; i < count; i++)
        {
            double t = count == 1 ? 0 : i / (double)(count - 1);
            points[i] = new InkPoint(
                from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t), pressure, null, null, null, i * 8L);
        }

        return points;
    }

    private string Pdf(PageSpec spec)
    {
        string path = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        Write(path, spec);
        return path;
    }

    private async Task<string> WriteAndSaveAsync(string source, params InkStroke[] strokes)
    {
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

    private static Task<StrokeReadResult> ReadAsync(string path) =>
        OnPageAsync(path, StrokeAnnotations.Read);

    private static Task<T> OnPageAsync<T>(string path, Func<nint, PageGeometry, T> work) => PdfiumThread.RunAsync(
        () =>
        {
            using var document = PdfiumDocument.Open(path);
            nint page = document.LoadPage(0);
            try
            {
                return work(page, PageGeometry.From(page));
            }
            finally
            {
                document.ClosePage(page);
            }
        },
        CancellationToken.None);

    private static Task<RenderedPage> RenderAsync(string path, bool withAnnotations, int scale = 1) =>
        OnPageAsync(path, (page, geometry) => PdfRenderer.RenderLoadedPage(
            page,
            (int)Math.Round(geometry.Dimensions.WidthPt * scale),
            (int)Math.Round(geometry.Dimensions.HeightPt * scale),
            withAnnotations));

    private static List<PointD> AppearanceVertices(string content)
    {
        string[] tokens = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var vertices = new List<PointD>();
        for (int i = 2; i < tokens.Length; i++)
        {
            if (tokens[i] is "m" or "l")
            {
                vertices.Add(new PointD(
                    double.Parse(tokens[i - 2], CultureInfo.InvariantCulture),
                    double.Parse(tokens[i - 1], CultureInfo.InvariantCulture)));
            }
        }

        return vertices;
    }

    private static int DarkRunLength(RenderedPage page, int column, int aroundRow)
    {
        int count = 0;
        for (int y = Math.Max(0, aroundRow - 60); y < Math.Min(page.PixelHeight, aroundRow + 60); y++)
        {
            if (IsDark(page, column, y))
            {
                count++;
            }
        }

        return count;
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
}
