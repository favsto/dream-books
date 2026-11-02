using DreamBooks.Core.Layout;

namespace DreamBooks.Core.Tests.Layout;

public class PositionHistoryTests
{
    private static ViewPosition P(int page, double y = 0) => new(page, y, 0, 1.0);

    [Fact]
    public void Push_then_pop_returns_last_first()
    {
        var h = new PositionHistory();
        h.Push(P(1));
        h.Push(P(2));
        Assert.True(h.CanGoBack);
        Assert.True(h.TryPop(out var a));
        Assert.Equal(P(2), a);
        Assert.True(h.TryPop(out var b));
        Assert.Equal(P(1), b);
        Assert.False(h.CanGoBack);
    }

    [Fact]
    public void Duplicate_of_top_is_ignored()
    {
        var h = new PositionHistory();
        h.Push(P(1, 10));
        h.Push(P(1, 10.5));
        Assert.True(h.TryPop(out _));
        Assert.False(h.TryPop(out _));
    }

    [Fact]
    public void Same_page_far_apart_or_other_page_is_kept()
    {
        var h = new PositionHistory();
        h.Push(P(1, 10));
        h.Push(P(1, 12));
        h.Push(P(2, 12));
        Assert.True(h.TryPop(out _));
        Assert.True(h.TryPop(out _));
        Assert.True(h.TryPop(out _));
        Assert.False(h.TryPop(out _));
    }

    [Fact]
    public void Capacity_drops_the_oldest()
    {
        var h = new PositionHistory(3);
        for (var i = 0; i < 5; i++) h.Push(P(i));
        Assert.True(h.TryPop(out var a));
        Assert.True(h.TryPop(out var b));
        Assert.True(h.TryPop(out var c));
        Assert.False(h.TryPop(out _));
        Assert.Equal([4, 3, 2], new[] { a.PageIndex, b.PageIndex, c.PageIndex });
    }

    [Fact]
    public void Pop_on_empty_returns_false()
    {
        var h = new PositionHistory();
        Assert.False(h.TryPop(out var p));
        Assert.Equal(default, p);
        Assert.False(h.CanGoBack);
    }

    [Fact]
    public void Changed_fires_on_push_pop_clear_only_when_state_changes()
    {
        var h = new PositionHistory();
        var n = 0;
        h.Changed += (_, _) => n++;
        h.Clear();
        Assert.Equal(0, n);
        h.TryPop(out _);
        Assert.Equal(0, n);
        h.Push(P(1));
        Assert.Equal(1, n);
        h.Push(P(1));
        Assert.Equal(1, n);
        h.TryPop(out _);
        Assert.Equal(2, n);
        h.Push(P(2));
        h.Clear();
        Assert.Equal(4, n);
    }

    [Fact]
    public void Clear_empties()
    {
        var h = new PositionHistory();
        h.Push(P(1));
        h.Clear();
        Assert.False(h.CanGoBack);
        Assert.False(h.TryPop(out _));
    }
}
