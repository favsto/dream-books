using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DreamBooks.Pdf.Interop;
using Xunit.Abstractions;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// The object stream packer (NFR-20, FR-80, FR-81): PDFium's plain full rewrite is re-packed into
/// Flate object streams plus a cross-reference stream that is valid per ISO 32000-1 7.5.7/7.5.8
/// (checked by the independent <see cref="PdfStructureChecker"/>), faithful (checked by
/// <see cref="PdfFidelity"/>), or refused with <see cref="PdfPackException"/>.
/// Real documents are only read as copies; output lists sizes only (NFR-12).
/// </summary>
public sealed class PdfObjectStreamPackerTests(ITestOutputHelper output) : IDisposable
{
    private const string _defaultFixturesFolder = @"C:\Users\Favsto\workspace\temp";

    private readonly string _dir = CreateDir();

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

    private static string CreateDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DreamBooks.Pdf.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string PathIn(string name) => Path.Combine(_dir, name);

    /// <summary>A synthetic document with outline, links, annotations and metadata, rewritten by PDFium.</summary>
    private async Task<string> RewrittenSyntheticAsync(int pageCount = 100)
    {
        string source = PathIn("source.pdf");
        TestPdfFactory.WriteWithFeatures(
            source,
            new TestPdfFactory.Features
            {
                Text = "Packed (object) streams \\ test",
                Info = new Dictionary<string, string> { ["Title"] = "A (title)", ["Author"] = "Someone" },
                Outline =
                [
                    new TestPdfFactory.OutlineItem("One", new TestPdfFactory.OutlineItem("Child (1)")),
                    new TestPdfFactory.OutlineItem("Two"),
                ],
                LinkCount = 3,
                SquareAnnotationCount = 2,
            },
            [.. Enumerable.Range(0, pageCount).Select(i => (595.0, 842.0 - i))]);
        string rewritten = PathIn("rewritten.pdf");
        await PdfFullRewrite.RewriteAsync(source, rewritten);
        return rewritten;
    }

    private async Task<(string Rewritten, string Packed, PackResult Result)> PackSyntheticAsync()
    {
        string rewritten = await RewrittenSyntheticAsync();
        string packed = PathIn("packed.pdf");
        PackResult result = PdfObjectStreamPacker.Pack(rewritten, packed);
        return (rewritten, packed, result);
    }

    [Fact]
    public async Task Packs_a_pdfium_rewrite_of_a_synthetic_document_and_the_result_is_faithful()
    {
        (string rewritten, string packed, PackResult result) = await PackSyntheticAsync();

        Assert.Equal(new FileInfo(rewritten).Length, result.InputBytes);
        Assert.Equal(new FileInfo(packed).Length, result.OutputBytes);
        Assert.True(result.OutputBytes < result.InputBytes * 0.95);
        Assert.True(result.PackedObjects > 100);
        Assert.True(result.ObjectStreams >= 2);

        FidelityReport report = await PdfiumThread.RunAsync(
            () => PdfFidelity.Compare(rewritten, packed), CancellationToken.None);
        Assert.True(report.IsFaithful, string.Join("; ", report.Differences));
    }

    [Fact]
    public async Task Packed_output_has_ObjStm_and_a_cross_reference_stream_and_no_classic_xref()
    {
        (_, string packed, PackResult result) = await PackSyntheticAsync();

        PdfStructureChecker.PackedStructure structure = PdfStructureChecker.Check(packed);

        Assert.Equal(result.ObjectStreams, structure.ObjectStreams.Count);
        Assert.Equal(result.PackedObjects, structure.Packed.Count);
        Assert.All(structure.ObjectStreams.Values, s => Assert.InRange(s.Numbers.Length, 1, 100));
        Assert.All(structure.ObjectStreams.Values, s => Assert.Equal(s.Numbers.Order(), s.Numbers));
        Assert.Contains("/Type/XRef", structure.XrefDictionary.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Header_version_is_at_least_1_5()
    {
        (string rewritten, string packed, _) = await PackSyntheticAsync();
        Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(File.ReadAllBytes(rewritten), 0, 8), StringComparison.Ordinal);

        Assert.Equal(15, PdfStructureChecker.Check(packed).Version);
        byte[] bytes = File.ReadAllBytes(packed);
        Assert.Equal("%PDF-1.5", Encoding.Latin1.GetString(bytes, 0, 8));
        Assert.Equal((byte)'%', bytes[10]); // binary-marker comment line kept
        Assert.True(bytes[11] >= 128);
    }

    [Fact]
    public async Task Stream_objects_are_copied_byte_for_byte()
    {
        (string rewritten, string packed, _) = await PackSyntheticAsync();

        PdfStructureChecker.ClassicStructure before = PdfStructureChecker.ReadClassic(rewritten);
        PdfStructureChecker.PackedStructure after = PdfStructureChecker.Check(packed);

        var streams = before.Objects.Values.Where(o => o.IsStream).ToList();
        Assert.NotEmpty(streams);
        foreach (PdfStructureChecker.TopLevelObject original in streams)
        {
            Assert.True(after.TopLevel.TryGetValue(original.Number, out var copy), "stream object missing");
            Assert.Equal(original.Raw, copy!.Raw);
            Assert.Equal(original.Payload, copy.Payload);
        }
    }

    [Fact]
    public async Task Objects_inside_object_streams_are_generation_0_and_not_streams()
    {
        (string rewritten, string packed, _) = await PackSyntheticAsync();

        PdfStructureChecker.ClassicStructure before = PdfStructureChecker.ReadClassic(rewritten);
        PdfStructureChecker.PackedStructure after = PdfStructureChecker.Check(packed);

        Assert.NotEmpty(after.Packed);
        foreach (int number in after.Packed.Keys)
        {
            PdfStructureChecker.TopLevelObject original = before.Objects[number];
            Assert.Equal(0, original.Generation);
            Assert.False(original.IsStream);
            Assert.Equal(original.Dictionary, after.Packed[number]);
        }

        // Every non-stream generation-0 object of the input was packed; nothing else was.
        Assert.Equal(
            before.Objects.Values.Where(o => !o.IsStream && o.Generation == 0).Select(o => o.Number),
            after.Packed.Keys);
    }

    [Fact]
    public async Task Xref_stream_entries_cover_every_object_and_offsets_point_to_object_headers()
    {
        (string rewritten, string packed, _) = await PackSyntheticAsync();

        PdfStructureChecker.ClassicStructure before = PdfStructureChecker.ReadClassic(rewritten);
        PdfStructureChecker.PackedStructure after = PdfStructureChecker.Check(packed);

        // /Index covers 0..Size-1 and every input object is in use in the output with the same number.
        Assert.Equal(Enumerable.Range(0, after.Size), after.Entries.Keys);
        foreach (int number in before.Objects.Keys)
        {
            Assert.NotEqual(0, after.Entries[number].Type);
        }

        Assert.Equal(0, after.Entries[0].Type);
        Assert.Equal(after.Size - 1, after.XrefStreamNumber);
        Assert.All(after.TopLevel.Values, o => Assert.Equal(after.Entries[o.Number].Field2, o.Offset));
        int inUse = after.Entries.Values.Count(e => e.Type != 0);
        Assert.Equal(before.Objects.Count + after.ObjectStreams.Count + 1, inUse);
    }

    [Fact]
    public async Task Trailer_root_info_and_id_survive_in_the_xref_stream_dictionary()
    {
        (string rewritten, string packed, _) = await PackSyntheticAsync();

        string trailer = PdfStructureChecker.ReadClassic(rewritten).Trailer;
        PdfStructureChecker.PackedStructure after = PdfStructureChecker.Check(packed);

        Assert.Contains($"/Root {after.Root.Number} {after.Root.Generation} R", trailer, StringComparison.Ordinal);
        Assert.NotNull(after.InfoEntry);
        Assert.Contains(after.InfoEntry!, trailer, StringComparison.Ordinal);
        Assert.NotNull(after.IdEntry);
        Assert.Contains(
            after.IdEntry!.Replace(" ", string.Empty, StringComparison.Ordinal),
            trailer.Replace(" ", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<</T (a (b) \\( c) /H <48656C6C6F> /C (100% sure) % x>> endobj (\n>>\nendobj\n", false)]
    [InlineData("<</S (endobj) /N (nested (paren) stream) /E (\\\\) /X (\\)) >> stream\r\nxx", true)]
    [InlineData("<</Length 5>>stream\nabcde\nendstream\nendobj", true)]
    [InlineData("<</H <41 42>>>endobj", false)]
    [InlineData("[1 0 R (a]) <</K [/x]>>] endobj", false)]
    [InlineData("12 endobj", false)]
    [InlineData(" null\r\nendobj", false)]
    public void Dictionary_tokenizer_handles_parentheses_hex_strings_and_comments_in_strings(string body, bool isStream)
    {
        byte[] bytes = Encoding.Latin1.GetBytes("1 0 obj" + body);
        string keyword = isStream ? "stream" : "endobj";
        int expected = Encoding.Latin1.GetString(bytes).LastIndexOf(isStream ? ">>" : "endobj", StringComparison.Ordinal);
        if (isStream)
        {
            expected = Encoding.Latin1.GetString(bytes).IndexOf("stream", expected, StringComparison.Ordinal);
        }

        int found = PdfObjectScanner.FindBodyEnd(bytes, 7, out bool stream);

        Assert.Equal(isStream, stream);
        Assert.Equal(expected, found);
        Assert.Equal(keyword, Encoding.Latin1.GetString(bytes, found, keyword.Length));
    }

    [Theory]
    [InlineData("<</T (abc >>endobj")] // unterminated string
    [InlineData("<</T <4G> >>endobj")] // bad hex string
    [InlineData("<</T 1 >endobj")] // lone '>'
    [InlineData("<</T [1 2>> ]endobj")] // mismatched nesting
    [InlineData("<</T 1>>")] // no endobj
    [InlineData("<</T 1>> trailer endobj")] // unknown keyword
    [InlineData("[1 2] stream\n")] // stream after a non-dictionary
    [InlineData("<</A 1>> 2 0 obj endobj")] // two object headers
    public void Dictionary_tokenizer_rejects_malformed_bodies(string body)
    {
        byte[] bytes = Encoding.Latin1.GetBytes("1 0 obj" + body);

        Assert.Throws<PdfPackException>(() => PdfObjectScanner.FindBodyEnd(bytes, 7, out _));
    }

    [Fact]
    public async Task Structure_checker_accepts_a_packed_object_with_keywords_inside_a_literal_string()
    {
        string source = PathIn("keywords.pdf");
        TestPdfFactory.WriteWithFeatures(
            source,
            new TestPdfFactory.Features
            {
                Text = "keywords",
                Info = new Dictionary<string, string> { ["Title"] = "a endobj b endstream c >> stream d" },
            },
            [.. Enumerable.Range(0, 100).Select(i => (595.0, 842.0 - i))]);
        string rewritten = PathIn("keywords-rewritten.pdf");
        await PdfFullRewrite.RewriteAsync(source, rewritten);
        string packed = PathIn("keywords-packed.pdf");
        PdfObjectStreamPacker.Pack(rewritten, packed);

        PdfStructureChecker.PackedStructure structure = PdfStructureChecker.Check(packed);

        Assert.Contains(structure.Packed.Values, body => body.Contains("endobj", StringComparison.Ordinal));
    }

    [Fact]
    public void Structure_checker_rejects_stream_keywords_outside_strings()
    {
        Assert.False(PdfStructureChecker.IsPlainObjectBody("<</A (x) >> stream\r\nabc"));
        Assert.False(PdfStructureChecker.IsPlainObjectBody("<</A 1>> endobj"));
        Assert.False(PdfStructureChecker.IsPlainObjectBody("[1 2] endstream"));
        Assert.True(PdfStructureChecker.IsPlainObjectBody("<</A (x endobj (y) \\) >> stream) /B <656E646F626A> % endobj in a comment\r\n>>"));
        Assert.True(PdfStructureChecker.IsPlainObjectBody("<</Endobj 1 /streamer 2>>"));
    }

    [Fact]
    public async Task Structure_checker_requires_entry_0_to_be_the_free_list_head()
    {
        (_, string packed, _) = await PackSyntheticAsync();

        PdfStructureChecker.PackedStructure structure = PdfStructureChecker.Check(packed);

        Assert.Equal(new PdfStructureChecker.XrefEntry(0, 0, 65535), structure.Entries[0]);
    }

    [Fact]
    public async Task Input_that_already_has_an_xref_stream_is_rejected()
    {
        (_, string packed, _) = await PackSyntheticAsync();
        string again = PathIn("again.pdf");

        Assert.Throws<PdfPackException>(() => PdfObjectStreamPacker.Pack(packed, again));
        Assert.False(File.Exists(again));
    }

    [Theory]
    [InlineData("/Prev 0 ")]
    [InlineData("/XRefStm 10 ")]
    [InlineData("/Encrypt 1 0 R ")]
    [InlineData("/Type/XRef ")]
    public async Task Trailer_keys_that_change_the_file_structure_are_rejected(string key)
    {
        string rewritten = await RewrittenSyntheticAsync();
        string modified = PathIn("modified.pdf");
        byte[] bytes = File.ReadAllBytes(rewritten);
        string text = Encoding.Latin1.GetString(bytes);
        int at = text.LastIndexOf("trailer", StringComparison.Ordinal) + "trailer\r\n<<".Length;
        text = text[..at] + key + text[at..];
        File.WriteAllBytes(modified, Encoding.Latin1.GetBytes(text));

        string target = PathIn("out.pdf");
        Assert.Throws<PdfPackException>(() => PdfObjectStreamPacker.Pack(modified, target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Input_with_Encrypt_in_the_trailer_is_rejected()
    {
        string encrypted = PathIn("encrypted.pdf");
        TestPdfFactory.WriteEncryptedRestricted(encrypted, (595, 842));
        string target = PathIn("out.pdf");

        var error = Assert.Throws<PdfPackException>(() => PdfObjectStreamPacker.Pack(encrypted, target));
        Assert.Contains("encrypt", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Garbage_and_truncated_input_is_rejected_without_creating_an_output_file()
    {
        string rewritten = await RewrittenSyntheticAsync();
        byte[] bytes = File.ReadAllBytes(rewritten);
        string text = Encoding.Latin1.GetString(bytes);

        var inputs = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("not a pdf"),
            Array.Empty<byte>(),
            bytes[..(bytes.Length / 2)],
            bytes[..^10],
            // An incremental update appended after %%EOF (a second xref section).
            bytes.Concat(Encoding.ASCII.GetBytes("99 0 obj\r\nnull\r\nendobj\r\nxref\r\n99 1\r\n0000000000 00000 n\r\ntrailer\r\n<</Size 100/Prev 1>>\r\nstartxref\r\n0\r\n%%EOF\r\n")).ToArray(),
            // An xref offset that does not point at its object.
            Encoding.Latin1.GetBytes(ReplaceFirstXrefOffset(text, +1)),
            // Junk between two objects.
            Encoding.Latin1.GetBytes(text.Replace("endobj\r\n2 0 obj", "endobj\r\njunk\r\n2 0 obj", StringComparison.Ordinal)),
        };

        for (int i = 0; i < inputs.Count; i++)
        {
            string input = PathIn($"bad-{i}.pdf");
            File.WriteAllBytes(input, inputs[i]);
            string target = PathIn($"bad-{i}-out.pdf");

            var error = Record.Exception(() => PdfObjectStreamPacker.Pack(input, target));

            Assert.True(error is PdfPackException, $"input {i}: {error?.GetType().Name ?? "no exception"}");
            Assert.False(File.Exists(target), $"input {i} left an output file");
        }
    }

    private static string ReplaceFirstXrefOffset(string text, int delta)
    {
        int xref = text.LastIndexOf("\nxref", StringComparison.Ordinal);
        int entry = text.IndexOf("00000 n", xref, StringComparison.Ordinal) - 11;
        long offset = long.Parse(text.AsSpan(entry, 10), System.Globalization.CultureInfo.InvariantCulture);
        return text[..entry]
            + (offset + delta).ToString("D10", System.Globalization.CultureInfo.InvariantCulture)
            + text[(entry + 10)..];
    }

    [Fact]
    public async Task Input_with_no_gain_is_rejected_with_the_no_gain_message()
    {
        // One page whose bulk is a long incompressible-ish text stream: packing the few plain
        // objects cannot save 5%.
        var random = new Random(1234);
        string text = new([.. Enumerable.Range(0, 30000).Select(_ => (char)random.Next('A', 'Z' + 1))]);
        string source = PathIn("bulky.pdf");
        TestPdfFactory.WriteWithFeatures(source, new TestPdfFactory.Features { Text = text }, (595, 842));
        string rewritten = PathIn("bulky-rewritten.pdf");
        await PdfFullRewrite.RewriteAsync(source, rewritten);
        string target = PathIn("out.pdf");

        var error = Assert.Throws<PdfPackException>(() => PdfObjectStreamPacker.Pack(rewritten, target));

        Assert.Equal("no gain", error.Message);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Input_file_is_never_modified()
    {
        string rewritten = await RewrittenSyntheticAsync();
        byte[] hashBefore = SHA256.HashData(File.ReadAllBytes(rewritten));
        DateTime writeBefore = File.GetLastWriteTimeUtc(rewritten);

        PdfObjectStreamPacker.Pack(rewritten, PathIn("packed.pdf"));

        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(rewritten)));
        Assert.Equal(writeBefore, File.GetLastWriteTimeUtc(rewritten));
    }

    [Fact]
    public async Task Existing_output_file_is_neither_overwritten_nor_deleted()
    {
        string rewritten = await RewrittenSyntheticAsync();
        string target = PathIn("exists.pdf");
        File.WriteAllText(target, "keep me");

        Assert.Throws<IOException>(() => PdfObjectStreamPacker.Pack(rewritten, target));

        Assert.Equal("keep me", File.ReadAllText(target));
    }

    [Fact]
    public async Task Cancellation_leaves_no_output_file()
    {
        string rewritten = await RewrittenSyntheticAsync();
        string target = PathIn("out.pdf");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => PdfObjectStreamPacker.Pack(rewritten, target, cts.Token));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Every_object_stream_and_flate_stream_decompresses_with_System_IO_Compression()
    {
        (_, string packed, _) = await PackSyntheticAsync();
        PdfStructureChecker.PackedStructure after = PdfStructureChecker.Check(packed);

        int inflated = 0;
        foreach (PdfStructureChecker.TopLevelObject obj in after.TopLevel.Values.Where(o => o.IsStream))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(obj.Dictionary, @"/Filter\s*(\[\s*)?/FlateDecode"))
            {
                _ = PdfStructureChecker.Inflate(obj.Payload);
                inflated++;
            }
        }

        Assert.True(inflated > after.ObjectStreams.Count);
    }

    /// <summary>The largest real PDF (copied), or null when none is available.</summary>
    private string? CopyLargestRealDocument()
    {
        string folder = Environment.GetEnvironmentVariable("DREAMBOOKS_FIXTURES") is { Length: > 0 } env
            ? env
            : _defaultFixturesFolder;
        FileInfo? largest = Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles("*.pdf").OrderByDescending(f => f.Length).FirstOrDefault()
            : null;
        if (largest is null)
        {
            // xUnit 2 has no runtime skip: report it and pass.
            output.WriteLine("SKIPPED: no real PDF available.");
            return null;
        }

        string copy = PathIn("real-copy.pdf");
        largest.CopyTo(copy);
        return copy;
    }

    [Fact]
    public async Task Packed_real_ebook_copy_is_at_least_20_percent_smaller_and_faithful_and_PDFium_renders_it()
    {
        string? copy = CopyLargestRealDocument();
        if (copy is null)
        {
            return;
        }

        string rewritten = PathIn("real-rewritten.pdf");
        string packed = PathIn("real-packed.pdf");
        await PdfFullRewrite.RewriteAsync(copy, rewritten);

        PackResult result = PdfObjectStreamPacker.Pack(rewritten, packed);

        output.WriteLine($"original {new FileInfo(copy).Length} bytes, rewritten {result.InputBytes} bytes, packed {result.OutputBytes} bytes");
        output.WriteLine($"{result.PackedObjects} objects packed into {result.ObjectStreams} object streams");
        Assert.True(result.OutputBytes < result.InputBytes * 0.8, "packed output is not 20% smaller");

        PdfStructureChecker.PackedStructure structure = PdfStructureChecker.Check(packed);
        Assert.Equal(result.PackedObjects, structure.Packed.Count);
        foreach (PdfStructureChecker.ObjectStream objStm in structure.ObjectStreams.Values)
        {
            Assert.Equal(objStm.Numbers.Order(), objStm.Numbers);
        }

        FidelityReport report = await PdfiumThread.RunAsync(
            () => PdfFidelity.Compare(rewritten, packed), CancellationToken.None);
        foreach (string line in report.Summary)
        {
            output.WriteLine($"  info: {line}");
        }

        Assert.True(report.IsFaithful, string.Join("; ", report.Differences));
    }

    [Fact]
    public async Task Packing_the_real_ebook_copy_stays_within_memory_and_time_limits()
    {
        string? copy = CopyLargestRealDocument();
        if (copy is null)
        {
            return;
        }

        string rewritten = PathIn("real-rewritten.pdf");
        await PdfFullRewrite.RewriteAsync(copy, rewritten);
        long size = new FileInfo(rewritten).Length;

        // Pack runs synchronously on this thread: count only this thread's allocations, so tests
        // running in parallel do not disturb the measurement.
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        PdfObjectStreamPacker.Pack(rewritten, PathIn("real-packed.pdf"));
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        output.WriteLine($"input {size} bytes, {watch.ElapsedMilliseconds} ms, {allocated} bytes allocated");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "packing took 20 s or more");
        Assert.True(allocated < 8 * size, "packing allocated 8x the file size or more");
    }

    /// <summary>
    /// Runs <paramref name="cycles"/> save cycles (PDFium full rewrite, then pack) starting from
    /// <paramref name="source"/>; each cycle starts from the previous packed file. Returns the
    /// /Size of every packed file, each one validated by the independent checker.
    /// </summary>
    private List<int> RewriteAndPackRepeatedly(string source, int cycles)
    {
        var sizes = new List<int>();
        string current = source;
        for (int i = 0; i < cycles; i++)
        {
            string rewritten = PathIn($"cycle-{i}-rewritten.pdf");
            string packed = PathIn($"cycle-{i}-packed.pdf");
            PdfFullRewrite.RewriteAsync(current, rewritten).GetAwaiter().GetResult();
            PdfObjectStreamPacker.Pack(rewritten, packed);
            PdfStructureChecker.PackedStructure structure = PdfStructureChecker.Check(packed);

            // PDFium copies the previous xref stream as a dead object: only the live one may remain.
            Assert.Single(structure.TopLevel.Values, o => o.IsStream
                && System.Text.RegularExpressions.Regex.IsMatch(o.Dictionary, @"/Type\s*/XRef\b"));
            sizes.Add(structure.Size);
            current = packed;
        }

        return sizes;
    }

    [Fact]
    public async Task Size_does_not_grow_over_repeated_rewrite_and_pack_cycles() // NFR-20, FR-81
    {
        string rewritten = await RewrittenSyntheticAsync();

        List<int> sizes = await Task.Run(() => RewriteAndPackRepeatedly(rewritten, 4));

        output.WriteLine("/Size per cycle: " + string.Join(", ", sizes));
        Assert.All(sizes.Skip(1), size => Assert.Equal(sizes[1], size));
        Assert.Equal(sizes[0], sizes[1]);
    }

    [Fact]
    public async Task Size_of_the_real_ebook_copy_does_not_grow_over_repeated_cycles() // NFR-20, FR-81
    {
        string? copy = CopyLargestRealDocument();
        if (copy is null)
        {
            return;
        }

        List<int> sizes = await Task.Run(() => RewriteAndPackRepeatedly(copy, 3));

        output.WriteLine("/Size per cycle: " + string.Join(", ", sizes));
        Assert.Equal(sizes[0], sizes[2]);
        Assert.Equal(sizes[0], sizes[1]);
    }

    private const int _xrefTypedNumber = 200;

    private const string _xrefTypedStream =
        "<</Length 3/Size 3/Type/XRef/W[ 1 1 1]>>stream\r\nabc\r\nendstream";

    /// <summary>
    /// Writes a file in PDFium's full-rewrite shape (CRLF, "N 0 obj", one classic xref table with
    /// a subsection per run of numbers, trailer, startxref, %%EOF). Object 1 is the catalog,
    /// 2 the page tree, 3..152 filler dictionaries (so packing gains well over 5%), plus
    /// <paramref name="extra"/>. <paramref name="catalogExtra"/> and <paramref name="trailerExtra"/>
    /// are appended inside the catalog and trailer dictionaries.
    /// </summary>
    private static void WritePdfiumShaped(
        string path, IEnumerable<(int Number, string Body)> extra, string catalogExtra = "", string trailerExtra = "")
    {
        var objects = new SortedDictionary<int, string>
        {
            [1] = $"<</Pages 2 0 R /Type/Catalog{catalogExtra}>>",
            [2] = "<</Count 0/Kids[]/Type/Pages>>",
        };
        for (int n = 3; n <= 152; n++)
        {
            objects[n] = $"<</Filler {n}/Name(filler object number {n} with some repeated text)/Next {n + 1} 0 R >>";
        }

        foreach ((int number, string body) in extra)
        {
            objects[number] = body;
        }

        var file = new StringBuilder("%PDF-1.7\r\n%¡³Å×\r\n");
        var offsets = new SortedDictionary<int, int>();
        foreach ((int number, string body) in objects)
        {
            offsets[number] = file.Length;
            file.Append(System.Globalization.CultureInfo.InvariantCulture, $"{number} 0 obj\r\n{body}\r\nendobj\r\n");
        }

        int xref = file.Length;
        file.Append("xref\r\n0 1\r\n0000000000 65535 f\r\n");
        int[] numbers = [.. offsets.Keys];
        for (int i = 0; i < numbers.Length;)
        {
            int j = i;
            while (j + 1 < numbers.Length && numbers[j + 1] == numbers[j] + 1)
            {
                j++;
            }

            file.Append(System.Globalization.CultureInfo.InvariantCulture, $"{numbers[i]} {j - i + 1}\r\n");
            for (int k = i; k <= j; k++)
            {
                file.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offsets[numbers[k]]:D10} 00000 n\r\n");
            }

            i = j + 1;
        }

        file.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"trailer\r\n<</Root 1 0 R /Size {numbers[^1] + 1}{trailerExtra}>>\r\nstartxref\r\n{xref}\r\n%%EOF\r\n");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(file.ToString()));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("trailer")]
    [InlineData("stream dictionary")]
    public void A_referenced_xref_typed_stream_is_kept_byte_for_byte(string referencedFrom) // NFR-20
    {
        string input = PathIn("referenced.pdf");
        var extra = new List<(int, string)> { (_xrefTypedNumber, _xrefTypedStream) };
        string catalogExtra = string.Empty;
        string trailerExtra = string.Empty;
        switch (referencedFrom)
        {
            case "catalog":
                catalogExtra = $"/AF[ {_xrefTypedNumber} 0 R]";
                break;
            case "trailer":
                trailerExtra = $"/Custom {_xrefTypedNumber} 0 R ";
                break;
            default:
                extra.Add((160, $"<</Length 4/Ref {_xrefTypedNumber} 0 R >>stream\r\nabcd\r\nendstream"));
                break;
        }

        WritePdfiumShaped(input, extra, catalogExtra, trailerExtra);
        PdfStructureChecker.TopLevelObject original = PdfStructureChecker.ReadClassic(input).Objects[_xrefTypedNumber];
        string packed = PathIn("referenced-packed.pdf");

        PdfObjectStreamPacker.Pack(input, packed);

        PdfStructureChecker.PackedStructure after = PdfStructureChecker.Check(packed);
        Assert.True(after.TopLevel.TryGetValue(_xrefTypedNumber, out var kept), "referenced object was dropped");
        Assert.Equal(original.Raw, kept!.Raw);
        Assert.True(after.XrefStreamNumber > _xrefTypedNumber, "a new object reuses the kept object's number");
        Assert.All(after.ObjectStreams.Keys, n => Assert.True(n > _xrefTypedNumber));
        Assert.Equal(after.XrefStreamNumber + 1, after.Size);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("comment")]
    [InlineData("payload")]
    [InlineData("none")]
    public void An_unreferenced_leftover_xref_stream_is_dropped(string mention) // NFR-20
    {
        // A number written inside a string, a comment or a stream payload is not a reference.
        string input = PathIn("leftover.pdf");
        var extra = new List<(int, string)> { (_xrefTypedNumber, _xrefTypedStream) };
        string catalogExtra = mention switch
        {
            "string" => $"/T({_xrefTypedNumber} 0 R)",
            "comment" => $"/T 1 % {_xrefTypedNumber} 0 R\r\n",
            _ => string.Empty,
        };
        if (mention == "payload")
        {
            string payload = $"{_xrefTypedNumber} 0 R";
            extra.Add((160, $"<</Length {payload.Length}>>stream\r\n{payload}\r\nendstream"));
        }

        WritePdfiumShaped(input, extra, catalogExtra);
        string packed = PathIn("leftover-packed.pdf");

        PdfObjectStreamPacker.Pack(input, packed);

        PdfStructureChecker.PackedStructure after = PdfStructureChecker.Check(packed);
        Assert.False(after.TopLevel.ContainsKey(_xrefTypedNumber));
        Assert.Single(after.TopLevel.Values, o => o.IsStream && Regex.IsMatch(o.Dictionary, @"/Type\s*/XRef\b"));
        int highestKept = mention == "payload" ? 160 : 152;
        Assert.Equal(highestKept + 1, after.ObjectStreams.Keys.Min());
    }

    [Fact]
    public void Three_xref_typed_streams_are_rejected_without_an_output_file() // NFR-20
    {
        string input = PathIn("three.pdf");
        WritePdfiumShaped(input, [(200, _xrefTypedStream), (201, _xrefTypedStream), (202, _xrefTypedStream)]);
        string target = PathIn("three-packed.pdf");

        Assert.Throws<PdfPackException>(() => PdfObjectStreamPacker.Pack(input, target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void A_stream_with_a_nested_Type_XRef_is_not_a_leftover_xref_stream() // NFR-20
    {
        string input = PathIn("nested.pdf");
        WritePdfiumShaped(input, [(_xrefTypedNumber, "<</Inner<</Type/XRef>>/Length 3>>stream\r\nabc\r\nendstream")]);
        PdfStructureChecker.TopLevelObject original = PdfStructureChecker.ReadClassic(input).Objects[_xrefTypedNumber];
        string packed = PathIn("nested-packed.pdf");

        PdfObjectStreamPacker.Pack(input, packed);

        PdfStructureChecker.PackedStructure after = PdfStructureChecker.Check(packed);
        Assert.Equal(original.Raw, after.TopLevel[_xrefTypedNumber].Raw);
    }
}
