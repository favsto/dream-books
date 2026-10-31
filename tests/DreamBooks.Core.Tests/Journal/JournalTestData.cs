using DreamBooks.Core.Documents;
using DreamBooks.Core.Journal;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Journal;

internal static class JournalTestData
{
    public static readonly DocumentFingerprint Fingerprint = new(100, 200, new string('a', 64));

    public static readonly InkColor Black = InkColor.FromRgb(0, 0, 0);

    public static InkPoint P(double x, double y) => new(x, y, 0.5f, null, null, null, 0);

    public static InkStroke Stroke(double y) => new(Guid.NewGuid(), InkTool.Pen, Black, 2, [P(10, y), P(600, y)]);

    public static InkStroke RichStroke() => new(
        Guid.NewGuid(),
        InkTool.Highlighter,
        new InkColor(0x80, 0xFF, 0xEE, 0x00),
        12.5,
        [new InkPoint(10.25, 20.5, 0.75f, -0.3f, 0.2f, 1.1f, 123456789012L), new InkPoint(11, 21, null, null, null, null, 0)]);

    public static string Header(string path = @"C:\docs\book.pdf")
        => JournalCodec.SerializeHeader(new JournalHeader(JournalCodec.CurrentFormatVersion, path, Fingerprint));

    public static void AssertSameStroke(InkStroke expected, InkStroke actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Tool, actual.Tool);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.BaseWidthPt, actual.BaseWidthPt);
        Assert.Equal(expected.Points, actual.Points);
    }

    public static Guid[] Ids(IEnumerable<InkStroke> strokes) => strokes.Select(s => s.Id).ToArray();
}
