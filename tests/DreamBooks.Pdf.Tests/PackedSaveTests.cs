using System.Security.Cryptography;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;
using Xunit.Abstractions;
using static DreamBooks.Pdf.Tests.TestPdfFactory;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// The saver returns a compact copy (object streams + cross-reference stream) when packing
/// works and is verified, and the plain verified copy on any other outcome (NFR-20, FR-81, NFR-10).
/// Real documents are only read as copies; output lists sizes only (NFR-12).
/// </summary>
public sealed class PackedSaveTests : IDisposable
{
    private const string _tempPattern = "*.drbk-tmp";
    private const string _realDocumentsFolder = @"C:\Users\Favsto\workspace\temp";
    private const string _fixturesVariable = "DREAMBOOKS_FIXTURES";

    // Upper bound for what one added stroke may add to a file (spike: ~6.8 KB per stroke).
    private const long _perStrokeAllowance = 16 * 1024;

    private readonly string _dir;
    private readonly ITestOutputHelper _output;

    public PackedSaveTests(ITestOutputHelper output)
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

    public enum Branch
    {
        Packed,
        PackThrows,
        PackedCopyCorrupted,
        Disabled,
    }

    [Fact]
    public async Task Save_returns_a_packed_copy_that_reopens_with_the_same_strokes() // NFR-20, FR-80, FR-81
    {
        string source = FeaturedPdf();
        InkStroke kept = Stroke(100);
        InkStroke erased = Stroke(200);
        string first = await new PdfDocumentSaver(packObjects: false).WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [], [kept, erased])]);
        FileReplacer.Replace(first, source);

        InkStroke added0 = Stroke(300);
        InkStroke added5 = Stroke(400);
        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
        [
            new PageChange(0, [erased.Id], [added0]),
            new PageChange(5, [], [added5]),
        ]);

        AssertPacked(temp);
        Assert.Equal([kept.Id, added0.Id], (await ReadPageAsync(temp, 0)).Strokes.Select(s => s.Id));
        InkStroke back = Assert.Single((await ReadPageAsync(temp, 5)).Strokes);
        Assert.Equal(StrokeJson.Serialize(added5), StrokeJson.Serialize(back));
        FidelityReport report = await PdfiumThread.RunAsync(
            () => PdfFidelity.Compare(source, temp), CancellationToken.None);
        Assert.DoesNotContain(report.Differences, d => !d.Contains("annotations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_packed_copy_is_listed_as_the_returned_path_and_named_with_the_temp_pattern() // NFR-10
    {
        string source = FeaturedPdf();

        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [], [Stroke(100)])]);

        Assert.Equal(Path.GetDirectoryName(source), Path.GetDirectoryName(temp));
        Assert.Matches(@"^featured\.pdf\.[0-9a-f]{8}\.drbk-tmp$", Path.GetFileName(temp));
        AssertPacked(temp);
        Assert.Equal([source, temp], Directory.GetFiles(_dir).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task No_stray_temp_remains_after_a_packed_save_when_the_replace_succeeds() // NFR-10
    {
        string source = FeaturedPdf();
        string destination = Path.Combine(_dir, "out.pdf");
        File.Copy(source, destination);

        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(source, destination,
            [new PageChange(0, [], [Stroke(100)])]);
        FileReplacer.Replace(temp, destination);
        File.Delete(source);

        Assert.Equal([destination], Directory.GetFiles(_dir));
        AssertPacked(destination);
        Assert.Single((await ReadPageAsync(destination, 0)).Strokes);
    }

    [Fact]
    public async Task Pack_failure_falls_back_to_the_unpacked_verified_copy() // NFR-20, NFR-10
    {
        string source = FeaturedPdf();
        InkStroke stroke = Stroke(100);
        string? packedPath = null;
        var saver = new PdfDocumentSaver
        {
            PackCopy = (input, output, ct) =>
            {
                packedPath = output;
                throw new PdfPackException("no gain");
            },
        };

        string temp = await saver.WriteVerifiedCopyAsync(source, source, [new PageChange(0, [], [stroke])]);

        Assert.NotNull(packedPath);
        Assert.NotEqual(packedPath, temp);
        AssertPlain(temp);
        Assert.Equal(stroke.Id, Assert.Single((await ReadPageAsync(temp, 0)).Strokes).Id);
        Assert.Equal([source, temp], Directory.GetFiles(_dir).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pack_verification_mismatch_falls_back_and_removes_the_packed_temp(bool truncate) // NFR-10
    {
        string source = FeaturedPdf();
        InkStroke stroke = Stroke(100);
        string? packedPath = null;
        var saver = new PdfDocumentSaver
        {
            AfterPackedWritten = packed =>
            {
                packedPath = packed;
                if (truncate)
                {
                    using var stream = new FileStream(packed, FileMode.Open, FileAccess.Write);
                    stream.SetLength(stream.Length / 2);
                }
                else
                {
                    DropFirstAppAnnotation(packed, pageIndex: 0);
                }
            },
        };

        string temp = await saver.WriteVerifiedCopyAsync(source, source, [new PageChange(0, [], [stroke])]);

        Assert.NotNull(packedPath);
        Assert.False(File.Exists(packedPath));
        AssertPlain(temp);
        Assert.Equal(stroke.Id, Assert.Single((await ReadPageAsync(temp, 0)).Strokes).Id);
        Assert.Equal([source, temp], Directory.GetFiles(_dir).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Packed_copy_that_is_not_faithful_falls_back() // NFR-10
    {
        string source = FeaturedPdf();
        string? packedPath = null;
        var saver = new PdfDocumentSaver
        {
            // Strokes, page sizes and first/last page text stay; an unchanged page gains an annotation.
            AfterPackedWritten = packed =>
            {
                packedPath = packed;
                AddSquareAnnotation(packed, pageIndex: 10);
            },
        };

        string temp = await saver.WriteVerifiedCopyAsync(source, source, [new PageChange(0, [], [Stroke(100)])]);

        Assert.NotNull(packedPath);
        Assert.False(File.Exists(packedPath));
        Assert.NotEqual(packedPath, temp);
        AssertPlain(temp);
        Assert.Equal((await ReadPageAsync(source, 10)).ForeignCount, (await ReadPageAsync(temp, 10)).ForeignCount);
        Assert.Equal([source, temp], Directory.GetFiles(_dir).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Plain_copy_that_cannot_be_deleted_is_returned_and_the_packed_copy_removed() // NFR-10
    {
        string source = FeaturedPdf();
        string? plainPath = null;
        string? packedPath = null;
        var saver = new PdfDocumentSaver
        {
            PackCopy = (input, output, ct) =>
            {
                plainPath = input;
                packedPath = output;
                return PdfObjectStreamPacker.Pack(input, output, ct);
            },
            DeleteTemp = path => path != plainPath && PdfDocumentSaver.DeleteFile(path),
        };

        string temp = await saver.WriteVerifiedCopyAsync(source, source, [new PageChange(0, [], [Stroke(100)])]);

        Assert.Equal(plainPath, temp);
        AssertPlain(temp);
        Assert.False(File.Exists(packedPath));
        Assert.Equal([source, temp], Directory.GetFiles(_dir).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Packed_copy_is_returned_when_the_plain_delete_succeeds_even_if_the_name_is_still_visible() // NFR-10
    {
        // Legacy delete semantics (FAT/exFAT/SMB): a delete-pending file still "exists" for a while.
        string source = FeaturedPdf();
        string? plainPath = null;
        string? packedPath = null;
        var saver = new PdfDocumentSaver
        {
            PackCopy = (input, output, ct) =>
            {
                plainPath = input;
                packedPath = output;
                return PdfObjectStreamPacker.Pack(input, output, ct);
            },
            DeleteTemp = path => path == plainPath || PdfDocumentSaver.DeleteFile(path),
        };

        string temp = await saver.WriteVerifiedCopyAsync(source, source, [new PageChange(0, [], [Stroke(100)])]);

        Assert.Equal(packedPath, temp);
        AssertPacked(temp);
    }

    [Fact]
    public async Task Failure_in_packing_after_cancellation_propagates_as_cancellation_and_removes_both_temps() // NFR-10
    {
        string source = FeaturedPdf();
        using var cts = new CancellationTokenSource();
        string? plainPath = null;
        string? packedPath = null;
        var saver = new PdfDocumentSaver
        {
            PackCopy = (input, output, ct) =>
            {
                plainPath = input;
                packedPath = output;
                File.WriteAllText(output, "half written");
                cts.Cancel();
                throw new IOException("Simulated I/O error after cancellation.");
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            saver.WriteVerifiedCopyAsync(source, source, [new PageChange(0, [], [Stroke(100)])], cts.Token));

        Assert.False(File.Exists(plainPath));
        Assert.False(File.Exists(packedPath));
        Assert.Equal([source], Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("bug")]
    [InlineData("stray-cancel")]
    public async Task Unexpected_exception_in_Pack_falls_back(string kind) // NFR-10
    {
        string source = FeaturedPdf();
        string? packedPath = null;
        var saver = new PdfDocumentSaver
        {
            PackCopy = (input, output, ct) =>
            {
                packedPath = output;
                File.WriteAllText(output, "half written"); // a buggy packer leaves its output behind
                throw kind switch
                {
                    "io" => new IOException("Simulated I/O error."),
                    "bug" => new NullReferenceException(),
                    _ => new OperationCanceledException(), // not the caller's token
                };
            },
        };

        string temp = await saver.WriteVerifiedCopyAsync(source, source, [new PageChange(0, [], [Stroke(100)])]);

        Assert.False(File.Exists(packedPath));
        AssertPlain(temp);
        Assert.Equal([source, temp], Directory.GetFiles(_dir).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_during_packing_removes_both_temp_files(bool duringVerification) // NFR-10
    {
        string source = FeaturedPdf();
        string destination = Path.Combine(_dir, "existing.pdf");
        Write(destination, (300, 300));
        byte[] sourceHash = Hash(source);
        byte[] destinationHash = Hash(destination);
        using var cts = new CancellationTokenSource();
        string? plainPath = null;
        string? packedPath = null;
        var saver = new PdfDocumentSaver
        {
            PackCopy = (input, output, ct) =>
            {
                plainPath = input;
                packedPath = output;
                if (!duringVerification)
                {
                    cts.Cancel();
                }

                return PdfObjectStreamPacker.Pack(input, output, ct);
            },
            AfterPackedWritten = _ => cts.Cancel(),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            saver.WriteVerifiedCopyAsync(source, destination, [new PageChange(0, [], [Stroke(100)])], cts.Token));

        Assert.NotNull(plainPath);
        Assert.False(File.Exists(plainPath));
        Assert.False(File.Exists(packedPath));
        Assert.Equal(sourceHash, Hash(source));
        Assert.Equal(destinationHash, Hash(destination));
        Assert.Empty(Directory.GetFiles(_dir, _tempPattern));
    }

    [Theory]
    [InlineData(Branch.Packed)]
    [InlineData(Branch.PackThrows)]
    [InlineData(Branch.PackedCopyCorrupted)]
    [InlineData(Branch.Disabled)]
    public async Task Source_and_destination_are_untouched_in_every_branch(Branch branch) // NFR-10
    {
        string source = FeaturedPdf();
        string destination = Path.Combine(_dir, "existing.pdf");
        Write(destination, (300, 300));
        byte[] sourceHash = Hash(source);
        byte[] destinationHash = Hash(destination);
        var saver = branch switch
        {
            Branch.PackThrows => new PdfDocumentSaver { PackCopy = (_, _, _) => throw new PdfPackException("no gain") },
            Branch.PackedCopyCorrupted => new PdfDocumentSaver { AfterPackedWritten = p => DropFirstAppAnnotation(p, 0) },
            Branch.Disabled => new PdfDocumentSaver(packObjects: false),
            _ => new PdfDocumentSaver(),
        };

        string temp = await saver.WriteVerifiedCopyAsync(source, destination, [new PageChange(0, [], [Stroke(100)])]);

        Assert.Equal(sourceHash, Hash(source));
        Assert.Equal(destinationHash, Hash(destination));
        Assert.Equal([temp], Directory.GetFiles(_dir, _tempPattern));
        Assert.Single((await ReadPageAsync(temp, 0)).Strokes);
        if (branch == Branch.Packed)
        {
            AssertPacked(temp);
        }
        else
        {
            AssertPlain(temp);
        }
    }

    [Fact]
    public async Task Packing_can_be_disabled() // NFR-20, FR-81
    {
        string source = FeaturedPdf();
        bool packCalled = false;
        var saver = new PdfDocumentSaver(packObjects: false)
        {
            PackCopy = (input, output, ct) =>
            {
                packCalled = true;
                return PdfObjectStreamPacker.Pack(input, output, ct);
            },
        };

        string temp = await saver.WriteVerifiedCopyAsync(source, source, [new PageChange(0, [], [Stroke(100)])]);

        Assert.False(packCalled);
        AssertPlain(temp);
        Assert.Single((await ReadPageAsync(temp, 0)).Strokes);
    }

    [Fact]
    public async Task Packing_is_skipped_above_the_size_limit() // NFR-06 (memory bound)
    {
        string source = FeaturedPdf();
        bool packCalled = false;
        var saver = new PdfDocumentSaver
        {
            PackingSizeLimit = 1024,
            PackCopy = (input, output, ct) =>
            {
                packCalled = true;
                return PdfObjectStreamPacker.Pack(input, output, ct);
            },
        };

        string temp = await saver.WriteVerifiedCopyAsync(source, source, [new PageChange(0, [], [Stroke(100)])]);

        Assert.False(packCalled);
        AssertPlain(temp);
    }

    [Fact]
    public async Task A_packed_source_file_can_be_saved_again_and_edited() // FR-80, FR-81
    {
        string source = FeaturedPdf();
        InkStroke a = Stroke(100);
        InkStroke b = Stroke(200);
        var saver = new PdfDocumentSaver();
        string first = await saver.WriteVerifiedCopyAsync(source, source, [new PageChange(0, [], [a, b])]);
        FileReplacer.Replace(first, source);
        AssertPacked(source);

        InkStroke c = Stroke(300);
        string second = await saver.WriteVerifiedCopyAsync(source, source,
            [new PageChange(0, [a.Id], [c]), new PageChange(3, [], [Stroke(400)])]);
        FileReplacer.Replace(second, source);

        AssertPacked(source);
        Assert.Equal([b.Id, c.Id], (await ReadPageAsync(source, 0)).Strokes.Select(s => s.Id));
        Assert.Single((await ReadPageAsync(source, 3)).Strokes);
        Assert.Equal([source], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Packed_save_of_the_real_ebook_copy_is_close_to_the_original_size() // NFR-20, FR-81
    {
        string? path = CopyLargestRealDocument();
        if (path is null)
        {
            return;
        }

        long original = new FileInfo(path).Length;
        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(path, path,
        [
            new PageChange(0, [], [Stroke(100)]),
            new PageChange(1, [], [Stroke(150)]),
            new PageChange(2, [], [Stroke(200)]),
        ]);
        FileReplacer.Replace(temp, path);
        long saved = new FileInfo(path).Length;

        _output.WriteLine($"original {original} bytes, after one save with 3 strokes {saved} bytes ({(double)saved / original:P1})");
        AssertPacked(path);
        Assert.True(saved < original * 1.15, "the saved file is not within 15% of the original size");
    }

    [Fact]
    public async Task Repeated_saves_of_the_packed_real_ebook_copy_stay_stable() // NFR-20, FR-81, FR-82
    {
        string? path = CopyLargestRealDocument();
        if (path is null)
        {
            return;
        }

        var saver = new PdfDocumentSaver();
        var sizes = new List<long> { new FileInfo(path).Length };
        for (int i = 0; i < 3; i++)
        {
            string temp = await saver.WriteVerifiedCopyAsync(path, path, [new PageChange(i, [], [Stroke(100 + (i * 50))])]);
            FileReplacer.Replace(temp, path);
            sizes.Add(new FileInfo(path).Length);
        }

        _output.WriteLine("sizes (original, save 1, 2, 3): " + string.Join(", ", sizes));
        AssertPacked(path);
        for (int i = 2; i < sizes.Count; i++)
        {
            long previous = sizes[i - 1];
            long bound = (long)(previous * 0.01) + _perStrokeAllowance;
            Assert.InRange(sizes[i], previous - bound, previous + bound);
        }

        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    // ---- helpers ----

    /// <summary>A 30-page document with outline, links, a foreign annotation and metadata: packing gains well over 5%.</summary>
    private string FeaturedPdf()
    {
        string path = Path.Combine(_dir, "featured.pdf");
        WriteWithFeatures(path, new Features
        {
            Text = "Dream Books packed save test",
            LinkCount = 2,
            SquareAnnotationCount = 1,
            Outline = [new OutlineItem("One", new OutlineItem("Child")), new OutlineItem("Two")],
            Info = new Dictionary<string, string> { ["Title"] = "Packed" },
        }, [.. Enumerable.Range(0, 30).Select(i => (595.0, 842.0 - i))]);
        return path;
    }

    private string? CopyLargestRealDocument()
    {
        string folder = Environment.GetEnvironmentVariable(_fixturesVariable) is { Length: > 0 } custom
            ? custom
            : _realDocumentsFolder;
        string? original = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.pdf").OrderByDescending(p => new FileInfo(p).Length).FirstOrDefault()
            : null;
        if (original is null)
        {
            // xUnit 2 has no runtime skip: report it and pass.
            _output.WriteLine($"SKIPPED: no real PDF in the fixtures folder (set {_fixturesVariable}).");
            return null;
        }

        string path = Path.Combine(_dir, "book.pdf");
        File.Copy(original, path);
        return path;
    }

    /// <summary>The file has object streams and a valid cross-reference stream.</summary>
    private static void AssertPacked(string path) => Assert.NotEmpty(PdfStructureChecker.Check(path).ObjectStreams);

    /// <summary>The file is PDFium's plain rewrite (classic xref table), i.e. not packed.</summary>
    private static void AssertPlain(string path)
    {
        Assert.NotEmpty(PdfStructureChecker.ReadClassic(path).Objects);
        Assert.Throws<InvalidDataException>(() => PdfStructureChecker.Check(path));
    }

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

    /// <summary>Removes the first app annotation of a page (PDFium thread); the file becomes a plain rewrite.</summary>
    private static void DropFirstAppAnnotation(string path, int pageIndex) => EditOnPdfiumThread(path, pageIndex, page =>
    {
        Guid id = StrokeAnnotations.Read(page, PageGeometry.From(page)).Strokes[0].Id;
        Assert.Equal(1, StrokeAnnotations.RemoveById(page, id));
    });

    /// <summary>Adds a square annotation to an unchanged page (PDFium thread): only the fidelity check sees it.</summary>
    private static void AddSquareAnnotation(string path, int pageIndex) => EditOnPdfiumThread(path, pageIndex, page =>
    {
        nint annot = PdfiumNative.FPDFPage_CreateAnnot(page, PdfiumNative.FPDF_ANNOT_SQUARE);
        Assert.NotEqual(0, annot);
        PdfiumNative.FPDFPage_CloseAnnot(annot);
    });

    private static void EditOnPdfiumThread(string path, int pageIndex, Action<nint> edit)
    {
        string rewritten = path + ".corrupted";
        using (var document = PdfiumDocument.Open(path))
        {
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
        }

        File.Move(rewritten, path, overwrite: true);
    }

    private static Task<StrokeReadResult> ReadPageAsync(string path, int pageIndex) => PdfiumThread.RunAsync(
        () =>
        {
            using var document = PdfiumDocument.Open(path);
            nint page = document.LoadPage(pageIndex);
            try
            {
                return StrokeAnnotations.Read(page, PageGeometry.From(page));
            }
            finally
            {
                document.ClosePage(page);
            }
        },
        CancellationToken.None);
}
