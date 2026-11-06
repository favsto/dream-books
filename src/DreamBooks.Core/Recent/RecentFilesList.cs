namespace DreamBooks.Core.Recent;

/// <summary>
/// The recently opened documents, newest first, at most <see cref="MaxCount"/> (FR-03). Paths compare case-insensitively.
/// Page and page count are kept inside their valid range: a list read from a damaged file never holds nonsense.
/// </summary>
public sealed class RecentFilesList
{
    public const int MaxCount = 20;

    private readonly List<RecentFile> _items = [];

    /// <param name="items">Newest first, as <see cref="Items"/> gives them; blank paths and repeated paths are dropped.</param>
    public RecentFilesList(IEnumerable<RecentFile>? items = null)
    {
        foreach (var item in items ?? [])
        {
            if (_items.Count >= MaxCount)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(item.Path) && IndexOf(item.Path) < 0)
            {
                _items.Add(Clamp(item));
            }
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RecentFile> Items => _items;

    /// <summary>Puts the file first, replacing an earlier entry of the same path; the oldest entries beyond the limit go.</summary>
    public void Touch(RecentFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(file.Path))
        {
            return;
        }

        var existing = IndexOf(file.Path);
        if (existing >= 0)
        {
            _items.RemoveAt(existing);
        }

        _items.Insert(0, Clamp(file));
        if (_items.Count > MaxCount)
        {
            _items.RemoveRange(MaxCount, _items.Count - MaxCount);
        }
    }

    /// <summary>True when the path was in the list.</summary>
    public bool Remove(string path)
    {
        var index = IndexOf(path);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        return true;
    }

    /// <summary>Where the reader stopped; the order does not change. An unknown path is ignored.</summary>
    public void UpdateProgress(string path, int page, int pageCount)
    {
        var index = IndexOf(path);
        if (index >= 0)
        {
            _items[index] = Clamp(_items[index] with { Page = page, PageCount = pageCount });
        }
    }

    private static RecentFile Clamp(RecentFile file)
    {
        var count = Math.Max(1, file.PageCount);
        return file with { PageCount = count, Page = Math.Clamp(file.Page, 0, count - 1) };
    }

    private int IndexOf(string path) =>
        _items.FindIndex(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
}
