using System.ComponentModel;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

/// <summary>
/// The marks list, previous/next mark with Back history, and the mark index lifecycle of the main view model:
/// scan per document, following the renderer swap, pausing while inking and saving (FR-100..FR-103).
/// </summary>
public sealed class MarksViewModelTests : IDisposable
{
    private const double _viewportWidthDip = 816; // 612 pt at 4/3 DIP per point: fit width = 100%
    private const double _viewportHeightDip = 600; // 450 pt at 100%
    private const double _windowPt = 450;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-marks-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();
    private readonly ManualScheduler _scheduler = new();
    private readonly List<string> _log = [];

    public MarksViewModelTests() => Directory.CreateDirectory(_dir);

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

    // ---- The list --------------------------------------------------------------------------------------------

    [Fact]
    public void Items_follow_the_index_and_are_ordered()
    {
        var (_, marks, index) = Standalone();
        Assert.True(marks.IsEmpty);
        Assert.Equal(Strings.MarksEmpty, marks.EmptyText);

        index.SetPage(5, [Mark(5, 100, 300)]);
        index.SetPage(1, [Mark(1, 400, 500), Mark(1, 50, 200)]);
        _scheduler.Advance(MarksViewModel.RefreshInterval);

        Assert.Equal(3, marks.Count);
        Assert.False(marks.IsEmpty);
        Assert.Equal([(1, 50.0), (1, 400.0), (5, 100.0)], marks.Items.Select(i => (i.Mark.PageIndex, i.Mark.TopPt)));
        Assert.Equal(["pag. 2", "pag. 2", "pag. 6"], marks.Items.Select(i => i.PageLabel));

        index.ClearPage(1);
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        Assert.Equal(5, Assert.Single(marks.Items).Mark.PageIndex);
    }

    [Fact]
    public void Index_changes_are_throttled_to_one_rebuild_per_interval()
    {
        var (_, marks, index) = Standalone();
        var rebuilds = 0;
        marks.PropertyChanged += (_, e) => rebuilds += e.PropertyName == nameof(MarksViewModel.Count) ? 1 : 0;

        index.SetPage(1, [Mark(1, 100, 300)]);
        index.SetPage(2, [Mark(2, 100, 300)]);
        index.SetPage(3, [Mark(3, 100, 300)]);
        _scheduler.Advance(99);
        Assert.Equal(0, rebuilds);
        Assert.Equal(0, marks.Count);

        _scheduler.Advance(1);
        Assert.Equal(1, rebuilds);
        Assert.Equal(3, marks.Count);

        index.SetPage(4, [Mark(4, 100, 300)]);
        _scheduler.Advance(50);
        index.SetPage(5, [Mark(5, 100, 300)]);
        _scheduler.Advance(49);
        Assert.Equal(1, rebuilds);
        _scheduler.Advance(1);
        Assert.Equal(2, rebuilds);
        Assert.Equal(5, marks.Count);
        _scheduler.Advance(1000);
        Assert.Equal(2, rebuilds); // nothing changed since: no idle rebuilds
    }

    // ---- Previous / next ----------------------------------------------------------------------------------

    [Fact]
    public void Next_goes_to_the_following_mark_records_history_once_and_back_returns()
    {
        var (viewport, marks, index) = Standalone();
        index.SetPage(2, [Mark(2, 100, 200)]);
        index.SetPage(5, [Mark(5, 300, 400)]);
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        var pushes = 0;
        viewport.History.Changed += (_, _) => pushes++;
        var jumps = 0;
        viewport.Jumped += (_, _) => jumps++;

        marks.NextCommand.Execute(null);
        Assert.Equal(CentredTop(viewport, 2, 100, 200), AbsoluteTop(viewport), 3);
        Assert.Equal(1, pushes);
        Assert.Equal(1, jumps);

        marks.NextCommand.Execute(null); // from the mark just shown, not from the viewport top above it
        Assert.Equal(CentredTop(viewport, 5, 300, 400), AbsoluteTop(viewport), 3);
        Assert.Equal(2, pushes);
        Assert.Null(marks.StatusText);

        viewport.BackCommand.Execute(null);
        Assert.Equal(CentredTop(viewport, 2, 100, 200), AbsoluteTop(viewport), 3);
        viewport.BackCommand.Execute(null);
        Assert.Equal(0, AbsoluteTop(viewport), 3);
        Assert.False(viewport.BackCommand.CanExecute(null));
    }

    [Fact]
    public void Previous_goes_to_the_preceding_mark()
    {
        var (viewport, marks, index) = Standalone();
        index.SetPage(2, [Mark(2, 100, 200)]);
        index.SetPage(5, [Mark(5, 300, 400)]);
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        ScrollToPageTop(viewport, 8);

        marks.PreviousCommand.Execute(null);
        Assert.Equal(CentredTop(viewport, 5, 300, 400), AbsoluteTop(viewport), 3);

        marks.PreviousCommand.Execute(null);
        Assert.Equal(CentredTop(viewport, 2, 100, 200), AbsoluteTop(viewport), 3);

        // From a plain scroll position between the two marks.
        ScrollToPageTop(viewport, 4);
        marks.PreviousCommand.Execute(null);
        Assert.Equal(CentredTop(viewport, 2, 100, 200), AbsoluteTop(viewport), 3);
        ScrollToPageTop(viewport, 4);
        marks.NextCommand.Execute(null);
        Assert.Equal(CentredTop(viewport, 5, 300, 400), AbsoluteTop(viewport), 3);
    }

    [Fact]
    public void Next_at_the_last_mark_does_not_jump_and_reports_no_other_mark()
    {
        var (viewport, marks, index) = Standalone();
        index.SetPage(2, [Mark(2, 100, 200)]);
        index.SetPage(5, [Mark(5, 300, 400)]);
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        marks.GoToMarkCommand.Execute(marks.Items[^1]);
        var position = viewport.Position;
        var pushes = 0;
        viewport.History.Changed += (_, _) => pushes++;

        marks.NextCommand.Execute(null);

        Assert.Equal(position, viewport.Position);
        Assert.Equal(0, pushes);
        Assert.Equal(Strings.MarksNoOther, marks.StatusText);
        _scheduler.Advance(MarksViewModel.StatusDuration - TimeSpan.FromMilliseconds(1));
        Assert.Equal(Strings.MarksNoOther, marks.StatusText);
        _scheduler.Advance(1);
        Assert.Null(marks.StatusText);

        // Scrolled past the last mark: still nothing after it.
        ScrollToPageTop(viewport, 8);
        position = viewport.Position;
        marks.NextCommand.Execute(null);
        Assert.Equal(position, viewport.Position);
        Assert.Equal(Strings.MarksNoOther, marks.StatusText);
    }

    [Fact]
    public void Previous_at_the_first_mark_does_not_jump()
    {
        var (viewport, marks, index) = Standalone();
        index.SetPage(2, [Mark(2, 100, 200)]);
        index.SetPage(5, [Mark(5, 300, 400)]);
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        marks.GoToMarkCommand.Execute(marks.Items[0]);
        var position = viewport.Position;
        var pushes = 0;
        viewport.History.Changed += (_, _) => pushes++;
        var jumps = 0;
        viewport.Jumped += (_, _) => jumps++;

        marks.PreviousCommand.Execute(null);

        Assert.Equal(position, viewport.Position);
        Assert.Equal(0, pushes);
        Assert.Equal(0, jumps);
        Assert.Equal(Strings.MarksNoOther, marks.StatusText);
    }

    [Fact]
    public async Task A_failing_mark_tracker_leaves_the_document_open_without_marks()
    {
        var pages = Pages(3);
        pages.Strokes[1] = [MarginLine()];
        var vm = NewVm(pages, _ => new FakeMarkPageSource(pages));
        vm.MarkTrackerFactory = (_, _, _) => throw new InvalidOperationException("broken");

        await OpenAsync(vm, "a.pdf");

        Assert.NotNull(vm.Session);
        Assert.Equal(1, vm.PageNumber);
        Assert.Null(vm.MarkIndexForTest);
        Assert.Null(vm.MarkScannerForTest);
        Assert.True(vm.Marks.IsEmpty);
        Assert.False(vm.Marks.NextCommand.CanExecute(null));
        Draw(vm); // editing works as usual
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void Commands_do_nothing_when_there_are_no_marks()
    {
        var (viewport, marks, _) = Standalone();
        ScrollToPageTop(viewport, 3);
        var position = viewport.Position;

        Assert.False(marks.NextCommand.CanExecute(null));
        Assert.False(marks.PreviousCommand.CanExecute(null));
        marks.NextCommand.Execute(null);
        marks.PreviousCommand.Execute(null);
        marks.GoToMarkCommand.Execute(null);

        Assert.Equal(position, viewport.Position);
        Assert.Null(marks.StatusText);
        Assert.False(viewport.BackCommand.CanExecute(null));

        // Without any document either.
        var empty = new MarksViewModel(new DocumentViewportViewModel(new ZoomViewModel(), _scheduler), _scheduler);
        Assert.True(empty.IsEmpty);
        Assert.False(empty.NextCommand.CanExecute(null));
        empty.NextCommand.Execute(null);
        empty.PreviousCommand.Execute(null);
        Assert.Null(empty.StatusText);
    }

    [Fact]
    public void GoToMark_centres_a_short_mark_and_aligns_the_top_of_a_tall_one()
    {
        var (viewport, marks, index) = Standalone();
        index.SetPage(4, [Mark(4, 300, 400)]);
        index.SetPage(6, [Mark(6, 100, 700)]); // 600 pt: taller than the 450 pt window
        _scheduler.Advance(MarksViewModel.RefreshInterval);

        marks.GoToMarkCommand.Execute(marks.Items[0]);
        Assert.Equal(CentredTop(viewport, 4, 300, 400), AbsoluteTop(viewport), 3);
        Assert.True(viewport.BackCommand.CanExecute(null));
        Assert.Equal(4, viewport.CurrentPage);

        marks.GoToMarkCommand.Execute(marks.Items[1]);
        Assert.Equal(viewport.Layout!.PageTopPt(6) + 100, AbsoluteTop(viewport), 3);
        Assert.Equal(6, viewport.CurrentPage);

        viewport.BackCommand.Execute(null);
        Assert.Equal(CentredTop(viewport, 4, 300, 400), AbsoluteTop(viewport), 3);
    }

    [Fact]
    public void Next_after_Back_goes_on_to_the_following_mark()
    {
        var (viewport, marks, index) = Standalone();
        SetThreeMarks(index);
        marks.NextCommand.Execute(null);
        marks.NextCommand.Execute(null);
        viewport.BackCommand.Execute(null); // at A again
        Assert.Equal(CentredTop(viewport, 2, 100, 200), AbsoluteTop(viewport), 3);

        marks.NextCommand.Execute(null);

        Assert.Equal(CentredTop(viewport, 5, 300, 400), AbsoluteTop(viewport), 3);
    }

    [Fact]
    public void Next_after_a_resize_goes_on_to_the_following_mark()
    {
        var (viewport, marks, index) = Standalone();
        SetThreeMarks(index);
        marks.NextCommand.Execute(null); // A

        viewport.ViewportWidthDip = 700; // fit width: the zoom and the window in points change, A stays in view
        Assert.NotEqual(1.0, viewport.Position.Zoom, 3);
        marks.NextCommand.Execute(null);

        Assert.Equal(CentredTop(viewport, 5, 300, 400, WindowPt(viewport)), AbsoluteTop(viewport), 3);
    }

    [Fact]
    public void Previous_after_Back_and_after_a_resize_goes_on_to_the_preceding_mark()
    {
        var (viewport, marks, index) = Standalone();
        SetThreeMarks(index);
        ScrollToPageTop(viewport, 9);
        marks.PreviousCommand.Execute(null); // C
        marks.PreviousCommand.Execute(null); // B
        viewport.BackCommand.Execute(null); // at C again

        marks.PreviousCommand.Execute(null);
        Assert.Equal(CentredTop(viewport, 5, 300, 400), AbsoluteTop(viewport), 3);

        viewport.ViewportWidthDip = 700;
        marks.PreviousCommand.Execute(null);
        Assert.Equal(CentredTop(viewport, 2, 100, 200, WindowPt(viewport)), AbsoluteTop(viewport), 3);
    }

    [Fact]
    public void Marks_clamped_to_the_end_of_the_document_leave_no_history_and_report_no_other_mark()
    {
        var (viewport, marks, index) = Standalone();
        index.SetPage(9, [Mark(9, 600, 650), Mark(9, 700, 750)]); // both centre past the end: the same clamped view
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        marks.GoToMarkCommand.Execute(marks.Items[0]);
        var position = viewport.Position;
        var pushes = 0;
        viewport.History.Changed += (_, _) => pushes++;
        var jumps = 0;
        viewport.Jumped += (_, _) => jumps++;

        marks.NextCommand.Execute(null);

        Assert.Equal(position, viewport.Position);
        Assert.Equal(0, pushes);
        Assert.Equal(0, jumps);
        Assert.Equal(Strings.MarksNoOther, marks.StatusText);
    }

    [Fact]
    public void Marks_clamped_to_the_start_are_skipped_and_Next_moves_on_to_a_later_mark()
    {
        var (viewport, marks, index) = Standalone();
        index.SetPage(0, [Mark(0, 10, 60), Mark(0, 100, 150)]); // both centred above the top: the view stays at 0
        index.SetPage(3, [Mark(3, 300, 400)]);
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        var pushes = 0;
        viewport.History.Changed += (_, _) => pushes++;

        marks.NextCommand.Execute(null);

        Assert.Equal(CentredTop(viewport, 3, 300, 400), AbsoluteTop(viewport), 3);
        Assert.Equal(1, pushes);
        viewport.BackCommand.Execute(null);
        Assert.Equal(0, AbsoluteTop(viewport), 3);
        Assert.False(viewport.BackCommand.CanExecute(null));
    }

    [Fact]
    public void GoToMark_on_the_mark_already_shown_leaves_no_history()
    {
        var (viewport, marks, index) = Standalone();
        SetThreeMarks(index);
        var pushes = 0;
        viewport.History.Changed += (_, _) => pushes++;
        var jumps = 0;
        viewport.Jumped += (_, _) => jumps++;

        marks.GoToMarkCommand.Execute(marks.Items[1]);
        marks.GoToMarkCommand.Execute(marks.Items[1]);

        Assert.Equal(1, pushes);
        Assert.Equal(1, jumps);
        viewport.BackCommand.Execute(null);
        Assert.False(viewport.BackCommand.CanExecute(null));
    }

    [Fact]
    public void Left_and_right_lines_of_one_passage_take_one_jump()
    {
        var (viewport, marks, index) = Standalone();
        index.SetPage(3, [Mark(3, 300, 400), new Mark(3, MarkSide.Right, 300.5, 420, [Guid.NewGuid()])]);
        index.SetPage(6, [Mark(6, 100, 200)]);
        _scheduler.Advance(MarksViewModel.RefreshInterval);

        marks.NextCommand.Execute(null);
        Assert.Equal(CentredTop(viewport, 3, 300, 400), AbsoluteTop(viewport), 3);
        marks.NextCommand.Execute(null); // the right line of the same passage is already shown
        Assert.Equal(CentredTop(viewport, 6, 100, 200), AbsoluteTop(viewport), 3);

        marks.PreviousCommand.Execute(null); // back to the passage: one jump, to either line
        Assert.InRange(AbsoluteTop(viewport), CentredTop(viewport, 3, 300, 400) - 1, CentredTop(viewport, 3, 300.5, 420) + 1);
        var position = viewport.Position;
        marks.PreviousCommand.Execute(null);
        Assert.Equal(position, viewport.Position);
        Assert.Equal(Strings.MarksNoOther, marks.StatusText);
    }

    [Fact]
    public void Unchanged_marks_keep_their_rows_across_refreshes()
    {
        var (_, marks, index) = Standalone();
        index.SetPage(5, [Mark(5, 100, 300)]);
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        var items = marks.Items;
        var row = marks.Items[0];

        index.SetPage(2, [Mark(2, 100, 300)]); // before it
        index.SetPage(8, [Mark(8, 100, 300)]); // after it
        _scheduler.Advance(MarksViewModel.RefreshInterval);

        Assert.Same(items, marks.Items);
        Assert.Equal(3, marks.Count);
        Assert.Same(row, marks.Items[1]);

        index.ClearPage(2);
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        Assert.Same(row, marks.Items[0]);
        Assert.Equal([5, 8], marks.Items.Select(i => i.Mark.PageIndex));
    }

    [Fact]
    public async Task Index_changes_made_on_another_thread_reach_the_list_without_a_synchronization_context()
    {
        var (marks, index) = await Task.Run(() =>
        {
            Assert.Null(SynchronizationContext.Current);
            var (_, vm, markIndex) = Standalone();
            return (vm, markIndex);
        });

        await Task.Run(() => index.SetPage(1, [Mark(1, 100, 300)]));
        _scheduler.Advance(MarksViewModel.RefreshInterval);

        Assert.Equal(1, marks.Count);
    }

    [Fact]
    public void The_dispatcher_scheduler_runs_actions_scheduled_from_another_thread_on_its_own_thread()
    {
        WpfTestThread.Run(() =>
        {
            var scheduler = new DispatcherDelayScheduler();
            var uiThread = Environment.CurrentManagedThreadId;
            int? ranOn = null;
            var cancelledRan = false;

            Task.Run(() =>
            {
                scheduler.Schedule(TimeSpan.FromMilliseconds(1), () => ranOn = Environment.CurrentManagedThreadId);
                scheduler.Schedule(TimeSpan.FromMilliseconds(1), () => cancelledRan = true).Dispose();
            }).Wait();
            WpfTestThread.PumpUntil(() => ranOn is not null);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            WpfTestThread.PumpUntil(() => watch.ElapsedMilliseconds > 50); // long past the cancelled one's due time

            Assert.Equal(uiThread, ranOn);
            Assert.False(cancelledRan);
        });
    }

    // ---- Lifecycle in the main view model -----------------------------------------------------------------

    [Fact]
    public async Task No_other_mark_is_not_reported_while_the_scan_still_has_pages_to_visit()
    {
        var pages = Pages(6);
        pages.Strokes[3] = [MarginLine()];
        var source = new FakeMarkPageSource(pages);
        var gate = source.Hold(3);
        var vm = NewVm(pages, _ => source);
        await OpenAsync(vm, "a.pdf");
        var scanner = vm.MarkScannerForTest!;
        await source.Started[3].Task.WaitAsync(_timeout);
        _scheduler.Advance(MarksViewModel.RefreshInterval);

        Assert.True(vm.Marks.IsScanning);
        Assert.True(vm.Marks.NextCommand.CanExecute(null)); // no mark yet, but the scan goes on
        vm.Marks.NextCommand.Execute(null);
        Assert.Null(vm.Marks.StatusText);

        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        Assert.False(vm.Marks.IsScanning);
        vm.Marks.NextCommand.Execute(null);
        Assert.Equal(3, vm.Viewport.Position.PageIndex);
        vm.Marks.NextCommand.Execute(null);
        Assert.Equal(Strings.MarksNoOther, vm.Marks.StatusText); // the scan is complete: now it is true
    }

    // ---- Lifecycle in the main view model (continued) ------------------------------------------------------

    [Fact]
    public async Task Opening_a_document_starts_the_scan_and_closing_disposes_it()
    {
        var pages = Pages(6);
        pages.Strokes[0] = [MarginLine()];
        pages.Strokes[3] = [MarginLine()];
        var source = new FakeMarkPageSource(pages);
        var vm = NewVm(pages, _ => source);

        await OpenAsync(vm, "a.pdf");

        var scanner = vm.MarkScannerForTest;
        var index = vm.MarkIndexForTest;
        Assert.NotNull(scanner);
        Assert.NotNull(index);
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        Assert.True(scanner.IsCompleted);
        Assert.Equal([1, 2, 3, 4, 5], source.Loads.Order()); // page 0 is loaded for editing: tracked live, not scanned
        Assert.Single(index.MarksOnPage(0));
        Assert.Single(index.MarksOnPage(3));
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        Assert.Equal(2, vm.Marks.Count);
        Assert.False(vm.Marks.IsScanning);

        Assert.True(await vm.CanCloseAsync());

        Assert.Null(vm.MarkScannerForTest);
        Assert.Throws<ObjectDisposedException>(() => scanner.Start(0)); // disposed
        Assert.True(vm.Editor!.CommitStroke(MarginLine(x: 590).Points, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1.5));
        Assert.Single(index.MarksOnPage(0)); // the tracker no longer listens
    }

    [Fact]
    public async Task Opening_another_document_uses_a_new_index_and_disposes_the_previous_scan()
    {
        var pages = Pages(4);
        var source = new FakeMarkPageSource(pages);
        var gate = source.Hold(2);
        var vm = NewVm(pages, _ => source);
        await OpenAsync(vm, "a.pdf");
        var firstScanner = vm.MarkScannerForTest!;
        var firstIndex = vm.MarkIndexForTest!;
        await source.Started[2].Task.WaitAsync(_timeout);

        await OpenAsync(vm, "b.pdf");
        gate.SetResult();

        Assert.NotSame(firstIndex, vm.MarkIndexForTest);
        Assert.NotSame(firstScanner, vm.MarkScannerForTest);
        Assert.Throws<ObjectDisposedException>(() => firstScanner.Start(0));
        await firstScanner.WhenIdleAsync().WaitAsync(_timeout);
        Assert.False(firstIndex.IsScanned(2)); // nothing published after the dispose
        await vm.MarkScannerForTest!.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal(4, vm.MarkIndexForTest!.ScannedCount);
    }

    [Fact]
    public async Task Scan_pauses_while_a_stroke_is_live_and_during_a_save()
    {
        // A live stroke (or eraser gesture) locks the zoom: the scan parks after the page in flight.
        var pages = Pages(6);
        var source = new FakeMarkPageSource(pages);
        var gate = source.Hold(1);
        var vm = NewVm(pages, _ => source);
        await OpenAsync(vm, "a.pdf");
        var scanner = vm.MarkScannerForTest!;
        await source.Started[1].Task.WaitAsync(_timeout);

        vm.ZoomVm.IsLocked = true;
        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal([1], source.Loads);
        Assert.False(scanner.IsCompleted);

        vm.ZoomVm.IsLocked = false;
        await scanner.WhenIdleAsync().WaitAsync(_timeout);
        Assert.True(scanner.IsCompleted);
        Assert.Equal([1, 2, 3, 4, 5], source.Loads.Order());

        // A save parks it too, across the renderer swap of a plain Save; the marks commands stay usable.
        var savePages = Pages(6);
        savePages.Strokes[0] = [MarginLine()];
        var viewer = new FakeViewerHandle(_log);
        var saveSource = new FakeMarkPageSource(savePages);
        var saveGate = saveSource.Hold(1);
        var writer = new FakeDocumentWriter(_log);
        var saving = NewVm(savePages, _ => viewer.IsReleased ? null : saveSource, viewer);
        saving.SaveService = new DocumentSaveService(writer, _dialogs);
        await OpenAsync(saving, "s.pdf");
        var before = saving.MarkScannerForTest!;
        await saveSource.Started[1].Task.WaitAsync(_timeout);
        Assert.True(saving.Editor!.CommitStroke(MarginLine(x: 590).Points, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1.5));

        writer.Gate = new TaskCompletionSource();
        var save = saving.SaveCommand.ExecuteAsync(null);
        await writer.Started.Task.WaitAsync(_timeout);
        saveGate.SetResult();
        await before.WhenIdleAsync().WaitAsync(_timeout);
        Assert.Equal([1], saveSource.Loads);
        Assert.True(saving.Marks.NextCommand.CanExecute(null));
        Assert.True(saving.Marks.PreviousCommand.CanExecute(null));

        writer.Gate.SetResult();
        await save.WaitAsync(_timeout);
        var after = saving.MarkScannerForTest!;
        Assert.NotSame(before, after); // the scan follows the reopened renderer
        await after.WhenIdleAsync().WaitAsync(_timeout);
        Assert.True(after.IsCompleted);
        Assert.Equal([1, 2, 3, 4, 5], saveSource.Loads.Order()); // page 1 is not scanned twice
    }

    [Fact]
    public async Task Scan_follows_a_renderer_swap_keeping_the_marks_and_scanning_each_page_once()
    {
        var pages = Pages(6);
        pages.Strokes[2] = [MarginLine()];
        pages.Strokes[5] = [MarginLine()];
        var viewer = new FakeViewerHandle(_log);
        var sources = new List<FakeMarkPageSource>();
        var vm = NewVm(pages, _ =>
        {
            if (viewer.IsReleased)
            {
                return null;
            }

            var source = new FakeMarkPageSource(pages);
            if (sources.Count == 0)
            {
                source.Hold(3);
            }

            sources.Add(source);
            return source;
        }, viewer);
        await OpenAsync(vm, "a.pdf");
        var index = vm.MarkIndexForTest!;
        var first = vm.MarkScannerForTest!;
        await sources[0].Started[3].Task.WaitAsync(_timeout);

        await viewer.ReleaseAsync();
        Assert.Null(vm.MarkScannerForTest);
        await viewer.ReopenAsync(Path.Combine(_dir, "a.pdf"), CancellationToken.None);
        var second = vm.MarkScannerForTest!;
        await second.WhenIdleAsync().WaitAsync(_timeout);
        sources[0].Gates[3].SetResult();
        await first.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal(2, sources.Count);
        Assert.Same(index, vm.MarkIndexForTest);
        Assert.Equal([1, 2, 3], sources[0].Loads);
        Assert.Equal([3, 4, 5], sources[1].Loads.Order());
        Assert.Equal(6, index.ScannedCount);
        Assert.Single(index.MarksOnPage(2));
        Assert.Single(index.MarksOnPage(5));
    }

    [Fact]
    public async Task Recentre_follows_a_jump_to_a_distant_page()
    {
        var pages = Pages(100);
        var source = new FakeMarkPageSource(pages);
        var gate = source.Hold(1);
        var vm = NewVm(pages, _ => source);
        await OpenAsync(vm, "a.pdf");
        var scanner = vm.MarkScannerForTest!;
        await source.Started[1].Task.WaitAsync(_timeout);

        await vm.GoToPageCommand.ExecuteAsync(80); // index 79, loaded for editing: tracked live
        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        var loads = source.Loads;
        Assert.Equal([1, 80, 78, 81, 77], loads.Take(5));
        Assert.Equal(98, loads.Count); // every page except the two loaded for editing, once
    }

    [Fact]
    public async Task Scrolling_recentres_on_the_page_where_the_view_settles()
    {
        var pages = Pages(100);
        var source = new FakeMarkPageSource(pages);
        var gate = source.Hold(1);
        var vm = NewVm(pages, _ => source);
        await OpenAsync(vm, "a.pdf");
        vm.Viewport.ViewportWidthDip = _viewportWidthDip;
        vm.Viewport.ViewportHeightDip = _viewportHeightDip;
        var scanner = vm.MarkScannerForTest!;
        await source.Started[1].Task.WaitAsync(_timeout);

        var layout = vm.Viewport.Layout!;
        _scheduler.Advance(DocumentViewportViewModel.CurrentPageDelay); // the resize above settles first
        var recentres = vm.MarkRecentresForTest;
        for (var page = 60; page >= 20; page -= 10)
        {
            vm.Viewport.SetScroll(0, layout.PageTopPt(page) * 4 / 3); // scroll ticks passing through: not settled
            _scheduler.Advance(50);
        }

        Assert.Equal(recentres, vm.MarkRecentresForTest); // no recentre per tick
        _scheduler.Advance(DocumentViewportViewModel.CurrentPageDelay); // settled: the session hears of page 21
        Assert.Equal(20, vm.Session!.CurrentPageIndex);
        Assert.Equal(recentres + 1, vm.MarkRecentresForTest);
        gate.SetResult();
        await scanner.WhenIdleAsync().WaitAsync(_timeout);

        Assert.Equal([1, 20, 21, 19], source.Loads.Take(4));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private (DocumentViewportViewModel Viewport, MarksViewModel Marks, MarkIndex Index) Standalone(int pageCount = 10)
    {
        var viewport = new DocumentViewportViewModel(new ZoomViewModel(), _scheduler);
        var layout = new DocumentLayout(Enumerable.Repeat(new PageDimensions(612, 792), pageCount).ToArray());
        viewport.Attach(null, layout, null);
        viewport.ViewportWidthDip = _viewportWidthDip;
        viewport.ViewportHeightDip = _viewportHeightDip;
        Assert.Equal(1.0, viewport.Position.Zoom, 6);
        var marks = new MarksViewModel(viewport, _scheduler);
        var index = new MarkIndex(pageCount);
        marks.Attach(index);
        return (viewport, marks, index);
    }

    private static double WindowPt(DocumentViewportViewModel viewport) =>
        _viewportHeightDip / (viewport.Position.Zoom * 4.0 / 3.0);

    private static Mark Mark(int page, double top, double bottom) => new(page, MarkSide.Left, top, bottom, [Guid.NewGuid()]);

    private static double AbsoluteTop(DocumentViewportViewModel viewport) =>
        viewport.Layout!.PageTopPt(viewport.Position.PageIndex) + viewport.Position.YPt;

    private static double CentredTop(
        DocumentViewportViewModel viewport, int page, double top, double bottom, double windowPt = _windowPt) =>
        viewport.Layout!.PageTopPt(page) + ((top + bottom) / 2) - (windowPt / 2);

    private void SetThreeMarks(MarkIndex index)
    {
        index.SetPage(2, [Mark(2, 100, 200)]); // A
        index.SetPage(5, [Mark(5, 300, 400)]); // B
        index.SetPage(8, [Mark(8, 300, 400)]); // C
        _scheduler.Advance(MarksViewModel.RefreshInterval);
    }

    private static void ScrollToPageTop(DocumentViewportViewModel viewport, int page) =>
        viewport.SetScroll(0, viewport.Layout!.PageTopPt(page) * 4 / 3);

    private static InkStroke MarginLine(double x = 30, double top = 100, double bottom = 260)
        => new(
            Guid.NewGuid(),
            InkTool.Pen,
            InkColor.FromRgb(0, 0, 0),
            1.5,
            Enumerable.Range(0, 20)
                .Select(i => new InkPoint(x, top + ((bottom - top) * i / 19.0), 0.5f, null, null, null, i))
                .ToArray());

    private static void Draw(MainViewModel vm) =>
        Assert.True(vm.Editor!.CommitStroke(MarginLine(x: 300).Points, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1.5));

    private static FakePageSource Pages(int count) => new(Enumerable.Repeat((612.0, 792.0), count).ToArray());

    private MainViewModel NewVm(
        FakePageSource pages, Func<OpenedDocument, IMarkPageSource?> marks, IViewerHandle? viewer = null)
    {
        var vm = new MainViewModel(_dialogs, scheduler: _scheduler)
        {
            MarkSourceFactory = marks,
            MarkScanIdleGap = TimeSpan.Zero,
        };
        vm.OpenDocument = (path, _) => Task.FromResult(new OpenedDocument(pages, pages, path, viewer));
        return vm;
    }

    private async Task OpenAsync(MainViewModel vm, string name)
    {
        var path = Path.Combine(_dir, name);
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, [1, 2, 3]); // fingerprinted, never parsed
        }

        _dialogs.NextPath = path;
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.Editor);
    }
}
