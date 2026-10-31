using System.Security.Cryptography;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;
using Xunit.Abstractions;
using static DreamBooks.Pdf.Tests.TestPdfFactory;

namespace DreamBooks.Pdf.Tests;

public sealed class PdfDocumentSaverTests : IDisposable
{
    private const string _sampleText = "Dream Books saver test";
    private const string _tempPattern = "*.drbk-tmp";
    private const string _realDocumentsFolder = @"C:\Users\Favsto\workspace\temp";
    private const string _fixturesVariable = "DREAMBOOKS_FIXTURES";

    // Upper bound for what one added stroke may add to a file (spike: ~6.8 KB per stroke).
    private const long _perStrokeAllowance = 16 * 1024;

    private readonly string _dir;
    private readonly ITestOutputHelper _output;

    public PdfDocumentSaverTests(ITestOutputHelper output)
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
    public async Task Saved_copy_contains_added_strokes_and_not_removed_ones() // FR-80, FR-81
    {
        string source = SamplePdf(pages: 3);
        InkStroke kept = Stroke(100);
        InkStroke erased = Stroke(200);
        await AddStrokesInPlaceAsync(source, 0, kept, erased);

        InkStroke added0 = Stroke(300);
        InkStroke added2 = Stroke(400);
        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
        [
            new PageChange(0, [erased.Id], [added0]),
            new PageChange(2, [], [added2]),
        ]);

        Assert.True(File.Exists(temp));
        Assert.Equal(Path.GetDirectoryName(source), Path.GetDirectoryName(temp));
        Assert.Matches(@"^source\.pdf\.[0-9a-f]{8}\.drbk-tmp$", Path.GetFileName(temp));

        Assert.Equal([kept.Id, added0.Id], (await ReadPageAsync(temp, 0)).Strokes.Select(s => s.Id));
        Assert.Empty((await ReadPageAsync(temp, 1)).Strokes);
        InkStroke back = Assert.Single((await ReadPageAsync(temp, 2)).Strokes);
        Assert.Equal(StrokeJson.Serialize(added2), StrokeJson.Serialize(back));
    }

    [Fact]
    public async Task Saved_copy_keeps_text_links_page_count_and_foreign_annotations() // FR-82, NFR-10
    {
        string source = Path.Combine(_dir, "featured.pdf");
        WriteWithFeatures(source, new Features
        {
            Text = _sampleText,
            LinkCount = 2,
            SquareAnnotationCount = 1,
            Outline = [new OutlineItem("One", new OutlineItem("Child")), new OutlineItem("Two")],
            Info = new Dictionary<string, string> { ["Title"] = "Saver" },
        }, (595, 842), (842, 595), (300, 400));

        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [], [Stroke(100)]), new PageChange(2, [], [Stroke(50)])]);

        var facts = await PdfiumThread.RunAsync(
            () =>
            {
                using var a = PdfiumDocument.Open(source);
                using var b = PdfiumDocument.Open(temp);
                var differences = new List<string>();
                if (a.PageCount != b.PageCount)
                {
                    differences.Add("page count");
                }

                for (int i = 0; i < a.PageCount; i++)
                {
                    nint pa = a.LoadPage(i);
                    nint pb = b.LoadPage(i);
                    try
                    {
                        if (PdfFidelity.ReadPageText(pa) != PdfFidelity.ReadPageText(pb))
                        {
                            differences.Add($"text {i}");
                        }

                        var ca = PdfFidelity.CountAnnotations(pa);
                        var cb = PdfFidelity.CountAnnotations(pb);
                        foreach (int subtype in ca.Keys.Union(cb.Keys).Where(s => s != PdfiumNative.FPDF_ANNOT_INK))
                        {
                            if (ca.GetValueOrDefault(subtype) != cb.GetValueOrDefault(subtype))
                            {
                                differences.Add($"annotations {subtype} on {i}");
                            }
                        }
                    }
                    finally
                    {
                        a.ClosePage(pa);
                        b.ClosePage(pb);
                    }
                }

                if (PdfFidelity.CountOutlineEntries(a.Handle) != PdfFidelity.CountOutlineEntries(b.Handle))
                {
                    differences.Add("outline");
                }

                if (PdfFidelity.ReadMetaText(a.Handle, "Title") != PdfFidelity.ReadMetaText(b.Handle, "Title"))
                {
                    differences.Add("title");
                }

                return (differences, pages: b.PageCount);
            },
            CancellationToken.None);

        Assert.Empty(facts.differences);
        Assert.Equal(3, facts.pages);
        StrokeReadResult first = await ReadPageAsync(temp, 0);
        Assert.Single(first.Strokes);
        Assert.Equal(3, first.ForeignCount); // two links and the square
    }

    [Fact]
    public async Task Source_file_is_byte_identical_after_writing_the_copy() // NFR-10
    {
        string source = SamplePdf(pages: 2);
        await AddStrokesInPlaceAsync(source, 1, Stroke(100));
        StrokeReadResult loaded = await ReadPageAsync(source, 1);
        byte[] before = Hash(source);
        string destination = Path.Combine(_dir, "copy.pdf");

        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, destination,
            [new PageChange(1, [loaded.Strokes[0].Id], [Stroke(200)])]);

        Assert.Equal(before, Hash(source));
        Assert.False(File.Exists(destination));
        Assert.StartsWith("copy.pdf.", Path.GetFileName(temp), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_with_no_changes_still_produces_a_valid_equal_copy() // FR-82
    {
        string source = SamplePdf(pages: 3);

        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source, []);

        FidelityReport report = await PdfiumThread.RunAsync(
            () => PdfFidelity.Compare(source, temp), CancellationToken.None);
        Assert.True(report.IsFaithful, string.Join("; ", report.Differences));
    }

    [Fact]
    public async Task Ids_not_loaded_by_the_session_are_never_removed() // FR-81
    {
        string source = SamplePdf(pages: 1);
        InkStroke a = Stroke(100);
        InkStroke b = Stroke(200);
        await AddStrokesInPlaceAsync(source, 0, a, b);

        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [Guid.NewGuid()], [])]);

        Assert.Equal([a.Id, b.Id], (await ReadPageAsync(temp, 0)).Strokes.Select(s => s.Id));
    }

    [Fact]
    public async Task Removed_id_matching_only_another_annotations_NM_removes_nothing() // FR-81
    {
        string source = SamplePdf(pages: 1);
        InkStroke other = Stroke(100);
        InkStroke plain = Stroke(200);
        Guid erased = Guid.NewGuid();
        await AddStrokesInPlaceAsync(source, 0, other, plain);
        await EditInPlaceAsync(source, 0, page =>
        {
            nint annot = PdfiumNative.FPDFPage_GetAnnot(page, 0);
            try
            {
                Assert.True(PdfiumAnnotations.SetString(annot, "NM", erased.ToString("N")));
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        });

        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [erased], [])]);

        Assert.Equal([other.Id, plain.Id], (await ReadPageAsync(temp, 0)).Strokes.Select(s => s.Id));
    }

    [Fact]
    public async Task Added_stroke_with_an_existing_id_replaces_it_without_duplicating() // FR-81, FR-63
    {
        string source = SamplePdf(pages: 1);
        InkStroke original = Stroke(100);
        InkStroke neighbour = Stroke(200);
        await AddStrokesInPlaceAsync(source, 0, original, neighbour, original); // already duplicated in the file
        InkStroke updated = new(original.Id, InkTool.Highlighter, InkColor.FromRgb(255, 255, 0), 8,
            [new InkPoint(10, 10, null, null, null, null, 0), new InkPoint(60, 15, null, null, null, null, 9)]);

        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [], [updated])]);

        StrokeReadResult read = await ReadPageAsync(temp, 0);
        Assert.Equal(2, read.Strokes.Count);
        InkStroke back = Assert.Single(read.Strokes, s => s.Id == original.Id);
        Assert.Equal(StrokeJson.Serialize(updated), StrokeJson.Serialize(back));
        Assert.Contains(read.Strokes, s => s.Id == neighbour.Id);
    }

    [Fact]
    public async Task Removed_and_re_added_with_the_same_id_ends_with_one_stroke() // FR-63, FR-81
    {
        string source = SamplePdf(pages: 1);
        InkStroke original = Stroke(100);
        await AddStrokesInPlaceAsync(source, 0, original);
        InkStroke again = new(original.Id, InkTool.Pen, InkColor.FromRgb(0, 0, 255), 3,
            [new InkPoint(10, 10, 0.4f, null, null, null, 0), new InkPoint(90, 20, 0.6f, null, null, null, 9)]);

        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [original.Id], [again])]);

        InkStroke back = Assert.Single((await ReadPageAsync(temp, 0)).Strokes);
        Assert.Equal(StrokeJson.Serialize(again), StrokeJson.Serialize(back));
    }

    [Fact]
    public async Task Newer_schema_and_corrupt_annotations_survive_a_save_untouched() // FR-83
    {
        Guid futureId = Guid.NewGuid();
        string future = "{\"v\":2,\"id\":\"" + futureId.ToString("N") + "\",\"future\":true}";
        const string corrupt = "{\"v\":1,\"id\":\"broken";
        InkStroke good = Stroke(300);
        string source = Path.Combine(_dir, "mixed.pdf");
        Write(source, new PageSpec(595, 842, WithForeignSquareAnnotation: true));
        await AddStrokesInPlaceAsync(source, 0, Stroke(100), Stroke(200), good);
        await SetPayloadsInPlaceAsync(source, 0, (1, future), (2, corrupt));

        InkStroke added = Stroke(400);
        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [good.Id, futureId], [added])]);

        StrokeReadResult read = await ReadPageAsync(temp, 0);
        Assert.Equal(added.Id, Assert.Single(read.Strokes).Id);
        Assert.Equal(1, read.ForeignCount);
        Assert.Equal(2, read.UnreadableAnnotationIndexes.Count);
        string?[] payloads = await PdfiumThread.RunAsync(
            () => OnPage(temp, 0, page => read.UnreadableAnnotationIndexes
                .Select(i => PayloadAt(page, i)).ToArray()),
            CancellationToken.None);
        Assert.Equal(new string?[] { future, corrupt }, payloads);
    }

    [Fact]
    public async Task Saving_a_rotated_offset_page_round_trips_strokes() // FR-26, FR-81
    {
        string source = Path.Combine(_dir, "rotated.pdf");
        Write(source, new PageSpec(700, 900, 90, OriginX: 100, OriginY: 200, CropBox: new PageBox(130, 240, 730, 1060)));
        InkStroke stroke = new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 4,
        [
            new InkPoint(150.123456789, 255.987654321, 0.8f, 1.5f, -2.25f, 45f, 0),
            new InkPoint(250.5, 255.25, 0.8f, null, null, null, 10),
            new InkPoint(750.0000001, 554.75, 0.8f, null, null, null, 20),
        ]);

        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [], [stroke])]);
        FileReplacer.Replace(temp, source);

        await using var renderer = await PdfRenderer.OpenAsync(source);
        Assert.Equal(new PageDimensions(820, 600), renderer.GetPageDimensions(0));
        PageStrokes strokes = await renderer.LoadStrokesAsync(0);
        InkStroke back = Assert.Single(strokes.Strokes);
        Assert.Equal(stroke.Points, back.Points);
        Assert.Equal(StrokeJson.Serialize(stroke), StrokeJson.Serialize(back));
    }

    [Fact]
    public async Task Encrypted_or_restricted_document_is_refused() // FR-82, NFR-11
    {
        string source = Path.Combine(_dir, "encrypted.pdf");
        WriteEncryptedRestricted(source, (595, 842));
        byte[] before = Hash(source);

        await Assert.ThrowsAsync<PdfSaveRefusedException>(() =>
            new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source, [new PageChange(0, [], [Stroke(100)])]));

        Assert.Equal(before, Hash(source));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task Change_on_a_page_out_of_range_is_rejected_before_any_file_is_created()
    {
        string source = SamplePdf(pages: 2);
        byte[] before = Hash(source);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source, [new PageChange(2, [], [Stroke(100)])]));

        Assert.Equal(before, Hash(source));
        AssertNoTempFiles();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Verification_mismatch_deletes_the_temp_file_and_throws(bool truncate) // NFR-10
    {
        (string source, string destination, byte[] sourceHash, byte[] destinationHash) = PrepareFailureCase(inPlace: false);
        string? seenTemp = null;
        var saver = new PdfDocumentSaver
        {
            AfterTempWritten = temp =>
            {
                seenTemp = temp;
                if (truncate)
                {
                    using var stream = new FileStream(temp, FileMode.Open, FileAccess.Write);
                    stream.SetLength(stream.Length / 2);
                }
                else
                {
                    DropFirstAppAnnotation(temp, pageIndex: 0);
                }
            },
        };

        var ex = await Assert.ThrowsAsync<SaveVerificationException>(() =>
            saver.WriteVerifiedCopyAsync(source, destination, [new PageChange(0, [], [Stroke(100)])]));

        Assert.NotNull(seenTemp);
        Assert.DoesNotContain(_dir, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_sampleText, ex.Message, StringComparison.Ordinal);
        AssertUntouched(source, sourceHash, destination, destinationHash);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Write_error_deletes_the_temp_file_and_leaves_the_source_unchanged(bool inPlace) // NFR-10
    {
        (string source, string destination, byte[] sourceHash, byte[] destinationHash) = PrepareFailureCase(inPlace);
        var saver = new PdfDocumentSaver { WrapTempStream = inner => new DiskFullStream(inner, failAfterBytes: 500) };

        await Assert.ThrowsAnyAsync<IOException>(() =>
            saver.WriteVerifiedCopyAsync(source, destination, [new PageChange(0, [], [Stroke(100)])]));

        AssertUntouched(source, sourceHash, destination, destinationHash);
    }

    [Fact]
    public async Task Write_error_in_a_missing_folder_leaves_nothing()
    {
        string source = SamplePdf(pages: 1);
        byte[] before = Hash(source);
        string destination = Path.Combine(_dir, "missing", "copy.pdf");

        await Assert.ThrowsAnyAsync<IOException>(() =>
            new PdfDocumentSaver().WriteVerifiedCopyAsync(source, destination, [new PageChange(0, [], [Stroke(100)])]));

        Assert.Equal(before, Hash(source));
        Assert.False(Directory.Exists(Path.Combine(_dir, "missing")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_deletes_the_temp_file(bool inPlace) // NFR-10
    {
        (string source, string destination, byte[] sourceHash, byte[] destinationHash) = PrepareFailureCase(inPlace);
        using var cts = new CancellationTokenSource();
        bool tempExisted = false;
        var saver = new PdfDocumentSaver
        {
            AfterTempWritten = temp =>
            {
                tempExisted = File.Exists(temp);
                cts.Cancel();
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            saver.WriteVerifiedCopyAsync(source, destination, [new PageChange(0, [], [Stroke(100)])], cts.Token));

        Assert.True(tempExisted);
        AssertUntouched(source, sourceHash, destination, destinationHash);
    }

    [Fact]
    public async Task Cancellation_between_pages_stops_before_writing() // NFR-10
    {
        (string source, string destination, byte[] sourceHash, byte[] destinationHash) = PrepareFailureCase(inPlace: true);
        using var cts = new CancellationTokenSource();
        var pagesDone = new List<int>();
        var saver = new PdfDocumentSaver
        {
            AfterPageChanged = page =>
            {
                pagesDone.Add(page);
                cts.Cancel();
            },
            AfterTempWritten = _ => Assert.Fail("the copy was written after cancellation"),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saver.WriteVerifiedCopyAsync(source, destination,
            [new PageChange(0, [], [Stroke(100)]), new PageChange(1, [], [Stroke(200)])], cts.Token));

        Assert.Equal([0], pagesDone);
        AssertUntouched(source, sourceHash, destination, destinationHash);
    }

    [Fact]
    public async Task Cancellation_before_starting_creates_nothing()
    {
        string source = SamplePdf(pages: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source, [], cts.Token));

        AssertNoTempFiles();
    }

    [Fact]
    public async Task Replace_blocked_after_a_verified_write_keeps_the_original_and_the_temp() // NFR-10
    {
        string source = SamplePdf(pages: 2);
        byte[] before = Hash(source);
        InkStroke stroke = Stroke(100);
        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [], [stroke])]);

        ReplaceFailedException ex;
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ex = Assert.Throws<ReplaceFailedException>(() =>
                FileReplacer.Replace(temp, source, attempts: 3, delay: TimeSpan.Zero));
        }

        Assert.Equal(before, Hash(source));
        Assert.Equal(temp, ex.TempPath);
        Assert.True(ex.TargetExists);
        Assert.True(ex.TempExists);
        Assert.Equal(stroke.Id, Assert.Single((await ReadPageAsync(temp, 0)).Strokes).Id);
    }

    [Fact]
    public async Task Writing_the_copy_while_the_viewer_has_the_document_open_succeeds() // NFR-04, NFR-10
    {
        string source = SamplePdf(pages: 2);
        await using var viewer = await PdfRenderer.OpenAsync(source);
        RenderedPage before = await viewer.RenderPageAsync(0, 100, 141);

        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [], [Stroke(100)])]);

        Assert.Single((await ReadPageAsync(temp, 0)).Strokes);
        RenderedPage after = await viewer.RenderPageAsync(0, 100, 141);
        Assert.Equal(before.Bgra, after.Bgra);
    }

    [Fact]
    public async Task Repeated_saves_do_not_grow_the_file() // FR-82
    {
        string path = SamplePdf(pages: 20);
        var saver = new PdfDocumentSaver();
        var sizes = new List<long>();

        // First save adds the stroke set; the next nine save the same set again.
        for (int i = 0; i < 10; i++)
        {
            IReadOnlyList<PageChange> changes = i == 0
                ? [new PageChange(0, [], [Stroke(100), Stroke(200)]), new PageChange(7, [], [Stroke(300)])]
                : [];
            string temp = await saver.WriteVerifiedCopyAsync(path, path, changes);
            FileReplacer.Replace(temp, path);
            sizes.Add(new FileInfo(path).Length);
        }

        _output.WriteLine("sizes: " + string.Join(", ", sizes));
        foreach (long size in sizes.Skip(2))
        {
            Assert.InRange(size, sizes[1] * 0.99, sizes[1] * 1.01);
        }

        Assert.Equal(2, (await ReadPageAsync(path, 0)).Strokes.Count);
        Assert.Single((await ReadPageAsync(path, 7)).Strokes);
        AssertNoTempFiles();
    }

    [Fact]
    public async Task Repeated_saves_of_the_real_ebook_do_not_grow_the_file() // FR-82
    {
        string folder = Environment.GetEnvironmentVariable(_fixturesVariable) is { Length: > 0 } custom
            ? custom
            : _realDocumentsFolder;
        string? original = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.pdf").OrderByDescending(p => new FileInfo(p).Length).FirstOrDefault() // the largest: the e-book
            : null;
        if (original is null)
        {
            _output.WriteLine($"SKIPPED: no real PDF in the fixtures folder (set {_fixturesVariable}).");
            return;
        }

        string path = Path.Combine(_dir, "book.pdf");
        File.Copy(original, path);
        var saver = new PdfDocumentSaver();
        var sizes = new List<long> { new FileInfo(path).Length };
        for (int i = 0; i < 3; i++)
        {
            string temp = await saver.WriteVerifiedCopyAsync(path, path, [new PageChange(i, [], [Stroke(100 + (i * 50))])]);
            FileReplacer.Replace(temp, path);
            sizes.Add(new FileInfo(path).Length);
        }

        _output.WriteLine("sizes (original, save 1, 2, 3): " + string.Join(", ", sizes));
        for (int i = 2; i < sizes.Count; i++)
        {
            long previous = sizes[i - 1];
            long bound = (long)(previous * 0.01) + _perStrokeAllowance;
            Assert.InRange(sizes[i], previous - bound, previous + bound);
        }
    }

    // ---- helpers ----

    private string SamplePdf(int pages)
    {
        string path = Path.Combine(_dir, "source.pdf");
        WriteWithText(path, _sampleText, Enumerable.Repeat((595.0, 842.0), pages).ToArray());
        return path;
    }

    private (string Source, string Destination, byte[] SourceHash, byte[] DestinationHash) PrepareFailureCase(bool inPlace)
    {
        string source = SamplePdf(pages: 2);
        string destination = source;
        if (!inPlace)
        {
            destination = Path.Combine(_dir, "existing.pdf");
            Write(destination, (300, 300));
        }

        return (source, destination, Hash(source), Hash(destination));
    }

    private void AssertUntouched(string source, byte[] sourceHash, string destination, byte[] destinationHash)
    {
        Assert.Equal(sourceHash, Hash(source));
        Assert.Equal(destinationHash, Hash(destination));
        AssertNoTempFiles();
    }

    private void AssertNoTempFiles() => Assert.Empty(Directory.GetFiles(_dir, _tempPattern, SearchOption.AllDirectories));

    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));

    private static InkStroke Stroke(double y)
    {
        var points = new InkPoint[20];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new InkPoint(100 + (i * 10), y + (i % 3), 0.5f, null, null, null, i * 8L);
        }

        return new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2, points);
    }

    /// <summary>Writes strokes into <paramref name="path"/> directly (a fixture, not the saver).</summary>
    private static Task AddStrokesInPlaceAsync(string path, int pageIndex, params InkStroke[] strokes) =>
        EditInPlaceAsync(path, pageIndex, page =>
        {
            var geometry = PageGeometry.From(page);
            foreach (InkStroke stroke in strokes)
            {
                StrokeAnnotations.Write(page, geometry, stroke);
            }
        });

    private static Task SetPayloadsInPlaceAsync(string path, int pageIndex, params (int Index, string Payload)[] payloads) =>
        EditInPlaceAsync(path, pageIndex, page =>
        {
            foreach ((int index, string payload) in payloads)
            {
                nint annot = PdfiumNative.FPDFPage_GetAnnot(page, index);
                try
                {
                    Assert.True(PdfiumAnnotations.SetString(annot, StrokeAnnotations.PayloadKey, payload));
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                }
            }
        });

    private static async Task EditInPlaceAsync(string path, int pageIndex, Action<nint> edit)
    {
        string rewritten = path + ".fixture";
        await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(path);
                nint page = document.LoadPage(pageIndex);
                try
                {
                    edit(page);
                }
                finally
                {
                    document.ClosePage(page);
                }

                document.SaveFullRewrite(rewritten);
            },
            CancellationToken.None);
        File.Move(rewritten, path, overwrite: true);
    }

    /// <summary>Removes the first app annotation of a page in the written copy (PDFium thread).</summary>
    private static void DropFirstAppAnnotation(string path, int pageIndex)
    {
        string rewritten = path + ".corrupted";
        using (var document = PdfiumDocument.Open(path))
        {
            nint page = document.LoadPage(pageIndex);
            try
            {
                Guid id = StrokeAnnotations.Read(page, PageGeometry.From(page)).Strokes[0].Id;
                Assert.Equal(1, StrokeAnnotations.RemoveById(page, id));
            }
            finally
            {
                document.ClosePage(page);
            }

            document.SaveFullRewrite(rewritten);
        }

        File.Move(rewritten, path, overwrite: true);
    }

    private static Task<StrokeReadResult> ReadPageAsync(string path, int pageIndex) => PdfiumThread.RunAsync(
        () => OnPage(path, pageIndex, page => StrokeAnnotations.Read(page, PageGeometry.From(page))),
        CancellationToken.None);

    private static T OnPage<T>(string path, int pageIndex, Func<nint, T> work)
    {
        using var document = PdfiumDocument.Open(path);
        nint page = document.LoadPage(pageIndex);
        try
        {
            return work(page);
        }
        finally
        {
            document.ClosePage(page);
        }
    }

    private static string? PayloadAt(nint page, int index)
    {
        nint annot = PdfiumNative.FPDFPage_GetAnnot(page, index);
        try
        {
            return PdfiumAnnotations.GetString(annot, StrokeAnnotations.PayloadKey);
        }
        finally
        {
            PdfiumNative.FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>Simulates a disk filling up after a byte budget.</summary>
    private sealed class DiskFullStream(Stream inner, int failAfterBytes) : Stream
    {
        private int _remaining = failAfterBytes;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > _remaining)
            {
                throw new IOException("Simulated disk full.");
            }

            _remaining -= buffer.Length;
            inner.Write(buffer);
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
