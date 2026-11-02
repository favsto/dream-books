using System.Diagnostics;
using System.Globalization;
using System.Windows.Media.Imaging;
using DreamBooks.App.Views;

namespace DreamBooks.App.Tests;

/// <summary>
/// The Pages tab's thumbnails never delay the pages the reader looks at (FR-103, NFR-03, NFR-04): with a panel's worth
/// of thumbnails queued, the first page of a freshly opened document and a page reached by a far jump are sharp as
/// quickly as without them. Real view model, view and PDFium renderer; only timings are reported (NFR-12).
/// </summary>
public sealed partial class MarksIntegrationTests
{
    /// <summary>A thumbnail queue the size of a scrolled Pages tab (realised rows plus the cache, twice over).</summary>
    private const int _queuedThumbnails = 24;

    [Fact]
    public void Queued_thumbnails_never_delay_the_first_page_or_a_far_jump()
    {
        var path = Author("thumbs.pdf", 400, new() { [10] = [MarginLine(40, 200, 360)] });
        AssertThumbnailsNeverDelay(MeasureWithThumbnails(path, "generated"));
    }

    [Fact]
    public void Queued_thumbnails_never_delay_the_pages_of_a_real_e_book_copy()
    {
        var copy = CopyLongestRealDocument();
        if (copy is null)
        {
            return; // skipped (reported in the output)
        }

        AssertThumbnailsNeverDelay(MeasureWithThumbnails(copy, "real copy"));
    }

    private static void AssertThumbnailsNeverDelay(ThumbnailTimes times)
    {
        Assert.True(times.FirstPageSharp <= _sharpBudget, $"first page sharp after {times.FirstPageSharp.TotalMilliseconds:0} ms");
        Assert.True(times.JumpSharp <= _sharpBudget, $"jump sharp after {times.JumpSharp.TotalMilliseconds:0} ms");
        Assert.True(times.Rendered > 0, "thumbnails were rendered");
    }

    /// <summary>
    /// Opens the document with thumbnails asked for before its first render (the panel open on the Pages tab), measures
    /// the first page's sharp bitmap, lets some thumbnails render, then jumps far with thumbnails still queued and
    /// measures the target's sharp bitmap; then the time per thumbnail render.
    /// </summary>
    private ThumbnailTimes MeasureWithThumbnails(string path, string label)
    {
        var result = default(ThumbnailTimes);
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler, probe: null, productionGap: true);
            var view = new DocumentView { DataContext = vm }; // real PDFium bitmaps
            var thumbnails = new List<Task<BitmapSource?>>();
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                var pageCount = vm.Viewport.PageCount;
                var watch = Stopwatch.StartNew();
                LayOut(view, 1000, 800);
                for (var i = 0; i < _queuedThumbnails; i++)
                {
                    thumbnails.Add(view.Bitmaps.RenderThumbnailAsync(i, CancellationToken.None));
                }

                WpfTestThread.PumpUntil(() => ShowsCurrentSharp(view, 0), _timeoutMs);
                var firstPage = watch.Elapsed;

                // Some thumbnails render once the window is idle; then a far jump while the rest are queued.
                WpfTestThread.PumpUntil(() => thumbnails.Count(t => t.IsCompleted) >= 3, _timeoutMs);
                for (var i = 0; i < _queuedThumbnails; i++)
                {
                    thumbnails.Add(view.Bitmaps.RenderThumbnailAsync((pageCount / 4) + i, CancellationToken.None));
                }

                var target = (pageCount * 3) / 4;
                watch.Restart();
                vm.Viewport.ScrollToPage(target, recordHistory: true);
                Settle(view);
                WpfTestThread.PumpUntil(() => ShowsCurrentSharp(view, target), _timeoutMs);
                var jump = watch.Elapsed;
                var stillQueued = view.Bitmaps.PendingThumbnailCount;

                watch.Restart();
                var before = thumbnails.Count(t => t.IsCompleted);
                WpfTestThread.PumpUntil(() => thumbnails.All(t => t.IsCompleted), _timeoutMs);
                var drained = watch.Elapsed;
                var rendered = thumbnails.Count(t => t.IsCompletedSuccessfully && t.Result is not null);
                var perThumbnail = thumbnails.Count - before > 0 ? drained / (thumbnails.Count - before) : TimeSpan.Zero;

                // For reference: the same kind of far jump with no thumbnail queued.
                var reference = (pageCount / 2) + 5;
                watch.Restart();
                vm.Viewport.ScrollToPage(reference, recordHistory: true);
                Settle(view);
                WpfTestThread.PumpUntil(() => ShowsCurrentSharp(view, reference), _timeoutMs);
                var plainJump = watch.Elapsed;

                Assert.Null(vm.ErrorMessage);
                _output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{label}, pages {pageCount}: first page sharp {firstPage.TotalMilliseconds:0} ms with {_queuedThumbnails} thumbnails asked; "
                    + $"far jump sharp {jump.TotalMilliseconds:0} ms with {stillQueued} still queued "
                    + $"({plainJump.TotalMilliseconds:0} ms with none); "
                    + $"{rendered}/{thumbnails.Count} rendered, {perThumbnail.TotalMilliseconds:0.0} ms each when idle"));
                result = new ThumbnailTimes(firstPage, jump, rendered);
            }
            finally
            {
                Close(vm, view);
            }
        });

        return result;
    }

    private readonly record struct ThumbnailTimes(TimeSpan FirstPageSharp, TimeSpan JumpSharp, int Rendered);
}
