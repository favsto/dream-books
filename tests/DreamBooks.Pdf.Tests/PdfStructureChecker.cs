using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// Independent, minimal reader of a packed PDF (object streams + cross-reference stream) that
/// validates the structure required by ISO 32000-1 sections 7.5.7 and 7.5.8 without PDFium and
/// without the packer's code. Every violation throws <see cref="InvalidDataException"/> naming
/// the rule (never document content). Test helper only: it trusts nothing it does not check.
/// </summary>
public static class PdfStructureChecker
{
    private static readonly Regex _header = new(@"\A%PDF-(\d)\.(\d)[\r\n]", RegexOptions.CultureInvariant);
    private static readonly Regex _objHeader = new(@"\G(\d+)[ ](\d+)[ ]obj\b", RegexOptions.CultureInvariant);
    private static readonly Regex _startXref = new(@"startxref\s+(\d+)\s+%%EOF\s*\z", RegexOptions.CultureInvariant);
    private static readonly Regex _classicXref = new(@"(^|[\r\n])xref[\r\n]", RegexOptions.CultureInvariant);
    private static readonly Regex _classicTrailer = new(@"(^|[\r\n])trailer[\s<]", RegexOptions.CultureInvariant);
    private static readonly Regex _streamKeyword = new(@">>\s*stream(\r\n|\n)", RegexOptions.CultureInvariant);
    private static readonly Regex _length = new(@"/Length\s+(\d+)(?!\s+\d+\s+R)", RegexOptions.CultureInvariant);

    /// <summary>Validates <paramref name="path"/> and returns what it found.</summary>
    public static PackedStructure Check(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text = Encoding.Latin1.GetString(bytes);

        Match header = _header.Match(text);
        Require(header.Success, "header is not %PDF-x.y");
        int version = (int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture) * 10)
            + int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
        Require(version >= 15, "header version is below 1.5");

        Require(!_classicXref.IsMatch(text), "a classic xref table is left in the file");
        Require(!_classicTrailer.IsMatch(text), "a classic trailer is left in the file");

        Match startXref = _startXref.Match(text);
        Require(startXref.Success, "file does not end with startxref N %%EOF");
        int xrefOffset = int.Parse(startXref.Groups[1].Value, CultureInfo.InvariantCulture);
        Require(xrefOffset > 0 && xrefOffset < bytes.Length, "startxref is out of range");

        TopLevelObject xrefObject = ReadTopLevelObject(bytes, text, xrefOffset);
        string xrefDict = xrefObject.Dictionary;
        Require(Regex.IsMatch(xrefDict, @"/Type\s*/XRef\b"), "startxref does not point at a /Type /XRef object");
        Require(Regex.IsMatch(xrefDict, @"/Filter\s*/FlateDecode\b"), "xref stream is not FlateDecode");
        Require(!Regex.IsMatch(xrefDict, @"/(Prev|DecodeParms|Encrypt|XRefStm)\b"), "xref stream has an unexpected key");
        Match root = Regex.Match(xrefDict, @"/Root\s+(\d+)\s+(\d+)\s+R");
        Require(root.Success, "xref stream has no /Root");
        Match info = Regex.Match(xrefDict, @"/Info\s+(\d+)\s+(\d+)\s+R");
        Match id = Regex.Match(xrefDict, @"/ID\s*\[\s*<([0-9A-Fa-f]*)>\s*<([0-9A-Fa-f]*)>\s*\]");
        int size = IntEntry(xrefDict, "Size");
        Match w = Regex.Match(xrefDict, @"/W\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s*\]");
        Require(w.Success, "xref stream has no /W");
        int[] widths = [.. Enumerable.Range(1, 3).Select(i => int.Parse(w.Groups[i].Value, CultureInfo.InvariantCulture))];
        Require(widths[0] >= 1 && widths[1] >= 1 && widths[2] >= 1, "/W has a zero width");
        Match index = Regex.Match(xrefDict, @"/Index\s*\[([\d\s]*)\]");
        int[] indexPairs = index.Success
            ? [.. index.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.Parse(s, CultureInfo.InvariantCulture))]
            : [0, size];
        Require(indexPairs.Length % 2 == 0, "/Index has an odd number of values");

        byte[] xrefData = Inflate(xrefObject.Payload);
        int entrySize = widths.Sum();
        int entryCount = 0;
        for (int i = 1; i < indexPairs.Length; i += 2)
        {
            entryCount += indexPairs[i];
        }

        Require(xrefData.Length == entryCount * entrySize, "xref stream data length does not match /W and /Index");

        var entries = new SortedDictionary<int, XrefEntry>();
        int position = 0;
        for (int i = 0; i < indexPairs.Length; i += 2)
        {
            for (int n = 0; n < indexPairs[i + 1]; n++)
            {
                int number = indexPairs[i] + n;
                Require(number < size, "/Size is not greater than every object number");
                long type = ReadField(xrefData, ref position, widths[0]);
                long f2 = ReadField(xrefData, ref position, widths[1]);
                long f3 = ReadField(xrefData, ref position, widths[2]);
                Require(type is 0 or 1 or 2, "xref entry has an unknown type");
                Require(entries.TryAdd(number, new XrefEntry((int)type, f2, f3)), "object listed twice in /Index");
            }
        }

        Require(entries.TryGetValue(0, out XrefEntry? zero) && zero.Type == 0 && zero.Field3 == 65535,
            "entry 0 is not a free entry with generation 65535");
        Require(entries.TryGetValue(xrefObject.Number, out XrefEntry? own) && own.Type == 1 && own.Field2 == xrefOffset,
            "xref stream has no type 1 entry pointing at itself");

        // Type 1: every offset points at "N G obj" with the same N and G.
        var topLevel = new SortedDictionary<int, TopLevelObject>();
        foreach ((int number, XrefEntry entry) in entries.Where(e => e.Value.Type == 1))
        {
            Require(entry.Field2 > 0 && entry.Field2 < bytes.Length, "type 1 offset out of range");
            TopLevelObject obj = ReadTopLevelObject(bytes, text, (int)entry.Field2);
            Require(obj.Number == number, "type 1 offset points at a different object number");
            Require(obj.Generation == entry.Field3, "type 1 offset points at a different generation");
            topLevel.Add(number, obj);
        }

        // Type 2: the object stream exists, is valid, and lists the object at that index.
        var objectStreams = new SortedDictionary<int, ObjectStream>();
        var packed = new SortedDictionary<int, string>();
        foreach ((int number, XrefEntry entry) in entries.Where(e => e.Value.Type == 2))
        {
            int streamNumber = (int)entry.Field2;
            if (!objectStreams.TryGetValue(streamNumber, out ObjectStream? objStm))
            {
                Require(topLevel.TryGetValue(streamNumber, out TopLevelObject? container),
                    "type 2 entry references an object stream that is not a type 1 object");
                objStm = ReadObjectStream(container!);
                objectStreams.Add(streamNumber, objStm);
            }

            Require(entry.Field3 < objStm.Numbers.Length, "type 2 index is beyond /N");
            Require(objStm.Numbers[entry.Field3] == number, "object stream does not list the object at that index");
            string body = objStm.Bodies[entry.Field3];
            Require(body.Length > 0 && !char.IsWhiteSpace(body[0]), "packed object does not start at First + offset");
            Require(!_objHeader.IsMatch(body, 0), "packed object still has an obj header");
            Require(IsPlainObjectBody(body), "packed object is or contains a stream");
            packed.Add(number, body);
        }

        foreach (ObjectStream objStm in objectStreams.Values)
        {
            foreach (int listed in objStm.Numbers)
            {
                Require(entries.TryGetValue(listed, out XrefEntry? e) && e.Type == 2,
                    "object stream lists an object whose xref entry is not type 2");
            }
        }

        Require(topLevel.Values.All(o => o.Number < size), "/Size is not greater than every object number");

        return new PackedStructure(
            version,
            size,
            xrefObject.Number,
            xrefDict,
            (int.Parse(root.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(root.Groups[2].Value, CultureInfo.InvariantCulture)),
            info.Success ? info.Value : null,
            id.Success ? id.Value : null,
            entries,
            topLevel,
            objectStreams,
            packed);
    }

    /// <summary>
    /// Reads a classic (uncompressed) PDF such as PDFium's full rewrite: in-use object number →
    /// (generation, offset), and the trailer dictionary text. Throws if the shape is unexpected.
    /// </summary>
    public static ClassicStructure ReadClassic(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text = Encoding.Latin1.GetString(bytes);
        Match startXref = _startXref.Match(text);
        Require(startXref.Success, "no startxref");
        int xrefOffset = int.Parse(startXref.Groups[1].Value, CultureInfo.InvariantCulture);
        Require(string.CompareOrdinal(text, xrefOffset, "xref", 0, 4) == 0, "startxref does not point at xref");
        int trailerAt = text.IndexOf("trailer", xrefOffset, StringComparison.Ordinal);
        Require(trailerAt > 0, "no trailer");
        var objects = new SortedDictionary<int, TopLevelObject>();
        var lines = text[(xrefOffset + 4)..trailerAt]
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        int i = 0;
        while (i < lines.Length)
        {
            string[] sub = lines[i++].Split(' ');
            int start = int.Parse(sub[0], CultureInfo.InvariantCulture);
            int count = int.Parse(sub[1], CultureInfo.InvariantCulture);
            for (int n = 0; n < count; n++, i++)
            {
                string[] parts = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts[2] == "n")
                {
                    TopLevelObject obj = ReadTopLevelObject(bytes, text, int.Parse(parts[0], CultureInfo.InvariantCulture));
                    Require(obj.Number == start + n, "classic xref offset mismatch");
                    objects.Add(obj.Number, obj);
                }
            }
        }

        return new ClassicStructure(objects, text[trailerAt..text.LastIndexOf("startxref", StringComparison.Ordinal)]);
    }

    /// <summary>
    /// True when <paramref name="body"/> has no <c>endobj</c>, <c>endstream</c> or <c>stream</c>
    /// keyword outside literal strings and comments (inside a string a keyword is just text).
    /// Hex strings hold only hex digits and whitespace, so they cannot contain a keyword.
    /// </summary>
    public static bool IsPlainObjectBody(string body)
    {
        var code = new StringBuilder(body.Length);
        int i = 0;
        while (i < body.Length)
        {
            char c = body[i];
            if (c == '(')
            {
                int depth = 0;
                do
                {
                    if (body[i] == '\\')
                    {
                        i++; // the escaped character, whatever it is
                    }
                    else if (body[i] == '(')
                    {
                        depth++;
                    }
                    else if (body[i] == ')')
                    {
                        depth--;
                    }

                    i++;
                }
                while (depth > 0 && i < body.Length);
                code.Append(" () ");
            }
            else if (c == '%')
            {
                while (i < body.Length && body[i] is not ('\r' or '\n'))
                {
                    i++;
                }
            }
            else
            {
                code.Append(c);
                i++;
            }
        }

        return !Regex.IsMatch(code.ToString(), @"(?<![/\w])(endobj|endstream|stream)(?!\w)", RegexOptions.CultureInvariant);
    }

    /// <summary>Inflates zlib data (PDF FlateDecode) with System.IO.Compression only.</summary>
    public static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static ObjectStream ReadObjectStream(TopLevelObject container)
    {
        string dict = container.Dictionary;
        Require(container.Generation == 0, "object stream has a non-zero generation");
        Require(container.IsStream, "object stream is not a stream");
        Require(Regex.IsMatch(dict, @"/Type\s*/ObjStm\b"), "object stream has no /Type /ObjStm");
        Require(Regex.IsMatch(dict, @"/Filter\s*/FlateDecode\b"), "object stream is not FlateDecode");
        Require(!Regex.IsMatch(dict, @"/(DecodeParms|Extends)\b"), "object stream has an unexpected key");
        int n = IntEntry(dict, "N");
        int first = IntEntry(dict, "First");
        byte[] data = Inflate(container.Payload);
        Require(first <= data.Length, "/First is beyond the object stream data");
        string all = Encoding.Latin1.GetString(data);
        string[] pairs = all[..first].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Require(pairs.Length == 2 * n, "object stream header does not have /N pairs");
        var numbers = new int[n];
        var offsets = new int[n];
        for (int i = 0; i < n; i++)
        {
            numbers[i] = int.Parse(pairs[2 * i], CultureInfo.InvariantCulture);
            offsets[i] = int.Parse(pairs[(2 * i) + 1], CultureInfo.InvariantCulture);
            Require(i == 0 || offsets[i] > offsets[i - 1], "object stream offsets are not increasing");
            Require(first + offsets[i] < data.Length, "object stream offset is beyond the data");
        }

        var bodies = new string[n];
        for (int i = 0; i < n; i++)
        {
            int start = first + offsets[i];
            int end = i + 1 < n ? first + offsets[i + 1] : data.Length;
            bodies[i] = all[start..end].TrimEnd();
        }

        return new ObjectStream(container.Number, numbers, bodies);
    }

    private static TopLevelObject ReadTopLevelObject(byte[] bytes, string text, int offset)
    {
        Match header = _objHeader.Match(text, offset);
        Require(header.Success, "offset does not point at an 'N G obj' header");
        int number = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
        int generation = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
        int bodyStart = header.Index + header.Length;
        int endobj = text.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
        Require(endobj > 0, "object has no endobj");
        Match stream = _streamKeyword.Match(text, bodyStart, endobj - bodyStart);
        if (stream.Success)
        {
            string dict = text[bodyStart..(stream.Index + 2)];
            Match length = _length.Match(dict);
            Require(length.Success, "stream has no direct /Length");
            int dataStart = stream.Index + stream.Length;
            int dataLength = int.Parse(length.Groups[1].Value, CultureInfo.InvariantCulture);
            Require(dataStart + dataLength <= bytes.Length, "stream /Length runs past the end of the file");
            Require(Regex.IsMatch(text[(dataStart + dataLength)..Math.Min(bytes.Length, dataStart + dataLength + 32)],
                @"\A\s*endstream\s+endobj"), "stream is not followed by endstream endobj");
            int end = text.IndexOf("endobj", dataStart + dataLength, StringComparison.Ordinal) + 6;
            return new TopLevelObject(number, generation, offset, dict, bytes[dataStart..(dataStart + dataLength)],
                bytes[offset..end], IsStream: true);
        }

        return new TopLevelObject(number, generation, offset, text[bodyStart..endobj].Trim(), [],
            bytes[offset..(endobj + 6)], IsStream: false);
    }

    private static int IntEntry(string dict, string key)
    {
        Match m = Regex.Match(dict, $@"/{key}\s+(\d+)(?!\s+\d+\s+R)");
        Require(m.Success, $"/{key} is missing or not a direct integer");
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static long ReadField(byte[] data, ref int position, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++)
        {
            value = (value << 8) | data[position++];
        }

        return value;
    }

    private static void Require(bool condition, string rule)
    {
        if (!condition)
        {
            throw new InvalidDataException("PDF structure check failed: " + rule);
        }
    }

    /// <summary>One cross-reference stream entry (type, field 2, field 3).</summary>
    public sealed record XrefEntry(int Type, long Field2, long Field3);

    /// <summary>
    /// An object stored at a byte offset. <see cref="Dictionary"/> is the stream dictionary (or the
    /// whole body for non-streams), <see cref="Payload"/> the raw stream data, <see cref="Raw"/>
    /// the bytes from "N G obj" through "endobj".
    /// </summary>
    public sealed record TopLevelObject(
        int Number, int Generation, int Offset, string Dictionary, byte[] Payload, byte[] Raw, bool IsStream);

    /// <summary>An object stream: the object numbers it lists, in order, and their bodies.</summary>
    public sealed record ObjectStream(int Number, int[] Numbers, string[] Bodies);

    /// <summary>What <see cref="Check"/> found in a valid packed file.</summary>
    public sealed record PackedStructure(
        int Version,
        int Size,
        int XrefStreamNumber,
        string XrefDictionary,
        (int Number, int Generation) Root,
        string? InfoEntry,
        string? IdEntry,
        IReadOnlyDictionary<int, XrefEntry> Entries,
        IReadOnlyDictionary<int, TopLevelObject> TopLevel,
        IReadOnlyDictionary<int, ObjectStream> ObjectStreams,
        IReadOnlyDictionary<int, string> Packed);

    /// <summary>What <see cref="ReadClassic"/> found: in-use objects and the trailer text.</summary>
    public sealed record ClassicStructure(IReadOnlyDictionary<int, TopLevelObject> Objects, string Trailer);
}
