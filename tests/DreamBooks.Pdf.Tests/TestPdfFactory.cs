using System.Globalization;
using System.Text;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// Writes minimal, valid PDF files for tests so no binary fixtures are committed.
/// Dependency-free (System.* only) so it can be linked into other test projects.
/// </summary>
public static class TestPdfFactory
{
    /// <summary>
    /// Writes a PDF with one page per entry in <paramref name="pages"/> (sizes in points).
    /// Each page draws a filled black 100x100 pt square at its top-left corner.
    /// </summary>
    public static void Write(string path, params (double w, double h)[] pages) =>
        WriteWithFeatures(path, new Features(), pages);

    /// <summary>
    /// Like <see cref="Write"/>, and each page also shows <paramref name="text"/> (printable
    /// ASCII) in Helvetica, so text extraction can be tested.
    /// </summary>
    public static void WriteWithText(string path, string text, params (double w, double h)[] pages)
    {
        ArgumentNullException.ThrowIfNull(text);
        WriteWithFeatures(path, new Features { Text = text }, pages);
    }

    /// <summary>
    /// Like <see cref="Write"/>, with the optional document features in <paramref name="features"/>.
    /// All strings must be printable ASCII.
    /// </summary>
    public static void WriteWithFeatures(string path, Features features, params (double w, double h)[] pages)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Length == 0)
        {
            throw new ArgumentException("At least one page is required.", nameof(pages));
        }

        var objects = new List<string?>();
        int Reserve()
        {
            objects.Add(null);
            return objects.Count;
        }

        void Set(int number, string body) => objects[number - 1] = body;

        int catalogObj = Reserve();
        int pagesObj = Reserve();
        var pageObjs = new int[pages.Length];
        var contentObjs = new int[pages.Length];
        for (int i = 0; i < pages.Length; i++)
        {
            pageObjs[i] = Reserve();
            contentObjs[i] = Reserve();
        }

        int fontObj = features.Text is null ? 0 : Reserve();
        string resources = fontObj == 0 ? "<< >>" : $"<< /Font << /F1 {fontObj} 0 R >> >>";

        // Annotations, all on the first page.
        var annotObjs = new List<int>();
        for (int i = 0; i < features.LinkCount; i++)
        {
            int obj = Reserve();
            annotObjs.Add(obj);
            double y = 300 + (30 * i);
            Set(obj, $"<< /Type /Annot /Subtype /Link /Rect [300 {Num(y)} 350 {Num(y + 20)}] /Border [0 0 0] "
                + $"/A << /S /URI /URI (https://example.invalid/{i}) >> >>");
        }

        for (int i = 0; i < features.SquareAnnotationCount; i++)
        {
            int obj = Reserve();
            annotObjs.Add(obj);
            double y = 300 + (30 * i);
            Set(obj, $"<< /Type /Annot /Subtype /Square /Rect [400 {Num(y)} 450 {Num(y + 20)}] /C [1 0 0] /Border [0 0 1] >>");
        }

        Set(pagesObj, $"<< /Type /Pages /Kids [{string.Join(" ", pageObjs.Select(n => $"{n} 0 R"))}] /Count {pages.Length} >>");
        for (int i = 0; i < pages.Length; i++)
        {
            (double w, double h) = pages[i];
            string annots = i == 0 && annotObjs.Count > 0
                ? $" /Annots [{string.Join(" ", annotObjs.Select(n => $"{n} 0 R"))}]"
                : string.Empty;
            Set(pageObjs[i],
                $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 {Num(w)} {Num(h)}] /Contents {contentObjs[i]} 0 R /Resources {resources}{annots} >>");

            string content = features.DrawSquare ? $"0 0 0 rg 0 {Num(h - 100)} 100 100 re f\n" : string.Empty;
            if (features.Text is not null)
            {
                content += $"BT /F1 12 Tf 120 {Num(h - 40)} Td ({Escape(features.Text)}) Tj ET\n";
            }

            Set(contentObjs[i], $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }

        if (fontObj != 0)
        {
            Set(fontObj, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        }

        string outlines = string.Empty;
        if (features.Outline is { Count: > 0 } outline)
        {
            int rootObj = Reserve();
            (int first, int last, int count) = WriteOutlineLevel(
                outline, rootObj, pageObjs[0], features.CyclicOutline, Reserve, Set);
            Set(rootObj, $"<< /Type /Outlines /First {first} 0 R /Last {last} 0 R /Count {count} >>");
            outlines = $" /Outlines {rootObj} 0 R";
        }

        Set(catalogObj, $"<< /Type /Catalog /Pages {pagesObj} 0 R{outlines} >>");

        string info = string.Empty;
        if (features.Info is { Count: > 0 } metadata)
        {
            int infoObj = Reserve();
            Set(infoObj, $"<< {string.Join(" ", metadata.Select(kv => $"/{kv.Key} ({Escape(kv.Value)})"))} >>");
            info = $" /Info {infoObj} 0 R";
        }

        using var stream = new MemoryStream();
        var offsets = new List<long>();

        void WriteAscii(string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        WriteAscii("%PDF-1.4\n");
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            WriteAscii($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xrefOffset = stream.Position;
        var xref = new StringBuilder();
        xref.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n");
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        xref.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root {catalogObj} 0 R{info} >>\n");
        xref.Append(CultureInfo.InvariantCulture, $"startxref\n{xrefOffset}\n%%EOF\n");
        WriteAscii(xref.ToString());

        File.WriteAllBytes(path, stream.ToArray());
    }

    /// <summary>
    /// Writes a PDF with one page per <see cref="PageSpec"/> (MediaBox origin and size, optional
    /// CropBox, /Rotate, optional foreign annotation). Every page draws, in unrotated user space:
    /// a black 100x100 pt square at the top-left of the visible box (CropBox, else MediaBox), a
    /// red 50x20 pt marker at its bottom-right, and a few lines of blue Helvetica text.
    /// </summary>
    public static void Write(string path, params PageSpec[] pages)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Length == 0)
        {
            throw new ArgumentException("At least one page is required.", nameof(pages));
        }

        var objects = new List<string?>();
        int Reserve()
        {
            objects.Add(null);
            return objects.Count;
        }

        void Set(int number, string body) => objects[number - 1] = body;

        int catalogObj = Reserve();
        int pagesObj = Reserve();
        int fontObj = Reserve();
        Set(fontObj, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Set(catalogObj, $"<< /Type /Catalog /Pages {pagesObj} 0 R >>");

        var pageObjs = new List<int>();
        foreach (PageSpec spec in pages)
        {
            ArgumentNullException.ThrowIfNull(spec);
            double mediaRight = spec.OriginX + spec.Width;
            double mediaTop = spec.OriginY + spec.Height;
            PageBox visible = spec.CropBox ?? new PageBox(spec.OriginX, spec.OriginY, mediaRight, mediaTop);

            int pageObj = Reserve();
            int contentObj = Reserve();
            pageObjs.Add(pageObj);

            var annotRefs = new List<string>();
            if (spec.WithForeignSquareAnnotation)
            {
                int annotObj = Reserve();
                Set(annotObj, $"<< /Type /Annot /Subtype /Square /Rect [{Num(visible.Left + 150)} {Num(visible.Bottom + 150)} "
                    + $"{Num(visible.Left + 210)} {Num(visible.Bottom + 190)}] /C [0 0.6 0] /Border [0 0 1] >>");
                annotRefs.Add($"{annotObj} 0 R");
            }

            string annots = string.Empty;
            if (spec.ExistingAnnotsAsIndirectArray)
            {
                int arrayObj = Reserve();
                Set(arrayObj, $"[{string.Join(" ", annotRefs)}]");
                annots = $" /Annots {arrayObj} 0 R";
            }
            else if (annotRefs.Count > 0)
            {
                annots = $" /Annots [{string.Join(" ", annotRefs)}]";
            }

            string crop = spec.CropBox is { } c
                ? $" /CropBox [{Num(c.Left)} {Num(c.Bottom)} {Num(c.Right)} {Num(c.Top)}]"
                : string.Empty;
            string rotate = spec.Rotate != 0 ? $" /Rotate {spec.Rotate.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
            Set(pageObj,
                $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [{Num(spec.OriginX)} {Num(spec.OriginY)} {Num(mediaRight)} {Num(mediaTop)}]"
                + $"{crop}{rotate} /Contents {contentObj} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >>{annots} >>");

            var content = new StringBuilder();
            content.Append(CultureInfo.InvariantCulture,
                $"0 0 0 rg {Num(visible.Left)} {Num(visible.Top - 100)} 100 100 re f\n");
            content.Append(CultureInfo.InvariantCulture,
                $"1 0 0 rg {Num(visible.Right - 50)} {Num(visible.Bottom)} 50 20 re f\n");
            content.Append(CultureInfo.InvariantCulture,
                $"0 0 1 rg BT /F1 12 Tf {Num(visible.Left + 120)} {Num(visible.Top - 40)} Td 14 TL ");
            content.Append("(Dream Books geometry page) Tj T* (Second line of text) Tj T* (Third line of text) Tj ET\n");
            string text = content.ToString();
            Set(contentObj, $"<< /Length {Encoding.ASCII.GetByteCount(text)} >>\nstream\n{text}endstream");
        }

        Set(pagesObj, $"<< /Type /Pages /Kids [{string.Join(" ", pageObjs.Select(n => $"{n} 0 R"))}] /Count {pageObjs.Count} >>");

        using var stream = new MemoryStream();
        void WriteAscii(string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        WriteAscii("%PDF-1.4\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            WriteAscii($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xrefOffset = stream.Position;
        var xref = new StringBuilder();
        xref.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        xref.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root {catalogObj} 0 R >>\n");
        xref.Append(CultureInfo.InvariantCulture, $"startxref\n{xrefOffset}\n%%EOF\n");
        WriteAscii(xref.ToString());

        File.WriteAllBytes(path, stream.ToArray());
    }

    /// <summary>
    /// Writes a PDF encrypted with the standard security handler (RC4 40-bit, revision 2), an
    /// empty user password and permissions that allow nothing, so it opens without a password
    /// but is restricted. Pages are empty (no content stream, so nothing needs encrypting).
    /// </summary>
    public static void WriteEncryptedRestricted(string path, params (double w, double h)[] pages)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Length == 0)
        {
            throw new ArgumentException("At least one page is required.", nameof(pages));
        }

        byte[] padding =
        [
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
        ];
        const int permissions = -64; // 0xFFFFFFC0: print, modify, copy and annotate all denied
        byte[] id = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        // Algorithm 3 (revision 2): /O from the owner password "owner" and the empty user password.
        byte[] ownerPadded = padding.ToArray();
        Encoding.ASCII.GetBytes("owner").CopyTo(ownerPadded, 0);
        Array.Copy(padding, 0, ownerPadded, 5, 27);
        byte[] ownerKey = System.Security.Cryptography.MD5.HashData(ownerPadded)[..5];
        byte[] o = Rc4(ownerKey, padding);

        // Algorithm 2: file key = first 5 bytes of MD5(user padded + O + P (LE) + ID).
        byte[] keyInput = padding.Concat(o).Concat(BitConverter.GetBytes(permissions)).Concat(id).ToArray();
        byte[] fileKey = System.Security.Cryptography.MD5.HashData(keyInput)[..5];

        // Algorithm 4: /U = RC4(file key, padding).
        byte[] u = Rc4(fileKey, padding);

        static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", pages.Select((_, i) => $"{3 + i} 0 R"))}] /Count {pages.Length} >>",
        };
        objects.AddRange(pages.Select(p =>
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(p.w)} {Num(p.h)}] /Resources << >> >>"));
        int encryptObj = objects.Count + 1;
        objects.Add($"<< /Filter /Standard /V 1 /R 2 /O <{Hex(o)}> /U <{Hex(u)}> /P {permissions} >>");

        using var stream = new MemoryStream();
        void WriteAscii(string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        WriteAscii("%PDF-1.4\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            WriteAscii($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xrefOffset = stream.Position;
        var xref = new StringBuilder();
        xref.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        xref.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Encrypt {encryptObj} 0 R /ID [<{Hex(id)}> <{Hex(id)}>] >>\n");
        xref.Append(CultureInfo.InvariantCulture, $"startxref\n{xrefOffset}\n%%EOF\n");
        WriteAscii(xref.ToString());

        File.WriteAllBytes(path, stream.ToArray());
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var state = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            state[i] = (byte)i;
        }

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + state[i]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
            output[n] = (byte)(data[n] ^ state[(state[i] + state[j]) & 0xFF]);
        }

        return output;
    }

    /// <summary>Writes a file that is not a PDF.</summary>
    public static void WriteGarbage(string path) => File.WriteAllText(path, "not a pdf");

    /// <summary>Writes one outline level; returns its first and last object and the descendant count.</summary>
    private static (int First, int Last, int Count) WriteOutlineLevel(
        IReadOnlyList<OutlineItem> items,
        int parentObj,
        int pageObj,
        bool cyclic,
        Func<int> reserve,
        Action<int, string> set)
    {
        int[] numbers = items.Select(_ => reserve()).ToArray();
        int total = items.Count;
        for (int i = 0; i < items.Count; i++)
        {
            var dict = new StringBuilder();
            dict.Append(CultureInfo.InvariantCulture,
                $"<< /Title ({Escape(items[i].Title)}) /Parent {parentObj} 0 R /Dest [{pageObj} 0 R /Fit]");
            if (i > 0)
            {
                dict.Append(CultureInfo.InvariantCulture, $" /Prev {numbers[i - 1]} 0 R");
            }

            if (i < items.Count - 1)
            {
                dict.Append(CultureInfo.InvariantCulture, $" /Next {numbers[i + 1]} 0 R");
            }
            else if (cyclic && items.Count > 1)
            {
                // Deliberately malformed: the last sibling points back to the first.
                dict.Append(CultureInfo.InvariantCulture, $" /Next {numbers[0]} 0 R");
            }

            if (items[i].Children.Length > 0)
            {
                (int first, int last, int count) = WriteOutlineLevel(
                    items[i].Children, numbers[i], pageObj, cyclic: false, reserve, set);
                dict.Append(CultureInfo.InvariantCulture, $" /First {first} 0 R /Last {last} 0 R /Count {count}");
                total += count;
            }

            dict.Append(" >>");
            set(numbers[i], dict.ToString());
        }

        return (numbers[0], numbers[^1], total);
    }

    private static string Escape(string text)
    {
        if (text.Any(c => c < ' ' || c > '~'))
        {
            throw new ArgumentException("Only printable ASCII is supported.", nameof(text));
        }

        return text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Optional document features for <see cref="WriteWithFeatures"/>.</summary>
    public sealed class Features
    {
        /// <summary>Text shown on every page in Helvetica.</summary>
        public string? Text { get; init; }

        /// <summary>Whether each page draws the black 100x100 pt square (default true).</summary>
        public bool DrawSquare { get; init; } = true;

        /// <summary>Document information entries, e.g. Title, Author (written to /Info).</summary>
        public IReadOnlyDictionary<string, string>? Info { get; init; }

        /// <summary>Top-level outline items, each with optional nested children; all point to page 1.</summary>
        public IReadOnlyList<OutlineItem>? Outline { get; init; }

        /// <summary>Makes the last top-level outline item's /Next point back to the first (malformed).</summary>
        public bool CyclicOutline { get; init; }

        /// <summary>Number of URI link annotations on page 1.</summary>
        public int LinkCount { get; init; }

        /// <summary>Number of Square annotations on page 1.</summary>
        public int SquareAnnotationCount { get; init; }
    }

    /// <summary>An outline (bookmark) entry with its children.</summary>
    public sealed record OutlineItem(string Title, params OutlineItem[] Children);

    /// <summary>
    /// One page for <see cref="Write(string, PageSpec[])"/>: MediaBox
    /// [OriginX OriginY OriginX+Width OriginY+Height], /Rotate, an optional foreign Square
    /// annotation, an optional indirect /Annots array, and an optional CropBox (must lie inside
    /// the MediaBox).
    /// </summary>
    public sealed record PageSpec(
        double Width,
        double Height,
        int Rotate = 0,
        double OriginX = 0,
        double OriginY = 0,
        bool WithForeignSquareAnnotation = false,
        bool ExistingAnnotsAsIndirectArray = false,
        PageBox? CropBox = null);

    /// <summary>A PDF box in user space (y up), as written in /MediaBox or /CropBox.</summary>
    public sealed record PageBox(double Left, double Bottom, double Right, double Top);
}
