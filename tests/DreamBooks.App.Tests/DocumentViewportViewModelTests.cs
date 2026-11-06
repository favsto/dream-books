using System.Windows;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Journal;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>
/// The multi-page view state: layout, current page, history, zoom and fit width, per-page erase, reveal on undo,
/// navigation during a save and the remembered position (FR-04, FR-06, FR-07, FR-09, FR-10, FR-26, FR-62).
/// </summary>
public sealed class DocumentViewportViewModelTests : IDisposable
{
    private const double _scale = 4.0 / 3.0; // DIP per point at 100%
    private const double _pitchPt = 792 + LayoutConstants.PageGapPt;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-viewport-" + Guid.NewGuid().ToString("N"));
    private readonly string _recoveryRoot;
    private readonly string _positionsRoot;
    private readonly FakeFileDialogService _dialogs = new();
    private readonly ManualScheduler _scheduler = new();

    public DocumentViewportViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _recoveryRoot = Path.Combine(_dir, "recovery");
        _positionsRoot = Path.Combine(_dir, "positions");
    }

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
    public async Task Layout_is_built_from_all_page_dimensions_at_open_without_loading_strokes()
    {
        var source = new FakePageSource((612, 792), (300, 400), (800, 600));
        var vm = NewVm();
        var layoutChanges = 0;
        vm.Viewport.LayoutChanged += (_, _) => layoutChanges++;

        await OpenAsync(vm, source);

        var layout = vm.Viewport.Layout;
        Assert.NotNull(layout);
        Assert.Equal(3, layout.PageCount);
        Assert.Equal(800, layout.ColumnWidthPt);
        Assert.Equal(792 + 400 + 600 + (3 * LayoutConstants.PageGapPt), layout.TotalHeightPt);
        Assert.Equal(792 + LayoutConstants.PageGapPt, layout.PageTopPt(1));
        Assert.Equal([0], source.Loads); // only the page shown first has its strokes read
        Assert.Equal(1, layoutChanges);
        Assert.Equal(0, vm.Viewport.CurrentPage);
        Assert.Equal(3, vm.Viewport.PageCount);
    }

    [Fact]
    public async Task CurrentPage_follows_the_viewport_centre_debounced_and_does_not_load_strokes()
    {
        var source = Pages(10);
        var vm = await OpenSizedAsync(source);
        var viewport = vm.Viewport;

        viewport.SetScroll(0, 4 * _pitchPt * _scale); // top edge at page 5's top: the centre is on page 5
        Assert.Equal(4, viewport.CurrentPage);
        Assert.Equal(0, vm.Session!.CurrentPageIndex);

        _scheduler.Advance(100);
        viewport.SetScroll(0, 6 * _pitchPt * _scale); // still moving: the report waits again
        Assert.Equal(6, viewport.CurrentPage);
        _scheduler.Advance(149);
        Assert.Equal(0, vm.Session.CurrentPageIndex);
        _scheduler.Advance(1);
        Assert.Equal(6, vm.Session.CurrentPageIndex);
        Assert.Equal(7, vm.PageNumber);
        Assert.Null(vm.Editor); // page 7 has no strokes loaded yet
        Assert.Equal([0], source.Loads);

        // The centre, not the top edge: the top is on page 7, but most of the window shows page 8.
        viewport.SetScroll(0, ((6 * _pitchPt) + 700) * _scale);
        Assert.Equal(6, viewport.Position.PageIndex);
        Assert.Equal(7, viewport.CurrentPage);
        Assert.Equal([6, 7], viewport.VisiblePages);
        Assert.Equal((4, 9), viewport.RetainedRange);
        Assert.Equal("8 / 10 · 100%", viewport.IndicatorText);
    }

    [Fact]
    public async Task GoToPage_clamps_records_history_and_BackCommand_returns()
    {
        var source = Pages(10);
        var vm = await OpenSizedAsync(source);
        var viewport = vm.Viewport;
        viewport.SetScroll(0, 100);
        var start = viewport.Position;
        Assert.False(viewport.BackCommand.CanExecute(null));

        await viewport.GoToPageCommand.ExecuteAsync(99);

        Assert.Equal(9, viewport.CurrentPage);
        Assert.Equal(9, viewport.Position.PageIndex);
        Assert.Equal(0, viewport.Position.YPt, 6);
        Assert.Equal(10, vm.PageNumber); // the target page is loaded and current at once
        Assert.NotNull(vm.Editor);
        Assert.Contains(9, source.Loads);
        Assert.True(viewport.History.CanGoBack);
        Assert.True(viewport.BackCommand.CanExecute(null));

        viewport.BackCommand.Execute(null);
        Assert.Equal(start.PageIndex, viewport.Position.PageIndex);
        Assert.Equal(start.YPt, viewport.Position.YPt, 6);
        Assert.Equal(0, viewport.CurrentPage);
        Assert.False(viewport.BackCommand.CanExecute(null));

        await viewport.GoToPageCommand.ExecuteAsync(-5);
        Assert.Equal(0, viewport.Position.PageIndex);
        Assert.Equal(0, viewport.Position.YPt, 6);
    }

    [Fact]
    public async Task Rail_drag_start_records_history_once()
    {
        var vm = await OpenSizedAsync(Pages(10));
        var viewport = vm.Viewport;
        var start = viewport.Position;

        viewport.BeginRailDrag();
        viewport.BeginRailDrag(); // a repeated start inside the same drag records nothing
        viewport.SetScroll(0, 1000);
        viewport.SetScroll(0, 3000);
        viewport.SetScroll(0, 5000);
        viewport.EndRailDrag();

        Assert.True(viewport.History.TryPop(out var recorded));
        Assert.Equal(start, recorded);
        Assert.False(viewport.History.CanGoBack);
    }

    [Fact]
    public async Task ZoomAt_is_ignored_while_ZoomVm_is_locked_and_keeps_the_anchor()
    {
        var vm = await OpenSizedAsync(Pages(10));
        var viewport = vm.Viewport;
        viewport.SetScroll(0, 4 * _pitchPt * _scale);
        var before = viewport.Position;
        var anchor = new Point(700, 200);

        vm.ZoomVm.IsLocked = true;
        viewport.ZoomAt(2, anchor);
        Assert.Equal(before, viewport.Position);
        Assert.Equal(1, vm.ZoomVm.Zoom);

        vm.ZoomVm.IsLocked = false;
        var positionChanges = 0;
        viewport.PositionChanged += (_, _) => positionChanges++;
        viewport.ZoomAt(2, anchor);

        var after = viewport.Position;
        Assert.Equal(2, after.Zoom, 6);
        Assert.Equal(2, vm.ZoomVm.Zoom, 6);
        Assert.Equal(1, positionChanges);
        var layout = viewport.Layout!;
        Assert.Equal(DocY(layout, before, anchor.Y), DocY(layout, after, anchor.Y), 0.5 / (2 * _scale));
        Assert.Equal(DocX(before, anchor.X, 1000), DocX(after, anchor.X, 1000), 0.5 / (2 * _scale));
    }

    [Fact]
    public async Task FitWidth_uses_the_widest_page_and_resize_recomputes_only_in_fit_mode()
    {
        var source = new FakePageSource((612, 792), (1224, 792), (612, 792));
        var vm = NewVm();
        await OpenAsync(vm, source);
        Assert.True(vm.ZoomVm.IsFitWidth); // the default for a document without a remembered position

        vm.Viewport.ViewportWidthDip = 1000;
        vm.Viewport.ViewportHeightDip = 800;
        var fit = 1000 / (1224 * _scale);
        Assert.Equal(fit, vm.ZoomVm.Zoom, 6);
        Assert.Equal(fit, vm.Viewport.Position.Zoom, 6);

        vm.Viewport.ViewportWidthDip = 2000;
        Assert.Equal(2 * fit, vm.ZoomVm.Zoom, 6);
        Assert.Equal(2 * fit, vm.Viewport.Position.Zoom, 6);

        vm.ZoomVm.Zoom = 1; // manual zoom: fit mode off
        Assert.False(vm.ZoomVm.IsFitWidth);
        vm.Viewport.ViewportWidthDip = 1500;
        Assert.Equal(1, vm.ZoomVm.Zoom);
        Assert.Equal(1, vm.Viewport.Position.Zoom);
    }

    [Fact]
    public async Task FitWidthCommand_fits_the_width_and_never_leaves_fit_mode() // FR-07, the chip's button
    {
        var vm = await OpenAsync(NewVm(), Pages(10));
        var viewport = vm.Viewport;
        viewport.ViewportWidthDip = 1000;
        viewport.ViewportHeightDip = 800;
        var fit = 1000 / (612 * _scale);

        vm.ZoomVm.ZoomInCommand.Execute(null);
        Assert.False(vm.ZoomVm.IsFitWidth);
        Assert.True(viewport.FitWidthCommand.CanExecute(null));
        viewport.FitWidthCommand.Execute(null);
        Assert.True(vm.ZoomVm.IsFitWidth);
        Assert.Equal(fit, vm.ZoomVm.Zoom, 6);

        viewport.FitWidthCommand.Execute(null); // already fitted: stays (unlike the double tap, which zooms to 200%)
        Assert.True(vm.ZoomVm.IsFitWidth);
        Assert.Equal(fit, vm.ZoomVm.Zoom, 6);
    }

    [Fact]
    public async Task Manual_zoom_turns_fit_mode_off_and_DoubleTap_toggles_it()
    {
        var vm = await OpenAsync(NewVm(), Pages(10));
        var viewport = vm.Viewport;
        viewport.ViewportWidthDip = 1000;
        viewport.ViewportHeightDip = 800;
        var fit = 1000 / (612 * _scale);
        Assert.True(vm.ZoomVm.IsFitWidth);

        vm.ZoomVm.ZoomInCommand.Execute(null);
        Assert.False(vm.ZoomVm.IsFitWidth);
        Assert.Equal(vm.ZoomVm.Zoom, viewport.Position.Zoom, 6);

        viewport.ToggleFitWidth(null); // double tap outside fit mode: fit width
        Assert.True(vm.ZoomVm.IsFitWidth);
        Assert.Equal(fit, vm.ZoomVm.Zoom, 6);
        Assert.Equal(fit, viewport.Position.Zoom, 6);

        viewport.ToggleFitWidth(300); // double tap in fit mode: 200% around the tap
        Assert.False(vm.ZoomVm.IsFitWidth);
        Assert.Equal(2, vm.ZoomVm.Zoom, 6);
        Assert.Equal(2, viewport.Position.Zoom, 6);

        viewport.ToggleFitWidth(300);
        Assert.True(vm.ZoomVm.IsFitWidth);
        viewport.ZoomAt(1.1, new Point(500, 400)); // pinch
        Assert.False(vm.ZoomVm.IsFitWidth);

        viewport.ToggleFitWidth(null);
        vm.ZoomVm.IsLocked = true; // a stroke is live: no zoom at all
        viewport.ToggleFitWidth(300);
        Assert.True(vm.ZoomVm.IsFitWidth);
        Assert.Equal(fit, vm.ZoomVm.Zoom, 6);
    }

    [Fact]
    public async Task Pen_on_a_visible_non_current_page_can_erase_without_changing_current_page()
    {
        var vm = await OpenSizedAsync(Pages(4), heightDip: 2000); // pages 1 and 2 in view, page 1 at the centre
        Assert.Equal([0, 1], vm.Viewport.VisiblePages);
        var other = await vm.EnsureEditorAsync(1);
        var line = Line(other, 100);
        vm.Undo.Clear();
        var previews = 0;
        vm.ErasePreviewChanged += (_, _) => previews++;

        vm.Tools.EraserMode = EraserMode.WholeStroke;
        vm.BeginErase(other);
        Assert.Same(other, vm.ActiveErasePage);
        Assert.True(vm.ZoomVm.IsLocked);
        vm.EraseAt(new PointD(100, 100));
        Assert.Empty(vm.ActiveErase!.Strokes);
        vm.EndErase();

        Assert.Null(vm.ActiveErase);
        Assert.Null(vm.ActiveErasePage);
        Assert.False(vm.ZoomVm.IsLocked);
        Assert.Empty(other.Page.Strokes);
        Assert.True(vm.Undo.CanUndo);
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(0, vm.Session!.CurrentPageIndex);
        Assert.Equal(0, vm.Viewport.CurrentPage);
        Assert.Equal(3, previews);
        Assert.DoesNotContain(line, other.Page.Strokes);
    }

    [Fact]
    public async Task Erase_on_one_page_blocks_a_second_gesture_until_it_ends()
    {
        var vm = await OpenSizedAsync(Pages(4), heightDip: 3000);
        var first = vm.Editor!;
        var third = await vm.EnsureEditorAsync(2);

        vm.BeginErase(first);
        var gesture = vm.ActiveErase;
        vm.BeginErase(third);
        Assert.Same(first, vm.ActiveErasePage);
        Assert.Same(gesture, vm.ActiveErase);

        vm.EndErase();
        vm.BeginErase(third);
        Assert.Same(third, vm.ActiveErasePage);
        vm.CancelErase();

        // An editor that is not this document's is refused.
        var foreign = new PageEditor(new Page(0, 612, 792), new Core.Undo.UndoManager());
        vm.BeginErase(foreign);
        Assert.Null(vm.ActiveErase);
        Assert.Null(vm.ActiveErasePage);
    }

    [Fact]
    public async Task Undo_of_a_stroke_on_a_visible_page_does_not_scroll()
    {
        var vm = await OpenSizedAsync(Pages(10), heightDip: 2000); // pages 1 and 2 in view, page 1 at the centre
        var viewport = vm.Viewport;
        Assert.Equal([0, 1], viewport.VisiblePages);
        var second = await vm.EnsureEditorAsync(1);
        Line(second, 100);
        var before = viewport.Position;
        var moves = 0;
        viewport.PositionChanged += (_, _) => moves++;

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.Empty(second.Page.Strokes);
        Assert.Equal(before, viewport.Position);
        Assert.Equal(0, moves);
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(0, viewport.CurrentPage);
    }

    [Fact]
    public async Task Undo_of_a_stroke_on_a_hidden_page_scrolls_to_reveal_it()
    {
        var vm = await OpenSizedAsync(Pages(10));
        var viewport = vm.Viewport;
        var first = vm.Editor!;
        Line(first, 100);
        await viewport.GoToPageCommand.ExecuteAsync(9);
        Assert.DoesNotContain(0, viewport.VisiblePages);

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.Empty(first.Page.Strokes);
        Assert.Equal(0, viewport.Position.PageIndex);
        Assert.Equal(0, viewport.Position.YPt, 6);
        Assert.Contains(0, viewport.VisiblePages);
        Assert.Equal(0, viewport.CurrentPage);
        Assert.Equal(1, vm.PageNumber); // at once, not after the debounce
        Assert.Same(first, vm.Editor);

        // The reveal is not a jump of the user: back still returns to where go-to-page started.
        Assert.True(viewport.History.TryPop(out var recorded));
        Assert.Equal(0, recorded.PageIndex);
        Assert.False(viewport.History.CanGoBack);
    }

    [Fact]
    public async Task Navigation_commands_stay_enabled_while_saving()
    {
        var log = new List<string>();
        var writer = new FakeDocumentWriter(log);
        var viewer = new FakeViewerHandle(log);
        var vm = NewVm();
        vm.SaveService = new DocumentSaveService(writer, _dialogs);
        var source = Pages(5);
        var path = WriteFile("saving.pdf");
        _dialogs.NextPath = path;
        vm.OpenDocument = (p, _) => Task.FromResult(new OpenedDocument(source, source, p, viewer));
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Line(vm.Editor!, 100);
        writer.Gate = new TaskCompletionSource();
        var saving = vm.SaveCommand.ExecuteAsync(null);
        await writer.Started.Task;
        Assert.True(vm.IsSaving);

        Assert.True(vm.PreviousPageCommand.CanExecute(null));
        Assert.True(vm.NextPageCommand.CanExecute(null));
        Assert.True(vm.GoToPageCommand.CanExecute(3));
        Assert.True(vm.Viewport.GoToPageCommand.CanExecute(3));
        Assert.True(vm.Viewport.PageDownCommand.CanExecute(null));
        Assert.True(vm.Viewport.PageUpCommand.CanExecute(null));
        Assert.True(vm.Viewport.HomeCommand.CanExecute(null));
        Assert.True(vm.Viewport.EndCommand.CanExecute(null));

        await vm.GoToPageCommand.ExecuteAsync(3);
        Assert.Equal(3, vm.PageNumber);
        Assert.True(vm.Viewport.BackCommand.CanExecute(null));
        Line(vm.Editor!, 200); // drawing goes on during the write
        await vm.UndoCommand.ExecuteAsync(null); // undo during the write stays safe
        Assert.Empty(vm.Editor!.Page.Strokes);
        vm.Viewport.BackCommand.Execute(null);
        Assert.Equal(0, vm.Viewport.CurrentPage);

        writer.Gate.SetResult();
        await saving;
        Assert.False(vm.IsSaving);
        Assert.False(vm.IsDirty); // the stroke drawn and undone during the save leaves the saved state
    }

    [Fact]
    public async Task Position_restore_prefers_the_journal_view_over_the_position_store()
    {
        var path = WriteFile("both.pdf");
        var vm = NewVm(withRecovery: true);
        await OpenAsync(vm, Pages(5), path);
        Line(vm.Editor!, 100); // dirty: the journal records the view
        SetSize(vm);
        vm.Viewport.SetScroll(0, ((2 * _pitchPt) + 30) * _scale);
        _scheduler.Advance(500);
        var journalled = vm.Viewport.Position;
        await vm.PositionWritesForTest;
        new ViewPositionStore(_positionsRoot).Save(path, new ViewPosition(4, 10, 306, 1.5)); // stored later
        await vm.SimulateCrashForTestAsync();

        var next = NewVm(withRecovery: true);
        await OpenAsync(next, Pages(5), path);

        Assert.Equal(journalled.PageIndex, next.Viewport.Position.PageIndex);
        Assert.Equal(journalled.YPt, next.Viewport.Position.YPt, 6);
        Assert.Equal(3, next.PageNumber);
        Assert.Equal(2, next.Viewport.CurrentPage);
        Assert.Equal(1, next.ZoomVm.Zoom);
        Assert.False(next.ZoomVm.IsFitWidth);
    }

    [Fact]
    public async Task Position_store_restores_a_saved_document_clamped()
    {
        var path = WriteFile("stored.pdf");
        new ViewPositionStore(_positionsRoot).Save(path, new ViewPosition(4, 5000, 306, 1.5));
        var vm = NewVm();

        await OpenAsync(vm, Pages(3), path); // the document has fewer pages than when it was stored

        var position = vm.Viewport.Position;
        Assert.Equal(2, position.PageIndex);
        Assert.Equal(792, position.YPt, 6); // inside the page, at most its bottom
        Assert.Equal(1.5, position.Zoom);
        Assert.Equal(1.5, vm.ZoomVm.Zoom);
        Assert.False(vm.ZoomVm.IsFitWidth);
        Assert.Equal(3, vm.PageNumber);
    }

    [Fact]
    public async Task Position_is_saved_when_the_view_settles_and_on_close()
    {
        var path = WriteFile("settle.pdf");
        var store = new ViewPositionStore(_positionsRoot);
        var vm = NewVm();
        await OpenAsync(vm, Pages(10), path);
        SetSize(vm);

        vm.Viewport.SetScroll(0, ((3 * _pitchPt) + 50) * _scale);
        _scheduler.Advance(499);
        await vm.PositionWritesForTest;
        Assert.Null(store.TryLoad(path));
        _scheduler.Advance(1);
        await vm.PositionWritesForTest;
        Assert.Equal(vm.Viewport.Position, store.TryLoad(path)?.Position);

        vm.Viewport.SetScroll(0, ((7 * _pitchPt) + 20) * _scale);
        Assert.True(await vm.CanCloseAsync()); // no wait: closing writes the last position
        Assert.Equal(7, store.TryLoad(path)?.Position.PageIndex);
        Assert.Equal(vm.Viewport.Position, store.TryLoad(path)?.Position);

        var reopened = NewVm();
        await OpenAsync(reopened, Pages(10), path);
        Assert.Equal(7, reopened.Viewport.Position.PageIndex);
        Assert.Equal(8, reopened.PageNumber);
    }

    [Fact]
    public async Task Journal_view_entry_uses_page_relative_offsets()
    {
        var path = WriteFile("journal.pdf");
        var vm = NewVm(withRecovery: true);
        await OpenAsync(vm, Pages(10), path);
        Line(vm.Editor!, 100);
        SetSize(vm);

        vm.Viewport.SetScroll(0, ((3 * _pitchPt) + 75) * _scale);
        _scheduler.Advance(500);

        var journal = Path.Combine(_recoveryRoot, RecoveryStore.DocumentKey(path), "journal.jsonl");
        string[] lines;
        using (var stream = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        var view = Assert.IsType<ViewEntry>(JournalCodec.ParseEntry(lines[^1]));
        Assert.Equal(3, view.PageIndex);
        Assert.Equal(1, view.Zoom);
        Assert.Equal(306, view.ScrollX, 6); // the viewport centre in column points
        Assert.Equal(75, view.ScrollY, 6); // below the top of page 4, in page points
    }

    [Fact]
    public async Task No_position_file_is_created_for_a_document_that_was_never_opened_successfully()
    {
        var broken = Pages(3);
        broken.FailingPages.Add(0);
        var brokenPath = WriteFile("broken.pdf");
        var vm = NewVm();

        await OpenAsync(vm, broken, brokenPath);
        Assert.Null(vm.Session);
        Assert.True(await vm.CanCloseAsync());
        Assert.False(Directory.Exists(_positionsRoot) && Directory.GetFiles(_positionsRoot).Length > 0);

        var goodPath = WriteFile("good.pdf");
        await OpenAsync(vm, Pages(3), goodPath);
        var again = Pages(3);
        again.FailingPages.Add(0);
        await OpenAsync(vm, again, brokenPath); // fails: the good document stays
        Assert.Equal(goodPath, vm.Session!.Path);
        Assert.True(await vm.CanCloseAsync());

        var store = new ViewPositionStore(_positionsRoot);
        Assert.Null(store.TryLoad(brokenPath));
        Assert.NotNull(store.TryLoad(goodPath));
    }

    [Fact]
    public async Task EnsureEditorAsync_refreshes_the_current_editor_and_is_cancelled_without_a_document()
    {
        var vm = await OpenSizedAsync(Pages(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MainViewModel(_dialogs).EnsureEditorAsync(0));

        vm.Viewport.SetScroll(0, 4 * _pitchPt * _scale);
        _scheduler.Advance(150);
        Assert.Null(vm.Editor);
        var editorChanges = 0;
        vm.PropertyChanged += (_, e) => editorChanges += e.PropertyName == nameof(MainViewModel.Editor) ? 1 : 0;

        var editor = await vm.EnsureEditorAsync(4);

        Assert.Same(editor, vm.Editor); // the current page got its editor without a page change
        Assert.Equal(1, editorChanges);
    }

    [Fact]
    public async Task PageDown_PageUp_Home_and_End_move_by_a_viewport_and_to_the_ends()
    {
        var vm = await OpenSizedAsync(Pages(10));
        var viewport = vm.Viewport;
        var layout = viewport.Layout!;

        viewport.PageDownCommand.Execute(null);
        Assert.Equal(800 * DocumentViewportViewModel.PageStepFraction, Offset(viewport), 3);
        viewport.PageUpCommand.Execute(null);
        Assert.Equal(0, Offset(viewport), 3);

        viewport.EndCommand.Execute(null);
        Assert.Equal((layout.ExtentHeightDip(1) - 800), Offset(viewport), 3);
        Assert.Equal(9, viewport.CurrentPage);
        Assert.True(viewport.History.CanGoBack);
        viewport.HomeCommand.Execute(null);
        Assert.Equal(0, Offset(viewport), 3);
        Assert.Equal(0, viewport.CurrentPage);
    }

    [Fact]
    public async Task Pending_centre_report_does_not_supersede_a_go_to_page()
    {
        var source = Pages(10);
        var gate = new TaskCompletionSource();
        source.Gates[5] = gate;
        var vm = await OpenSizedAsync(source);

        vm.Viewport.SetScroll(0, 2 * _pitchPt * _scale); // a centre report is now pending
        var going = vm.Viewport.GoToPageCommand.ExecuteAsync(6); // and the user jumps before it fires
        _scheduler.Advance(150);
        Assert.Equal(0, vm.Session!.CurrentPageIndex); // the old report was dropped
        gate.SetResult();
        await going;

        Assert.Equal(5, vm.Session.CurrentPageIndex);
        Assert.Equal(6, vm.PageNumber);
        Assert.Equal(5, vm.Editor?.Page.Index);
    }

    [Fact]
    public async Task Restore_makes_the_centre_page_current_with_its_editor()
    {
        var path = WriteFile("centre.pdf");
        new ViewPositionStore(_positionsRoot).Save(path, new ViewPosition(6, 500, 306, 1)); // top on page 7, centre on 8
        var vm = NewVm();
        vm.Viewport.ViewportWidthDip = 1000;
        vm.Viewport.ViewportHeightDip = 800;

        await OpenAsync(vm, Pages(10), path);

        Assert.Equal(6, vm.Viewport.Position.PageIndex);
        Assert.Equal(7, vm.Viewport.CurrentPage);
        Assert.Equal(7, vm.Session!.CurrentPageIndex);
        Assert.Equal(8, vm.PageNumber);
        Assert.Equal(7, vm.Editor?.Page.Index);
    }

    [Fact]
    public async Task Fit_width_mode_is_remembered_and_recomputed_for_another_width()
    {
        var path = WriteFile("fit.pdf");
        var vm = await OpenAsync(NewVm(), Pages(5), path);
        vm.Viewport.ViewportWidthDip = 1000;
        vm.Viewport.ViewportHeightDip = 800;
        Assert.True(vm.ZoomVm.IsFitWidth);
        var scale = 1000 / 612.0; // DIP per point in fit mode
        vm.Viewport.SetScroll(0, ((2 * _pitchPt) + 30) * scale);
        Assert.True(await vm.CanCloseAsync());
        Assert.True(new ViewPositionStore(_positionsRoot).TryLoad(path)?.FitWidth);

        var wider = NewVm();
        wider.Viewport.ViewportWidthDip = 1500;
        wider.Viewport.ViewportHeightDip = 800;
        await OpenAsync(wider, Pages(5), path);

        Assert.True(wider.ZoomVm.IsFitWidth);
        Assert.Equal(1500 / (612 * _scale), wider.ZoomVm.Zoom, 6);
        Assert.Equal(2, wider.Viewport.Position.PageIndex);
        Assert.Equal(30, wider.Viewport.Position.YPt, 6);

        var unsized = await OpenAsync(NewVm(), Pages(5), path); // the width is not known yet: recomputed once it is
        Assert.True(unsized.ZoomVm.IsFitWidth);
        unsized.Viewport.ViewportWidthDip = 2000;
        Assert.Equal(2000 / (612 * _scale), unsized.ZoomVm.Zoom, 6);
    }

    [Fact]
    public async Task Journal_view_restores_fit_width_mode_at_another_width()
    {
        var path = WriteFile("fit-journal.pdf");
        var vm = await OpenAsync(NewVm(withRecovery: true), Pages(5), path);
        Line(vm.Editor!, 100);
        vm.Viewport.ViewportWidthDip = 1000;
        vm.Viewport.ViewportHeightDip = 800;
        vm.Viewport.SetScroll(0, ((3 * _pitchPt) + 40) * (1000 / 612.0));
        _scheduler.Advance(500);
        await vm.PositionWritesForTest;
        new ViewPositionStore(_positionsRoot).Save(path, new ViewPosition(0, 0, 306, 3)); // the journal must win
        await vm.SimulateCrashForTestAsync();

        var next = NewVm(withRecovery: true);
        next.Viewport.ViewportWidthDip = 1500;
        next.Viewport.ViewportHeightDip = 800;
        await OpenAsync(next, Pages(5), path);

        Assert.True(next.ZoomVm.IsFitWidth);
        Assert.Equal(1500 / (612 * _scale), next.ZoomVm.Zoom, 6);
        Assert.Equal(3, next.Viewport.Position.PageIndex);
        Assert.Equal(40, next.Viewport.Position.YPt, 6);
    }

    [Fact]
    public async Task Failed_go_to_page_still_makes_the_target_current_without_an_editor()
    {
        var source = Pages(10);
        source.FailingPages.Add(5);
        var vm = await OpenSizedAsync(source);

        await vm.Viewport.GoToPageCommand.ExecuteAsync(6);

        Assert.Equal(Resources.Strings.ErrorPageChangeFailed, vm.ErrorMessage);
        Assert.Equal(5, vm.Viewport.CurrentPage);
        Assert.Equal(5, vm.Session!.CurrentPageIndex);
        Assert.Equal(6, vm.PageNumber);
        Assert.Null(vm.Editor);
    }

    [Fact]
    public async Task Previous_and_Next_do_not_record_history_and_stop_at_the_ends()
    {
        var vm = await OpenSizedAsync(Pages(5));
        var viewport = vm.Viewport;
        var moves = 0;
        viewport.PositionChanged += (_, _) => moves++;

        await vm.PreviousPageCommand.ExecuteAsync(null); // already on page 1
        Assert.Equal(0, moves);
        Assert.Equal(0, viewport.CurrentPage);

        await vm.NextPageCommand.ExecuteAsync(null);
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(2, viewport.CurrentPage);
        Assert.Equal(3, vm.PageNumber);
        Assert.NotNull(vm.Editor);
        Assert.False(viewport.History.CanGoBack);

        await vm.PreviousPageCommand.ExecuteAsync(null);
        Assert.Equal(1, viewport.CurrentPage);
        Assert.False(viewport.History.CanGoBack);

        await viewport.GoToPageCommand.ExecuteAsync(5);
        var atEnd = viewport.Position;
        moves = 0;
        await vm.NextPageCommand.ExecuteAsync(null); // already on the last page
        Assert.Equal(0, moves);
        Assert.Equal(atEnd, viewport.Position);
        Assert.Equal(4, viewport.CurrentPage);
    }

    [Fact]
    public async Task Restored_position_in_the_gap_above_a_page_is_kept()
    {
        var store = new ViewPositionStore(_positionsRoot);
        var inGap = WriteFile("gap.pdf");
        var beyond = WriteFile("beyond.pdf");
        store.Save(inGap, new ViewPosition(3, -5, 306, 1));
        store.Save(beyond, new ViewPosition(3, -50, 306, 1));

        var vm = await OpenAsync(NewVm(), Pages(10), inGap);
        Assert.Equal(-5, vm.Viewport.Position.YPt, 6);
        var other = await OpenAsync(NewVm(), Pages(10), beyond);
        Assert.Equal(-LayoutConstants.PageGapPt, other.Viewport.Position.YPt, 6);
    }

    [Fact]
    public async Task Fit_width_skipped_while_locked_catches_up_when_the_lock_is_released()
    {
        var vm = await OpenAsync(NewVm(), Pages(10));
        vm.Viewport.ViewportWidthDip = 1000;
        vm.Viewport.ViewportHeightDip = 800;
        var fit = vm.ZoomVm.Zoom;

        vm.ZoomVm.IsLocked = true; // a stroke is live while the window is resized
        vm.Viewport.ViewportWidthDip = 2000;
        Assert.Equal(fit, vm.ZoomVm.Zoom);

        vm.ZoomVm.IsLocked = false;
        Assert.Equal(2000 / (612 * _scale), vm.ZoomVm.Zoom, 6);
        Assert.Equal(vm.ZoomVm.Zoom, vm.Viewport.Position.Zoom, 6);
        Assert.True(vm.ZoomVm.IsFitWidth);
    }

    [Fact]
    public async Task Zoom_at_the_limit_raises_no_PositionChanged()
    {
        var vm = await OpenSizedAsync(Pages(10));
        vm.ZoomVm.Zoom = 8;
        var moves = 0;
        vm.Viewport.PositionChanged += (_, _) => moves++;

        vm.Viewport.ZoomAt(2, new Point(500, 400));

        Assert.Equal(0, moves);
        Assert.Equal(8, vm.Viewport.Position.Zoom);
    }

    [Fact]
    public async Task Old_journal_view_with_DIP_offsets_restores_on_the_right_page()
    {
        var path = WriteFile("old-journal.pdf");
        var vm = await OpenAsync(NewVm(withRecovery: true), Pages(3), path);
        Line(vm.Editor!, 100);
        // What the removed single-page view journalled: no fit flag, DIP offsets inside page 2.
        vm.JournalForTest!.RecordView(1, 2.0, 30, 400);
        await vm.SimulateCrashForTestAsync();

        var next = await OpenAsync(NewVm(withRecovery: true), Pages(3), path);

        var position = next.Viewport.Position;
        Assert.Equal(1, position.PageIndex);
        Assert.Equal(400 / (2 * _scale), position.YPt, 6); // DIPs at 200% turned into page points
        Assert.Equal(306, position.ColumnXPt, 6);
        Assert.Equal(2, position.Zoom);
        Assert.False(next.ZoomVm.IsFitWidth);
        Assert.Equal(2, next.PageNumber);
    }

    [Fact]
    public async Task EnsureEditorAsync_is_cancelled_when_the_document_is_replaced_mid_load()
    {
        var first = Pages(5);
        var gate = new TaskCompletionSource();
        first.Gates[3] = gate;
        var vm = await OpenAsync(NewVm(), first);
        var loading = vm.EnsureEditorAsync(3);

        await OpenAsync(vm, Pages(2));
        gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.Equal(2, vm.Session!.PageCount);
        Assert.Equal(0, vm.Editor?.Page.Index);
    }

    [Fact]
    public async Task Jumps_raise_Jumped_and_plain_scrolling_does_not()
    {
        var vm = await OpenSizedAsync(Pages(20));
        var jumps = 0;
        vm.Viewport.Jumped += (_, _) => jumps++;

        vm.Viewport.SetScroll(0, 300);
        vm.Viewport.ZoomAt(1.2, new Point(500, 400));
        Assert.Equal(0, jumps);

        vm.Viewport.EndCommand.Execute(null);
        Assert.Equal(1, jumps);
        vm.Viewport.HomeCommand.Execute(null);
        Assert.Equal(2, jumps);
        vm.Viewport.BackCommand.Execute(null);
        Assert.Equal(3, jumps);
        vm.Viewport.PageDownCommand.Execute(null);
        Assert.Equal(4, jumps);
        vm.Viewport.ScrollToPage(7, recordHistory: false);
        Assert.Equal(5, jumps);
        await vm.Viewport.GoToPageCommand.ExecuteAsync(12);
        Assert.Equal(6, jumps);

        vm.Viewport.BeginRailDrag();
        vm.Viewport.SetScroll(0, 5000);
        Assert.Equal(6, jumps); // scrubbing: not yet
        vm.Viewport.EndRailDrag();
        Assert.Equal(7, jumps);
        vm.Viewport.BeginRailDrag();
        vm.Viewport.EndRailDrag(); // a press that moved nothing
        Assert.Equal(7, jumps);
    }

    [Fact]
    public async Task Failed_stroke_load_of_a_page_for_the_view_is_reported()
    {
        var source = Pages(5);
        source.FailingPages.Add(3);
        var vm = await OpenAsync(NewVm(), source);
        Assert.Null(vm.ErrorMessage);

        await Assert.ThrowsAsync<IOException>(() => vm.EnsureEditorAsync(3));

        Assert.Equal(Strings.ErrorPageChangeFailed, vm.ErrorMessage); // never the exception text (NFR-12)
        await Assert.ThrowsAsync<IOException>(() => vm.EnsureEditorAsync(3)); // still failing, said once
        Assert.Equal(Strings.ErrorPageChangeFailed, vm.ErrorMessage);
    }

    [Fact]
    public async Task Layout_change_to_a_shorter_document_never_exposes_the_previous_ranges()
    {
        var vm = await OpenSizedAsync(Pages(1076));
        var viewport = vm.Viewport;
        await viewport.GoToPageCommand.ExecuteAsync(1070);
        Assert.True(viewport.RetainedRange.First > 1000);
        var seen = new List<((int First, int Last) Retained, int[] Visible, int Count)>();
        viewport.LayoutChanged += (_, _) => seen.Add((viewport.RetainedRange, viewport.VisiblePages.ToArray(), viewport.PageCount));

        await OpenAsync(vm, Pages(3));

        var (retained, visible, count) = Assert.Single(seen);
        Assert.Equal(3, count);
        Assert.True(retained.Last < count, $"retained {retained} for {count} pages");
        Assert.All(visible, page => Assert.InRange(page, 0, count - 1));
        Assert.True(viewport.RetainedRange.Last < 3);
        Assert.All(viewport.VisiblePages, page => Assert.InRange(page, 0, 2));
    }

    private static double Offset(DocumentViewportViewModel viewport) =>
        viewport.Layout!.ToOffsets(viewport.Position, viewport.ViewportWidthDip, viewport.ViewportHeightDip).OffsetYDip;

    private static double DocY(DocumentLayout layout, ViewPosition p, double anchorY) =>
        layout.PageTopPt(p.PageIndex) + p.YPt + (anchorY / (p.Zoom * _scale));

    private static double DocX(ViewPosition p, double anchorX, double viewportWidth) =>
        p.ColumnXPt + ((anchorX - (viewportWidth / 2)) / (p.Zoom * _scale));

    private static FakePageSource Pages(int count) => new(Enumerable.Repeat((612.0, 792.0), count).ToArray());

    private static InkStroke Line(PageEditor editor, double y)
    {
        InkPoint[] points = [new(50, y, 0.5f, null, null, null, 0), new(150, y, 0.5f, null, null, null, 10)];
        Assert.True(editor.CommitStroke(points, InkTool.Pen, Palette.Colors[0], 1));
        return editor.Page.Strokes[^1];
    }

    private MainViewModel NewVm(bool withRecovery = false) =>
        new(_dialogs, withRecovery ? new RecoveryStore(_recoveryRoot) : null, new ViewPositionStore(_positionsRoot), _scheduler);

    /// <summary>Opens 100% zoom on a 1000 × <paramref name="heightDip"/> DIP viewport, fit mode off.</summary>
    private async Task<MainViewModel> OpenSizedAsync(FakePageSource source, double heightDip = 800)
    {
        var vm = await OpenAsync(NewVm(), source);
        SetSize(vm, heightDip);
        return vm;
    }

    private static void SetSize(MainViewModel vm, double heightDip = 800)
    {
        vm.ZoomVm.Zoom = 1;
        vm.Viewport.ViewportWidthDip = 1000;
        vm.Viewport.ViewportHeightDip = heightDip;
        vm.Viewport.SetScroll(0, 0);
    }

    private async Task<MainViewModel> OpenAsync(MainViewModel vm, FakePageSource source, string? path = null)
    {
        _dialogs.NextPath = path ?? WriteFile(Guid.NewGuid().ToString("N") + ".pdf");
        source.InstallOn(vm);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        return vm;
    }

    private string WriteFile(string name)
    {
        var path = Path.Combine(_dir, name);
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, [1, 2, 3]); // fingerprinted, never parsed
        }

        return path;
    }
}
