using System.Windows;
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
            Assert.Equal(20, surface.StockStrokes[0].StylusPoints[0].X, 9);
            Assert.Equal(Visibility.Collapsed, surface.CustomLayer.Visibility);

            surface.Scale = 4; // zoom rescales mode A strokes from the page points (FR-26)
            Assert.Equal(40, surface.StockStrokes[0].StylusPoints[0].X, 9);

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
