using System.Globalization;
using System.Text;

namespace DreamBooks.Pdf.Tests;

/// <summary>
/// Writes PDFs with a known text layer for the search tests: each <see cref="TextLine"/> is its own text object
/// (<c>BT … Tj ET</c>) in Helvetica with WinAnsiEncoding, so Latin-1 letters (è, à, ù, É…) are real text. The
/// characters U+FB01 and U+FB02 are written as the single glyphs <c>/fi</c> and <c>/fl</c> (one PDF character
/// each, a ligature). Every page also draws a small grey square so a page without lines still has content.
/// </summary>
public static class TextPdfFactory
{
    /// <summary>Helvetica advance widths (1/1000 em) for the characters the tests measure.</summary>
    private static readonly Dictionary<char, int> _helveticaWidths = new()
    {
        [' '] = 278,
        ['a'] = 556,
        ['b'] = 556,
        ['c'] = 500,
        ['d'] = 556,
        ['e'] = 556,
        ['f'] = 278,
        ['g'] = 556,
        ['h'] = 556,
        ['i'] = 222,
        ['l'] = 222,
        ['m'] = 833,
        ['n'] = 556,
        ['o'] = 556,
        ['p'] = 556,
        ['r'] = 333,
        ['s'] = 500,
        ['t'] = 278,
        ['u'] = 556,
        ['v'] = 500,
        ['z'] = 500,
    };

    public static void Write(string path, params TextPageSpec[] pages)
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
        Set(catalogObj, $"<< /Type /Catalog /Pages {pagesObj} 0 R >>");
        Set(fontObj, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding "
            + "<< /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [1 /fi /fl] >> >>");

        var pageObjs = new List<int>();
        foreach (TextPageSpec spec in pages)
        {
            ArgumentNullException.ThrowIfNull(spec);
            int pageObj = Reserve();
            int contentObj = Reserve();
            pageObjs.Add(pageObj);

            string crop = spec.CropBox is { } c
                ? $" /CropBox [{Num(c.Left)} {Num(c.Bottom)} {Num(c.Right)} {Num(c.Top)}]"
                : string.Empty;
            string rotate = spec.Rotate != 0 ? $" /Rotate {spec.Rotate.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
            Set(pageObj,
                $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 {Num(spec.Width)} {Num(spec.Height)}]{crop}{rotate}"
                + $" /Contents {contentObj} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");

            var content = new StringBuilder("0.5 g 0 0 10 10 re f 0 g\n");
            foreach (TextLine line in spec.Lines)
            {
                content.Append(CultureInfo.InvariantCulture,
                    $"BT /F1 {Num(spec.FontSize)} Tf {Num(line.X)} {Num(line.Y)} Td ({Encode(line.Text)}) Tj ET\n");
            }

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

    /// <summary>The Helvetica advance width of <paramref name="text"/> at <paramref name="fontSize"/>, in points.</summary>
    public static double Width(string text, double fontSize = 12) =>
        text.Sum(c => _helveticaWidths.TryGetValue(c, out int w)
            ? w
            : throw new ArgumentException("No width table entry for a character.", nameof(text))) * fontSize / 1000.0;

    private static string Encode(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '(' or ')' or '\\':
                    sb.Append('\\').Append(c);
                    break;
                case >= ' ' and <= '~':
                    sb.Append(c);
                    break;
                case >= ' ' and <= 'ÿ': // WinAnsi equals Latin-1 here
                    sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    break;
                case 'ﬁ':
                    sb.Append("\\001");
                    break;
                case 'ﬂ':
                    sb.Append("\\002");
                    break;
                default:
                    throw new ArgumentException("A character the test font cannot show.", nameof(text));
            }
        }

        return sb.ToString();
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A text object: <paramref name="Text"/> drawn with its baseline starting at (X, Y) in user space.</summary>
    public sealed record TextLine(double X, double Y, string Text);

    /// <summary>A page with MediaBox [0 0 Width Height], an optional CropBox and /Rotate, and its text lines.</summary>
    public sealed record TextPageSpec(
        double Width,
        double Height,
        IReadOnlyList<TextLine> Lines,
        int Rotate = 0,
        TestPdfFactory.PageBox? CropBox = null,
        double FontSize = 12);
}
