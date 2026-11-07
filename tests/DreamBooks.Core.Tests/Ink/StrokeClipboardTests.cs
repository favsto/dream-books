using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Ink;

public class StrokeClipboardTests
{
    [Fact]
    public void Set_raises_Changed_and_IsEmpty_is_false()
    {
        var clipboard = new StrokeClipboard();
        var raised = 0;
        clipboard.Changed += (_, _) => raised++;
        Assert.True(clipboard.IsEmpty);

        clipboard.Set([new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1, [new InkPoint(1, 1, 0.5f, null, null, null, 0)])]);

        Assert.False(clipboard.IsEmpty);
        Assert.Single(clipboard.Strokes);
        Assert.Equal(1, raised);
    }
}
