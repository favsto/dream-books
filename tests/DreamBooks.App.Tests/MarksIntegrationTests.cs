using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Journal;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Interop;
using DreamBooks.Pdf.Tests;
using Xunit.Abstractions;

namespace DreamBooks.App.Tests;

/// <summary>
/// Cross-layer sweep of the marks (FR-100..FR-103, NFR-06): the real <see cref="MainViewModel"/>, the real
/// <see cref="DocumentView"/> (never shown), PDFium, the real scan over <see cref="MarkPageSource"/> and the real save
/// pipeline on generated PDFs, plus a copy of a real e-book when one is available. The scan is derived data: it never
/// changes what is saved, the dirty state or the journal. Real documents are only read as copies and only counts,
/// times and sizes are reported (NFR-12).
/// </summary>
[Collection(nameof(MarkIntegrationCollection))]
public sealed class MarksIntegrationTests : IDisposable
{
    private const string _realDocumentsFolder = @"C:\Users\Favsto\workspace\temp";
    private const string _fixturesVariable = "DREAMBOOKS_FIXTURES";
    private const double _widthPt = 612;
    private const double _heightPt = 792;
    private const int _textAnnotSubtype = 1; // FPDF_ANNOT_TEXT
    private const int _timeoutMs = 60_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-marks-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly FakeFileDialogService _dialogs = new();

    public MarksIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    private string RecoveryRoot => Path.Combine(_dir, "recovery");

    private string PositionsRoot => Path.Combine(_dir, "positions");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- The full scan of a long document --------------------------------------------------------------------

    [Fact]
    public void A_300_page_document_with_margin_lines_on_twelve_pages_is_fully_indexed_ticked_and_navigable()
    {
        // Twelve pages, with the merge cases: two close lines (one mark), a left/right pair (two marks, one passage),
        // two far lines (two marks).
        var strokes = new Dictionary<int, InkStroke[]>
        {
            [3] = [MarginLine(40, 200, 360)],
            [17] = [MarginLine(580, 100, 300)],
            [40] = [MarginLine(40, 300, 420)],
            [41] = [MarginLine(40, 200, 320), MarginLine(45, 340, 470)], // gap 20 pt: merged
            [88] = [MarginLine(40, 200, 400), MarginLine(580, 200, 400)], // both sides of one passage
            [120] = [MarginLine(40, 500, 700), Scribble()], // a normal stroke beside it is no mark
            [150] = [MarginLine(40, 100, 230), MarginLine(40, 500, 640)], // far apart: two marks
            [199] = [MarginLine(580, 300, 500)],
            [230] = [MarginLine(40, 50, 200)],
            [260] = [MarginLine(40, 600, 760)],
            [280] = [MarginLine(580, 400, 560)],
            [298] = [MarginLine(40, 200, 360)],
        };
        var path = Author("long.pdf", 300, strokes);
        var expectedPages = strokes.Keys.Order().ToArray();
        var probe = new ScanProbe();

        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler, probe);
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                var scanner = vm.MarkScannerForTest!;
                WaitScanCompleted(scanner);
                Refresh(scheduler, view);

                // The index: exactly those pages, with the merge cases resolved.
                var index = vm.MarkIndexForTest!;
                Assert.Equal(300, index.ScannedCount);
                Assert.Equal(0, scanner.FailedCount);
                Assert.Equal(expectedPages, index.AllMarks().Select(m => m.PageIndex).Distinct().ToArray());
                Assert.Single(index.MarksOnPage(41));
                Assert.Equal(200, index.MarksOnPage(41)[0].TopPt, 6);
                Assert.Equal(470, index.MarksOnPage(41)[0].BottomPt, 6);
                Assert.Equal([MarkSide.Left, MarkSide.Right], index.MarksOnPage(88).Select(m => m.Side).Order().ToArray());
                Assert.Single(index.MarksOnPage(120));
                Assert.Equal(2, index.MarksOnPage(150).Count);
                Assert.Equal(14, index.TotalMarkCount);
                Assert.Equal(14, vm.Marks.Count);
                Assert.False(vm.Marks.IsScanning);
                Assert.True(probe.MaxInFlight <= 1, $"{probe.MaxInFlight} loads in flight");
                Assert.Equal(12, probe.NonNullCount); // only the pages with our annotations got their strokes parsed

                // The scan kept marks, not strokes: only pages the viewer touched are in the renderer's stroke cache,
                // and a scanned page opened for editing afterwards shows all its strokes.
                Assert.InRange(Wait(vm.Renderer!.CachedStrokePageCountAsync()), 0, view.ActiveSurfaces.Count);
                Assert.False(vm.Session!.IsPageLoaded(150));
                Assert.Equal(
                    strokes[150].Select(s => s.Id),
                    Wait(vm.EnsureEditorAsync(150)).Page.Strokes.Select(s => s.Id));

                // The ticks are the marks, laid out with the knob's own mapping.
                var rail = view.Rail;
                var ticks = ExpectedTicks(vm, rail);
                Assert.Equal(ticks.Count, rail.Ticks.Centres.Count);
                for (var i = 0; i < ticks.Count; i++)
                {
                    Assert.Equal(ticks[i], rail.Ticks.Centres[i], 6);
                }

                Assert.InRange(ticks.Count, 12, 14);

                // Next visits every passage in document order; at the end it stays put and says so.
                var visited = new List<Mark>();
                var before = new List<ViewPosition>();
                for (var step = 0; step < 20; step++)
                {
                    var position = vm.Viewport.Position;
                    vm.Marks.NextCommand.Execute(null);
                    Settle(view);
                    if (vm.Viewport.Position == position)
                    {
                        break;
                    }

                    before.Add(position);
                    visited.Add(MarkShown(vm));
                }

                Assert.Equal(Strings.MarksNoOther, vm.Marks.StatusText);
                Assert.Equal(expectedPages, visited.Select(m => m.PageIndex).Distinct().ToArray());
                Assert.Equal(13, visited.Count); // 14 marks, the left/right pair of page 89 is one stop
                Assert.True(IsOrdered(visited), "Next went backwards");

                // Back returns to where each jump started.
                var last = vm.Viewport.Position;
                vm.Viewport.BackCommand.Execute(null);
                Settle(view);
                Assert.Equal(before[^1], vm.Viewport.Position);
                vm.Viewport.ScrollToMark(visited[^1], recordHistory: false);
                Settle(view);
                Assert.Equal(last, vm.Viewport.Position);

                // Previous walks back through the same stops.
                var back = new List<Mark>();
                for (var step = 0; step < 20; step++)
                {
                    var position = vm.Viewport.Position;
                    vm.Marks.PreviousCommand.Execute(null);
                    Settle(view);
                    if (vm.Viewport.Position == position)
                    {
                        break;
                    }

                    back.Add(MarkShown(vm));
                }

                Assert.Equal(visited.Take(visited.Count - 1).Reverse().Select(m => (m.PageIndex, m.TopPt)), back.Select(m => (m.PageIndex, m.TopPt)));

                // The scan read the document only: not dirty, nothing to undo.
                Assert.False(vm.IsDirty);
                Assert.False(vm.Session!.Undo.CanUndo);
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    // ---- Live tracking -----------------------------------------------------------------------------------------

    [Fact]
    public void A_margin_line_drawn_live_shows_its_tick_without_a_scan_and_follows_erase_undo_and_redo()
    {
        var path = WritePdf("live.pdf", 10);
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler, probe: null);
            vm.MarkSourceFactory = static _ => null; // no scan at all: only the live tracker
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                vm.ZoomVm.Zoom = 1;
                Settle(view);
                ScrollTo(view, view.Pages.PageRect(2).Top);
                LoadEditors(view);
                Assert.Null(vm.MarkScannerForTest);
                var rail = view.Rail;
                Refresh(scheduler, view);
                Assert.Empty(rail.Ticks.Centres);

                var surface = view.SurfaceFor(2)!;
                var id = PenMarginLine(surface, x: 40, top: 150, bottom: 350);
                Refresh(scheduler, view);
                var mark = Assert.Single(vm.MarkIndexForTest!.AllMarks());
                Assert.Equal(2, mark.PageIndex);
                Assert.Equal([id], mark.StrokeIds);
                Assert.Single(rail.Ticks.Centres);
                Assert.Single(vm.Marks.Items);

                // Erased with the pen's back end: the tick goes.
                Assert.True(surface.BeginEraseGesture(inverted: true, stylusId: 5));
                for (var y = 140.0; y <= 360; y += 4)
                {
                    surface.EraseAtView(new Point(40 * surface.Scale, y * surface.Scale));
                }

                surface.EndEraseGesture();
                Assert.Empty(surface.Editor!.Page.Strokes);
                Refresh(scheduler, view);
                Assert.Empty(vm.MarkIndexForTest!.AllMarks());
                Assert.Empty(rail.Ticks.Centres);

                // Undo brings it back, redo erases it again, undo of the stroke itself removes it.
                Wait(vm.UndoCommand.ExecuteAsync(null));
                Refresh(scheduler, view);
                Assert.Single(rail.Ticks.Centres);
                Wait(vm.RedoCommand.ExecuteAsync(null));
                Refresh(scheduler, view);
                Assert.Empty(rail.Ticks.Centres);
                Wait(vm.UndoCommand.ExecuteAsync(null));
                Wait(vm.UndoCommand.ExecuteAsync(null));
                Refresh(scheduler, view);
                Assert.Empty(rail.Ticks.Centres);
                Assert.Empty(vm.Marks.Items);
                Wait(vm.RedoCommand.ExecuteAsync(null));
                Refresh(scheduler, view);
                Assert.Single(rail.Ticks.Centres);
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    // ---- The scan does not hurt --------------------------------------------------------------------------------

    [Fact]
    public void During_a_long_scan_ink_save_and_scrolling_go_on_and_the_scan_pauses_for_strokes_and_saves()
    {
        var marked = new[] { 10, 60, 150, 220, 290 };
        var path = Author("busy.pdf", 300, marked.ToDictionary(p => p, _ => new[] { MarginLine(40, 200, 360) }));
        var probe = new ScanProbe { DelayPerPage = TimeSpan.FromMilliseconds(4) }; // a scan of a few seconds
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler, probe);
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            var writer = new GatedWriter(new PdfDocumentSaver());
            vm.SaveService = new DocumentSaveService(writer, _dialogs);
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                vm.ZoomVm.Zoom = 1;
                Settle(view);
                LoadEditors(view);
                var first = vm.MarkScannerForTest!;
                WpfTestThread.PumpUntil(() => first.ProcessedCount > 5, _timeoutMs);
                Assert.False(first.IsCompleted);

                // 1. A stroke: the scan parks while it is live; the commit is quick.
                var surface = view.SurfaceFor(0)!;
                var scale = surface.Scale;
                surface.BeginPenStroke(stylusId: 1, hasPressure: true);
                surface.AppendPenPoints(VerticalPoints(40, 200, 360, scale), timestamp: 10);
                Assert.True(vm.ZoomVm.IsLocked);
                Wait(first.WhenIdleAsync());
                var parked = first.ProcessedCount;
                Thread.Sleep(60);
                Assert.Equal(parked, first.ProcessedCount);
                var commit = Stopwatch.StartNew();
                surface.FinishPenStroke();
                commit.Stop();
                var drawn = surface.Editor!.Page.Strokes[^1].Id;
                Assert.True(commit.ElapsedMilliseconds < 250, $"the commit took {commit.ElapsedMilliseconds} ms");
                Assert.Equal([drawn], Assert.Single(vm.MarkIndexForTest!.MarksOnPage(0)).StrokeIds); // live
                WpfTestThread.PumpUntil(() => first.ProcessedCount > parked, _timeoutMs); // resumed

                // 2. A save: the scan parks while it runs; scrolling and navigation go on.
                var saveWatch = Stopwatch.StartNew();
                var saving = vm.SaveCommand.ExecuteAsync(null);
                WpfTestThread.PumpUntil(() => writer.Started.Task.IsCompleted, _timeoutMs);
                Wait(first.WhenIdleAsync());
                var duringSave = first.ProcessedCount;
                Thread.Sleep(60);
                Assert.Equal(duringSave, first.ProcessedCount);
                Assert.False(first.IsCompleted);
                ScrollTo(view, view.Pages.PageRect(5).Top + 30);
                Assert.Equal(5, vm.Viewport.VisiblePages[0]);
                Wait(vm.GoToPageCommand.ExecuteAsync(40));
                Assert.Equal(39, vm.Viewport.CurrentPage);

                writer.Gate.SetResult();
                Wait(saving);
                saveWatch.Stop();
                Assert.Null(vm.ErrorMessage);
                Assert.False(vm.IsDirty);

                // 3. The plain Save swapped the renderer: a new scan over the same index goes on to the end.
                var second = vm.MarkScannerForTest!;
                Assert.NotSame(first, second);
                WaitScanCompleted(second);
                Refresh(scheduler, view);
                var index = vm.MarkIndexForTest!;
                Assert.Equal(300, index.ScannedCount);
                Assert.Equal(marked.Prepend(0).ToArray(), index.AllMarks().Select(m => m.PageIndex).ToArray());
                Assert.Equal(6, vm.Marks.Count);
                Assert.Equal(6, view.Rail.Ticks.Centres.Count);
                Assert.True(probe.MaxInFlight <= 1, $"{probe.MaxInFlight} loads in flight");

                // No page published twice: every load is at most once, except the one cut short by the swap.
                var twice = probe.Loads.Where(pair => pair.Value > 1).ToArray();
                Assert.True(twice.Length <= 1, $"{twice.Length} pages loaded twice");
                Assert.All(twice, pair => Assert.Equal(2, pair.Value));
                _output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"commit during scan {commit.Elapsed.TotalMilliseconds:0.0} ms, save during scan {saveWatch.ElapsedMilliseconds} ms, "
                    + $"loads {probe.Loads.Values.Sum()} for {probe.Loads.Count} pages"));

                // What the file now holds: the stroke drawn during the scan and the five stored lines.
                var fresh = Wait(PdfRenderer.OpenAsync(path));
                try
                {
                    Assert.Equal(drawn, Assert.Single(Wait(fresh.LoadStrokesAsync(0)).Strokes).Id);
                    foreach (var page in marked)
                    {
                        Assert.Single(Wait(fresh.LoadStrokesAsync(page)).Strokes);
                    }
                }
                finally
                {
                    Wait(fresh.DisposeAsync().AsTask());
                }
            }
            finally
            {
                writer.Gate.TrySetResult();
                Close(vm, view);
            }
        });
    }

    [Fact]
    public void Closing_the_document_mid_scan_stops_it_unsubscribes_everything_and_leaves_no_unobserved_exception()
    {
        var path = Author("close.pdf", 200, new Dictionary<int, InkStroke[]> { [100] = [MarginLine(40, 200, 360)] });
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
            var probe = new ScanProbe { DelayPerPage = TimeSpan.FromMilliseconds(4) };
            WpfTestThread.Run(() =>
            {
                var scheduler = new ManualScheduler();
                var vm = NewVm(path, scheduler, probe);
                var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
                try
                {
                    Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                    LayOut(view, 1000, 800);
                    var scanner = vm.MarkScannerForTest!;
                    var index = vm.MarkIndexForTest!;
                    var session = vm.Session!;
                    WpfTestThread.PumpUntil(() => scanner.ProcessedCount > 3, _timeoutMs);
                    Assert.NotNull(EventHandlers(index, nameof(MarkIndex.Changed)));
                    Assert.NotNull(EventHandlers(scanner, nameof(MarkScanner.Progress)));
                    Assert.NotNull(EventHandlers(session, nameof(DocumentSession.PageStrokesChanged)));

                    Assert.True(Wait(vm.CanCloseAsync()));

                    Assert.Null(vm.MarkScannerForTest);
                    Assert.Null(vm.MarkIndexForTest);
                    Wait(scanner.WhenIdleAsync());
                    Assert.False(scanner.IsCompleted);
                    var stopped = scanner.ProcessedCount;
                    Thread.Sleep(80);
                    Assert.InRange(scanner.ProcessedCount, stopped, stopped + 1); // at most the load in flight
                    Assert.Null(EventHandlers(index, nameof(MarkIndex.Changed)));
                    Assert.Null(EventHandlers(scanner, nameof(MarkScanner.Progress)));
                    Assert.Null(EventHandlers(session, nameof(DocumentSession.PageStrokesChanged)));
                    Assert.Empty(vm.Marks.Items);
                    Assert.False(vm.Marks.IsScanning);
                    Assert.True(probe.MaxInFlight <= 1);
                }
                finally
                {
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

    [Fact]
    public void A_damaged_page_in_the_middle_does_not_stop_the_scan_and_the_other_pages_are_still_marked()
    {
        var strokes = new Dictionary<int, InkStroke[]>
        {
            [5] = [MarginLine(40, 200, 360)],
            [29] = [MarginLine(40, 200, 360)], // its load fails (as if PDFium could not open it)
            [30] = [MarginLine(40, 200, 360), MarginLine(580, 200, 360)], // both payloads damaged
            [31] = [MarginLine(40, 200, 360), MarginLine(580, 200, 360)], // the second payload damaged
            [55] = [MarginLine(580, 200, 360)],
        };
        var path = Author("damaged.pdf", 60, strokes, damage: new Dictionary<int, bool> { [30] = true, [31] = false });
        var probe = new ScanProbe { ThrowOn = [29] };
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler, probe);
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                var scanner = vm.MarkScannerForTest!;
                WaitScanCompleted(scanner);
                Refresh(scheduler, view);

                var index = vm.MarkIndexForTest!;
                Assert.Equal(1, scanner.FailedCount);
                Assert.False(index.IsScanned(29));
                Assert.Equal(59, index.ScannedCount);
                Assert.Equal([5, 31, 55], index.AllMarks().Select(m => m.PageIndex).ToArray());
                Assert.Equal(MarkSide.Left, Assert.Single(index.MarksOnPage(31)).Side);
                Assert.Empty(index.MarksOnPage(30));
                Assert.Equal(3, vm.Marks.Count);
                Assert.Null(vm.ErrorMessage); // the scan never reports anything to the user
                Assert.False(vm.IsDirty);
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    // ---- The scan never changes what is saved, the dirty state or the journal ------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_scanned_document_saves_the_same_strokes_foreign_annotations_and_page_content(bool saveAs, bool withEdit)
    {
        var original = AuthorMixed("mixed.pdf");
        var probe = new ScanProbe();
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(original, scheduler, probe, withRecovery: true);
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            var target = original;
            InkStroke? added = null;
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                WaitScanCompleted(vm.MarkScannerForTest!);
                Refresh(scheduler, view);
                scheduler.Advance(MainViewModel.ViewSettleDelay);
                Wait(vm.PositionWritesForTest);
                Assert.Equal([2, 5, 9], vm.MarkIndexForTest!.AllMarks().Select(m => m.PageIndex).Distinct().ToArray());

                // Opened and scanned, never edited: not dirty, nothing to undo, no change in the journal.
                Assert.False(vm.IsDirty);
                Assert.False(vm.Session!.Undo.CanUndo);
                Assert.Empty(vm.Session.GetChanges());
                AssertNoStrokeEntriesInTheJournal(original);

                if (withEdit)
                {
                    InkPoint[] points = [new(300, 300, 0.5f, null, null, null, 0), new(400, 330, 0.7f, null, null, null, 20)];
                    Assert.True(vm.Editor!.CommitStroke(points, InkTool.Pen, Palette.Colors[0], 1.5));
                    added = vm.Editor.Page.Strokes[^1];
                    Assert.Equal(0, vm.Editor.Page.Index);
                    Assert.True(vm.IsDirty);
                }

                if (saveAs)
                {
                    target = Path.Combine(_dir, "saved-as.pdf");
                    _dialogs.NextSavePath = target;
                    Wait(vm.SaveAsCommand.ExecuteAsync(null));
                }
                else
                {
                    var copy = Path.Combine(_dir, "original-copy.pdf");
                    File.Copy(original, copy);
                    Wait(vm.SaveCommand.ExecuteAsync(null));
                    original = copy; // what the file held before the save
                }

                Assert.Null(vm.ErrorMessage);
                Assert.False(vm.IsDirty);

                // The scan follows the new renderer and keeps its marks (and the live one, when drawn).
                WaitScanCompleted(vm.MarkScannerForTest!);
                Assert.Equal(12, vm.MarkIndexForTest!.ScannedCount);
                Assert.Equal([2, 5, 9], vm.MarkIndexForTest.AllMarks().Select(m => m.PageIndex).Distinct().ToArray());
            }
            finally
            {
                _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
                Assert.True(Wait(vm.CanCloseAsync()));
                Close(vm, view);
            }

            // Reopened: the same strokes on every page (plus the edit), the same foreign annotations, text and look.
            var expected = Wait(ReadStrokesAsync(original));
            if (added is not null)
            {
                expected[0] = [.. expected[0], StrokeJson.Serialize(added)];
            }

            Assert.Equal(expected, Wait(ReadStrokesAsync(target)));
            Assert.Equal(Wait(ReadForeignAnnotationsAsync(original)), Wait(ReadForeignAnnotationsAsync(target)));
            var report = Wait(PdfiumThread.RunAsync(() => PdfFidelity.Compare(original, target), CancellationToken.None));
            var differences = report.Differences;
            if (withEdit)
            {
                // The one added stroke is the only difference: one more ink annotation on page 1.
                Assert.Equal(["page 1: annotations Ink 0 vs 1"], differences.Where(d => !d.Contains("pixels", StringComparison.Ordinal)));
            }
            else
            {
                Assert.Empty(differences);
            }
        });
    }

    // ---- The real book ---------------------------------------------------------------------------------------

    [Fact]
    public void Scanning_a_real_e_book_copy_is_quick_bounded_in_memory_and_never_runs_two_loads_at_once()
    {
        var copy = CopyLongestRealDocument();
        if (copy is null)
        {
            return; // skipped (reported in the output)
        }

        // The plain copy (no app annotations), then a copy with margin lines on thirty pages through the real saver.
        Measure(copy, "plain copy", expectedPagesWithMarks: 0);
        var pageCount = PageCountOf(copy);
        var marked = Enumerable.Range(0, 30).Select(i => (i * (pageCount / 30)) + 1).ToArray();
        var annotated = Path.Combine(_dir, "book-marked.pdf");
        var saveWatch = Stopwatch.StartNew();
        AuthorInto(copy, annotated, marked.ToDictionary(p => p, _ => new[] { MarginLine(30, 200, 360) }));
        _output.WriteLine($"authoring 30 margin lines through the saver: {saveWatch.ElapsedMilliseconds} ms");
        Measure(annotated, "copy with margin lines", expectedPagesWithMarks: marked.Length);
    }

    private void Measure(string path, string label, int expectedPagesWithMarks)
    {
        var probe = new ScanProbe { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler, probe, productionGap: true); // the production pause between pages
            var view = new DocumentView { DataContext = vm }; // real PDFium bitmaps
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                WpfTestThread.PumpUntil(() => view.SurfaceFor(vm.Viewport.VisiblePages[0])!.PageImage is not null, _timeoutMs);
                IdleBitmaps(view); // the window's renders are done: what grows from here is the scan's
                var scanner = vm.MarkScannerForTest!;
                var before = PrivateBytes();

                var watch = Stopwatch.StartNew();
                probe.Gate!.SetResult();
                WpfTestThread.PumpUntil(() => scanner.IsCompleted, 120_000);
                watch.Stop();
                Refresh(scheduler, view);
                IdleBitmaps(view);
                var after = PrivateBytes();
                var index = vm.MarkIndexForTest!;
                var pages = vm.Viewport.PageCount;
                var deltaMb = (after - before) / (1024.0 * 1024.0);
                _output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{label}: pages {pages}, scan {watch.ElapsedMilliseconds} ms ({watch.Elapsed.TotalMilliseconds / pages:0.00} ms/page), "
                    + $"pages with app annotations {probe.NonNullCount}, marks {index.TotalMarkCount}, failed {scanner.FailedCount}, "
                    + $"max loads in flight {probe.MaxInFlight}, private memory delta {deltaMb:0.0} MB, ticks {view.Rail.Ticks.Centres.Count}"));
                if (watch.Elapsed > TimeSpan.FromSeconds(5))
                {
                    _output.WriteLine($"WARNING: the scan took more than 5 s ({watch.ElapsedMilliseconds} ms).");
                }

                Assert.Equal(pages, index.ScannedCount);
                Assert.Equal(0, scanner.FailedCount);
                Assert.Equal(expectedPagesWithMarks, probe.NonNullCount);
                Assert.Equal(expectedPagesWithMarks, index.AllMarks().Select(m => m.PageIndex).Distinct().Count());
                Assert.True(probe.MaxInFlight <= 1, $"{probe.MaxInFlight} loads in flight");
                Assert.True(after - before < 100L * 1024 * 1024, $"memory grew by {deltaMb:0.0} MB");
                Assert.False(vm.IsDirty);
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private MainViewModel NewVm(
        string path, ManualScheduler scheduler, ScanProbe? probe, bool withRecovery = false, bool productionGap = false)
    {
        _dialogs.NextPath = path;
        var vm = new MainViewModel(
            _dialogs,
            withRecovery ? new RecoveryStore(RecoveryRoot) : null,
            new ViewPositionStore(PositionsRoot),
            scheduler)
        {
            MarkScanIdleGap = productionGap ? null : TimeSpan.Zero, // null: the scanner's own default pause
        };
        if (probe is not null)
        {
            vm.MarkSourceFactory = opened =>
                opened.Viewer?.Current is { } renderer ? new ProbeSource(new MarkPageSource(renderer), probe) : null;
        }

        return vm;
    }

    private void AssertNoStrokeEntriesInTheJournal(string path)
    {
        var journal = new RecoveryStore(RecoveryRoot).Find(path);
        if (journal is not null)
        {
            Assert.DoesNotContain(journal.Contents.Entries, e => e is not ViewEntry);
        }
    }

    /// <summary>A vertical pen line with a little waver: a margin line when it is in the outer zone and long enough.</summary>
    private static InkStroke MarginLine(double x, double top, double bottom) =>
        new(
            Guid.NewGuid(),
            InkTool.Pen,
            InkColor.FromRgb(0, 0, 0),
            1.5,
            Enumerable.Range(0, 24)
                .Select(i => new InkPoint(x + ((i % 2) * 0.8), top + ((bottom - top) * i / 23.0), 0.5f, null, null, null, i * 8L))
                .ToArray());

    /// <summary>A horizontal stroke in the text: never a mark.</summary>
    private static InkStroke Scribble() =>
        new(
            Guid.NewGuid(),
            InkTool.Pen,
            InkColor.FromRgb(0, 0, 0),
            1.5,
            Enumerable.Range(0, 10).Select(i => new InkPoint(200 + (i * 20), 300, 0.5f, null, null, null, i * 8L)).ToArray());

    private static StylusPoint[] VerticalPoints(double x, double top, double bottom, double scale) =>
        Enumerable.Range(0, 24).Select(i => new StylusPoint(x * scale, (top + ((bottom - top) * i / 23.0)) * scale, 0.5f)).ToArray();

    /// <summary>A margin line drawn with the pen on a surface (as the stylus handlers do); returns its id.</summary>
    private static Guid PenMarginLine(InkSurface surface, double x, double top, double bottom)
    {
        surface.BeginPenStroke(stylusId: 1, hasPressure: true);
        surface.AppendPenPoints(VerticalPoints(x, top, bottom, surface.Scale), timestamp: 10);
        surface.FinishPenStroke();
        return surface.Editor!.Page.Strokes[^1].Id;
    }

    private string WritePdf(string name, int pages)
    {
        var path = Path.Combine(_dir, name);
        TestPdfFactory.Write(path, Enumerable.Repeat((_widthPt, _heightPt), pages).ToArray());
        return path;
    }

    /// <summary>A generated PDF with the given strokes written through the real saver (optionally with damaged payloads).</summary>
    private string Author(string name, int pages, Dictionary<int, InkStroke[]> strokes, Dictionary<int, bool>? damage = null)
    {
        var source = WritePdf("source-" + name, pages);
        var path = Path.Combine(_dir, name);
        AuthorInto(source, path, strokes);
        if (damage is not null)
        {
            Damage(path, damage);
        }

        return path;
    }

    private void AuthorInto(string source, string path, Dictionary<int, InkStroke[]> strokes)
    {
        var changes = strokes.Select(pair => new PageChange(pair.Key, [], pair.Value)).ToArray();
        var temp = Task.Run(() => new PdfDocumentSaver().WriteVerifiedCopyAsync(source, path, changes)).GetAwaiter().GetResult();
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Replaces the payload of the second app annotation (or of every one, when true) on each page with junk.</summary>
    private void Damage(string path, Dictionary<int, bool> damage)
    {
        var damaged = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(path);
                foreach (var (pageIndex, all) in damage)
                {
                    var page = document.LoadPage(pageIndex);
                    try
                    {
                        var count = PdfiumNative.FPDFPage_GetAnnotCount(page);
                        for (var i = all ? 0 : 1; i < count; i++)
                        {
                            var annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
                            try
                            {
                                Assert.True(PdfiumAnnotations.SetString(annot, StrokeAnnotations.PayloadKey, "{not json"));
                            }
                            finally
                            {
                                PdfiumNative.FPDFPage_CloseAnnot(annot);
                            }

                            if (!all)
                            {
                                break;
                            }
                        }
                    }
                    finally
                    {
                        document.ClosePage(page);
                    }
                }

                document.SaveFullRewrite(damaged);
            },
            CancellationToken.None).GetAwaiter().GetResult();
        File.Move(damaged, path, overwrite: true);
    }

    /// <summary>
    /// Twelve pages with text: links and a square (foreign) on page 1, a foreign text note on page 7 (with one of our
    /// strokes too), our margin lines on pages 3, 6 and 10 and a plain stroke on page 6.
    /// </summary>
    private string AuthorMixed(string name)
    {
        var generated = Path.Combine(_dir, "generated-" + name);
        TestPdfFactory.WriteWithFeatures(
            generated,
            new TestPdfFactory.Features { Text = "Dream Books marks", LinkCount = 2, SquareAnnotationCount = 1 },
            Enumerable.Repeat((_widthPt, _heightPt), 12).ToArray());
        var withNote = Path.Combine(_dir, "note-" + name);
        PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(generated);
                var page = document.LoadPage(6);
                try
                {
                    var annot = PdfiumNative.FPDFPage_CreateAnnot(page, _textAnnotSubtype);
                    Assert.NotEqual(0, annot);
                    try
                    {
                        Assert.True(PdfiumAnnotations.SetString(annot, "T", "Someone else"));
                        Assert.True(PdfiumAnnotations.SetString(annot, "Contents", "A note"));
                    }
                    finally
                    {
                        PdfiumNative.FPDFPage_CloseAnnot(annot);
                    }
                }
                finally
                {
                    document.ClosePage(page);
                }

                document.SaveFullRewrite(withNote);
            },
            CancellationToken.None).GetAwaiter().GetResult();

        var path = Path.Combine(_dir, name);
        AuthorInto(withNote, path, new Dictionary<int, InkStroke[]>
        {
            [2] = [MarginLine(40, 200, 360)],
            [5] = [MarginLine(580, 100, 300), Scribble()],
            [6] = [Scribble()],
            [9] = [MarginLine(40, 400, 600)],
        });
        return path;
    }

    /// <summary>Our strokes of every page, serialised (the stroke model's own JSON), in file order.</summary>
    private static async Task<List<string>[]> ReadStrokesAsync(string path)
    {
        await using var renderer = await PdfRenderer.OpenAsync(path);
        var pages = new List<string>[renderer.PageCount];
        for (var page = 0; page < pages.Length; page++)
        {
            var strokes = await renderer.LoadStrokesAsync(page);
            Assert.Equal(0, strokes.UnreadableCount);
            pages[page] = strokes.Strokes.Select(StrokeJson.Serialize).ToList();
        }

        return pages;
    }

    /// <summary>Per page, the subtypes of the annotations that are not ours, in file order.</summary>
    private static Task<string> ReadForeignAnnotationsAsync(string path) =>
        PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(path);
                var lines = new List<string>();
                for (var index = 0; index < document.PageCount; index++)
                {
                    var page = document.LoadPage(index);
                    try
                    {
                        var subtypes = new List<int>();
                        var count = PdfiumNative.FPDFPage_GetAnnotCount(page);
                        for (var i = 0; i < count; i++)
                        {
                            var annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
                            try
                            {
                                if (PdfiumNative.FPDFAnnot_HasKey(annot, StrokeAnnotations.PayloadKey) == 0)
                                {
                                    subtypes.Add(PdfiumNative.FPDFAnnot_GetSubtype(annot));
                                }
                            }
                            finally
                            {
                                PdfiumNative.FPDFPage_CloseAnnot(annot);
                            }
                        }

                        lines.Add($"{index}: {string.Join(",", subtypes)}");
                    }
                    finally
                    {
                        document.ClosePage(page);
                    }
                }

                return string.Join("; ", lines);
            },
            CancellationToken.None);

    private static int PageCountOf(string path) =>
        Task.Run(async () =>
        {
            await using var renderer = await PdfRenderer.OpenAsync(path);
            return renderer.PageCount;
        }).GetAwaiter().GetResult();

    /// <summary>A copy of the real PDF with the most pages, or null (reported) when there is none.</summary>
    private string? CopyLongestRealDocument()
    {
        var folder = Environment.GetEnvironmentVariable(_fixturesVariable) is { Length: > 0 } custom
            ? custom
            : _realDocumentsFolder;
        var candidates = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.pdf") : [];
        string? longest = null;
        var most = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                var count = PageCountOf(candidate);
                if (count > most)
                {
                    (longest, most) = (candidate, count);
                }
            }
            catch (Exception ex) when (ex is PdfOpenException or IOException)
            {
            }
        }

        if (longest is null || most < 100)
        {
            // xUnit 2 has no runtime skip: report it and pass.
            _output.WriteLine($"SKIPPED: no long real PDF in the fixtures folder (set {_fixturesVariable}).");
            return null;
        }

        var path = Path.Combine(_dir, "book.pdf");
        File.Copy(longest, path);
        return path;
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    /// <summary>The mark the view now shows: the one whose jump target is the current position.</summary>
    private static Mark MarkShown(MainViewModel vm)
    {
        var top = vm.Viewport.AbsoluteTopPt(vm.Viewport.Position);
        return vm.MarkIndexForTest!.AllMarks()
            .First(m => vm.Viewport.TargetPositionFor(m) is { } target && Math.Abs(vm.Viewport.AbsoluteTopPt(target) - top) < 1);
    }

    private static bool IsOrdered(IReadOnlyList<Mark> marks)
    {
        for (var i = 1; i < marks.Count; i++)
        {
            if ((marks[i].PageIndex, marks[i].TopPt).CompareTo((marks[i - 1].PageIndex, marks[i - 1].TopPt)) <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<double> ExpectedTicks(MainViewModel vm, ScrollRail rail) =>
        RailTickLayout.Positions(vm.Marks.Marks, vm.Viewport.Layout!, vm.Viewport.WindowHeightPt, rail.TrackLength)
            .Select(p => p + (rail.KnobHeight / 2))
            .ToArray();

    /// <summary>The backing delegate of a field-like event (null when nobody listens).</summary>
    private static Delegate? EventHandlers(object target, string name) =>
        (Delegate?)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void WaitScanCompleted(MarkScanner scanner) =>
        WpfTestThread.PumpUntil(() => scanner.IsCompleted, _timeoutMs);

    /// <summary>The marks list's throttle runs, and the rail (shown) draws its ticks.</summary>
    private static void Refresh(ManualScheduler scheduler, DocumentView view)
    {
        scheduler.Advance(MarksViewModel.RefreshInterval);
        view.Rail.NotifyActivity();
        Settle(view);
    }

    private static void Close(MainViewModel vm, DocumentView view)
    {
        view.DataContext = null;
        if (vm.Renderer is { } renderer)
        {
            Wait(renderer.DisposeAsync().AsTask());
        }
    }

    private static void LayOut(DocumentView view, double width, double height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        Settle(view);
    }

    private static void Settle(DocumentView view)
    {
        for (var i = 0; i < 6; i++)
        {
            view.UpdateLayout();
            WpfTestThread.Drain();
        }
    }

    private static void ScrollTo(DocumentView view, double y)
    {
        view.Viewport.ScrollToVerticalOffset(y);
        Settle(view);
    }

    private static void IdleBitmaps(DocumentView view)
    {
        var scheduler = view.Bitmaps.Scheduler!;
        WpfTestThread.PumpUntil(() => scheduler.WhenIdleAsync().IsCompleted, _timeoutMs);
        WpfTestThread.Drain();
    }

    private static void LoadEditors(DocumentView view)
    {
        view.FlushEditorLoads();
        WpfTestThread.PumpUntil(() => view.ActiveSurfaces.Values.All(s => s.Editor is not null));
    }

    private static void Wait(Task task)
    {
        WpfTestThread.PumpUntil(() => task.IsCompleted, _timeoutMs);
        task.GetAwaiter().GetResult();
    }

    private static T Wait<T>(Task<T> task)
    {
        WpfTestThread.PumpUntil(() => task.IsCompleted, _timeoutMs);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>What the scan did, shared by every source a document gets (one per renderer).</summary>
    private sealed class ScanProbe
    {
        private int _inFlight;
        private int _maxInFlight;
        private int _nonNull;

        /// <summary>Holds every load until set (to measure from a known point).</summary>
        public TaskCompletionSource? Gate { get; init; }

        /// <summary>Extra time per page, to make a scan long enough to act during it.</summary>
        public TimeSpan DelayPerPage { get; init; }

        /// <summary>Pages whose load throws, as when PDFium cannot open them.</summary>
        public int[] ThrowOn { get; init; } = [];

        /// <summary>Completed loads per page.</summary>
        public ConcurrentDictionary<int, int> Loads { get; } = new();

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public int NonNullCount => Volatile.Read(ref _nonNull);

        public void Enter()
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }
        }

        public void Exit(int page, bool nonNull)
        {
            Interlocked.Decrement(ref _inFlight);
            Loads.AddOrUpdate(page, 1, (_, n) => n + 1);
            if (nonNull)
            {
                Interlocked.Increment(ref _nonNull);
            }
        }
    }

    /// <summary>The real page source, observed: loads in flight, loads per page, optional delay, gate and failures.</summary>
    private sealed class ProbeSource(IMarkPageSource inner, ScanProbe probe) : IMarkPageSource
    {
        public int PageCount => inner.PageCount;

        public PageDimensions GetPageDimensions(int pageIndex) => inner.GetPageDimensions(pageIndex);

        public async Task<PageStrokes?> TryLoadStrokesForScanAsync(int pageIndex, CancellationToken ct)
        {
            probe.Enter();
            PageStrokes? result = null;
            try
            {
                if (probe.Gate is { } gate)
                {
                    await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                }

                if (probe.DelayPerPage > TimeSpan.Zero)
                {
                    await Task.Delay(probe.DelayPerPage, ct).ConfigureAwait(false);
                }

                if (probe.ThrowOn.Contains(pageIndex))
                {
                    throw new InvalidOperationException("damaged page");
                }

                result = await inner.TryLoadStrokesForScanAsync(pageIndex, ct).ConfigureAwait(false);
                return result;
            }
            finally
            {
                probe.Exit(pageIndex, result is not null);
            }
        }
    }

    /// <summary>The real writer behind a gate, so a test can act while a save is between its steps.</summary>
    private sealed class GatedWriter(IDocumentWriter inner) : IDocumentWriter
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> WriteVerifiedCopyAsync(
            string sourcePath, string destinationPath, IReadOnlyList<PageChange> changes, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Gate.Task;
            return await inner.WriteVerifiedCopyAsync(sourcePath, destinationPath, changes, ct);
        }
    }
}
