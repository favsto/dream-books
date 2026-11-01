using System.Globalization;
using System.Windows;
using System.Windows.Input;
using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;
using Xunit.Abstractions;

namespace DreamBooks.App.Tests;

/// <summary>
/// Margin lines drawn the way a hand draws them on the Surface (FR-100, FR-101, FR-103): hundreds of raw digitizer
/// samples with jitter, a slight bow, a dwell where the pen lands and lifts, varying pressure, through the real
/// <see cref="InkSurface"/> pen seams of the real <see cref="DocumentView"/> on the real <see cref="MainViewModel"/> with
/// the app's own dispatcher scheduler (no manual time), at the zooms the device uses.
/// </summary>
[Collection(nameof(MarkIntegrationCollection))]
public sealed class MarksPenInputTests : IDisposable
{
    private const double _widthPt = 612;
    private const double _heightPt = 792;
    private const int _timeoutMs = 30_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-markpen-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly FakeFileDialogService _dialogs = new();

    public MarksPenInputTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
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
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static TheoryData<double, double, int> HandDrawnLines => new()
    {
        // zoom (0 = fit width), height in points, samples
        { 0, 120, 300 },
        { 1.5, 120, 400 },
        { 4, 120, 400 },
        { 0, 80, 200 },
        { 1.5, 80, 150 },
        { 4, 75, 400 },
        { 0.75, 75, 300 },
    };

    [Theory]
    [MemberData(nameof(HandDrawnLines))]
    public void A_hand_drawn_margin_line_appears_in_the_index_the_list_and_the_rail(double zoom, double heightPt, int samples)
    {
        var path = WritePdf("pen.pdf", 6);
        WpfTestThread.Run(() =>
        {
            var vm = new MainViewModel(_dialogs, null, new ViewPositionStore(Path.Combine(_dir, "positions")));
            vm.MarkSourceFactory = static _ => null; // only the live tracker: the scan has its own tests
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1368, 912); // a Surface Pro screen at 200 %, in DIPs
                if (zoom == 0)
                {
                    if (!vm.ZoomVm.IsFitWidth)
                    {
                        vm.Viewport.ToggleFitWidth(null);
                    }
                }
                else
                {
                    vm.ZoomVm.Zoom = zoom;
                }

                Settle(view);
                LoadEditors(view);
                var page = vm.Viewport.VisiblePages[0];
                var surface = view.SurfaceFor(page)!;
                var top = view.Viewport.VerticalOffset - view.Pages.PageRect(page).Top; // DIPs into the page
                var topPt = Math.Max(60, (top / surface.Scale) + 20);

                DrawByHand(surface, xPt: 40, topPt, heightPt, samples, seed: 7);
                var stroke = surface.Editor!.Page.Strokes[^1];
                Describe(stroke, surface.Scale, vm.ZoomVm.Zoom);

                PumpAtMost(() => vm.Marks.Count > 0, 2000); // the list's throttle runs on the dispatcher
                var mark = Assert.Single(vm.MarkIndexForTest!.AllMarks());
                Assert.Equal(page, mark.PageIndex);
                Assert.Equal(MarkSide.Left, mark.Side);
                Assert.Single(vm.Marks.Items);
                view.Rail.NotifyActivity();
                Settle(view);
                Assert.Single(view.Rail.Ticks.Centres);
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    [Fact]
    public void Hand_drawn_margin_lines_saved_and_reopened_are_found_live_and_by_the_scan()
    {
        var path = WritePdf("reopen.pdf", 8);
        WpfTestThread.Run(() =>
        {
            // Drawn at fit width on page 1 and page 4, then saved.
            var vm = new MainViewModel(_dialogs);
            var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1368, 912);
                if (!vm.ZoomVm.IsFitWidth)
                {
                    vm.Viewport.ToggleFitWidth(null);
                }

                Settle(view);
                LoadEditors(view);
                DrawByHand(view.SurfaceFor(0)!, xPt: 40, topPt: 100, heightPt: 90, samples: 300, seed: 3);
                view.Viewport.ScrollToVerticalOffset(view.Pages.PageRect(3).Top);
                Settle(view);
                LoadEditors(view);
                DrawByHand(view.SurfaceFor(3)!, xPt: 575, topPt: 300, heightPt: 120, samples: 350, seed: 5);
                PumpAtMost(() => vm.Marks.Count == 2, 2000);
                Assert.Equal([0, 3], vm.MarkIndexForTest!.AllMarks().Select(m => m.PageIndex).ToArray());
                Wait(vm.SaveCommand.ExecuteAsync(null));
                Assert.Null(vm.ErrorMessage);
                Assert.False(vm.IsDirty);
            }
            finally
            {
                Close(vm, view);
            }

            // Reopened: page 1 is shown (tracked live), page 4 is not loaded and comes from the scan, which starts on
            // the app's own dispatcher timers once the first renders are done.
            _dialogs.NextPath = path;
            vm = new MainViewModel(_dialogs);
            view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer { Auto = true }, DataContext = vm };
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                LayOut(view, 1368, 912);
                LoadEditors(view);
                PumpAtMost(() => vm.MarkScannerForTest is { IsCompleted: true } && vm.Marks.Count == 2, 10_000);
                Assert.True(vm.MarkScannerForTest!.IsCompleted);
                Assert.False(vm.Session!.IsPageLoaded(3));
                var marks = vm.MarkIndexForTest!.AllMarks();
                Assert.Equal([(0, MarkSide.Left), (3, MarkSide.Right)], marks.Select(m => (m.PageIndex, m.Side)).ToArray());
                Assert.Equal(2, vm.Marks.Items.Count);
                Assert.False(vm.Marks.IsScanning);
                view.Rail.NotifyActivity();
                Settle(view);
                Assert.Equal(2, view.Rail.Ticks.Centres.Count);
            }
            finally
            {
                Close(vm, view);
            }
        });
    }

    private static void Close(MainViewModel vm, DocumentView view)
    {
        view.DataContext = null;
        if (vm.Renderer is { } renderer)
        {
            Wait(renderer.DisposeAsync().AsTask());
        }
    }

    private static void PumpAtMost(Func<bool> condition, int ms)
    {
        try
        {
            WpfTestThread.PumpUntil(condition, ms);
        }
        catch (TimeoutException)
        {
            // the assertions below say what is missing
        }
    }

    /// <summary>
    /// A margin line as a hand draws it: a dwell where the pen lands, uneven speed, a slight bow, ±1 DIP jitter on
    /// every raw sample, varying pressure, a small hook where it lifts. Samples arrive in batches, as the stylus events
    /// deliver them.
    /// </summary>
    internal static void DrawByHand(InkSurface surface, double xPt, double topPt, double heightPt, int samples, int seed)
    {
        var random = new Random(seed);
        var scale = surface.Scale;
        var points = new List<StylusPoint>();
        double Jitter() => (random.NextDouble() * 2) - 1; // ±1 DIP
        for (var i = 0; i < 8; i++)
        {
            points.Add(new StylusPoint((xPt * scale) + (Jitter() * 0.3), (topPt * scale) + (Jitter() * 0.3), 0.2f + (i * 0.05f)));
        }

        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / (samples - 1);
            var eased = t - (Math.Sin(2 * Math.PI * t) / (2 * Math.PI)); // slow at both ends
            var bowPt = 3 * Math.Sin(Math.PI * t);
            var x = ((xPt + bowPt) * scale) + Jitter();
            var y = ((topPt + (heightPt * eased)) * scale) + Jitter();
            var pressure = (float)(0.45 + (0.3 * Math.Sin(3 * Math.PI * t)) + (0.05 * Jitter()));
            points.Add(new StylusPoint(x, y, Math.Clamp(pressure, 0.05f, 1f)));
        }

        var endX = (xPt * scale) + Jitter();
        var endY = (topPt + heightPt) * scale;
        for (var i = 1; i <= 4; i++)
        {
            points.Add(new StylusPoint(endX + (i * 0.8 * scale), endY - (i * 0.3 * scale), 0.3f));
        }

        surface.BeginPenStroke(stylusId: 1, hasPressure: true);
        for (var i = 0; i < points.Count; i += 4)
        {
            surface.AppendPenPoints(points.Skip(i).Take(4).ToArray(), timestamp: 10 + i);
        }

        surface.FinishPenStroke();
    }

    private void Describe(InkStroke stroke, double scale, double zoom)
    {
        var pts = stroke.Points;
        double path = 0;
        for (var i = 1; i < pts.Count; i++)
        {
            path += Math.Sqrt(Math.Pow(pts[i].X - pts[i - 1].X, 2) + Math.Pow(pts[i].Y - pts[i - 1].Y, 2));
        }

        var dx = pts[^1].X - pts[0].X;
        var dy = pts[^1].Y - pts[0].Y;
        var dist = Math.Sqrt((dx * dx) + (dy * dy));
        var angle = Math.Atan2(Math.Abs(dx), Math.Abs(dy)) * 180 / Math.PI;
        var meanX = pts.Average(p => p.X);
        double resampled = 0;
        var kept = pts[0];
        for (var i = 1; i < pts.Count; i++)
        {
            var d = Math.Sqrt(Math.Pow(pts[i].X - kept.X, 2) + Math.Pow(pts[i].Y - kept.Y, 2));
            if (d >= MarkRules.PathSampleSpacingFraction * dist || i == pts.Count - 1)
            {
                resampled += d;
                kept = pts[i];
            }
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"zoom {zoom:0.###} scale {scale:0.###}: {pts.Count} points, x {pts.Min(p => p.X):0.0}..{pts.Max(p => p.X):0.0} pt, "
            + $"y {pts.Min(p => p.Y):0.0}..{pts.Max(p => p.Y):0.0} pt, angle {angle:0.0} deg, raw path/dist {path / dist:0.000}, "
            + $"resampled path/dist {resampled / dist:0.000}, "
            + $"mean x {meanX:0.0} (zone {MarkRules.MarginZoneFraction * _widthPt:0.0})"));
    }

    private string WritePdf(string name, int pages)
    {
        var path = Path.Combine(_dir, name);
        TestPdfFactory.Write(path, Enumerable.Repeat((_widthPt, _heightPt), pages).ToArray());
        _dialogs.NextPath = path;
        return path;
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
}
