using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DreamBooks.App.Resources;
using DreamBooks.App.Ink;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;
using DreamBooks.Core.Search;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Interop;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// Cross-layer sweep of the text search and the Pages tab (FR-13, FR-103, FR-105, NFR-06) on generated PDFs with a text
/// layer: the real <see cref="MainViewModel"/>, <see cref="DocumentView"/> (never shown), PDFium search through the real
/// renderer, the mark scan and the save pipeline. The search and the thumbnails are view-only: they never change what is
/// saved, the dirty state or the journal. Only counts and times are reported (NFR-12).
/// </summary>
public sealed partial class MarksIntegrationTests
{
    /// <summary>The query of the generated documents: every planted variant folds to it (case and accents).</summary>
    private const string _query = "perche";

    /// <summary>Planted hits of the 300-page text document: page and baseline (user space, y up); page 250 has two on one line.</summary>
    private static readonly (int Page, double Baseline, string Text)[] _planted =
    [
        (3, 700, "Il motivo perché conta"),
        (47, 400, "PERCHÉ NO"),
        (120, 100, "Perche si"),
        (199, 650, "e perchè mai"),
        (250, 500, "perché e perché"),
        (288, 300, "dunque perche"),
    ];

    // ---- Search over a long document --------------------------------------------------------------------------

    [Fact]
    public void A_300_page_search_finds_the_planted_hits_ticks_them_beside_marks_and_notes_and_visits_them_in_order()
    {
        var path = AuthorText("search.pdf", 300, new()
        {
            [10] = [MarginLine(40, 200, 360)], // a mark
            [30] = [Scribble()], // a note
        });

        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler, probe: null);
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                scheduler.Advance(MainViewModel.MarkScanStartDelay);
                WaitScanCompleted(vm.MarkScannerForTest!);
                var start = vm.Viewport.Position;

                ApplyQuery(vm, scheduler, _query);
                WaitSearchCompleted(vm);
                RefreshAll(scheduler, view);

                // Exactly the planted hits: case and accents folded, two on one line counted twice.
                var index = vm.Search.Index!;
                Assert.Equal(300, index.SearchedCount);
                Assert.Equal(0, vm.SearchScannerForTest!.FailedCount);
                Assert.Equal(7, index.TotalHits);
                Assert.Equal([3, 47, 120, 199, 250, 250, 288], index.AllHits().Select(h => h.PageIndex).ToArray());
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.SearchResultsFormat, 7), vm.Search.CountText);

                // Three tick kinds, each placed with the knob's mapping: the search ticks are the hits (the two on one
                // line share a tick), the marks' and notes' ticks are still there.
                var rail = view.Rail;
                var expected = RailTickLayout.SearchPositions(index.AllHits(), vm.Viewport.Layout!, vm.Viewport.WindowHeightPt, rail.TrackLength)
                    .Select(p => p + (rail.KnobHeight / 2))
                    .ToArray();
                Assert.Equal(6, expected.Length);
                Assert.Equal(expected.Length, rail.SearchTicks.Centres.Count);
                for (var i = 0; i < expected.Length; i++)
                {
                    Assert.Equal(expected[i], rail.SearchTicks.Centres[i], 6);
                }

                Assert.Single(rail.Ticks.Centres);
                Assert.Single(rail.NoteTicks.Centres);

                // Next visits every hit in order (the same-line pair one by one) and wraps with its message.
                var visited = new List<SearchHit>();
                for (var step = 0; step < 7; step++)
                {
                    vm.Search.NextCommand.Execute(null);
                    Settle(view);
                    var current = vm.Search.Current!;
                    visited.Add(current);

                    // The current hit is highlighted on its (visible) page, with the stronger brush.
                    var surface = view.SurfaceFor(current.PageIndex)!;
                    Assert.Contains(surface.HighlightLayer.ViewRects(), r => r.IsCurrent);
                }

                Assert.Equal(index.AllHits(), visited);
                Assert.Null(vm.Search.StatusText);
                vm.Search.NextCommand.Execute(null);
                Settle(view);
                Assert.Equal(visited[0], vm.Search.Current);
                Assert.Equal(Strings.SearchWrappedToStart, vm.Search.StatusText);
                vm.Search.PreviousCommand.Execute(null);
                Settle(view);
                Assert.Equal(visited[^1], vm.Search.Current);
                Assert.Equal(Strings.SearchWrappedToEnd, vm.Search.StatusText);
                vm.Search.PreviousCommand.Execute(null);
                Settle(view);
                Assert.Equal(visited[^2], vm.Search.Current);
                Assert.Equal(250, vm.Viewport.Position.PageIndex);
                Assert.Contains(view.SurfaceFor(250)!.HighlightLayer.ViewRects(), r => !r.IsCurrent); // its neighbour

                // Back returns to where the reader was before the search, in one step.
                vm.Viewport.BackCommand.Execute(null);
                Settle(view);
                Assert.Equal(start, vm.Viewport.Position);

                // Esc: no highlight on any surface, no search tick; the marks' and notes' ticks stay.
                vm.Search.NextCommand.Execute(null);
                Settle(view);
                Assert.Contains(view.ActiveSurfaces.Values, s => s.HighlightLayer.HasHighlights);
                vm.Search.ClearCommand.Execute(null);
                RefreshAll(scheduler, view);
                Assert.Null(vm.Search.Index);
                Assert.All(view.ActiveSurfaces.Values, s => Assert.False(s.HighlightLayer.HasHighlights));
                Assert.Empty(rail.SearchTicks.Centres);
                Assert.Single(rail.Ticks.Centres);
                Assert.Single(rail.NoteTicks.Centres);

                // Searching read the document only.
                Assert.False(vm.IsDirty);
                Assert.False(vm.Session!.Undo.CanUndo);
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    // ---- Search with the mark scan, a stroke, a save and a new query ---------------------------------------------

    [Fact]
    public void A_search_during_the_mark_scan_a_stroke_and_a_save_parks_resumes_and_a_new_query_never_sees_stale_hits()
    {
        var path = AuthorText("busy-search.pdf", 300, new()
        {
            [20] = [MarginLine(40, 200, 360)],
            [180] = [MarginLine(580, 200, 360)],
        });
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
            {
                unobserved.Add(e.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var marks = new ScanProbe { DelayPerPage = TimeSpan.FromMilliseconds(3) };
            var searches = new SearchProbe { DelayPerPage = TimeSpan.FromMilliseconds(4) };
            WpfTestThread.Run(() =>
            {
                var scheduler = new ManualScheduler();
                var vm = NewVm(path, scheduler, marks);
                vm.SearchSourceFactory = opened => opened.Viewer?.Current is { } renderer ? new ProbeSearchSource(renderer, searches) : null;
                var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
                var writer = new GatedWriter(new PdfDocumentSaver());
                vm.SaveService = new DocumentSaveService(writer, _dialogs);
                try
                {
                    Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                    LayOut(view, 1000, 800);
                    scheduler.Advance(MainViewModel.MarkScanStartDelay);
                    vm.ZoomVm.Zoom = 1;
                    Settle(view);
                    LoadEditors(view);
                    var markScanner = vm.MarkScannerForTest!;
                    WpfTestThread.PumpUntil(() => markScanner.ProcessedCount > 3, _timeoutMs);

                    // 1. A search while the mark scan runs: both go on.
                    ApplyQuery(vm, scheduler, _query);
                    var first = vm.SearchScannerForTest!;
                    WpfTestThread.PumpUntil(() => first.ProcessedCount > 5, _timeoutMs);
                    Assert.False(first.IsCompleted);
                    Assert.False(markScanner.IsCompleted);

                    // 2. A stroke: both scans park while it is live; the commit is quick; both resume.
                    var surface = view.SurfaceFor(0)!;
                    surface.BeginPenStroke(stylusId: 1, hasPressure: true);
                    surface.AppendPenPoints(VerticalPoints(300, 200, 360, surface.Scale), timestamp: 10);
                    Wait(first.WhenIdleAsync());
                    Wait(markScanner.WhenIdleAsync());
                    var parked = first.ProcessedCount;
                    Thread.Sleep(60);
                    Assert.Equal(parked, first.ProcessedCount);
                    var commit = Stopwatch.StartNew();
                    surface.FinishPenStroke();
                    commit.Stop();
                    Assert.True(commit.ElapsedMilliseconds < 250, $"the commit took {commit.ElapsedMilliseconds} ms");
                    WpfTestThread.PumpUntil(() => first.ProcessedCount > parked, _timeoutMs);

                    // 3. A plain Save mid-search: the search parks, then goes on over the new renderer into the same index.
                    var index = vm.Search.Index!;
                    var saving = vm.SaveCommand.ExecuteAsync(null);
                    WpfTestThread.PumpUntil(() => writer.Started.Task.IsCompleted, _timeoutMs);
                    Wait(first.WhenIdleAsync());
                    var duringSave = first.ProcessedCount;
                    Thread.Sleep(60);
                    Assert.Equal(duringSave, first.ProcessedCount);
                    Assert.True(vm.Search.IsSearching);
                    writer.Gate.SetResult();
                    Wait(saving);
                    Assert.Null(vm.ErrorMessage);
                    Assert.False(vm.IsDirty);
                    var second = vm.SearchScannerForTest!;
                    Assert.NotSame(first, second);
                    Assert.Same(index, vm.Search.Index);
                    WaitSearchCompleted(vm);
                    RefreshAll(scheduler, view);
                    Assert.Equal(300, index.SearchedCount);
                    Assert.Equal([3, 47, 120, 199, 250, 250, 288], index.AllHits().Select(h => h.PageIndex).ToArray());
                    Assert.Equal(6, view.Rail.SearchTicks.Centres.Count);
                    WaitScanCompleted(vm.MarkScannerForTest!);
                    Assert.Equal([20, 180], vm.MarkIndexForTest!.AllMarks().Select(m => m.PageIndex).Distinct().ToArray());
                    Assert.True(searches.MaxInFlight <= 1, $"{searches.MaxInFlight} searches in flight");

                    // 4. A new query mid-scan cancels the old one: its index holds only the new query's hits.
                    ApplyQuery(vm, scheduler, "lorem");
                    var old = vm.SearchScannerForTest!;
                    var oldIndex = vm.Search.Index!;
                    WpfTestThread.PumpUntil(() => old.ProcessedCount > 3, _timeoutMs);
                    Assert.False(old.IsCompleted);
                    ApplyQuery(vm, scheduler, "PERCHÉ");
                    var fresh = vm.Search.Index!;
                    Assert.NotSame(oldIndex, fresh);
                    Assert.Equal(SearchState.Cancelled, old.State);
                    WaitSearchCompleted(vm);
                    RefreshAll(scheduler, view);
                    Assert.Equal([3, 47, 120, 199, 250, 250, 288], fresh.AllHits().Select(h => h.PageIndex).ToArray());
                    var oldSearched = oldIndex.SearchedCount;
                    Thread.Sleep(60);
                    Assert.Equal(oldSearched, oldIndex.SearchedCount);
                    Assert.Equal(6, view.Rail.SearchTicks.Centres.Count);
                    Assert.True(searches.MaxInFlight <= 1, $"{searches.MaxInFlight} searches in flight");
                    _output.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"stroke commit during search and mark scan {commit.Elapsed.TotalMilliseconds:0.0} ms, "
                        + $"page searches {searches.Searches.Values.Sum()} for {searches.Searches.Count} pages"));
                }
                finally
                {
                    writer.Gate.TrySetResult();
                    Close(vm, view);
                }
            });
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (unobserved)
        {
            Assert.Empty(unobserved);
        }
    }

    // ---- Search and thumbnails never change what is saved, the dirty state or the journal -------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_searched_document_with_the_pages_tab_open_saves_the_same_strokes_foreign_annotations_and_page_content(
        bool saveAs, bool withEdit)
    {
        var original = AuthorMixed("searched.pdf");
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(original, scheduler, probe: null, withRecovery: true);
            var view = new DocumentView { DataContext = vm }; // real PDFium bitmaps and thumbnails
            var target = original;
            InkStroke? added = null;
            PagesListView? list = null;
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                IdleBitmaps(view);
                scheduler.Advance(MainViewModel.MarkScanStartDelay);
                WaitScanCompleted(vm.MarkScannerForTest!);

                // Search every page, open the Pages tab and let its thumbnails (bitmap and ink) load.
                ApplyQuery(vm, scheduler, "BOOKS");
                WaitSearchCompleted(vm);
                RefreshAll(scheduler, view);
                Assert.Equal(12, vm.Search.Index!.SearchedCount);
                Assert.Equal(12, vm.Search.Index.TotalHits);
                vm.Search.NextCommand.Execute(null);
                Settle(view);
                Assert.NotNull(vm.Search.Current);
                var cache = NewThumbnailCache(vm, view);
                list = ShowPagesList(vm, cache);
                WpfTestThread.PumpUntil(() => cache.Count >= 3 && view.Bitmaps.PendingThumbnailCount == 0, _timeoutMs);
                scheduler.Advance(MainViewModel.ViewSettleDelay);
                Wait(vm.PositionWritesForTest);

                Assert.False(vm.IsDirty);
                Assert.False(vm.Session!.Undo.CanUndo);
                Assert.Empty(vm.Session.GetChanges());
                AssertNoStrokeEntriesInTheJournal(original);

                if (withEdit)
                {
                    LoadEditors(view);
                    InkPoint[] points = [new(300, 300, 0.5f, null, null, null, 0), new(400, 330, 0.7f, null, null, null, 20)];
                    var editor = view.SurfaceFor(0)!.Editor!;
                    Assert.True(editor.CommitStroke(points, InkTool.Pen, Palette.Colors[0], 1.5));
                    added = editor.Page.Strokes[^1];
                    Assert.True(vm.IsDirty);
                }

                if (saveAs)
                {
                    target = Path.Combine(_dir, "searched-as.pdf");
                    _dialogs.NextSavePath = target;
                    Wait(vm.SaveAsCommand.ExecuteAsync(null));
                }
                else
                {
                    var copy = Path.Combine(_dir, "searched-copy.pdf");
                    File.Copy(original, copy);
                    Wait(vm.SaveCommand.ExecuteAsync(null));
                    original = copy;
                }

                Assert.Null(vm.ErrorMessage);
                Assert.False(vm.IsDirty);

                // The search keeps its results over the new renderer.
                RefreshAll(scheduler, view);
                Assert.Equal(12, vm.Search.Index!.TotalHits);
            }
            finally
            {
                list?.OnHidden();
                _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
                Assert.True(Wait(vm.CanCloseAsync()));
                Close(vm, view);
            }

            var expected = Wait(ReadStrokesAsync(original));
            if (added is not null)
            {
                expected[0] = [.. expected[0], StrokeJson.Serialize(added)];
            }

            Assert.Equal(expected, Wait(ReadStrokesAsync(target)));
            Assert.Equal(Wait(ReadForeignAnnotationsAsync(original)), Wait(ReadForeignAnnotationsAsync(target)));
            var differences = Wait(PdfiumThread.RunAsync(() => PdfFidelity.Compare(original, target), CancellationToken.None)).Differences;
            if (withEdit)
            {
                Assert.Equal(["page 1: annotations Ink 0 vs 1"], differences.Where(d => !d.Contains("pixels", StringComparison.Ordinal)));
            }
            else
            {
                Assert.Empty(differences);
            }
        });
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A text PDF of <paramref name="pages"/> pages (a filler line on each, the planted hits) with the given strokes
    /// written through the real saver.
    /// </summary>
    private string AuthorText(string name, int pages, Dictionary<int, InkStroke[]> strokes)
    {
        var specs = new TextPdfFactory.TextPageSpec[pages];
        for (var page = 0; page < pages; page++)
        {
            var lines = new List<TextPdfFactory.TextLine> { new(72, 760, "lorem ipsum dolor") };
            lines.AddRange(_planted.Where(p => p.Page == page).Select(p => new TextPdfFactory.TextLine(72, p.Baseline, p.Text)));
            specs[page] = new TextPdfFactory.TextPageSpec(_widthPt, _heightPt, lines);
        }

        var source = Path.Combine(_dir, "text-" + name);
        TextPdfFactory.Write(source, specs);
        var path = Path.Combine(_dir, name);
        AuthorInto(source, path, strokes);
        return path;
    }

    /// <summary>Types the query and lets its debounce run: the old search stops and the new one starts.</summary>
    private static void ApplyQuery(MainViewModel vm, ManualScheduler scheduler, string query)
    {
        vm.Search.QueryText = query;
        scheduler.Advance(SearchViewModel.QueryDelay);
    }

    private static void WaitSearchCompleted(MainViewModel vm) =>
        WpfTestThread.PumpUntil(() => vm.SearchScannerForTest is { IsCompleted: true }, _timeoutMs);

    /// <summary>The marks list's and the search's throttles run, and the rail (shown) draws its ticks.</summary>
    private static void RefreshAll(ManualScheduler scheduler, DocumentView view)
    {
        scheduler.Advance(SearchViewModel.RefreshInterval);
        Refresh(scheduler, view);
    }

    /// <summary>The window's thumbnail cache: bitmaps and ink through the view's low-priority queue.</summary>
    private static PageThumbnailCache NewThumbnailCache(MainViewModel vm, DocumentView view)
    {
        var cache = new PageThumbnailCache(
            (pageIndex, ct) => view.Bitmaps.RenderThumbnailAsync(pageIndex, ct),
            (pageIndex, ct) => view.Bitmaps.RunWhenIdleAsync(token => vm.LoadPageInkAsync(pageIndex, token), ct));
        cache.SetPaused(true); // as the window: until the Pages tab is shown
        return cache;
    }

    /// <summary>The Pages tab, laid out as in the panel and shown.</summary>
    private static PagesListView ShowPagesList(MainViewModel vm, PageThumbnailCache cache)
    {
        var list = new PagesListView { DataContext = vm.Pages, Thumbnails = cache };
        ScrollRailTests.LayOut(list, 320, 800);
        list.OnShown();
        list.UpdateLayout();
        WpfTestThread.Drain();
        return list;
    }

    private static List<ListBoxItem> RealisedRows(PagesListView list)
    {
        list.UpdateLayout();
        return Descendants<ListBoxItem>(list.List).ToList();
    }

    private static ScrollViewer ListScrollViewer(PagesListView list) => Descendants<ScrollViewer>(list.List).First();

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>What the search scan did, shared by every source a document gets (one per renderer).</summary>
    private sealed class SearchProbe
    {
        private int _inFlight;
        private int _maxInFlight;

        public TimeSpan DelayPerPage { get; init; }

        public ConcurrentDictionary<int, int> Searches { get; } = new();

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public void Enter()
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }
        }

        public void Exit(int page)
        {
            Interlocked.Decrement(ref _inFlight);
            Searches.AddOrUpdate(page, 1, (_, n) => n + 1);
        }
    }

    /// <summary>The real renderer as search source, observed: searches in flight, per page, optional delay.</summary>
    private sealed class ProbeSearchSource(ISearchPageSource inner, SearchProbe probe) : ISearchPageSource
    {
        public int PageCount => inner.PageCount;

        public async Task<SearchPageResult> SearchPageAsync(int pageIndex, string foldedQuery, int maxHits, CancellationToken ct)
        {
            probe.Enter();
            try
            {
                if (probe.DelayPerPage > TimeSpan.Zero)
                {
                    await Task.Delay(probe.DelayPerPage, ct).ConfigureAwait(false);
                }

                return await inner.SearchPageAsync(pageIndex, foldedQuery, maxHits, ct).ConfigureAwait(false);
            }
            finally
            {
                probe.Exit(pageIndex);
            }
        }
    }
}
