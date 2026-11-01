using System.Windows;
using System.Windows.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;
using DreamBooks.Core.Search;
using static DreamBooks.App.Tests.FakeSearchPageSource;

namespace DreamBooks.App.Tests;

/// <summary>
/// The text search of the main view model (FR-13, FR-105): the debounced field, the count, next/previous with wrap
/// and Back history, a new index per query, the scan's lifecycle (pauses, renderer swap, another document) and the
/// window's shortcuts. Only counts are asserted on; the query never reaches any output (NFR-12).
/// </summary>
public sealed class SearchViewModelTests : IDisposable
{
    private const double _viewportWidthDip = 816; // 612 pt at 4/3 DIP per point: fit width = 100%
    private const double _viewportHeightDip = 600; // 450 pt at 100%

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-search-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();
    private readonly ManualScheduler _scheduler = new();
    private readonly List<string> _log = [];

    public SearchViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Typing_starts_a_search_after_the_debounce_and_clears_on_empty()
    {
        var source = new FakeSearchPageSource(10);
        source.Hits[3] = [Hit(3, 100)];
        var vm = await OpenAsync(source);

        vm.Search.QueryText = "so";
        _scheduler.Advance(200);
        vm.Search.QueryText = "sole"; // typing on restarts the delay
        _scheduler.Advance(SearchViewModel.QueryDelay - TimeSpan.FromMilliseconds(1));
        Assert.Null(vm.SearchScannerForTest);
        Assert.Null(vm.Search.Index);
        Assert.Equal(string.Empty, vm.Search.CountText);

        _scheduler.Advance(1);
        var scanner = vm.SearchScannerForTest;
        Assert.NotNull(scanner);
        Assert.NotNull(vm.Search.Index);
        await CompleteAsync(scanner);
        Assert.True(vm.Search.HasResults);
        Assert.False(vm.Search.IsSearching);
        Assert.Equal(10, source.Searched.Count); // the whole document, once

        // Emptied: the results go at once, without waiting for the delay, and the scan is disposed.
        vm.Search.QueryText = " ";
        Assert.Null(vm.SearchScannerForTest);
        Assert.Null(vm.Search.Index);
        Assert.False(vm.Search.HasResults);
        Assert.Equal(string.Empty, vm.Search.CountText);
    }

    [Fact]
    public async Task Count_text_formats_for_zero_some_truncated_and_scanning()
    {
        var source = new FakeSearchPageSource(10);
        source.HitsByQuery["tre"] = new() { [2] = [Hit(2, 100), Hit(2, 300)], [7] = [Hit(7, 100)] };
        source.HitsByQuery["uno"] = new() { [4] = [Hit(4, 100)] };
        source.HitsByQuery["molti"] = new() { [0] = [Hit(0, 100), Hit(0, 200), Hit(0, 300)], [1] = [Hit(1, 100)] };
        var gate = source.Hold(0);
        var vm = await OpenAsync(source);

        // Scanning, nothing found yet.
        var scanner = Search(vm, "tre");
        await source.Started(0).WaitAsync(_timeout);
        _scheduler.Advance(SearchViewModel.RefreshInterval);
        Assert.True(vm.Search.IsSearching);
        Assert.Equal(Strings.SearchInProgress, vm.Search.CountText);
        Assert.True(vm.Search.NextCommand.CanExecute(null)); // results may still come

        gate.SetResult();
        await CompleteAsync(scanner);
        Assert.Equal("3 risultati", vm.Search.CountText);
        vm.Search.NextCommand.Execute(null);
        Assert.Equal("1 di 3", vm.Search.CountText);
        vm.Search.NextCommand.Execute(null);
        Assert.Equal("2 di 3", vm.Search.CountText);

        await CompleteAsync(Search(vm, "uno"));
        Assert.Equal(Strings.SearchOneResult, vm.Search.CountText);

        await CompleteAsync(Search(vm, "nulla"));
        Assert.Equal(Strings.SearchNoResults, vm.Search.CountText);
        Assert.False(vm.Search.HasResults);
        Assert.False(vm.Search.NextCommand.CanExecute(null));

        // The cap: what was found, with a "+".
        vm.SearchMaxHits = 2;
        await CompleteAsync(Search(vm, "molti"));
        Assert.True(vm.Search.Index!.Truncated);
        Assert.Equal("2+ risultati", vm.Search.CountText);
        vm.Search.NextCommand.Execute(null);
        Assert.Equal("1 di 2+", vm.Search.CountText);
    }

    [Fact]
    public async Task Next_goes_to_the_following_hit_records_history_and_previous_returns()
    {
        var source = new FakeSearchPageSource(10);
        var a = Hit(2, 100);
        var b = Hit(2, 500);
        var c = Hit(2, 500, left: 300); // on b's line: visited one by one
        var d = Hit(6, 300);
        source.Hits[2] = [a, b, c];
        source.Hits[6] = [d];
        var vm = await OpenAsync(source);
        var start = vm.Viewport.Position;
        await CompleteAsync(Search(vm, "x"));
        Assert.Null(vm.Search.Current);

        vm.Search.NextCommand.Execute(null);
        Assert.Equal(a, vm.Search.Current);
        Assert.Equal(vm.Viewport.TargetPositionFor(a), vm.Viewport.Position); // centred
        Assert.Equal(2, vm.Viewport.CurrentPage);
        Assert.True(vm.Viewport.History.CanGoBack);

        vm.Search.NextCommand.Execute(null);
        Assert.Equal(b, vm.Search.Current);
        vm.Search.NextCommand.Execute(null);
        Assert.Equal(c, vm.Search.Current);
        vm.Search.NextCommand.Execute(null);
        Assert.Equal(d, vm.Search.Current);
        Assert.Equal(vm.Viewport.TargetPositionFor(d), vm.Viewport.Position);

        vm.Search.PreviousCommand.Execute(null);
        Assert.Equal(c, vm.Search.Current);
        vm.Search.PreviousCommand.Execute(null);
        Assert.Equal(b, vm.Search.Current);
        Assert.Null(vm.Search.StatusText);

        // Back returns to where the reader was before the search moved the view.
        vm.Viewport.BackCommand.Execute(null);
        Assert.Equal(start, vm.Viewport.Position);
        Assert.False(vm.Viewport.History.CanGoBack); // one entry: stepping on through the results recorded nothing more

        // Away from the current result, the next step starts from the view again: the first result at or below it.
        vm.Viewport.ScrollToPage(5, recordHistory: false);
        vm.Search.NextCommand.Execute(null);
        Assert.Equal(d, vm.Search.Current);
        Assert.True(vm.Viewport.History.CanGoBack); // a new entry for the jump from the view
    }

    [Fact]
    public async Task Next_wraps_with_a_status_message()
    {
        var source = new FakeSearchPageSource(10);
        var first = Hit(1, 100);
        var last = Hit(8, 400);
        source.Hits[1] = [first];
        source.Hits[8] = [last];
        var vm = await OpenAsync(source);
        await CompleteAsync(Search(vm, "x"));

        vm.Search.NextCommand.Execute(null);
        vm.Search.NextCommand.Execute(null);
        Assert.Equal(last, vm.Search.Current);
        Assert.Null(vm.Search.StatusText);

        vm.Search.NextCommand.Execute(null);
        Assert.Equal(first, vm.Search.Current);
        Assert.Equal(Strings.SearchWrappedToStart, vm.Search.StatusText);
        _scheduler.Advance(SearchViewModel.StatusDuration);
        Assert.Null(vm.Search.StatusText);

        vm.Search.PreviousCommand.Execute(null);
        Assert.Equal(last, vm.Search.Current);
        Assert.Equal(Strings.SearchWrappedToEnd, vm.Search.StatusText);

        // A single result wraps onto itself, and says so.
        source.HitsByQuery["solo"] = new() { [4] = [Hit(4, 200)] };
        await CompleteAsync(Search(vm, "solo"));
        vm.Search.NextCommand.Execute(null);
        _scheduler.Advance(SearchViewModel.StatusDuration);
        vm.Search.NextCommand.Execute(null);
        Assert.Equal(Hit(4, 200), vm.Search.Current);
        Assert.Equal(Strings.SearchWrappedToStart, vm.Search.StatusText);
    }

    [Fact]
    public async Task Next_waits_for_pages_not_searched_yet_instead_of_wrapping()
    {
        var source = new FakeSearchPageSource(10);
        source.Hits[1] = [Hit(1, 100)];
        source.Hits[5] = [Hit(5, 100)];
        var gate5 = source.Hold(5);
        var gate1 = source.Hold(1);
        var vm = await OpenAsync(source);
        vm.Viewport.ScrollToPage(3, recordHistory: false); // the scan starts here: 3 … 9, then 0 … 2
        _scheduler.Advance(DocumentViewportViewModel.CurrentPageDelay);
        var scanner = Search(vm, "x");
        await source.Started(5).WaitAsync(_timeout);
        _scheduler.Advance(SearchViewModel.RefreshInterval);

        // Page 5 is still being searched: nothing to go to yet (the result there may exist), and no wrap claimed.
        Assert.True(vm.Search.NextCommand.CanExecute(null));
        vm.Search.NextCommand.Execute(null);
        Assert.Null(vm.Search.Current);
        Assert.Null(vm.Search.StatusText);

        gate5.SetResult();
        await source.Started(1).WaitAsync(_timeout);
        _scheduler.Advance(SearchViewModel.RefreshInterval);
        vm.Search.NextCommand.Execute(null);
        Assert.Equal(Hit(5, 100), vm.Search.Current);

        // Wrapping needs pages 0 and 1, and page 1 is being searched: Next waits rather than jump to a later result.
        vm.Search.NextCommand.Execute(null);
        Assert.Equal(Hit(5, 100), vm.Search.Current);
        Assert.Null(vm.Search.StatusText);

        gate1.SetResult();
        await CompleteAsync(scanner);
        vm.Search.NextCommand.Execute(null);
        Assert.Equal(Hit(1, 100), vm.Search.Current);
        Assert.Equal(Strings.SearchWrappedToStart, vm.Search.StatusText);
    }

    [Fact]
    public async Task A_new_query_replaces_the_results_and_highlights_of_the_old_one()
    {
        var source = new FakeSearchPageSource(10);
        source.HitsByQuery["vecchio"] = new() { [1] = [Hit(1, 100), Hit(1, 300)], [6] = [Hit(6, 100)] };
        source.HitsByQuery["nuovo"] = new() { [3] = [Hit(3, 200)] };
        var vm = await OpenAsync(source);
        var changed = new List<int>();
        vm.Search.PageHighlightsChanged += (_, page) => changed.Add(page);

        var old = Search(vm, "vecchio");
        await CompleteAsync(old);
        var oldIndex = vm.Search.Index!;
        Assert.Contains(1, changed);
        Assert.Contains(6, changed);
        vm.Search.NextCommand.Execute(null);
        Assert.Equal(Hit(1, 100).Rects, vm.Search.CurrentRectsForPage(1));
        Assert.Equal(Hit(1, 300).Rects, vm.Search.HitsForPage(1)); // the others, without the current one

        changed.Clear();
        var fresh = Search(vm, "nuovo");
        Assert.NotSame(old, fresh);
        Assert.NotSame(oldIndex, vm.Search.Index); // a new index per query
        Assert.Null(vm.Search.Current);
        Assert.Equal(-1, changed[0]); // every page drops the old highlights at once
        Assert.Empty(vm.Search.HitsForPage(1));
        Assert.Empty(vm.Search.CurrentRectsForPage(1));

        await CompleteAsync(fresh);
        Assert.Equal(Hit(3, 200).Rects, vm.Search.HitsForPage(3));
        Assert.Empty(vm.Search.HitsForPage(6));
        Assert.Empty(oldIndex.HitsOnPage(3)); // nothing of the new query reached the old index
        Assert.Equal(1, vm.Search.Index!.TotalHits);
    }

    [Fact]
    public async Task Esc_or_Clear_removes_results_highlights_and_returns_focus()
    {
        var source = new FakeSearchPageSource(10);
        source.Hits[0] = [Hit(0, 100)];
        var vm = await OpenAsync(source);
        await CompleteAsync(Search(vm, "x"));
        vm.Search.NextCommand.Execute(null);
        var changed = new List<int>();
        var focus = 0;
        vm.Search.PageHighlightsChanged += (_, page) => changed.Add(page);
        vm.Search.FocusDocumentRequested += (_, _) => focus++;

        Assert.True(vm.Search.ClearCommand.CanExecute(null));
        vm.Search.ClearCommand.Execute(null);

        Assert.Equal(string.Empty, vm.Search.QueryText);
        Assert.Null(vm.Search.Index);
        Assert.Null(vm.Search.Current);
        Assert.Null(vm.SearchScannerForTest);
        Assert.Empty(vm.Search.HitsForPage(0));
        Assert.Empty(vm.Search.CurrentRectsForPage(0));
        Assert.Contains(-1, changed);
        Assert.Equal(string.Empty, vm.Search.CountText);
        Assert.Equal(1, focus);
    }

    [Fact]
    public async Task No_text_layer_shows_the_message_once_per_document()
    {
        var source = new FakeSearchPageSource(10) { NoText = true };
        var vm = await OpenAsync(source);
        await CompleteAsync(Search(vm, "parola"));
        Assert.Equal(Strings.SearchNoTextLayer, vm.Search.NoTextLayerMessage);
        Assert.Equal(Strings.SearchNoResults, vm.Search.CountText);

        // The document has no text: another query searches nothing again, the message stays.
        var searched = source.Searched.Count;
        vm.Search.QueryText = "altra";
        _scheduler.Advance(SearchViewModel.QueryDelay);
        await Task.Delay(30);
        Assert.Equal(searched, source.Searched.Count);
        Assert.Equal(Strings.SearchNoTextLayer, vm.Search.NoTextLayerMessage);

        // Another document starts afresh.
        var other = new FakeSearchPageSource(10);
        vm.SearchSourceFactory = _ => other;
        await OpenFileAsync(vm, "b.pdf");
        Assert.Null(vm.Search.NoTextLayerMessage);
        Assert.Equal(string.Empty, vm.Search.QueryText);
    }

    [Fact]
    public async Task When_every_page_fails_the_search_says_it_failed_not_that_there_is_no_text()
    {
        var source = new FakeSearchPageSource(4) { FailAll = true };
        var vm = await OpenAsync(source);
        var scanner = Search(vm, "parola");
        await CompleteAsync(scanner);
        Assert.Equal(4, scanner.FailedCount);
        Assert.Null(vm.Search.NoTextLayerMessage);
        Assert.Equal(Strings.SearchFailed, vm.Search.CountText);
        Assert.False(vm.Search.DocumentHasNoText);
    }

    [Fact]
    public async Task Search_pauses_while_a_stroke_is_live_and_during_a_save()
    {
        var source = new FakeSearchPageSource(6);
        source.Hits[4] = [Hit(4, 100)];
        var vm = await OpenAsync(source);

        // A stroke is live (the zoom is locked): the scan waits.
        vm.ZoomVm.IsLocked = true;
        var scanner = Search(vm, "x");
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        await Task.Delay(40);
        Assert.Empty(source.Searched);
        Assert.True(vm.Search.IsSearching);
        vm.ZoomVm.IsLocked = false;
        await CompleteAsync(scanner);
        Assert.Equal(6, source.Searched.Count);

        // A save parks the next query's scan too; it goes on when the save is over (over the reopened renderer).
        var writer = new FakeDocumentWriter(_log) { Gate = new TaskCompletionSource() };
        var viewer = new FakeViewerHandle(_log);
        var saving = await OpenAsync(new FakeSearchPageSource(6), viewer, writer);
        var saveSource = new FakeSearchPageSource(6);
        saveSource.Hits[2] = [Hit(2, 100)];
        saving.SearchSourceFactory = _ => saveSource;
        Assert.True(saving.Editor!.CommitStroke(
            [new InkPoint(10, 20, 0.5f, null, null, null, 0), new InkPoint(30, 20, 0.5f, null, null, null, 10)],
            InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1.5));
        var save = saving.SaveCommand.ExecuteAsync(null);
        await writer.Started.Task.WaitAsync(_timeout);
        Assert.True(saving.IsSaving);
        var during = Search(saving, "x");
        await during.WhenIdleAsync().WaitAsync(_timeout);
        await Task.Delay(40);
        Assert.Empty(saveSource.Searched);

        writer.Gate.SetResult();
        await save.WaitAsync(_timeout);
        Assert.False(saving.IsSaving);
        await CompleteAsync(saving.SearchScannerForTest!);
        Assert.True(saving.Search.HasResults);
    }

    [Fact]
    public async Task Renderer_swap_after_Save_keeps_the_hits_found_and_continues()
    {
        var viewer = new FakeViewerHandle(_log);
        var sources = new List<FakeSearchPageSource>();
        var vm = await OpenAsync(new FakeSearchPageSource(8), viewer);
        vm.SearchSourceFactory = _ =>
        {
            if (viewer.IsReleased)
            {
                return null;
            }

            var source = new FakeSearchPageSource(8);
            source.Hits[1] = [Hit(1, 100)];
            source.Hits[6] = [Hit(6, 100)];
            if (sources.Count == 0)
            {
                source.Hold(4);
            }

            sources.Add(source);
            return source;
        };
        vm.Viewport.ScrollToPage(1, recordHistory: false);
        _scheduler.Advance(DocumentViewportViewModel.CurrentPageDelay);
        var first = Search(vm, "x");
        var index = vm.Search.Index!;
        await sources[0].Started(4).WaitAsync(_timeout);
        _scheduler.Advance(SearchViewModel.RefreshInterval);
        Assert.Single(index.HitsOnPage(1)); // found before the save

        // The save releases the renderer: no scan, but the search is still running (pending).
        await viewer.ReleaseAsync();
        Assert.Null(vm.SearchScannerForTest);
        Assert.Equal(SearchState.Cancelled, first.State);

        // The reopened renderer gets a new scan over the same index: what was found stays, the rest is searched once.
        await viewer.ReopenAsync(Path.Combine(_dir, "a.pdf"), CancellationToken.None);
        var second = vm.SearchScannerForTest!;
        Assert.NotSame(first, second);
        Assert.Same(index, vm.Search.Index);
        await CompleteAsync(second);
        Assert.DoesNotContain(1, sources[1].Searched);
        Assert.Equal(2, index.TotalHits);
        Assert.Equal(8, index.SearchedCount);
        Assert.Equal("2 risultati", vm.Search.CountText);

        // A renderer closed under the scan (the document closing) does not throw out of Start.
        sources.Clear();
        vm.SearchSourceFactory = _ => new FakeSearchPageSource(8) { Disposed = true };
        await viewer.ReleaseAsync();
        await viewer.ReopenAsync(Path.Combine(_dir, "a.pdf"), CancellationToken.None);
        Assert.Null(vm.SearchScannerForTest);
        Assert.Same(index, vm.Search.Index);
    }

    [Fact]
    public async Task Opening_another_document_cancels_the_search()
    {
        var source = new FakeSearchPageSource(10);
        var gate = source.Hold(2);
        source.Hits[0] = [Hit(0, 100)];
        var vm = await OpenAsync(source);
        vm.Viewport.ScrollToPage(2, recordHistory: false);
        _scheduler.Advance(DocumentViewportViewModel.CurrentPageDelay);
        var scanner = Search(vm, "x");
        await source.Started(2).WaitAsync(_timeout);

        await OpenFileAsync(vm, "b.pdf");
        Assert.Equal(SearchState.Cancelled, scanner.State);
        Assert.Null(vm.SearchScannerForTest);
        Assert.Null(vm.Search.Index);
        Assert.Equal(string.Empty, vm.Search.QueryText);
        Assert.Equal(string.Empty, vm.Search.CountText);
        Assert.False(vm.Search.IsSearching);

        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        _scheduler.Advance(SearchViewModel.RefreshInterval);
        Assert.Null(vm.Search.Index); // nothing of the old scan comes back
    }

    [Fact]
    public async Task Commands_stay_enabled_while_saving()
    {
        var source = new FakeSearchPageSource(10);
        source.Hits[3] = [Hit(3, 100)];
        var writer = new FakeDocumentWriter(_log) { Gate = new TaskCompletionSource() };
        var viewer = new FakeViewerHandle(_log);
        var vm = await OpenAsync(source, viewer, writer);
        await CompleteAsync(Search(vm, "x"));
        Assert.True(vm.Editor!.CommitStroke(
            [new InkPoint(10, 20, 0.5f, null, null, null, 0), new InkPoint(30, 20, 0.5f, null, null, null, 10)],
            InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1.5));

        var save = vm.SaveCommand.ExecuteAsync(null);
        await writer.Started.Task.WaitAsync(_timeout);
        Assert.True(vm.IsSaving);
        Assert.True(vm.Search.NextCommand.CanExecute(null));
        Assert.True(vm.Search.PreviousCommand.CanExecute(null));
        Assert.True(vm.Search.ClearCommand.CanExecute(null));
        vm.Search.NextCommand.Execute(null);
        Assert.Equal(Hit(3, 100), vm.Search.Current);

        writer.Gate.SetResult();
        await save.WaitAsync(_timeout);
    }

    [Fact]
    public void Ctrl_F_focuses_the_box_and_Enter_Shift_Enter_F3_navigate()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                var vm = (MainViewModel)window.DataContext;
                var search = vm.Search;

                // The field and the buttons: bound, named, 40 x 40 touch targets, keyboard reachable.
                Assert.Equal(Strings.SearchField, System.Windows.Automation.AutomationProperties.GetName(window.SearchField));
                Assert.Same(search.PreviousCommand, window.PreviousResultButton.Command);
                Assert.Same(search.NextCommand, window.NextResultButton.Command);
                Assert.Same(search.ClearCommand, window.ClearSearchButton.Command);
                foreach (var button in new[] { window.PreviousResultButton, window.NextResultButton, window.ClearSearchButton })
                {
                    Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button)));
                    Assert.True(button.ActualWidth >= 40 && button.ActualHeight >= 40, "40 x 40 touch target");
                    Assert.True(button.Focusable && button.IsTabStop, "keyboard reachable");
                }

                // Nothing is taken without a search (Esc stays the panel's / the fields', F3 does nothing harmful).
                Assert.False(window.RouteKey(Key.Escape, ModifierKeys.None, null));
                Assert.False(window.RouteKey(Key.Enter, ModifierKeys.None, window.GoToPageField));

                // Ctrl+F focuses the field (from anywhere, also from another field).
                var requests = window.SearchFocusRequests;
                Assert.True(window.RouteKey(Key.F, ModifierKeys.Control, null));
                Assert.True(window.RouteKey(Key.F, ModifierKeys.Control, window.GoToPageField));
                Assert.Equal(requests + 2, window.SearchFocusRequests);

                // A search with three results, without a real document: a layout and an index are enough.
                var layout = new DocumentLayout(Enumerable.Repeat(new PageDimensions(612, 792), 6).ToArray());
                vm.Viewport.Attach(null, layout, null);
                var index = new SearchIndex(6);
                search.Attach(index);
                index.SetPage(1, [Hit(1, 100)]);
                index.SetPage(3, [Hit(3, 100), Hit(3, 400)]);
                for (var p = 0; p < 6; p++)
                {
                    if (!index.IsSearched(p))
                    {
                        index.SetPage(p, []);
                    }
                }

                WpfTestThread.PumpUntil(() => search.HasResults);

                Assert.True(window.RouteKey(Key.F3, ModifierKeys.None, null));
                Assert.Equal(Hit(1, 100), search.Current);
                Assert.True(window.RouteKey(Key.Enter, ModifierKeys.None, window.SearchField));
                Assert.Equal(Hit(3, 100), search.Current);
                Assert.True(window.RouteKey(Key.F3, ModifierKeys.None, window.GoToPageField)); // anywhere
                Assert.Equal(Hit(3, 400), search.Current);
                Assert.True(window.RouteKey(Key.Enter, ModifierKeys.Shift, window.SearchField));
                Assert.Equal(Hit(3, 100), search.Current);
                Assert.True(window.RouteKey(Key.F3, ModifierKeys.Shift, null));
                Assert.Equal(Hit(1, 100), search.Current);
                Assert.False(window.RouteKey(Key.Enter, ModifierKeys.None, window.BackButton)); // Enter elsewhere is not ours

                // The existing bindings stay: no search key clashes with them.
                var gestures = window.InputBindings.OfType<KeyBinding>().Select(b => (b.Key, b.Modifiers)).ToList();
                Assert.DoesNotContain(gestures, g => g.Key is Key.F or Key.F3 || (g.Key == Key.Enter && g.Modifiers != ModifierKeys.None));

                // Esc in the field clears the search and gives the keyboard back to the document.
                search.QueryText = "x";
                var focus = window.DocumentFocusRequests;
                Assert.True(window.RouteKey(Key.Escape, ModifierKeys.None, window.SearchField));
                Assert.Equal(string.Empty, search.QueryText);
                Assert.Equal(focus + 1, window.DocumentFocusRequests);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private SearchScanner Search(MainViewModel vm, string query)
    {
        vm.Search.QueryText = query;
        _scheduler.Advance(SearchViewModel.QueryDelay);
        return Assert.IsType<SearchScanner>(vm.SearchScannerForTest);
    }

    private async Task CompleteAsync(SearchScanner scanner)
    {
        var deadline = DateTime.UtcNow + _timeout;
        while (!scanner.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "The search did not complete.");
            await Task.Delay(5);
        }

        _scheduler.Advance(SearchViewModel.RefreshInterval);
    }

    private async Task<MainViewModel> OpenAsync(
        FakeSearchPageSource source, IViewerHandle? viewer = null, FakeDocumentWriter? writer = null)
    {
        var pages = new FakePageSource(Enumerable.Repeat((612.0, 792.0), source.PageCount).ToArray());
        var vm = new MainViewModel(_dialogs, scheduler: _scheduler)
        {
            MarkSourceFactory = _ => null,
            SearchSourceFactory = _ => source,
            MarkScanIdleGap = TimeSpan.Zero,
        };
        vm.OpenDocument = (path, _) => Task.FromResult(new OpenedDocument(pages, pages, path, viewer));
        if (writer is not null)
        {
            vm.SaveService = new DocumentSaveService(writer, _dialogs);
        }

        await OpenFileAsync(vm, "a.pdf");
        vm.Viewport.ViewportWidthDip = _viewportWidthDip;
        vm.Viewport.ViewportHeightDip = _viewportHeightDip;
        _scheduler.Advance(DocumentViewportViewModel.CurrentPageDelay);
        return vm;
    }

    private async Task OpenFileAsync(MainViewModel vm, string name)
    {
        var path = Path.Combine(_dir, name);
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, [1, 2, 3]); // fingerprinted, never parsed
        }

        _dialogs.NextPath = path;
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
    }
}
