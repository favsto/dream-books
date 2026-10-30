using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Model;

public class PageTests
{
    private static InkStroke NewStroke() => new(
        Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1,
        [new InkPoint(1, 2, 0.5f, null, null, null, 0)]);

    [Fact]
    public void Page_requires_positive_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Page(0, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Page(0, 100, -1));
    }

    [Fact]
    public void SetStrokes_replaces_and_raises_StrokesChanged_once()
    {
        var page = new Page(0, 595, 842);
        var count = 0;
        page.StrokesChanged += (_, _) => count++;
        var source = new List<InkStroke> { NewStroke(), NewStroke() };

        page.SetStrokes(source);
        source.Clear();

        Assert.Equal(1, count);
        Assert.Equal(2, page.Strokes.Count);
    }
}
