using DreamBooks.App.Services;

namespace DreamBooks.App.Tests;

/// <summary>
/// A scheduler driven by hand: nothing runs until <see cref="Advance"/> moves time past its due time. Scheduling
/// and cancelling are thread-safe (a background scan may schedule); actions run on the thread calling Advance.
/// </summary>
internal sealed class ManualScheduler : IDelayScheduler
{
    private readonly object _gate = new();
    private readonly List<Item> _items = [];
    private TimeSpan _now;

    public TimeSpan Now
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        lock (_gate)
        {
            var item = new Item(_now + delay, action, this);
            _items.Add(item);
            return item;
        }
    }

    public void Advance(TimeSpan by)
    {
        TimeSpan target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            Item? next;
            lock (_gate)
            {
                next = _items.Where(i => i.Due <= target).OrderBy(i => i.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _items.Remove(next);
                _now = next.Due;
            }

            next.Action();
        }
    }

    public void Advance(int milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));

    private void Remove(Item item)
    {
        lock (_gate)
        {
            _items.Remove(item);
        }
    }

    private sealed class Item(TimeSpan due, Action action, ManualScheduler owner) : IDisposable
    {
        public TimeSpan Due { get; } = due;

        public Action Action { get; } = action;

        public void Dispose() => owner.Remove(this);
    }
}
