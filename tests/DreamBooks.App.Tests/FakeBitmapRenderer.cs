using System.Windows.Media;
using System.Windows.Media.Imaging;
using DreamBooks.Core.Rendering;

namespace DreamBooks.App.Tests;

/// <summary>
/// Page bitmaps on demand for the render scheduler: every request waits until the test completes it, unless
/// <see cref="Auto"/> completes it at once. Records each request with the thread it arrived on, so tests can check that
/// no render is ever asked on the UI thread (NFR-04). Thread-safe: requests arrive on thread-pool threads.
/// </summary>
internal sealed class FakeBitmapRenderer : IBitmapRenderer<BitmapSource>
{
    private readonly Lock _lock = new();
    private readonly List<Request> _requests = [];

    /// <summary>Completes every request at once with a bitmap of the requested size.</summary>
    public bool Auto { get; set; }

    /// <summary>A cancelled request ends with no bitmap, as a real renderer would (otherwise it finishes anyway).</summary>
    public bool HonourCancellation { get; set; } = true;

    /// <summary>Pages whose renders return null (e.g. while a save has the file released).</summary>
    public Func<int, bool>? Fails { get; set; }

    /// <summary>Pages whose renders throw (a misbehaving renderer).</summary>
    public Func<int, bool>? Throws { get; set; }

    public Request[] Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public Request[] Pending => Requests.Where(r => !r.Completion.Task.IsCompleted).ToArray();

    public Task<BitmapSource?> RenderAsync(int pageIndex, int pixelWidth, int pixelHeight, CancellationToken ct)
    {
        var request = new Request(
            pageIndex,
            pixelWidth,
            pixelHeight,
            ct,
            Environment.CurrentManagedThreadId,
            new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_lock)
        {
            _requests.Add(request);
        }

        if (Throws?.Invoke(pageIndex) == true)
        {
            throw new InvalidOperationException("Fake renderer failure.");
        }

        if (Fails?.Invoke(pageIndex) == true)
        {
            request.Completion.TrySetResult(null);
        }
        else if (Auto)
        {
            request.Completion.TrySetResult(BitmapOf(pixelWidth, pixelHeight));
        }
        else if (HonourCancellation)
        {
            ct.Register(() => request.Completion.TrySetCanceled(ct));
        }

        return request.Completion.Task;
    }

    /// <summary>Completes the latest pending request of a page with a bitmap of its size.</summary>
    public Request Complete(int pageIndex)
    {
        var request = Pending.Last(r => r.Page == pageIndex);
        request.Completion.TrySetResult(BitmapOf(request.Width, request.Height));
        return request;
    }

    /// <summary>Completes every request of a page, pending or cancelled-but-running.</summary>
    public void CompleteAll(int pageIndex)
    {
        foreach (var request in Requests.Where(r => r.Page == pageIndex))
        {
            request.Completion.TrySetResult(BitmapOf(request.Width, request.Height));
        }
    }

    public void CompletePending()
    {
        foreach (var request in Pending)
        {
            request.Completion.TrySetResult(BitmapOf(request.Width, request.Height));
        }
    }

    public static BitmapSource BitmapOf(int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public sealed record Request(
        int Page, int Width, int Height, CancellationToken Token, int ThreadId, TaskCompletionSource<BitmapSource?> Completion);
}
