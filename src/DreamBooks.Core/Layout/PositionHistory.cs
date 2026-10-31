namespace DreamBooks.Core.Layout;

/// <summary>
/// Bounded back stack of view positions (FR-09). Pushing a position equal to the top (same page,
/// less than 1 pt apart vertically) is ignored; beyond capacity the oldest entry is dropped.
/// </summary>
public sealed class PositionHistory(int capacity = 20)
{
    private const double _toleranceYPt = 1.0;

    private readonly List<ViewPosition> _items = [];

    public event EventHandler? Changed;

    public bool CanGoBack => _items.Count > 0;

    public void Push(ViewPosition p)
    {
        if (_items.Count > 0)
        {
            var top = _items[^1];
            if (top.PageIndex == p.PageIndex && Math.Abs(top.YPt - p.YPt) < _toleranceYPt)
            {
                return;
            }
        }

        _items.Add(p);
        if (_items.Count > capacity)
        {
            _items.RemoveAt(0);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TryPop(out ViewPosition p)
    {
        if (_items.Count == 0)
        {
            p = default;
            return false;
        }

        p = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0)
        {
            return;
        }

        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
