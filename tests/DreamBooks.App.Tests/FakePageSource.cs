using DreamBooks.App.ViewModels;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

/// <summary>
/// An in-memory page source: page sizes, stored strokes and unreadable counts per page, loads counted.
/// Loads complete synchronously, so navigation finishes without awaiting another thread.
/// </summary>
internal sealed class FakePageSource : IPageSource, IAsyncDisposable
{
    private readonly PageDimensions[] _dimensions;

    public FakePageSource(params (double Width, double Height)[] pages) =>
        _dimensions = pages.Select(p => new PageDimensions(p.Width, p.Height)).ToArray();

    public Dictionary<int, IReadOnlyList<InkStroke>> Strokes { get; } = [];

    public Dictionary<int, int> Unreadable { get; } = [];

    public HashSet<int> FailingPages { get; } = [];

    public List<int> Loads { get; } = [];

    public bool IsDisposed { get; private set; }

    public int PageCount => _dimensions.Length;

    public PageDimensions GetPageDimensions(int pageIndex) => _dimensions[pageIndex];

    /// <summary>Pages whose load waits until the gate is completed.</summary>
    public Dictionary<int, TaskCompletionSource> Gates { get; } = [];

    public Task<PageStrokes> LoadStrokesAsync(int pageIndex, CancellationToken ct = default)
    {
        Loads.Add(pageIndex);
        if (Gates.TryGetValue(pageIndex, out var gate))
        {
            return GatedAsync(gate.Task, pageIndex);
        }

        return Complete(pageIndex);
    }

    private async Task<PageStrokes> GatedAsync(Task gate, int pageIndex)
    {
        await gate;
        return await Complete(pageIndex);
    }

    private Task<PageStrokes> Complete(int pageIndex)
    {
        if (FailingPages.Contains(pageIndex))
        {
            return Task.FromException<PageStrokes>(new IOException("unreadable page"));
        }

        return Task.FromResult(new PageStrokes(
            Strokes.GetValueOrDefault(pageIndex) ?? [],
            Unreadable.GetValueOrDefault(pageIndex)));
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Makes <paramref name="vm"/> open this source for whatever path the dialog returns.</summary>
    public void InstallOn(MainViewModel vm) =>
        vm.OpenDocument = (path, _) => Task.FromResult(new OpenedDocument(this, this, path));
}
