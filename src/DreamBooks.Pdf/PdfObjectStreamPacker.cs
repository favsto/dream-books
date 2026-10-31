using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace DreamBooks.Pdf;

/// <summary>
/// Re-packs PDFium's regular full-rewrite output into a smaller, equivalent PDF 1.5 file
/// (NFR-20, FR-80, FR-81): the plain generation-0 objects go into Flate-compressed object streams
/// (ISO 32000-1 7.5.7) indexed by a cross-reference stream (7.5.8); stream objects are copied
/// byte for byte and keep their object numbers. The input must be exactly the shape PDFium
/// writes (header, "N G obj ... endobj" objects, one classic xref table, trailer, startxref,
/// %%EOF); anything else throws <see cref="PdfPackException"/>. The input file is only read; the
/// output is created new and deleted on any failure. Pure byte work: runs on any thread.
/// </summary>
internal static class PdfObjectStreamPacker
{
    /// <summary>Inputs above this size are refused (the whole file is held in memory).</summary>
    internal const long MaxInputBytes = 512L * 1024 * 1024;

    /// <summary>Most objects stored in one object stream.</summary>
    internal const int MaxObjectsPerStream = 100;

    /// <summary>The output must be at least this fraction smaller than the input.</summary>
    internal const double MinimumGain = 0.05;

    private const int _cancellationCheckInterval = 256;

    private static readonly byte[] _eol = "\r\n"u8.ToArray();
    private static readonly byte[] _defaultBinaryMarker = [(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\r', (byte)'\n'];

    // Trailer keys that would change the file structure, or clash with the xref stream dictionary.
    private static readonly string[] _refusedTrailerKeys =
    [
        "Prev", "XRefStm", "Encrypt", "Type", "W", "Index", "Filter", "DecodeParms", "Length",
        "F", "FFilter", "FDecodeParms", "DL", "N", "First", "Extends",
    ];

    /// <summary>
    /// Packs <paramref name="inputPath"/> into the new file <paramref name="outputPath"/> (which
    /// must not exist). Throws <see cref="PdfPackException"/> when the input is not a shape this
    /// packer fully understands or when the output would not be at least 5% smaller,
    /// <see cref="OperationCanceledException"/> on cancellation, <see cref="IOException"/> on
    /// I/O errors; in every failure case no output file is left behind.
    /// </summary>
    public static PackResult Pack(string inputPath, string outputPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ct.ThrowIfCancellationRequested();

        byte[] data = ReadInput(inputPath);
        ParsedFile parsed;
        try
        {
            parsed = Parse(data, ct);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Defensive: a malformed input ran a slice past the end of the data.
            throw new PdfPackException("input is malformed");
        }
        catch (IndexOutOfRangeException)
        {
            throw new PdfPackException("input is malformed");
        }

        var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        bool succeeded = false;
        try
        {
            (int packedObjects, int objectStreams) result;
            using (file)
            {
                result = Write(data, parsed, file, ct);
                file.Flush(flushToDisk: true);
            }

            long outputBytes = new FileInfo(outputPath).Length;
            if (outputBytes > data.Length * (1 - MinimumGain))
            {
                throw new PdfPackException("no gain");
            }

            succeeded = true;
            return new PackResult(data.Length, outputBytes, result.packedObjects, result.objectStreams);
        }
        finally
        {
            if (!succeeded)
            {
                TryDelete(outputPath);
            }
        }
    }

    private static byte[] ReadInput(string inputPath)
    {
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = input.Length;
        if (length > MaxInputBytes)
        {
            throw new PdfPackException("input is too large to pack");
        }

        var data = new byte[length];
        input.ReadExactly(data);
        return data;
    }

    // ---------------------------------------------------------------- parsing

    private static ParsedFile Parse(byte[] data, CancellationToken ct)
    {
        ReadOnlySpan<byte> span = data;

        // Header "%PDF-x.y" + EOL, then an optional binary-marker comment line.
        if (span.Length < 16 || !span.StartsWith("%PDF-"u8) || !char.IsAsciiDigit((char)span[5])
            || span[6] != '.' || !char.IsAsciiDigit((char)span[7]))
        {
            throw new PdfPackException("input has no PDF header");
        }

        int version = ((span[5] - '0') * 10) + (span[7] - '0');
        int headerEnd = SkipEol(span, 8);
        if (headerEnd == 8)
        {
            throw new PdfPackException("header line has unexpected content");
        }

        int bodyStart = headerEnd;
        byte[]? binaryMarker = null;
        if (span[headerEnd] == '%')
        {
            int lineEnd = headerEnd;
            while (lineEnd < span.Length && span[lineEnd] != '\r' && span[lineEnd] != '\n')
            {
                lineEnd++;
            }

            bodyStart = SkipEol(span, lineEnd);
            binaryMarker = data[headerEnd..bodyStart];
        }

        // Tail: "startxref" EOL offset EOL "%%EOF" [EOL], nothing else.
        int startXrefAt = span.LastIndexOf("startxref"u8);
        if (startXrefAt < 0)
        {
            throw new PdfPackException("input has no startxref");
        }

        int t = SkipWhitespace(span, startXrefAt + 9);
        long xrefOffset = ReadInteger(span, ref t);
        t = SkipWhitespace(span, t);
        if (!span[t..].StartsWith("%%EOF"u8) || SkipWhitespace(span, t + 5) != span.Length)
        {
            throw new PdfPackException("input does not end with a single %%EOF");
        }

        if (xrefOffset <= bodyStart || xrefOffset >= startXrefAt
            || !span[(int)xrefOffset..].StartsWith("xref"u8)
            || !PdfObjectScanner.IsWhitespace(span[(int)xrefOffset + 4]))
        {
            throw new PdfPackException("startxref does not point at a classic xref table");
        }

        // The classic xref table.
        var inUse = new List<XrefRow>();
        var free = new List<XrefRow>();
        int p = SkipWhitespace(span, (int)xrefOffset + 4);
        while (!span[p..].StartsWith("trailer"u8))
        {
            long first = ReadInteger(span, ref p);
            ExpectSpaces(span, ref p);
            long count = ReadInteger(span, ref p);
            p = SkipWhitespace(span, p);
            if (first < 0 || count <= 0 || first + count > int.MaxValue)
            {
                throw new PdfPackException("xref subsection header is invalid");
            }

            for (long n = 0; n < count; n++)
            {
                if (p + 20 > startXrefAt)
                {
                    throw new PdfPackException("xref table is truncated");
                }

                long offset = ReadFixedDigits(span, p, 10);
                long generation = ReadFixedDigits(span, p + 11, 5);
                byte kind = span[p + 17];
                ReadOnlySpan<byte> eol = span.Slice(p + 18, 2);
                if (span[p + 10] != ' ' || span[p + 16] != ' ' || (kind != 'n' && kind != 'f')
                    || !(eol.SequenceEqual(" \r"u8) || eol.SequenceEqual(" \n"u8) || eol.SequenceEqual("\r\n"u8)))
                {
                    throw new PdfPackException("xref entry is malformed");
                }

                var row = new XrefRow((int)(first + n), offset, (int)generation);
                (kind == 'n' ? inUse : free).Add(row);
                p += 20;
            }

            p = SkipWhitespace(span, p);
        }

        // The trailer dictionary, directly followed by startxref.
        p = SkipWhitespace(span, p + 7);
        List<TrailerEntry> trailer = ParseDictionary(span, ref p);
        if (SkipWhitespace(span, p) != startXrefAt)
        {
            throw new PdfPackException("unexpected content after the trailer");
        }

        int size = -1;
        int rootNumber = -1;
        var copiedEntries = new List<TrailerEntry>();
        foreach (TrailerEntry entry in trailer)
        {
            string key = Encoding.ASCII.GetString(span[entry.KeyStart..entry.KeyEnd]);
            if (Array.IndexOf(_refusedTrailerKeys, key) >= 0)
            {
                throw new PdfPackException(key == "Encrypt"
                    ? "input is encrypted"
                    : "trailer has a key that changes the file structure");
            }

            if (key == "Size")
            {
                int s = entry.ValueStart;
                long value = ReadInteger(span, ref s);
                if (s != entry.ValueEnd || value <= 0 || value >= int.MaxValue / 2)
                {
                    throw new PdfPackException("trailer /Size is invalid");
                }

                size = (int)value;
                continue;
            }

            if (key == "Root")
            {
                rootNumber = ReadReference(span, entry);
            }

            copiedEntries.Add(entry);
        }

        if (size < 0 || rootNumber < 0)
        {
            throw new PdfPackException("trailer has no /Size or /Root");
        }

        // Every listed object number is below /Size and listed once.
        var listed = new bool[size];
        foreach (XrefRow row in inUse.Concat(free))
        {
            if (row.Number >= size || listed[row.Number] || (row.Number == 0 && inUse.Contains(row)))
            {
                throw new PdfPackException("xref table lists an object twice or beyond /Size");
            }

            listed[row.Number] = true;
        }

        if (!inUse.Any(r => r.Number == rootNumber))
        {
            throw new PdfPackException("trailer /Root is not an object in use");
        }

        // Objects: sorted by offset, they must tile the body exactly.
        inUse.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        if (inUse.Count == 0 || SkipWhitespace(span, bodyStart) != inUse[0].Offset)
        {
            throw new PdfPackException("unexpected content before the first object");
        }

        var objects = new List<ObjectInfo>(inUse.Count);
        for (int k = 0; k < inUse.Count; k++)
        {
            if (k % _cancellationCheckInterval == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            XrefRow row = inUse[k];
            long sliceEnd = k + 1 < inUse.Count ? inUse[k + 1].Offset : xrefOffset;
            if (sliceEnd <= row.Offset)
            {
                throw new PdfPackException("xref offsets overlap");
            }

            objects.Add(ParseObject(span[..(int)sliceEnd], row));
        }

        objects.Sort((a, b) => a.Number.CompareTo(b.Number));
        KeepReferencedXrefStreams(span, objects, copiedEntries, ct);
        return new ParsedFile(version, data[..8], data[8..headerEnd], binaryMarker, size,
            objects, free, copiedEntries);
    }

    /// <summary>
    /// A leftover cross-reference stream (see <see cref="IsXrefStreamDictionary"/>) is dropped only
    /// if nothing refers to it: every object's dictionary part (the body of a plain object, the
    /// dictionary of a stream object, never stream data) and every trailer value is scanned for an
    /// "N G R" reference outside strings and comments. A referenced one is kept as an ordinary
    /// stream object. More than two candidates is not a shape PDFium's rewrite produces: refused.
    /// </summary>
    private static void KeepReferencedXrefStreams(
        ReadOnlySpan<byte> span, List<ObjectInfo> objects, List<TrailerEntry> trailer, CancellationToken ct)
    {
        var candidates = new HashSet<int>(objects.Where(o => o.StaleXrefStream).Select(o => o.Number));
        if (candidates.Count == 0)
        {
            return;
        }

        if (candidates.Count > 2)
        {
            throw new PdfPackException("input has more than two cross-reference streams");
        }

        var referenced = new HashSet<int>();
        var found = new HashSet<int>();
        for (int k = 0; k < objects.Count; k++)
        {
            if (k % _cancellationCheckInterval == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            ObjectInfo obj = objects[k];
            found.Clear();
            PdfObjectScanner.FindReferences(span, obj.BodyStart, obj.DictionaryEnd, candidates, found);
            found.Remove(obj.Number); // a reference to itself does not keep an object alive
            referenced.UnionWith(found);
        }

        foreach (TrailerEntry entry in trailer)
        {
            PdfObjectScanner.FindReferences(span, entry.ValueStart, entry.ValueEnd, candidates, referenced);
        }

        for (int k = 0; k < objects.Count; k++)
        {
            if (objects[k].StaleXrefStream && referenced.Contains(objects[k].Number))
            {
                objects[k] = objects[k] with { StaleXrefStream = false };
            }
        }
    }

    /// <summary>Parses the object at <paramref name="row"/>; <paramref name="slice"/> ends where the next one starts.</summary>
    private static ObjectInfo ParseObject(ReadOnlySpan<byte> slice, XrefRow row)
    {
        int p = (int)row.Offset;
        long number = ReadInteger(slice, ref p);
        ExpectSpaces(slice, ref p);
        long generation = ReadInteger(slice, ref p);
        ExpectSpaces(slice, ref p);
        if (number != row.Number || generation != row.Generation || !slice[p..].StartsWith("obj"u8))
        {
            throw new PdfPackException("xref offset does not point at its object");
        }

        int bodyStart = p + 3;
        if (bodyStart >= slice.Length
            || (!PdfObjectScanner.IsWhitespace(slice[bodyStart]) && !PdfObjectScanner.IsDelimiter(slice[bodyStart])))
        {
            throw new PdfPackException("object header is malformed");
        }

        int keyword = PdfObjectScanner.FindBodyEnd(slice, bodyStart, out bool isStream);
        int endobj;
        if (isStream)
        {
            int dataStart = keyword + 6;
            if (slice[dataStart..].StartsWith("\r\n"u8))
            {
                dataStart += 2;
            }
            else if (slice[dataStart..].StartsWith("\n"u8))
            {
                dataStart++;
            }
            else
            {
                throw new PdfPackException("stream keyword is not followed by an end of line");
            }

            endobj = slice[dataStart..].LastIndexOf("endobj"u8);
            if (endobj < 0)
            {
                throw new PdfPackException("stream object has no endobj");
            }

            endobj += dataStart;
            int q = endobj;
            while (q > dataStart && PdfObjectScanner.IsWhitespace(slice[q - 1]))
            {
                q--;
            }

            if (q - 9 < dataStart || !slice[(q - 9)..q].SequenceEqual("endstream"u8))
            {
                throw new PdfPackException("stream object has no endstream");
            }
        }
        else
        {
            endobj = keyword;
        }

        int objectEnd = endobj + 6;
        if (SkipWhitespace(slice, objectEnd) != slice.Length)
        {
            throw new PdfPackException("unexpected content between objects");
        }

        int bodyFrom = SkipWhitespace(slice, bodyStart);
        int bodyTo = endobj;
        while (bodyTo > bodyFrom && PdfObjectScanner.IsWhitespace(slice[bodyTo - 1]))
        {
            bodyTo--;
        }

        if (bodyTo == bodyFrom)
        {
            throw new PdfPackException("object body is empty");
        }

        bool packable = !isStream && row.Generation == 0
            && !(slice[bodyFrom..bodyTo].StartsWith("<<"u8) && slice[bodyFrom..bodyTo].IndexOf("/Linearized"u8) >= 0);
        bool staleXrefStream = isStream && IsXrefStreamDictionary(slice, bodyFrom);
        return new ObjectInfo(
            row.Number, row.Generation, (int)row.Offset, objectEnd, bodyFrom, bodyTo, isStream ? keyword : bodyTo, packable, staleXrefStream);
    }

    /// <summary>
    /// True when the stream dictionary at <paramref name="p"/> has a top-level <c>/Type /XRef</c>.
    /// PDFium's full rewrite of a file with a cross-reference stream copies that (no longer used)
    /// stream as an ordinary object; nothing refers to it, so the packer drops it.
    /// </summary>
    private static bool IsXrefStreamDictionary(ReadOnlySpan<byte> slice, int p)
    {
        foreach (TrailerEntry entry in ParseDictionary(slice, ref p))
        {
            if (slice[entry.KeyStart..entry.KeyEnd].SequenceEqual("Type"u8)
                && slice[entry.ValueStart..entry.ValueEnd].SequenceEqual("/XRef"u8))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses "&lt;&lt; /Key value ... &gt;&gt;" at <paramref name="p"/> and leaves <paramref name="p"/> after it.</summary>
    private static List<TrailerEntry> ParseDictionary(ReadOnlySpan<byte> span, ref int p)
    {
        if (!span[p..].StartsWith("<<"u8))
        {
            throw new PdfPackException("trailer is not a dictionary");
        }

        var entries = new List<TrailerEntry>();
        p += 2;
        while (true)
        {
            p = PdfObjectScanner.SkipWhitespaceAndComments(span, p);
            if (p >= span.Length)
            {
                throw new PdfPackException("trailer is not terminated");
            }

            if (span[p..].StartsWith(">>"u8))
            {
                p += 2;
                return entries;
            }

            if (span[p] != '/')
            {
                throw new PdfPackException("trailer key is not a name");
            }

            int keyStart = p + 1;
            int keyEnd = PdfObjectScanner.SkipRegular(span, keyStart);
            int valueStart = PdfObjectScanner.SkipWhitespaceAndComments(span, keyEnd);
            int valueEnd = SkipValue(span, valueStart, depth: 0);
            entries.Add(new TrailerEntry(keyStart, keyEnd, valueStart, valueEnd));
            p = valueEnd;
        }
    }

    /// <summary>Index just past the one PDF value (with "N G R" references) starting at <paramref name="p"/>.</summary>
    private static int SkipValue(ReadOnlySpan<byte> span, int p, int depth)
    {
        if (depth > 32 || p >= span.Length)
        {
            throw new PdfPackException("trailer value is malformed");
        }

        byte c = span[p];
        if (c == '(')
        {
            return PdfObjectScanner.SkipLiteralString(span, p);
        }

        if (span[p..].StartsWith("<<"u8))
        {
            p += 2;
            while (true)
            {
                p = PdfObjectScanner.SkipWhitespaceAndComments(span, p);
                if (span[p..].StartsWith(">>"u8))
                {
                    return p + 2;
                }

                p = SkipValue(span, p, depth + 1);
            }
        }

        if (c == '<')
        {
            return PdfObjectScanner.SkipHexString(span, p);
        }

        if (c == '[')
        {
            p++;
            while (true)
            {
                p = PdfObjectScanner.SkipWhitespaceAndComments(span, p);
                if (p < span.Length && span[p] == ']')
                {
                    return p + 1;
                }

                p = SkipValue(span, p, depth + 1);
            }
        }

        if (c == '/')
        {
            return PdfObjectScanner.SkipRegular(span, p + 1);
        }

        int end = PdfObjectScanner.SkipRegular(span, p);
        ReadOnlySpan<byte> token = span[p..end];
        if (token.SequenceEqual("true"u8) || token.SequenceEqual("false"u8) || token.SequenceEqual("null"u8))
        {
            return end;
        }

        if (!PdfObjectScanner.IsNumber(token))
        {
            throw new PdfPackException("trailer value is malformed");
        }

        // "N G R" is one value.
        int q = PdfObjectScanner.SkipWhitespaceAndComments(span, end);
        int genEnd = PdfObjectScanner.SkipRegular(span, q);
        if (genEnd > q && IsUnsignedInteger(span[q..genEnd]) && IsUnsignedInteger(token))
        {
            int r = PdfObjectScanner.SkipWhitespaceAndComments(span, genEnd);
            int rEnd = PdfObjectScanner.SkipRegular(span, r);
            if (span[r..rEnd].SequenceEqual("R"u8))
            {
                return rEnd;
            }
        }

        return end;
    }

    private static int ReadReference(ReadOnlySpan<byte> span, TrailerEntry entry)
    {
        int p = entry.ValueStart;
        long number = ReadInteger(span, ref p);
        p = SkipWhitespace(span, p);
        long generation = ReadInteger(span, ref p);
        p = SkipWhitespace(span, p);
        if (generation != 0 || p + 1 != entry.ValueEnd || span[p] != 'R' || number > int.MaxValue)
        {
            throw new PdfPackException("trailer reference is invalid");
        }

        return (int)number;
    }

    // ---------------------------------------------------------------- writing

    private static (int PackedObjects, int ObjectStreams) Write(
        byte[] data, ParsedFile parsed, FileStream output, CancellationToken ct)
    {
        var writer = new CountingWriter(output);

        // Header: at least PDF 1.5 (object and cross-reference streams), keep a higher version.
        writer.Write(parsed.Version >= 15 ? parsed.HeaderVersion : "%PDF-1.5"u8);
        writer.Write(parsed.HeaderEol);
        writer.Write(parsed.BinaryMarker ?? _defaultBinaryMarker);

        var packable = parsed.Objects.Where(o => o.Packable).ToList();
        int streamCount = (packable.Count + MaxObjectsPerStream - 1) / MaxObjectsPerStream;
        // New objects are numbered right after the highest object written, not after the input's
        // /Size: PDFium's rewrite of a packed file keeps the old object stream numbers as free
        // entries and the old xref stream as a dead object (dropped here), and numbering above
        // them would grow /Size on every save. A full rewrite has no history, so that free tail
        // can be dropped.
        int firstStreamNumber = parsed.Objects.Where(o => !o.StaleXrefStream).Select(o => o.Number).DefaultIfEmpty(0).Max() + 1;
        int xrefNumber = firstStreamNumber + streamCount;
        int newSize = xrefNumber + 1;

        // Cross-reference rows: absent numbers default to free (type 0, next 0, generation 0).
        var types = new byte[newSize];
        var field2 = new long[newSize];
        var field3 = new int[newSize];
        foreach (XrefRow row in parsed.Free.Where(r => r.Number < firstStreamNumber))
        {
            field2[row.Number] = row.Offset < firstStreamNumber ? row.Offset : 0;
            field3[row.Number] = row.Generation;
        }

        // Objects that stay top-level are copied byte for byte, in object-number order.
        int written = 0;
        foreach (ObjectInfo obj in parsed.Objects)
        {
            if (++written % _cancellationCheckInterval == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (!obj.Packable && !obj.StaleXrefStream)
            {
                types[obj.Number] = 1;
                field2[obj.Number] = writer.Position;
                field3[obj.Number] = obj.Generation;
                writer.Write(data.AsSpan(obj.Start, obj.End - obj.Start));
                writer.Write(_eol);
            }
        }

        // Object streams: up to MaxObjectsPerStream objects each, in ascending object number.
        using var compressed = new MemoryStream();
        var header = new StringBuilder();
        for (int s = 0; s < streamCount; s++)
        {
            ct.ThrowIfCancellationRequested();
            int streamNumber = firstStreamNumber + s;
            List<ObjectInfo> chunk = packable.GetRange(
                s * MaxObjectsPerStream, Math.Min(MaxObjectsPerStream, packable.Count - (s * MaxObjectsPerStream)));

            header.Clear();
            int relative = 0;
            for (int i = 0; i < chunk.Count; i++)
            {
                ObjectInfo obj = chunk[i];
                header.Append(CultureInfo.InvariantCulture, $"{obj.Number} {relative}").Append(i + 1 < chunk.Count ? ' ' : '\n');
                relative += obj.BodyEnd - obj.BodyStart + 1;
                types[obj.Number] = 2;
                field2[obj.Number] = streamNumber;
                field3[obj.Number] = i;
            }

            byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            compressed.SetLength(0);
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(headerBytes);
                foreach (ObjectInfo obj in chunk)
                {
                    zlib.Write(data.AsSpan(obj.BodyStart, obj.BodyEnd - obj.BodyStart));
                    zlib.WriteByte((byte)'\n');
                }
            }

            types[streamNumber] = 1;
            field2[streamNumber] = writer.Position;
            writer.WriteAscii(string.Create(CultureInfo.InvariantCulture,
                $"{streamNumber} 0 obj\r\n<</Type/ObjStm/N {chunk.Count}/First {headerBytes.Length}/Filter/FlateDecode/Length {compressed.Length}>>stream\r\n"));
            writer.Write(compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
            writer.WriteAscii("\r\nendstream\r\nendobj\r\n");
        }

        // The cross-reference stream, with its own entry.
        long xrefOffset = writer.Position;
        types[xrefNumber] = 1;
        field2[xrefNumber] = xrefOffset;
        if (!parsed.Free.Any(r => r.Number == 0))
        {
            field3[0] = 65535; // head of the free list
        }

        int w2 = Math.Max(4, BytesFor(field2.Max()));
        int w3 = Math.Max(2, BytesFor(field3.Max()));
        int rowSize = 1 + w2 + w3;
        var table = new byte[newSize * rowSize];
        for (int n = 0; n < newSize; n++)
        {
            int at = n * rowSize;
            table[at] = types[n];
            WriteBigEndian(table.AsSpan(at + 1, w2), field2[n]);
            WriteBigEndian(table.AsSpan(at + 1 + w2, w3), field3[n]);
        }

        compressed.SetLength(0);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(table);
        }

        writer.WriteAscii(string.Create(CultureInfo.InvariantCulture,
            $"{xrefNumber} 0 obj\r\n<</Type/XRef/Size {newSize}/W[1 {w2} {w3}]/Index[0 {newSize}]"));
        foreach (TrailerEntry entry in parsed.TrailerEntries)
        {
            writer.WriteAscii("/");
            writer.Write(data.AsSpan(entry.KeyStart, entry.KeyEnd - entry.KeyStart));
            if (!PdfObjectScanner.IsDelimiter(data[entry.ValueStart]))
            {
                writer.WriteAscii(" ");
            }

            writer.Write(data.AsSpan(entry.ValueStart, entry.ValueEnd - entry.ValueStart));
        }

        writer.WriteAscii(string.Create(CultureInfo.InvariantCulture,
            $"/Filter/FlateDecode/Length {compressed.Length}>>stream\r\n"));
        writer.Write(compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        writer.WriteAscii(string.Create(CultureInfo.InvariantCulture,
            $"\r\nendstream\r\nendobj\r\nstartxref\r\n{xrefOffset}\r\n%%EOF\r\n"));

        return (packable.Count, streamCount);
    }

    // ---------------------------------------------------------------- helpers

    private static int BytesFor(long value)
    {
        int bytes = 1;
        while (value > 0xFF)
        {
            value >>= 8;
            bytes++;
        }

        return bytes;
    }

    private static void WriteBigEndian(Span<byte> target, long value)
    {
        for (int i = target.Length - 1; i >= 0; i--)
        {
            target[i] = (byte)value;
            value >>= 8;
        }
    }

    private static int SkipEol(ReadOnlySpan<byte> span, int p)
    {
        if (p < span.Length && span[p] == '\r')
        {
            p++;
        }

        if (p < span.Length && span[p] == '\n')
        {
            p++;
        }

        return p;
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> span, int p)
    {
        while (p < span.Length && PdfObjectScanner.IsWhitespace(span[p]))
        {
            p++;
        }

        return p;
    }

    private static void ExpectSpaces(ReadOnlySpan<byte> span, ref int p)
    {
        int start = p;
        while (p < span.Length && span[p] == ' ')
        {
            p++;
        }

        if (p == start)
        {
            throw new PdfPackException("expected a space");
        }
    }

    private static long ReadInteger(ReadOnlySpan<byte> span, ref int p)
    {
        int start = p;
        long value = 0;
        while (p < span.Length && char.IsAsciiDigit((char)span[p]))
        {
            if (p - start >= 15)
            {
                throw new PdfPackException("number is too long");
            }

            value = (value * 10) + (span[p] - '0');
            p++;
        }

        if (p == start)
        {
            throw new PdfPackException("expected a number");
        }

        return value;
    }

    private static long ReadFixedDigits(ReadOnlySpan<byte> span, int p, int count)
    {
        int end = p;
        long value = ReadInteger(span, ref end);
        if (end != p + count)
        {
            throw new PdfPackException("xref entry is malformed");
        }

        return value;
    }

    private static bool IsUnsignedInteger(ReadOnlySpan<byte> token)
    {
        foreach (byte b in token)
        {
            if (!char.IsAsciiDigit((char)b))
            {
                return false;
            }
        }

        return token.Length > 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record XrefRow(int Number, long Offset, int Generation);

    private sealed record TrailerEntry(int KeyStart, int KeyEnd, int ValueStart, int ValueEnd);

    /// <summary>
    /// One in-use object: [Start, End) is "N G obj" through "endobj"; [BodyStart, BodyEnd) is the
    /// trimmed body of a non-stream object; [BodyStart, DictionaryEnd) is the part that may hold
    /// references (the whole body, or a stream's dictionary). <see cref="StaleXrefStream"/> objects are
    /// not written.
    /// </summary>
    private sealed record ObjectInfo(
        int Number,
        int Generation,
        int Start,
        int End,
        int BodyStart,
        int BodyEnd,
        int DictionaryEnd,
        bool Packable,
        bool StaleXrefStream);

    private sealed record ParsedFile(
        int Version,
        byte[] HeaderVersion,
        byte[] HeaderEol,
        byte[]? BinaryMarker,
        int Size,
        List<ObjectInfo> Objects,
        List<XrefRow> Free,
        List<TrailerEntry> TrailerEntries);

    /// <summary>Writes to the output and tracks the byte offset (for the xref entries).</summary>
    private sealed class CountingWriter(Stream stream)
    {
        public long Position { get; private set; }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            stream.Write(bytes);
            Position += bytes.Length;
        }

        public void WriteAscii(string text) => Write(Encoding.ASCII.GetBytes(text));
    }
}
