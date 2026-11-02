using System.Diagnostics;
using System.Globalization;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Model;
using DreamBooks.Core.Search;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>
/// The search and the Pages tab on long documents (FR-13, FR-103, NFR-03, NFR-04, NFR-06): a copy of a real e-book when
/// one is available (skipped otherwise) and a generated 400-page text document. With the mark scan, a search scan and
/// the thumbnail queue all running, pages still show quickly, also during a save; memory stays bounded; the search
/// never runs two jobs at once nor leaks text-page handles. Only counts, times and sizes are reported (NFR-12): never a
/// query, a match or page text.
/// </summary>
public sealed partial class MarksIntegrationTests
{
    /// <summary>A query that is (almost certainly) on no page: the search reads every page, the worst case for time.</summary>
    private const string _rareQuery = "zqxjv";

    /// <summary>Two short words, one of which is common in an Italian or an English book (counts only are reported).</summary>
    private static readonly string[] _commonQueries = ["che", "the"];

    // ---- Search over the real book ------------------------------------------------------------------------------

    [Fact]
    public void Searching_a_real_e_book_copy_is_quick_bounded_in_memory_one_job_at_a_time_and_leaks_no_handle()
    {
        var copy = CopyLongestRealDocument();
        if (copy is null)
        {
            return; // skipped (reported in the output)
        }

        var probe = new SearchProbe();
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(copy, scheduler, probe: null, productionGap: true);
            vm.SearchSourceFactory = opened => opened.Viewer?.Current is { } renderer ? new ProbeSearchSource(renderer, probe) : null;
            var view = new DocumentView { DataContext = vm }; // real PDFium bitmaps
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                WpfTestThread.PumpUntil(() => ShowsCurrentSharp(view, vm.Viewport.VisiblePages[0]), _timeoutMs);
                IdleBitmaps(view);
                var pages = vm.Viewport.PageCount;
                var before = PrivateBytes();
                var peak = before;

                var lines = new List<string>();
                var times = new List<TimeSpan>();
                var queries = _commonQueries.Concat([_rareQuery, _rareQuery, _rareQuery]).ToArray();
                for (var q = 0; q < queries.Length; q++)
                {
                    vm.Search.ClearCommand.Execute(null); // the same query typed again would keep the finished search
                    Assert.Null(vm.Search.Index);
                    var watch = Stopwatch.StartNew();
                    ApplyQuery(vm, scheduler, queries[q]);
                    WpfTestThread.PumpUntil(() => vm.SearchScannerForTest is { IsCompleted: true }, 120_000);
                    watch.Stop();
                    RefreshAll(scheduler, view);
                    var scanner = vm.SearchScannerForTest!;
                    var index = vm.Search.Index!;
                    Assert.Equal(0, scanner.FailedCount);
                    Assert.True(index.Truncated || index.SearchedCount == pages, "an untruncated search read every page");
                    times.Add(watch.Elapsed);
                    peak = Math.Max(peak, PrivateBytes());
                    lines.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"query {q + 1}: {watch.ElapsedMilliseconds} ms, pages searched {index.SearchedCount}, hits {index.TotalHits}, "
                        + $"truncated {index.Truncated}, search ticks {view.Rail.SearchTicks.Centres.Count}"));
                }

                vm.Search.ClearCommand.Execute(null);
                RefreshAll(scheduler, view);
                var after = PrivateBytes();
                var deltaMb = (Math.Max(peak, after) - before) / (1024.0 * 1024.0);
                var full = times[^3..];
                _output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"real copy, pages {pages}: {string.Join("; ", lines)}; max searches in flight {probe.MaxInFlight}, "
                    + $"peak private memory delta {deltaMb:0.0} MB, text-page handles open after {PdfTextSearcher.OpenHandleCount}"));
                if (full.Max() > TimeSpan.FromSeconds(10))
                {
                    _output.WriteLine($"WARNING: a full search took more than 10 s ({full.Max().TotalMilliseconds:0} ms).");
                }

                // One job at a time, no handle left open, the PDFium thread alive and repeated full searches stable.
                Assert.True(probe.MaxInFlight <= 1, $"{probe.MaxInFlight} searches in flight");
                Assert.Equal(0, PdfTextSearcher.OpenHandleCount);
                Assert.True(full.Max() < (full.Min() * 3) + TimeSpan.FromSeconds(1), "repeated full searches slowed down");
                Assert.NotNull(Wait(vm.Renderer!.RenderPageAsync(0, 100, 130)));
                Assert.True(deltaMb < 100, $"memory grew by {deltaMb:0.0} MB");
                Assert.False(vm.IsDirty);
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    // ---- All three background jobs on the real book ----------------------------------------------------------

    [Fact]
    public void With_the_mark_scan_a_search_and_thumbnails_running_a_real_e_book_copy_shows_its_pages_quickly()
    {
        var copy = CopyLongestRealDocument();
        if (copy is null)
        {
            return; // skipped (reported in the output)
        }

        var marks = new ScanProbe { DelayPerPage = TimeSpan.FromMilliseconds(3) }; // keeps the mark scan running
        var searches = new SearchProbe { DelayPerPage = TimeSpan.FromMilliseconds(3) }; // likewise the search
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(copy, scheduler, marks, productionGap: true);
            vm.SearchSourceFactory = opened => opened.Viewer?.Current is { } renderer ? new ProbeSearchSource(renderer, searches) : null;
            var view = new DocumentView { DataContext = vm }; // real PDFium bitmaps
            var cache = NewThumbnailCache(vm, view);
            PagesListView? list = null;
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                var pages = vm.Viewport.PageCount;

                // Everything asked for at once: the first renders, the Pages tab's thumbnails, both scans.
                var watch = Stopwatch.StartNew();
                LayOut(view, 1000, 800);
                list = ShowPagesList(vm, cache);
                scheduler.Advance(MainViewModel.MarkScanStartDelay);
                ApplyQuery(vm, scheduler, _rareQuery);
                var firstPage = vm.Viewport.VisiblePages[0];
                WpfTestThread.PumpUntil(() => ShowsCurrentSharp(view, firstPage), _timeoutMs);
                var firstSharp = watch.Elapsed;

                // The list scrolled to the middle (its thumbnails queued), both scans still going: a far jump.
                var viewer = ListScrollViewer(list);
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight / 2);
                list.UpdateLayout();
                WpfTestThread.Drain();
                WpfTestThread.PumpUntil(() => vm.MarkScannerForTest is not null && vm.SearchScannerForTest is not null, _timeoutMs);
                var markScanner = vm.MarkScannerForTest!;
                var searchScanner = vm.SearchScannerForTest!;
                WpfTestThread.PumpUntil(() => markScanner.ProcessedCount > 5 && searchScanner.ProcessedCount > 5, _timeoutMs);
                var queued = view.Bitmaps.PendingThumbnailCount;
                var bothRunning = !markScanner.IsCompleted && !searchScanner.IsCompleted;

                var target = (pages * 3) / 4;
                watch.Restart();
                vm.Viewport.ScrollToPage(target, recordHistory: true);
                Settle(view);
                WpfTestThread.PumpUntil(() => view.SurfaceFor(target)?.PageImage is not null, _timeoutMs);
                var preview = watch.Elapsed;
                WpfTestThread.PumpUntil(() => ShowsCurrentSharp(view, target), _timeoutMs);
                var sharp = watch.Elapsed;

                Assert.Null(vm.ErrorMessage);
                _output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"real copy, pages {pages}, mark scan + search + thumbnails: first page sharp {firstSharp.TotalMilliseconds:0} ms; "
                    + $"far jump preview {preview.TotalMilliseconds:0} ms, sharp {sharp.TotalMilliseconds:0} ms "
                    + $"(thumbnails queued {queued}, both scans running {bothRunning})"));
                WarnIfSlow(preview, sharp, firstSharp);
                Assert.True(bothRunning, "both scans were running at the jump");
                Assert.True(preview <= _previewBudget, $"preview after {preview.TotalMilliseconds:0} ms");
                Assert.True(sharp <= _sharpBudget && firstSharp <= _sharpBudget, $"sharp after {sharp.TotalMilliseconds:0} ms");
            }
            finally
            {
                list?.OnHidden();
                Close(vm, view);
            }
        });
    }

    // ---- A save while searching with the Pages tab open --------------------------------------------------------

    [Fact]
    public void A_far_jump_while_a_save_verifies_during_a_search_with_the_pages_tab_open_shows_its_page_quickly()
    {
        var path = AuthorText("save-search.pdf", 400, new() { [5] = [MarginLine(40, 200, 360)] });
        MeasureSaveWhileSearching(path, "generated");
    }

    [Fact]
    public void A_far_jump_while_a_save_of_a_real_e_book_copy_verifies_during_a_search_with_the_pages_tab_open_shows_its_page_quickly()
    {
        var copy = CopyLongestRealDocument();
        if (copy is null)
        {
            return; // skipped (reported in the output)
        }

        MeasureSaveWhileSearching(copy, "real copy");
    }

    /// <summary>
    /// The document made dirty, a search running (slowed so that it runs through the save), the Pages tab shown and
    /// scrolled (thumbnails queued), then a plain Save: during its packed-copy check, a far jump shows the target's
    /// preview and sharp bitmap quickly; the target never turns white again until the save's renderer swap has shown it
    /// sharp again; the search goes on afterwards over the new renderer and finishes.
    /// </summary>
    private void MeasureSaveWhileSearching(string path, string label)
    {
        var searches = new SearchProbe { DelayPerPage = TimeSpan.FromMilliseconds(2) };
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler, probe: null, productionGap: true);
            vm.SearchSourceFactory = opened => opened.Viewer?.Current is { } renderer ? new ProbeSearchSource(renderer, searches) : null;
            var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.SaveService = new DocumentSaveService(new PdfDocumentSaver { AfterPackedWritten = _ => verifying.TrySetResult() }, _dialogs);
            var view = new DocumentView { DataContext = vm }; // real PDFium bitmaps
            var cache = NewThumbnailCache(vm, view);
            PagesListView? list = null;
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                IdleBitmaps(view);
                scheduler.Advance(MainViewModel.MarkScanStartDelay);
                LoadEditors(view);
                var stroke = new InkPoint[] { new(100, 100, 0.5f, null, null, null, 0), new(200, 120, 0.5f, null, null, null, 10) };
                Assert.True(view.SurfaceFor(0)!.Editor!.CommitStroke(stroke, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1));
                Assert.True(vm.IsDirty);

                ApplyQuery(vm, scheduler, _rareQuery);
                list = ShowPagesList(vm, cache);
                var listViewer = ListScrollViewer(list);
                listViewer.ScrollToVerticalOffset(listViewer.ScrollableHeight / 3);
                list.UpdateLayout();
                WpfTestThread.Drain();
                var index = vm.Search.Index!;

                var saving = vm.SaveCommand.ExecuteAsync(null);
                WpfTestThread.PumpUntil(() => verifying.Task.IsCompleted || saving.IsCompleted, _timeoutMs);
                Assert.False(saving.IsCompleted);
                Assert.True(vm.Search.IsSearching);

                var pages = vm.Viewport.PageCount;
                var target = pages - 20;
                var watch = Stopwatch.StartNew();
                vm.Viewport.ScrollToPage(target, recordHistory: true);
                Settle(view);
                WpfTestThread.PumpUntil(() => view.SurfaceFor(target)?.PageImage is not null, _timeoutMs);
                var preview = watch.Elapsed;
                WpfTestThread.PumpUntil(() => ShowsCurrentSharp(view, target), _timeoutMs);
                var sharp = watch.Elapsed;

                var surface = view.SurfaceFor(target)!;
                WpfTestThread.PumpUntil(
                    () =>
                    {
                        Assert.NotNull(surface.PageImage); // never white again
                        return saving.IsCompleted;
                    },
                    _timeoutMs);
                Wait(saving);
                var saved = watch.Elapsed;
                var swapWatch = Stopwatch.StartNew();
                WpfTestThread.PumpUntil(
                    () =>
                    {
                        Assert.NotNull(surface.PageImage);
                        return ShowsCurrentSharp(view, target);
                    },
                    _timeoutMs);
                var afterSwap = swapWatch.Elapsed;
                Assert.Null(vm.ErrorMessage);
                Assert.False(vm.IsDirty);

                // The search goes on over the new renderer, into the same index, to the end.
                WpfTestThread.PumpUntil(() => vm.SearchScannerForTest is { IsCompleted: true }, 120_000);
                Assert.Same(index, vm.Search.Index);
                Assert.Equal(pages, index.SearchedCount);
                Assert.True(searches.MaxInFlight <= 1, $"{searches.MaxInFlight} searches in flight");
                _output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{label}, pages {pages}, save + search + Pages tab: jump preview {preview.TotalMilliseconds:0} ms, "
                    + $"sharp {sharp.TotalMilliseconds:0} ms, save done {saved.TotalMilliseconds:0} ms, "
                    + $"sharp again {afterSwap.TotalMilliseconds:0} ms after the save, thumbnails cached {cache.Count}"));
                WarnIfSlow(preview, sharp, afterSwap);
                Assert.True(preview <= _previewBudget, $"preview after {preview.TotalMilliseconds:0} ms");
                Assert.True(sharp <= _sharpBudget, $"sharp after {sharp.TotalMilliseconds:0} ms");
                Assert.True(afterSwap <= _sharpBudget, $"sharp {afterSwap.TotalMilliseconds:0} ms after the save");
            }
            finally
            {
                list?.OnHidden();
                Close(vm, view);
            }
        });
    }

    // ---- The Pages tab over the real book, with the scans ------------------------------------------------------

    [Fact]
    public void The_pages_tab_of_a_real_e_book_copy_realises_only_visible_rows_and_stays_in_budget_with_both_scans_running()
    {
        var copy = CopyLongestRealDocument();
        if (copy is null)
        {
            return; // skipped (reported in the output)
        }

        var searches = new SearchProbe();
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(copy, scheduler, probe: null, productionGap: true);
            vm.SearchSourceFactory = opened => opened.Viewer?.Current is { } renderer ? new ProbeSearchSource(renderer, searches) : null;
            var view = new DocumentView { DataContext = vm }; // real PDFium bitmaps
            var cache = NewThumbnailCache(vm, view);
            PagesListView? list = null;
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                WpfTestThread.PumpUntil(() => ShowsCurrentSharp(view, vm.Viewport.VisiblePages[0]), _timeoutMs);
                IdleBitmaps(view);
                var pages = vm.Viewport.PageCount;
                var before = PrivateBytes();

                // The mark scan, a long search and the Pages tab together.
                scheduler.Advance(MainViewModel.MarkScanStartDelay);
                ApplyQuery(vm, scheduler, _rareQuery);
                var watch = Stopwatch.StartNew();
                list = ShowPagesList(vm, cache);
                var opened = watch.Elapsed;
                Assert.InRange(RealisedRows(list).Count, 1, 30);

                // Scroll the list through every page, letting the visible rows load (as a reader looking for a page).
                var viewer = ListScrollViewer(list);
                var maxRealised = 0;
                var seen = new HashSet<int>();
                var steps = 0;
                var peak = before;
                watch.Restart();
                for (var offset = 0.0; ; offset += viewer.ViewportHeight)
                {
                    viewer.ScrollToVerticalOffset(Math.Min(offset, viewer.ScrollableHeight));
                    list.UpdateLayout();
                    WpfTestThread.Drain();
                    var rows = RealisedRows(list).Select(r => ((PageItem)r.DataContext).PageIndex).ToArray();
                    maxRealised = Math.Max(maxRealised, rows.Length);
                    seen.UnionWith(rows);
                    TryPumpUntil(() => rows.All(p => cache.TryGetBitmap(p, out _)), 2_000);
                    Assert.True(cache.Bytes <= PageThumbnailCache.DefaultByteBudget, $"cache at {cache.Bytes} bytes");
                    steps++;
                    if (steps % 50 == 0)
                    {
                        peak = Math.Max(peak, PrivateBytes());
                    }

                    if (offset >= viewer.ScrollableHeight)
                    {
                        break;
                    }
                }

                var scrolled = watch.Elapsed;
                Assert.Contains(pages - 1, seen);

                // With the panel open (rows loaded, scans still possibly going): the document scrolled far is sharp quickly.
                var target = pages / 3;
                watch.Restart();
                vm.Viewport.ScrollToPage(target, recordHistory: true);
                Settle(view);
                WpfTestThread.PumpUntil(() => ShowsCurrentSharp(view, target), _timeoutMs);
                var jump = watch.Elapsed;

                WpfTestThread.PumpUntil(
                    () => vm.SearchScannerForTest is { IsCompleted: true } && vm.MarkScannerForTest is { IsCompleted: true },
                    120_000);
                peak = Math.Max(peak, PrivateBytes());
                var deltaMb = (peak - before) / (1024.0 * 1024.0);
                _output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"real copy, pages {pages}, Pages tab + mark scan + search: list shown in {opened.TotalMilliseconds:0} ms, "
                    + $"scrolled through {steps} screens in {scrolled.TotalMilliseconds:0} ms, rows realised at most {maxRealised}, "
                    + $"thumbnails cached {cache.Count} ({cache.Bytes / (1024.0 * 1024.0):0.0} MB), far jump sharp {jump.TotalMilliseconds:0} ms, "
                    + $"peak private memory delta {deltaMb:0.0} MB, max searches in flight {searches.MaxInFlight}"));
                if (deltaMb > 150)
                {
                    _output.WriteLine($"WARNING: memory grew by more than 150 MB ({deltaMb:0.0} MB).");
                }

                Assert.InRange(maxRealised, 1, 40);
                Assert.True(cache.Bytes <= PageThumbnailCache.DefaultByteBudget);
                Assert.True(searches.MaxInFlight <= 1, $"{searches.MaxInFlight} searches in flight");
                Assert.True(jump <= _sharpBudget, $"jump sharp after {jump.TotalMilliseconds:0} ms");
                Assert.True(deltaMb < 250, $"memory grew by {deltaMb:0.0} MB");
                Assert.False(vm.IsDirty);
                Assert.Null(vm.ErrorMessage);
            }
            finally
            {
                list?.OnHidden();
                Close(vm, view);
            }
        });
    }

    private void WarnIfSlow(TimeSpan preview, TimeSpan sharp, TimeSpan otherSharp)
    {
        if (preview > _previewBudget || sharp > _sharpBudget || otherSharp > _sharpBudget)
        {
            _output.WriteLine("WARNING: slower than the budget (300 ms preview, 1 s sharp).");
        }
    }

    private static bool TryPumpUntil(Func<bool> condition, int timeoutMs)
    {
        try
        {
            WpfTestThread.PumpUntil(condition, timeoutMs);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
