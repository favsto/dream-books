using System.Diagnostics;
using System.Globalization;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Model;
using DreamBooks.Core.Rendering;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>
/// A jump to a far mark while background work holds the PDFium thread (a plain Save verifying its copy, then the
/// renderer swap; the mark scan; a search scan) shows the target page quickly: its preview first, then its sharp bitmap,
/// and it never turns white again at the swap (NFR-03, NFR-04, FR-102). Real view model, view, PDFium renderer, saver
/// and scans; only timings are reported (NFR-12).
/// </summary>
public sealed partial class MarksIntegrationTests
{
    /// <summary>The target page shows its preview within this time after the jump.</summary>
    private static readonly TimeSpan _previewBudget = TimeSpan.FromMilliseconds(300);

    /// <summary>The target page is sharp within this time after the jump, and again after the save's renderer swap.</summary>
    private static readonly TimeSpan _sharpBudget = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void A_far_mark_jump_shows_its_page_quickly_while_background_work_runs(bool save, bool search)
    {
        var marked = new[] { 210, 395 };
        var path = Author("jump.pdf", 400, marked.ToDictionary(p => p, _ => new[] { MarginLine(40, 200, 360) }));
        AssertQuick(MeasureJump(path, save, search));
    }

    [Fact]
    public void A_far_mark_jump_while_a_plain_save_of_a_real_e_book_copy_verifies_shows_its_page_quickly()
    {
        var copy = CopyLongestRealDocument();
        if (copy is null)
        {
            return; // skipped (reported in the output)
        }

        var pageCount = PageCountOf(copy);
        var marked = new[] { pageCount / 2, pageCount - 3 };
        var annotated = Path.Combine(_dir, "book-jump.pdf");
        AuthorInto(copy, annotated, marked.ToDictionary(p => p, _ => new[] { MarginLine(30, 200, 360) }));
        AssertQuick(MeasureJump(annotated, save: true, search: false));
    }

    private static void AssertQuick(JumpTimes times)
    {
        Assert.True(times.Preview <= _previewBudget, $"preview after {times.Preview.TotalMilliseconds:0} ms");
        Assert.True(times.Sharp <= _sharpBudget, $"sharp after {times.Sharp.TotalMilliseconds:0} ms");
        Assert.True(times.SharpAfterSwap <= _sharpBudget, $"sharp {times.SharpAfterSwap.TotalMilliseconds:0} ms after the save");
    }

    /// <summary>
    /// Opens the document with real bitmaps and the production scan pacing, waits for the first bitmaps and the scan's
    /// first mark, makes it dirty (a stroke on page 1), optionally starts a search, and optionally a plain Save: the jump
    /// to the next (far) mark then happens while the save verifies its copy (the longest PDFium work of a save). Measures
    /// from the jump to the target's first bitmap and to its current sharp bitmap, and after the save (renderer swapped)
    /// to its sharp bitmap of the new generation; the target never shows the placeholder again meanwhile.
    /// </summary>
    private JumpTimes MeasureJump(string path, bool save, bool search)
    {
        var result = default(JumpTimes);
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler, probe: null, productionGap: true);
            var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.SaveService = new DocumentSaveService(
                new PdfDocumentSaver { AfterPackedWritten = _ => verifying.TrySetResult() }, _dialogs);
            var view = new DocumentView { DataContext = vm }; // real PDFium bitmaps
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1000, 800);
                IdleBitmaps(view);
                scheduler.Advance(MainViewModel.MarkScanStartDelay); // the scan starts
                LoadEditors(view);
                var stroke = new InkPoint[] { new(100, 100, 0.5f, null, null, null, 0), new(200, 120, 0.5f, null, null, null, 10) };
                Assert.True(view.SurfaceFor(0)!.Editor!.CommitStroke(stroke, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1));
                WpfTestThread.PumpUntil(() => vm.MarkIndexForTest!.TotalMarkCount > 0, _timeoutMs); // a mark is known

                if (search)
                {
                    vm.Search.QueryText = "zzz-not-there";
                    scheduler.Advance(TimeSpan.FromSeconds(1)); // the query is applied: the search scan runs
                }

                Task saving = Task.CompletedTask;
                if (save)
                {
                    saving = vm.SaveCommand.ExecuteAsync(null);
                    WpfTestThread.PumpUntil(() => verifying.Task.IsCompleted || saving.IsCompleted, _timeoutMs);
                    Assert.False(saving.IsCompleted);
                }

                var watch = Stopwatch.StartNew();
                Assert.True(vm.Marks.NextCommand.CanExecute(null));
                vm.Marks.NextCommand.Execute(null);
                Settle(view);
                var target = vm.Viewport.Position.PageIndex;
                Assert.True(target > 100, $"jumped to page {target}");
                WpfTestThread.PumpUntil(() => view.SurfaceFor(target)?.PageImage is not null, _timeoutMs);
                var preview = watch.Elapsed;
                WpfTestThread.PumpUntil(() => ShowsCurrentSharp(view, target), _timeoutMs);
                var sharp = watch.Elapsed;

                var savedAt = TimeSpan.Zero;
                var afterSwap = TimeSpan.Zero;
                if (save)
                {
                    var surface = view.SurfaceFor(target)!;
                    WpfTestThread.PumpUntil(
                        () =>
                        {
                            Assert.NotNull(surface.PageImage); // never white again
                            return saving.IsCompleted;
                        },
                        _timeoutMs);
                    Wait(saving);
                    savedAt = watch.Elapsed;
                    var swapWatch = Stopwatch.StartNew();
                    WpfTestThread.PumpUntil(
                        () =>
                        {
                            Assert.NotNull(surface.PageImage);
                            return ShowsCurrentSharp(view, target);
                        },
                        _timeoutMs);
                    afterSwap = swapWatch.Elapsed;
                    Assert.False(vm.IsDirty);
                }

                Assert.Null(vm.ErrorMessage);
                _output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"save {save}, search {search}, pages {vm.Viewport.PageCount}: preview {preview.TotalMilliseconds:0} ms, "
                    + $"sharp {sharp.TotalMilliseconds:0} ms, save done {savedAt.TotalMilliseconds:0} ms, "
                    + $"sharp again {afterSwap.TotalMilliseconds:0} ms after the save, scanned {vm.MarkIndexForTest!.ScannedCount}"));
                result = new JumpTimes(preview, sharp, afterSwap);
            }
            finally
            {
                Close(vm, view);
            }
        });

        return result;
    }

    private static bool ShowsCurrentSharp(DocumentView view, int page) =>
        view.Bitmaps.Scheduler is { } scheduler
        && scheduler.TryGet(page, out var cached)
        && cached!.Quality == RenderQuality.Sharp
        && !scheduler.IsStale(page, cached)
        && ReferenceEquals(view.SurfaceFor(page)?.PageImage, cached.Bitmap);

    private readonly record struct JumpTimes(TimeSpan Preview, TimeSpan Sharp, TimeSpan SharpAfterSwap);
}
