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
    public static void Write(string path, params (double w, double h)[] pages)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Length == 0)
        {
            throw new ArgumentException("At least one page is required.", nameof(pages));
        }

        // Object numbering: 1 = catalog, 2 = pages, then (page, content) pairs.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
        };

        var kids = new StringBuilder();
        for (int i = 0; i < pages.Length; i++)
        {
            kids.Append(CultureInfo.InvariantCulture, $"{3 + (2 * i)} 0 R ");
        }

        objects.Add($"<< /Type /Pages /Kids [{kids.ToString().TrimEnd()}] /Count {pages.Length} >>");

        for (int i = 0; i < pages.Length; i++)
        {
            (double w, double h) = pages[i];
            int contentObj = 4 + (2 * i);
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(w)} {Num(h)}] /Contents {contentObj} 0 R /Resources << >> >>");

            string content = $"0 0 0 rg 0 {Num(h - 100)} 100 100 re f\n";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
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

        xref.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        xref.Append(CultureInfo.InvariantCulture, $"startxref\n{xrefOffset}\n%%EOF\n");
        WriteAscii(xref.ToString());

        File.WriteAllBytes(path, stream.ToArray());
    }

    /// <summary>Writes a file that is not a PDF.</summary>
    public static void WriteGarbage(string path) => File.WriteAllText(path, "not a pdf");

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
