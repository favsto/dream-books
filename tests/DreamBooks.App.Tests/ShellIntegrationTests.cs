using System.Windows;
using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;

namespace DreamBooks.App.Tests;

/// <summary>
/// The phase 4a shell with a document open, end to end on the STA test thread (never shown): theme switch, the toolbar
/// retracting under a real pen stroke, saving with a flyout open, documents of different lengths one after another,
/// window resize, and NFR-32 across every floating surface. In the theme collection: one test switches the theme.
/// </summary>
[Collection(nameof(ThemeCollection))]
public sealed class ShellIntegrationTests : IDisposable
{
    private const double _width = 1200;
    private const double _height = 800;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-shell-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly FakeFileDialogService _dialogs = new();
    private readonly FakeDocumentWriter _writer;
    private readonly ManualScheduler _scheduler = new();
    private int _pages = 12;

    public ShellIntegrationTests()
    {
        Directory.CreateDirectory(_dir);
        _writer = new FakeDocumentWriter(_log);
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
    public async Task A_theme_switch_with_a_document_open_keeps_the_pages_the_ink_and_the_toolbar()
    {
        var vm = await OpenAsync();
        WpfTestThread.Run(() =>
        {
            using var theme = new ThemeService(new FakeSystemThemeSource());
            var window = NewWindow(vm, theme);
            try
            {
                var view = window.DocumentViewer;
                var surface = LoadedSurface(view, 0);
                PenLine(surface, 100);
                var strokes = surface.Editor!.Page.Strokes.Count;
                var lightPill = ((SolidColorBrush)window.FloatingToolbar.Pill.DiffuseLayer.Background).Color;

                theme.SetMode(AppThemeMode.Dark);
                WpfTestThread.Drain();
                Lay(window);

                Assert.Same(surface, view.SurfaceFor(0)); // the pages are not rebuilt
                Assert.Equal(strokes, surface.Editor!.Page.Strokes.Count);
                Assert.True(vm.IsDirty);
                Assert.NotEqual(lightPill, ((SolidColorBrush)window.FloatingToolbar.Pill.DiffuseLayer.Background).Color);
                PenLine(surface, 200); // and it still inks
                Assert.Equal(strokes + 1, surface.Editor.Page.Strokes.Count);
            }
            finally
            {
                theme.SetMode(AppThemeMode.Light);
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public async Task A_pen_stroke_on_the_page_retracts_the_toolbar_and_it_returns_600_ms_after_pen_up()
    {
        var vm = await OpenAsync();
        WpfTestThread.Run(() =>
        {
            var window = NewWindow(vm);
            try
            {
                var toolbar = window.FloatingToolbar;
                var surface = LoadedSurface(window.DocumentViewer, 0);
                ToolbarViewTests.OpenFlyout(toolbar, OptionsPanel.Pen);
                Assert.Equal(OptionsPanel.Pen, vm.Toolbar.OpenPanel);

                // The stylus handlers' seams: pen down and points, as a real stroke (FR-20).
                var scale = surface.Scale;
                surface.BeginPenStroke(stylusId: 1, hasPressure: true);
                surface.AppendPenPoints([new(50 * scale, 100 * scale, 0.5f), new(60 * scale, 110 * scale, 0.6f)], timestamp: 10);
                _scheduler.Advance(ToolbarVisibilityState.WritingBeforeRetract - TimeSpan.FromMilliseconds(1));
                Assert.Equal(ToolbarPhase.Visible, vm.Toolbar.Phase);

                _scheduler.Advance(1);
                surface.AppendPenPoints([new(200 * scale, 120 * scale, 0.7f)], timestamp: 320);
                _scheduler.Advance(ToolbarVisibilityState.RetractDuration);
                Assert.Equal(ToolbarPhase.Hidden, vm.Toolbar.Phase);
                Assert.Equal(OptionsPanel.None, vm.Toolbar.OpenPanel); // the writing closed the panel
                Assert.False(toolbar.Pill.IsHitTestVisible); // the pen writes where the pill was
                Assert.Null(toolbar.ShownFlyout);

                surface.FinishPenStroke();
                _scheduler.Advance(ToolbarVisibilityState.PenUpBeforeShow - TimeSpan.FromMilliseconds(1));
                Assert.Equal(ToolbarPhase.Hidden, vm.Toolbar.Phase);
                _scheduler.Advance(1);
                Assert.Equal(ToolbarPhase.Visible, vm.Toolbar.Phase);
                Assert.True(toolbar.Pill.IsHitTestVisible);
                Assert.Equal(OptionsPanel.None, vm.Toolbar.OpenPanel); // never reopened by itself
                Assert.Single(surface.Editor!.Page.Strokes);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public async Task Saving_with_a_flyout_open_keeps_the_flyout_and_its_controls_working()
    {
        var vm = await OpenAsync();
        WpfTestThread.Run(() =>
        {
            var window = NewWindow(vm);
            try
            {
                var surface = LoadedSurface(window.DocumentViewer, 0);
                PenLine(surface, 100);
                ToolbarViewTests.OpenFlyout(window.FloatingToolbar, OptionsPanel.Pen);
                Assert.True(vm.IsDirty);

                _writer.Gate = new TaskCompletionSource();
                var saving = vm.SaveCommand.ExecuteAsync(null);
                WpfTestThread.PumpUntil(() => vm.IsSaving);
                Assert.Equal(OptionsPanel.Pen, vm.Toolbar.OpenPanel);
                var red = vm.Toolbar.Pen.Presets[3].Color;
                vm.Toolbar.Pen.SelectColorCommand.Execute(red); // the flyout's swatches still work
                Assert.Equal(red, vm.Tools.Color);

                _writer.Gate.SetResult();
                WpfTestThread.PumpUntil(() => saving.IsCompleted);
                Assert.True(saving.IsCompletedSuccessfully);
                Assert.False(vm.IsDirty);
                Assert.Equal(OptionsPanel.Pen, vm.Toolbar.OpenPanel);
                Assert.Equal(Visibility.Visible, window.FloatingToolbar.ShownFlyout!.Visibility);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public async Task Documents_of_different_lengths_open_one_after_another_in_the_shell()
    {
        var vm = await OpenAsync(pages: 30);
        WpfTestThread.Run(() =>
        {
            var window = NewWindow(vm);
            try
            {
                ScrollToEnd(window.DocumentViewer); // the end of the long one
                foreach (var pages in new[] { 2, 30, 1, 12 })
                {
                    _pages = pages;
                    _dialogs.NextPath = WriteFile($"doc{pages}.pdf");
                    var open = vm.OpenPdfCommand.ExecuteAsync(null); // on the window's thread, as a menu tap
                    WpfTestThread.PumpUntil(() => open.IsCompleted);
                    Assert.True(open.IsCompletedSuccessfully);
                    var view = window!.DocumentViewer;
                    Lay(window);
                    Assert.Null(vm.ErrorMessage);
                    Assert.Equal(pages, vm.Viewport.PageCount);
                    Assert.NotEmpty(view.ActiveSurfaces);
                    Assert.All(view.ActiveSurfaces.Keys, page => Assert.InRange(page, 0, pages - 1));
                    Assert.Equal(ToolbarPhase.Visible, vm.Toolbar.Phase);
                    ScrollToEnd(view);
                    Assert.Equal(pages - 1, vm.Viewport.VisiblePages[^1]);
                }
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public async Task Resizing_the_window_keeps_every_floating_element_inside_and_the_flyout_under_the_title_bar()
    {
        var vm = await OpenAsync();
        WpfTestThread.Run(() =>
        {
            var window = NewWindow(vm);
            try
            {
                var toolbar = window.FloatingToolbar;
                ToolbarViewTests.OpenFlyout(toolbar, OptionsPanel.Pen);
                var titleBar = ((Thickness)window.FindResource("TitleBarClearance")).Top;
                foreach (var (width, height) in new[] { (1200.0, 800.0), (520.0, 380.0), (1600.0, 1000.0), (800.0, 600.0) })
                {
                    Lay(window, width, height);
                    var host = window.PageHost;
                    var pill = ToolbarViewTests.Bounds(toolbar.Pill, host);
                    Assert.Equal(width / 2, pill.X + (pill.Width / 2), 1);
                    Assert.True(pill.Bottom <= height && pill.Top >= 0, $"pill at {pill} in {width} x {height}");
                    var flyout = ToolbarViewTests.Bounds(toolbar.ShownFlyout!, host);
                    Assert.True(flyout.Top >= titleBar - 0.5 && flyout.Bottom <= pill.Top + 0.5, $"flyout at {flyout} in {width} x {height}");
                    var menu = ToolbarViewTests.Bounds(window.MainMenu.MenuButton, host);
                    Assert.True(menu.Left >= 0 && menu.Top >= titleBar - 0.5, $"menu button at {menu}");
                    Assert.Equal(new Rect(0, 0, width, height), new Rect(host.RenderSize));
                    Assert.Equal(width, window.DocumentViewer.Viewport.ViewportWidth, 1);
                    Assert.True(vm.Viewport.IsFitWidth);
                }
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public async Task Transparency_off_makes_every_floating_surface_in_the_window_opaque()
    {
        var vm = await OpenAsync();
        WpfTestThread.Run(() =>
        {
            var window = NewWindow(vm);
            try
            {
                window.FloatingToolbar.TransparencyEnabled = () => false;
                window.FloatingToolbar.ApplyTransparency();
                window.MainMenu.TransparencyEnabled = () => false;
                window.MainMenu.ApplyTransparency();
                window.IndicatorChip.TransparencyEnabled = () => false;
                window.IndicatorChip.ApplyTransparency();
                window.NavButtons.TransparencyEnabled = () => false;
                window.NavButtons.ApplyTransparency();
                window.SearchBar.TransparencyEnabled = () => false;
                window.SearchBar.ApplyTransparency();
                window.Notices.TransparencyEnabled = () => false;
                window.Notices.ApplyTransparency();
                Lay(window);

                // Every floating surface in the window, in the visual and the logical tree (closed flyouts too): none
                // was forgotten (NFR-32).
                var root = (FrameworkElement)window.Content;
                var surfaces = MainMenuTests.LogicalDescendants(root).OfType<FloatingSurface>()
                    .Concat(ToolbarViewTests.Descendants(root).OfType<FloatingSurface>())
                    .Distinct()
                    .ToList();
                Assert.True(surfaces.Count >= 8, $"{surfaces.Count} floating surfaces");
                Assert.All(surfaces, s => Assert.True(s.IsOpaque, $"{s.Name} in {s.Parent} is translucent"));
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    private async Task<MainViewModel> OpenAsync(int pages = 12)
    {
        _pages = pages;
        _dialogs.NextPath = WriteFile("a.pdf");
        var vm = new MainViewModel(_dialogs, scheduler: _scheduler, clock: () => _scheduler.Now)
        {
            OpenDocument = (opened, _) =>
            {
                var source = new FakePageSource(Enumerable.Repeat((612.0, 792.0), _pages).ToArray());
                return Task.FromResult(new OpenedDocument(source, source, opened, new FakeViewerHandle(_log)));
            },
        };
        vm.SaveService = new DocumentSaveService(_writer, _dialogs);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Editor);
        return vm;
    }

    private string WriteFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1, 2, 3]); // fingerprinted and copied by the fake writer, never parsed
        return path;
    }

    private static MainWindow NewWindow(MainViewModel vm, ThemeService? theme = null)
    {
        var window = new MainWindow(theme) { DataContext = vm, TransparencyEnabled = () => true, MicaSupported = () => true };
        window.DocumentViewer.BitmapRenderer = new FakeBitmapRenderer { Auto = true };
        window.DocumentViewer.Rail.AnimationsOverride = false;
        window.MarksPanel.AnimationsEnabled = () => false;
        window.FloatingToolbar.AnimationsEnabled = () => false;
        window.FloatingToolbar.TransparencyEnabled = () => true;
        window.FloatingToolbar.ApplyMotionSetting();
        window.MainMenu.AnimationsEnabled = () => false;
        Lay(window);
        return window;
    }

    private static void Lay(MainWindow window, double width = _width, double height = _height)
    {
        ScrollRailTests.LayOut((FrameworkElement)window.Content, width, height);
        ScrollRailTests.Settle(window.DocumentViewer);
    }

    private static void ScrollToEnd(DocumentView view)
    {
        view.Viewport.ScrollToVerticalOffset(view.Viewport.ScrollableHeight);
        ScrollRailTests.Settle(view);
    }

    private static InkSurface LoadedSurface(DocumentView view, int page)
    {
        view.FlushEditorLoads();
        WpfTestThread.PumpUntil(() => view.SurfaceFor(page)?.Editor is not null);
        return view.SurfaceFor(page)!;
    }

    private static void PenLine(InkSurface surface, double yPt)
    {
        var scale = surface.Scale;
        surface.BeginPenStroke(stylusId: 1, hasPressure: true);
        surface.AppendPenPoints([new(50 * scale, yPt * scale, 0.5f), new(200 * scale, (yPt + 20) * scale, 0.7f)], timestamp: 10);
        surface.FinishPenStroke();
    }
}
