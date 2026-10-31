using DreamBooks.App.Services;

namespace DreamBooks.App.Tests;

/// <summary>A scheduler driven by hand: nothing runs until <see cref="Advance"/> moves time past its due time.</summary>
internal sealed class ManualScheduler : IDelayScheduler
{
    private readonly List<Item> _items = [];

    public TimeSpan Now { get; private set; }

    public int Pending => _items.Count;

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var item = new Item(Now + delay, action, _items);
        _items.Add(item);
        return item;
    }

    public void Advance(TimeSpan by)
    {
        var target = Now + by;
        while (_items.Where(i => i.Due <= target).OrderBy(i => i.Due).FirstOrDefault() is { } next)
        {
            _items.Remove(next);
            Now = next.Due;
            next.Action();
        }

        Now = target;
    }

    public void Advance(int milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));

    private sealed class Item(TimeSpan due, Action action, List<Item> owner) : IDisposable
    {
        public TimeSpan Due { get; } = due;

        public Action Action { get; } = action;

        public void Dispose() => owner.Remove(this);
    }
}
