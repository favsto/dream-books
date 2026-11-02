using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Journal;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Interop;
using DreamBooks.Pdf.Tests;
using Xunit.Abstractions;

namespace DreamBooks.App.Tests;

/// <summary>
/// Highlighters end to end (FR-40..FR-46, FR-100): the real <see cref="MainViewModel"/> and <see cref="DocumentView"/>
/// (never shown), the pen seams of the page surfaces, PDFium and the real save pipeline on a generated text PDF, plus a
/// copy of a real e-book when one is available (only counts and times are reported, NFR-12). How a highlight looks is
/// checked on the device.
/// </summary>
[Collection(nameof(MarkIntegrationCollection))]
public sealed class HighlighterIntegrationTests : IDisposable
{
    private const string _realDocumentsFolder = @"C:\Users\Favsto\workspace\temp";
    private const string _fixturesVariable = "DREAMBOOKS_FIXTURES";
    private const double _widthPt = 612;
    private const double _heightPt = 792;
    private const double _lineTopPt = 300; // the baseline of the text line, in page points (y down)
    private const int _timeoutMs = 60_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-hl-int-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly FakeFileDialogService _dialogs = new();

    public HighlighterIntegrationTests(ITestOutputHelper output)
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Three_highlights_survive_save_and_reopen_as_centre_marks_and_leave_the_pen_ink_alone(bool saveAs) // FR-40, FR-100
    {
        var original = AuthorTextPdf("text.pdf");
        var pen = Assert.Single(Sync(ReadStrokesAsync(original))[0]);
        var before = Path.Combine(_dir, "before-save.pdf");
        File.Copy(original, before);
        var target = saveAs ? Path.Combine(_dir, "saved-as.pdf") : original;
        Dictionary<int, InkStroke[]> drawn = [];

        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(original, scheduler);
            vm.MarkSourceFactory = static _ => null; // only the live tracker before the save
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                vm.ZoomVm.Zoom = 1;
                Settle(view);
                vm.Tools.ActiveTool = AppTool.Highlighter;

                // Page 1: round tip (the default), drawn with a small waver, straight-line off.
                Highlight(view, 0, Wavy(196, 400, _lineTopPt - 4, 2));

                // Page 3: chisel at 45 degrees, green.
                vm.Tools.Highlighter.SelectColorCommand.Execute(HighlighterPresets.Green);
                vm.Tools.Highlighter.IsChisel = true;
                Assert.Equal(45, vm.Tools.Highlighter.ChiselAngleDeg);
                Highlight(view, 2, Wavy(196, 400, _lineTopPt - 4, 1.5));

                // Page 5: round tip, pink, straight-line mode: a nearly horizontal pass becomes its two ends.
                vm.Tools.Highlighter.SelectColorCommand.Execute(HighlighterPresets.Pink);
                vm.Tools.Highlighter.IsRound = true;
                vm.Tools.Highlighter.IsStraightLine = true;
                Highlight(view, 4, Wavy(196, 400, _lineTopPt - 4, 0.5));
                Refresh(scheduler, view);

                var round = Highlighters(vm, 0);
                var chisel = Highlighters(vm, 2);
                var straight = Highlighters(vm, 4);
                Assert.Equal(HighlighterPresets.Yellow, round.Color);
                Assert.Null(round.Chisel);
                Assert.Equal(16, round.BaseWidthPt);
                Assert.True(round.Points.Count > 2);
                Assert.Equal(HighlighterPresets.Green, chisel.Color);
                Assert.Equal(new ChiselTip(45, HighlighterSettings.ChiselRatio), chisel.Chisel);
                Assert.Equal(22, chisel.BaseWidthPt);
                Assert.Equal(HighlighterPresets.Pink, straight.Color);
                Assert.Equal(2, straight.Points.Count);
                Assert.Equal(straight.Points[0].Y, straight.Points[1].Y);
                AssertCentreMarks(vm, view);

                // Undo and redo of the straight one: straightening first, then the pass itself, and back.
                Wait(vm.UndoCommand.ExecuteAsync(null));
                Refresh(scheduler, view);
                Assert.True(Highlighters(vm, 4).Points.Count > 2);
                Wait(vm.UndoCommand.ExecuteAsync(null));
                Refresh(scheduler, view);
                Assert.DoesNotContain(StrokesOf(vm, 4), s => s.Tool == InkTool.Highlighter);
                Assert.Equal([0, 2], vm.MarkIndexForTest!.AllMarks().Select(m => m.PageIndex).ToArray());
                Assert.Equal(2, view.Rail.Ticks.Centres.Count);
                Assert.Equal(2, vm.Marks.Count);
                Wait(vm.RedoCommand.ExecuteAsync(null));
                Wait(vm.RedoCommand.ExecuteAsync(null));
                Refresh(scheduler, view);
                Assert.Equal(straight.Points, Highlighters(vm, 4).Points);
                AssertCentreMarks(vm, view);
                Assert.True(vm.IsDirty);

                foreach (var page in new[] { 0, 2, 4 })
                {
                    drawn[page] = [.. StrokesOf(vm, page)];
                }

                if (saveAs)
                {
                    _dialogs.NextSavePath = target;
                    Wait(vm.SaveAsCommand.ExecuteAsync(null));
                }
                else
                {
                    Wait(vm.SaveCommand.ExecuteAsync(null));
                }

                Assert.Null(vm.ErrorMessage);
                Assert.False(vm.IsDirty);
            }
            finally
            {
                Close(vm, view);
            }
        });

        // The file: our strokes exactly as drawn, the pen stroke byte for byte, nothing else changed but the ink.
        var saved = Sync(ReadStrokesAsync(target));
        Assert.Equal(StrokeJson.Serialize(pen), StrokeJson.Serialize(Assert.Single(saved[0], s => s.Tool == InkTool.Pen)));
        foreach (var (page, strokes) in drawn)
        {
            Assert.Equal(Sorted(strokes), Sorted(saved[page]));
        }

        Assert.All(new[] { 1, 3, 5 }, page => Assert.Empty(saved[page]));
        var differences = Sync(PdfiumThread.RunAsync(() => PdfFidelity.Compare(before, target), CancellationToken.None)).Differences;
        Assert.Equal(
            ["page 1: annotations Ink 1 vs 2", "page 3: annotations Ink 0 vs 1", "page 5: annotations Ink 0 vs 1"],
            differences.Where(d => !d.Contains("pixels", StringComparison.Ordinal)));
        if (saveAs)
        {
            Assert.Equal(File.ReadAllBytes(before), File.ReadAllBytes(original)); // Save as leaves the original alone
        }

        // Reopened through the real view model: highlighters, centre marks on the rail and in the list, clean.
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(target, scheduler);
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                Assert.Null(vm.ErrorMessage);
                Assert.NotNull(vm.Session);
                LayOut(view, 1000, 800);
                scheduler.Advance(MainViewModel.MarkScanStartDelay);
                WpfTestThread.PumpUntil(() => vm.MarkScannerForTest!.IsCompleted, _timeoutMs);
                Refresh(scheduler, view);
                scheduler.Advance(MainViewModel.ViewSettleDelay);
                Wait(vm.PositionWritesForTest);

                AssertCentreMarks(vm, view);
                foreach (var (page, strokes) in drawn)
                {
                    var reopened = Wait(vm.EnsureEditorAsync(page)).Page.Strokes;
                    Assert.Equal(Sorted(strokes), Sorted(reopened));
                    var highlighter = Assert.Single(reopened, s => s.Tool == InkTool.Highlighter);
                    Assert.Equal(HighlighterPresets.Alpha, highlighter.Color.A);
                }

                Assert.False(vm.IsDirty);
                Assert.False(vm.Session!.Undo.CanUndo);
                Assert.Empty(vm.Session.GetChanges());
                var journal = new RecoveryStore(RecoveryRoot).Find(target);
                if (journal is not null)
                {
                    Assert.DoesNotContain(journal.Contents.Entries, e => e is not ViewEntry);
                }
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    [Fact]
    public void A_highlight_on_a_real_e_book_copy_round_trips_through_save() // FR-40, NFR-10
    {
        var copy = CopyLongestRealDocument();
        if (copy is null)
        {
            return; // skipped (reported in the output)
        }

        int pages = 0;
        int page = 0;
        InkStroke? highlight = null;
        long saveMs = 0;
        WpfTestThread.Run(() =>
        {
            var vm = NewVm(copy, new ManualScheduler());
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                pages = vm.Viewport.PageCount;
                page = pages / 2;
                var editor = Wait(vm.EnsureEditorAsync(page));
                var size = (editor.Page.WidthPt, editor.Page.HeightPt);
                var existing = editor.Page.Strokes.Count;

                // A passage across the middle of the page, at generated coordinates (the copy's text is never read).
                vm.Tools.ActiveTool = AppTool.Highlighter;
                var points = Wavy(size.WidthPt * 0.3, size.WidthPt * 0.7, size.HeightPt * 0.4, 1.5);
                Assert.True(vm.CommitInk(editor, points, AppTool.Highlighter, vm.Tools.Color, vm.Tools.BaseWidthPt));
                Assert.Equal(existing + 1, editor.Page.Strokes.Count);
                highlight = editor.Page.Strokes[^1];
                Assert.Equal(InkTool.Highlighter, highlight.Tool);

                var watch = Stopwatch.StartNew();
                Wait(vm.SaveCommand.ExecuteAsync(null));
                saveMs = watch.ElapsedMilliseconds;
                Assert.Null(vm.ErrorMessage);
                Assert.False(vm.IsDirty);
            }
            finally
            {
                Close(vm, view: null);
            }
        });

        var back = Sync(ReadPageStrokesAsync(copy, page));
        var read = Assert.Single(back, s => s.Id == highlight!.Id);
        Assert.Equal(StrokeJson.Serialize(highlight!), StrokeJson.Serialize(read));
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"real copy: pages {pages}, highlight points {highlight!.Points.Count}, app strokes on the page {back.Count}, save {saveMs} ms"));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private MainViewModel NewVm(string path, ManualScheduler scheduler)
    {
        _dialogs.NextPath = path;
        return new MainViewModel(_dialogs, new RecoveryStore(RecoveryRoot), new ViewPositionStore(PositionsRoot), scheduler)
        {
            MarkScanIdleGap = TimeSpan.Zero,
        };
    }

    /// <summary>
    /// Six text pages, one line on each at <see cref="_lineTopPt"/> from x = 200, and one pen stroke below the text of
    /// the first page, written through the real saver (so the save under test has a stored pen stroke to keep).
    /// </summary>
    private string AuthorTextPdf(string name)
    {
        var source = Path.Combine(_dir, "source-" + name);
        var specs = Enumerable.Range(0, 6)
            .Select(_ => new TextPdfFactory.TextPageSpec(
                _widthPt, _heightPt, [new TextPdfFactory.TextLine(200, _heightPt - _lineTopPt, "lorem ipsum dolor sit amet")]))
            .ToArray();
        TextPdfFactory.Write(source, specs);
        var pen = new InkStroke(
            Guid.NewGuid(),
            InkTool.Pen,
            InkColor.FromRgb(20, 40, 160),
            1.5,
            Enumerable.Range(0, 12).Select(i => new InkPoint(200 + (i * 15), 500 + (i % 3), 0.3f + (i * 0.05f), null, null, null, i * 8L)).ToArray());
        var path = Path.Combine(_dir, name);
        var temp = Task.Run(() => new PdfDocumentSaver().WriteVerifiedCopyAsync(source, path, [new PageChange(0, [], [pen])]))
            .GetAwaiter().GetResult();
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>A left-to-right pass at <paramref name="y"/> with a waver of <paramref name="amplitude"/> points.</summary>
    private static InkPoint[] Wavy(double left, double right, double y, double amplitude) =>
        Enumerable.Range(0, 21)
            .Select(i => new InkPoint(
                left + ((right - left) * i / 20.0), y + (amplitude * Math.Sin(i * 0.9)), 0.3f + (i % 5 * 0.1f), null, null, null, i * 8L))
            .ToArray();

    /// <summary>Draws a highlight with the pen on the page's surface, as the stylus handlers do.</summary>
    private static void Highlight(DocumentView view, int page, InkPoint[] points)
    {
        ScrollTo(view, view.Pages.PageRect(page).Top);
        LoadEditors(view);
        var surface = view.SurfaceFor(page)!;
        surface.BeginPenStroke(stylusId: 1, hasPressure: true);
        surface.AppendPenPoints(
            points.Select(p => new StylusPoint(p.X * surface.Scale, p.Y * surface.Scale, p.Pressure ?? 0.5f)).ToArray(),
            timestamp: 10);
        surface.FinishPenStroke();
    }

    /// <summary>The page's strokes in the session (its surface may have been recycled since it was drawn on).</summary>
    private static IReadOnlyList<InkStroke> StrokesOf(MainViewModel vm, int page) => Wait(vm.EnsureEditorAsync(page)).Page.Strokes;

    private static InkStroke Highlighters(MainViewModel vm, int page) =>
        Assert.Single(StrokesOf(vm, page), s => s.Tool == InkTool.Highlighter);

    /// <summary>Three centre marks on pages 1, 3 and 5: in the index, in the marks list and as ticks on the rail.</summary>
    private static void AssertCentreMarks(MainViewModel vm, DocumentView view)
    {
        var marks = vm.MarkIndexForTest!.AllMarks();
        Assert.Equal([0, 2, 4], marks.Select(m => m.PageIndex).ToArray());
        Assert.All(marks, m => Assert.Equal(MarkSide.Centre, m.Side));
        Assert.All(marks, m => Assert.InRange(m.BottomPt - m.TopPt, MarkRules.HighlightMinHeightPt, 30));
        Assert.Equal(3, vm.Marks.Count);
        Assert.All(vm.Marks.Items, item => Assert.Contains(Strings.MarkSideCentre, item.AutomationName, StringComparison.Ordinal));
        var rail = view.Rail;
        var expected = RailTickLayout.Positions(vm.Marks.Marks, vm.Viewport.Layout!, vm.Viewport.WindowHeightPt, rail.TrackLength)
            .Select(p => p + (rail.KnobHeight / 2))
            .ToArray();
        Assert.Equal(3, expected.Length);
        Assert.Equal(expected.Length, rail.Ticks.Centres.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], rail.Ticks.Centres[i], 6);
        }
    }

    private static string[] Sorted(IEnumerable<InkStroke> strokes) => [.. strokes.Select(StrokeJson.Serialize).Order(StringComparer.Ordinal)];

    private static async Task<List<InkStroke>[]> ReadStrokesAsync(string path)
    {
        await using var renderer = await PdfRenderer.OpenAsync(path);
        var pages = new List<InkStroke>[renderer.PageCount];
        for (var page = 0; page < pages.Length; page++)
        {
            var strokes = await renderer.LoadStrokesAsync(page);
            Assert.Equal(0, strokes.UnreadableCount);
            pages[page] = [.. strokes.Strokes];
        }

        return pages;
    }

    private static async Task<List<InkStroke>> ReadPageStrokesAsync(string path, int page)
    {
        await using var renderer = await PdfRenderer.OpenAsync(path);
        var strokes = await renderer.LoadStrokesAsync(page);
        Assert.Equal(0, strokes.UnreadableCount);
        return [.. strokes.Strokes];
    }

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

    /// <summary>The marks list's throttle runs, and the rail (shown) draws its ticks.</summary>
    private static void Refresh(ManualScheduler scheduler, DocumentView view)
    {
        scheduler.Advance(MarksViewModel.RefreshInterval);
        view.Rail.NotifyActivity();
        Settle(view);
    }

    /// <summary>Closes the document as the window does (releasing its lock), then the renderer.</summary>
    private void Close(MainViewModel vm, DocumentView? view)
    {
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
        Assert.True(Wait(vm.CanCloseAsync()));
        if (view is not null)
        {
            view.DataContext = null;
        }

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

    /// <summary>Waits for <paramref name="task"/> off the WPF test thread.</summary>
    private static T Sync<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static T Wait<T>(Task<T> task)
    {
        WpfTestThread.PumpUntil(() => task.IsCompleted, _timeoutMs);
        return task.GetAwaiter().GetResult();
    }
}
