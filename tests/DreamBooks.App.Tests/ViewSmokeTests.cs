using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// Builds the real views on an STA thread without showing them, so XAML, resource and wiring errors fail
/// `dotnet test` (agents never launch the app). Ink feel is checked on the device, not here.
/// </summary>
public sealed class ViewSmokeTests : IDisposable
{
    private static readonly InkPoint[] _line =
    [
        new(10, 10, 0.4f, null, null, null, 0),
        new(60, 40, 0.8f, null, null, null, 10),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-view-" + Guid.NewGuid().ToString("N"));

    public ViewSmokeTests() => Directory.CreateDirectory(_dir);

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
    public void Main_window_loads_its_xaml_and_hosts_the_page_view()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                Assert.IsType<MainViewModel>(window.DataContext);
                Assert.NotNull(window.PageViewer);
                LayOut((FrameworkElement)window.Content, 1000, 700);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Ink_surface_is_page_size_times_scale_and_switches_modes()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            var editor = vm.Editor!;
            surface.Scale = 2;
            LayOut(surface, 2000, 2000);
            Assert.Equal(new Size(400, 600), surface.DesiredSize);

            Assert.True(editor.CommitStroke(_line, InkTool.Pen, vm.Tools.Color, 1));

            // Mode B: outline layer shows the stroke, the stock presenter holds none.
            Assert.IsType<CustomDynamicRenderer>(surface.ActiveRenderer);
            Assert.Single(surface.CustomLayer.Strokes);
            Assert.Empty(surface.StockStrokes);
            Assert.Equal(Visibility.Visible, surface.CustomLayer.Visibility);

            surface.Mode = RendererMode.StockWpf;
            Assert.IsNotType<CustomDynamicRenderer>(surface.ActiveRenderer);
            Assert.Equal(1, surface.PlugInCount);
            Assert.Single(surface.StockStrokes);
            Assert.Equal(10 * InkSurface.StockReferenceScale, surface.StockStrokes[0].StylusPoints[0].X, 9);
            Assert.Equal(2 / InkSurface.StockReferenceScale, surface.StockStrokeScale, 9);
            Assert.Equal(Visibility.Collapsed, surface.CustomLayer.Visibility);

            surface.Scale = 4; // zoom rescales mode A strokes by transform only (FR-26)
            Assert.Equal(10 * InkSurface.StockReferenceScale, surface.StockStrokes[0].StylusPoints[0].X, 9);
            Assert.Equal(4 / InkSurface.StockReferenceScale, surface.StockStrokeScale, 9);

            surface.Mode = RendererMode.Custom;
            Assert.IsType<CustomDynamicRenderer>(surface.ActiveRenderer);
            Assert.Equal(1, surface.PlugInCount);
            Assert.Empty(surface.StockStrokes);
            Assert.Equal(4, surface.CustomLayer.Scale, 9);

            vm.Undo.Undo();
            Assert.Empty(surface.CustomLayer.Strokes);
            LayOut(surface, 2000, 2000);
        });
    }

    [Fact]
    public async Task Wet_renderer_is_disabled_while_the_eraser_tool_is_active()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            Assert.True(surface.ActiveRenderer.Enabled);

            vm.Tools.ActiveTool = AppTool.Eraser;
            Assert.False(surface.ActiveRenderer.Enabled);

            vm.Tools.ActiveTool = AppTool.Pen;
            vm.Tools.BeginInvertedEraser(); // pen back end (Review Focus 3)
            Assert.False(surface.ActiveRenderer.Enabled);

            vm.Tools.EndInvertedEraser();
            Assert.True(surface.ActiveRenderer.Enabled);
        });
    }

    private static readonly InkPoint[] _otherLine =
    [
        new(100, 100, 0.5f, null, null, null, 0),
        new(150, 200, 0.6f, null, null, null, 10),
    ];

    [Fact]
    public async Task Mode_a_commit_adds_one_stroke_to_the_existing_collection()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            surface.Mode = RendererMode.StockWpf;
            vm.Editor!.CommitStroke(_line, InkTool.Pen, vm.Tools.Color, 1);
            var collection = surface.StockStrokes;
            var first = collection[0];

            vm.Editor.CommitStroke(_otherLine, InkTool.Pen, vm.Tools.Color, 1);

            Assert.Same(collection, surface.StockStrokes);
            Assert.Equal(2, collection.Count);
            Assert.Same(first, collection[0]);
        });
    }

    [Fact]
    public async Task Mode_a_zoom_keeps_the_stroke_instances_and_changes_only_the_scale()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            surface.Mode = RendererMode.StockWpf;
            vm.Editor!.CommitStroke(_line, InkTool.Pen, vm.Tools.Color, 1);
            var collection = surface.StockStrokes;
            var stroke = collection[0];
            var x = stroke.StylusPoints[0].X;

            surface.Scale = 8 * Units.DipPerPt;

            Assert.Same(collection, surface.StockStrokes);
            Assert.Same(stroke, surface.StockStrokes[0]);
            Assert.Equal(x, stroke.StylusPoints[0].X, 9);

            // Screen position of the first point = page point × Scale (FR-26).
            Assert.Equal(10 * 8 * Units.DipPerPt, x * surface.StockStrokeScale, 9);
        });
    }

    [Fact]
    public async Task Mode_a_undo_removes_only_the_matching_stroke()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            surface.Mode = RendererMode.StockWpf;
            vm.Editor!.CommitStroke(_line, InkTool.Pen, vm.Tools.Color, 1);
            var collection = surface.StockStrokes;
            var first = collection[0];
            vm.Editor.CommitStroke(_otherLine, InkTool.Pen, vm.Tools.Color, 1);

            vm.Undo.Undo();

            Assert.Same(collection, surface.StockStrokes);
            Assert.Same(first, Assert.Single(collection));
        });
    }

    [Fact]
    public async Task Zoom_is_locked_while_a_mouse_stroke_is_live_and_the_stroke_commits_on_release()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            surface.Scale = 1;
            LayOut(surface, 1000, 1000);
            var zoom = vm.ZoomVm.Zoom;

            surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseDownEvent,
            });
            Assert.True(surface.IsInking);

            vm.ZoomVm.ZoomInCommand.Execute(null); // panel +
            vm.ZoomVm.ZoomAt(2); // Ctrl+wheel and pinch
            vm.ZoomVm.FitWidth(1000, 200);
            Assert.Equal(zoom, vm.ZoomVm.Zoom);

            surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseUpEvent,
            });
            Assert.False(surface.IsInking);
            Assert.Single(vm.Editor!.Page.Strokes);

            vm.ZoomVm.ZoomInCommand.Execute(null);
            Assert.NotEqual(zoom, vm.ZoomVm.Zoom);
        });
    }

    [Fact]
    public async Task Failed_page_render_yields_no_bitmap_instead_of_throwing()
    {
        Assert.Null(await PageBitmap.TryRenderAsync(_ => throw new ArgumentException("bad size"), CancellationToken.None));
        Assert.Null(await PageBitmap.TryRenderAsync(_ => Task.FromException<RenderedPage>(new IOException()), CancellationToken.None));
        Assert.Null(await PageBitmap.TryRenderAsync(ct => Task.FromCanceled<RenderedPage>(ct), new CancellationToken(canceled: true)));

        var ok = await PageBitmap.TryRenderAsync(
            _ => Task.FromResult(new RenderedPage(1, 1, 4, [1, 2, 3, 255])), CancellationToken.None);
        Assert.NotNull(ok);
        Assert.True(ok.IsFrozen);
    }

    [Fact]
    public void Custom_layer_caches_one_outline_per_stroke_and_resets_on_curve_change()
    {
        WpfTestThread.Run(() =>
        {
            var layer = new CustomInkLayer();
            var stroke = new InkStroke(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1, _line);
            layer.Strokes = [stroke];
            LayOut(layer, 100, 100);
            Render(layer);
            Assert.Equal(1, layer.CachedCount);

            layer.Scale = 3; // zoom changes only the transform
            Assert.Equal(1, layer.CachedCount);

            layer.Curve = PressureCurve.Firm;
            Assert.Equal(0, layer.CachedCount);

            Render(layer);
            layer.Strokes = [];
            Assert.Equal(0, layer.CachedCount);
        });
    }

    [Fact]
    public void Rendered_page_becomes_a_frozen_bgra_bitmap()
    {
        var stride = 3 * 4 + 4; // padded rows
        var pixels = new byte[stride * 2];
        pixels[0] = 0x10; // B of pixel (0,0)
        pixels[stride + 8] = 0x20; // B of pixel (2,1)

        var bitmap = PageBitmap.FromRendered(new RenderedPage(3, 2, stride, pixels));

        Assert.True(bitmap.IsFrozen);
        Assert.Equal(3, bitmap.PixelWidth);
        Assert.Equal(2, bitmap.PixelHeight);
        Assert.Equal(PixelFormats.Bgra32, bitmap.Format);
        var copy = new byte[3 * 4 * 2];
        bitmap.CopyPixels(copy, 3 * 4, 0);
        Assert.Equal(0x10, copy[0]);
        Assert.Equal(0x20, copy[12 + 8]);
    }

    /// <summary>Opens a one-page 200 × 300 pt PDF, then runs the test on the WPF thread.</summary>
    private async Task WithDocumentAsync(Action<MainViewModel> test)
    {
        var path = Path.Combine(_dir, "page.pdf");
        TestPdfFactory.Write(path, (200, 300));
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path });
        await vm.OpenPdfCommand.ExecuteAsync(null);
        try
        {
            Assert.NotNull(vm.Editor);
            WpfTestThread.Run(() => test(vm));
        }
        finally
        {
            await vm.Renderer!.DisposeAsync();
        }
    }

    private static InkSurface CreateSurface(MainViewModel vm) => new() { ViewModel = vm, Page = vm.Editor!.Page };

    private static void LayOut(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
    }

    private static void Render(Visual visual)
    {
        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
    }
}
