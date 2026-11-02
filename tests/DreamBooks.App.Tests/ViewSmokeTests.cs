using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;
using Ellipse = System.Windows.Shapes.Ellipse;

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
    public void Main_window_loads_its_xaml_and_hosts_the_document_view()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                Assert.IsType<MainViewModel>(window.DataContext);
                Assert.NotNull(window.DocumentViewer);
                LayOut((FrameworkElement)window.Content, 1000, 700);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Main_window_has_the_save_buttons_the_saving_indicator_and_the_title_binding()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                LayOut((FrameworkElement)window.Content, 1000, 700);
                var vm = (MainViewModel)window.DataContext;
                Assert.Same(vm.SaveCommand, window.SaveButton.Command);
                Assert.Same(vm.SaveAsCommand, window.SaveAsButton.Command);
                Assert.Equal(Strings.Save, AutomationProperties.GetName(window.SaveButton));
                Assert.Equal(Strings.SaveAs, AutomationProperties.GetName(window.SaveAsButton));
                Assert.True(window.SaveButton.MinHeight >= 40 && window.SaveAsButton.MinHeight >= 40);
                Assert.Equal(Visibility.Collapsed, window.SavingIndicator.Visibility); // not saving
                Assert.Equal(Strings.AppTitle, window.Title);
                Assert.Equal(Visibility.Collapsed, window.JournalWarningText.Visibility); // no journal fault
                Assert.Equal(Strings.JournalWarningLabel, AutomationProperties.GetName(window.JournalWarningText));
                Assert.EndsWith(Path.Combine("DreamBooks", "recovery"), MainWindow.RecoveryRoot(), StringComparison.OrdinalIgnoreCase);

                var gestures = window.InputBindings.OfType<KeyBinding>()
                    .Select(b => (b.Key, b.Modifiers, b.Command))
                    .ToList();
                Assert.Contains((Key.S, ModifierKeys.Control, (ICommand)vm.SaveCommand), gestures);
                Assert.Contains((Key.S, ModifierKeys.Control | ModifierKeys.Shift, (ICommand)vm.SaveAsCommand), gestures);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Close_decision_cancels_on_a_faulted_check_and_waits_on_a_running_one()
    {
        Assert.True(MainWindow.SynchronousCloseDecision(Task.FromResult(true)));
        Assert.False(MainWindow.SynchronousCloseDecision(Task.FromResult(false)));
        Assert.False(MainWindow.SynchronousCloseDecision(Task.FromException<bool>(new IOException("boom"))));
        Assert.False(MainWindow.SynchronousCloseDecision(Task.FromCanceled<bool>(new CancellationToken(canceled: true))));
        Assert.Null(MainWindow.SynchronousCloseDecision(new TaskCompletionSource<bool>().Task));
    }

    [Fact]
    public void Dialog_defaults_are_the_safe_choice_and_escape_always_cancels()
    {
        var signature = FileDialogService.SignatureQuestion;
        Assert.Equal(Strings.AnswerCancel, signature.Labels[signature.DefaultIndex]); // Enter never invalidates
        Assert.Equal(signature.DefaultIndex, signature.CancelIndex);

        var changed = FileDialogService.FileChangedQuestion;
        Assert.Equal(Strings.AnswerCancel, changed.Labels[changed.DefaultIndex]); // Enter never overwrites
        Assert.Equal(Strings.AnswerCancel, changed.Labels[changed.CancelIndex]);

        var unsaved = FileDialogService.SaveChangesQuestion;
        Assert.Equal(Strings.AnswerSave, unsaved.Labels[unsaved.DefaultIndex]);
        Assert.Equal(Strings.AnswerCancel, unsaved.Labels[unsaved.CancelIndex]);

        var mismatch = FileDialogService.JournalMismatchQuestion;
        Assert.Equal(Strings.AnswerCancel, mismatch.Labels[mismatch.DefaultIndex]); // Enter neither applies nor discards
        Assert.Equal(Strings.AnswerCancel, mismatch.Labels[mismatch.CancelIndex]);
        Assert.Equal([Strings.AnswerApplyAnyway, Strings.AnswerDiscard, Strings.AnswerCancel], mismatch.Labels);

        var orphan = FileDialogService.OrphanJournalQuestion(@"C:\Docs\a.pdf");
        Assert.Equal(Strings.AnswerKeep, orphan.Labels[orphan.DefaultIndex]); // Enter never discards
        Assert.Equal(Strings.AnswerKeep, orphan.Labels[orphan.CancelIndex]);
        Assert.Contains(@"C:\Docs\a.pdf", orphan.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Choice_dialog_has_one_named_touch_sized_button_per_answer()
    {
        WpfTestThread.Run(() =>
        {
            var dialog = new ChoiceDialog(FileDialogService.FileChangedQuestion);
            var buttons = dialog.Buttons;
            Assert.Equal(3, buttons.Count);
            Assert.Equal(Strings.AnswerOverwriteAnyway, buttons[1].Content);
            Assert.Equal(Strings.AnswerOverwriteAnyway, AutomationProperties.GetName(buttons[1]));
            Assert.False(buttons[0].IsDefault);
            Assert.True(buttons[2].IsDefault);
            Assert.True(buttons[2].IsCancel);
            Assert.All(buttons, b => Assert.True(b.MinHeight >= 40 && b.MinWidth >= 40));
            Assert.Equal(Strings.AppTitle, dialog.Title);
            Assert.Null(dialog.Choice);
            dialog.Close();
        });
    }

    [Fact]
    public async Task Ink_surface_is_page_size_times_scale_and_zoom_only_changes_the_layer_scale()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            var editor = vm.Editor!;
            surface.Scale = 2;
            LayOut(surface, 2000, 2000);
            Assert.Equal(new Size(400, 600), surface.DesiredSize);

            Assert.True(editor.CommitStroke(_line, InkTool.Pen, vm.Tools.Color, 1));
            Assert.Single(surface.CustomLayer.Strokes);
            Assert.Equal(Visibility.Visible, surface.CustomLayer.Visibility);

            surface.Scale = 4;
            Assert.Equal(4, surface.CustomLayer.Scale, 9);
            Assert.Single(surface.CustomLayer.Strokes);

            vm.Undo.Undo();
            Assert.Empty(surface.CustomLayer.Strokes);
            LayOut(surface, 2000, 2000);
        });
    }

    [Fact]
    public async Task One_wet_renderer_is_installed_and_refuses_touch_and_back_end_devices()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            Assert.Equal(1, surface.PlugInCount);
            var renderer = Assert.IsType<CustomDynamicRenderer>(surface.ActiveRenderer);

            Assert.False(renderer.IsIgnoredDevice(tabletId: 1, stylusId: 10));

            renderer.SetIgnoredDevices([1], [11]);
            Assert.True(renderer.IsIgnoredDevice(tabletId: 1, stylusId: 10)); // touch tablet
            Assert.True(renderer.IsIgnoredDevice(tabletId: 2, stylusId: 11)); // pen back end
            Assert.False(renderer.IsIgnoredDevice(tabletId: 2, stylusId: 10)); // pen tip

            Assert.Equal(1, surface.PlugInCount);
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
    public async Task Commit_adds_one_stroke_and_undo_removes_only_the_matching_one()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            vm.Editor!.CommitStroke(_line, InkTool.Pen, vm.Tools.Color, 1);
            var first = vm.Editor.Page.Strokes[0];
            vm.Editor.CommitStroke(_otherLine, InkTool.Pen, vm.Tools.Color, 1);
            Assert.Equal(2, surface.CustomLayer.Strokes.Count);

            vm.Undo.Undo();

            Assert.Same(first, Assert.Single(surface.CustomLayer.Strokes));
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
            vm.Viewport.ZoomAt(2, new Point(10, 10)); // Ctrl+wheel and pinch
            vm.Viewport.ToggleFitWidth(null); // double tap and the Fit width button
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
    public async Task Mouse_erase_with_the_eraser_tool_previews_then_returns_to_the_page_strokes()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            surface.Scale = 1;
            LayOut(surface, 1000, 1000);
            vm.Editor!.CommitStroke(_line, InkTool.Pen, vm.Tools.Color, 1);
            vm.Tools.ActiveTool = AppTool.Eraser;
            vm.Undo.Clear();

            surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseDownEvent,
            });
            Assert.True(surface.IsInking);
            Assert.NotNull(vm.ActiveErase);
            Assert.True(vm.ZoomVm.IsLocked);
            Assert.Same(vm.ActiveErase!.Strokes, surface.DisplayStrokes);

            surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseUpEvent,
            });
            Assert.False(surface.IsInking);
            Assert.Null(vm.ActiveErase);
            Assert.False(vm.ZoomVm.IsLocked);
            Assert.Same(vm.Editor.Page.Strokes, surface.DisplayStrokes);
            Assert.Single(vm.Editor.Page.Strokes);
        });
    }

    [Fact]
    public async Task Back_end_erase_is_one_undo_step_and_gives_the_previous_tool_back()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            surface.Scale = 2;
            vm.Editor!.CommitStroke(_line, InkTool.Pen, vm.Tools.Color, 1); // (10,10)-(60,40) pt
            vm.Editor.CommitStroke(_otherLine, InkTool.Pen, vm.Tools.Color, 1);
            var kept = vm.Editor.Page.Strokes[1];
            vm.Undo.Clear();

            Assert.True(surface.BeginEraseGesture(inverted: true, stylusId: 7));
            Assert.Equal(AppTool.Eraser, vm.Tools.EffectiveTool);
            Assert.False(surface.ActiveRenderer.Enabled);
            surface.EraseAtView(new Point(20, 20)); // page point (10,10): start of the first line

            // Live preview: the first line is gone from the view, the page is untouched.
            Assert.Equal([kept], surface.DisplayStrokes);
            Assert.Equal(2, vm.Editor.Page.Strokes.Count);
            Assert.Single(surface.CustomLayer.Strokes);
            Assert.Equal(Visibility.Visible, surface.EraserCursor.Visibility);

            surface.EndEraseGesture();
            surface.EndEraseGesture(); // a late lost-capture: no second finish

            Assert.Equal([kept], vm.Editor.Page.Strokes);
            Assert.Same(vm.Editor.Page.Strokes, surface.DisplayStrokes);
            Assert.False(vm.Tools.IsInverted);
            Assert.Equal(AppTool.Pen, vm.Tools.EffectiveTool);
            Assert.True(surface.ActiveRenderer.Enabled);
            Assert.False(vm.ZoomVm.IsLocked);
            Assert.True(vm.Undo.Undo());
            Assert.False(vm.Undo.CanUndo); // exactly one step

            Assert.Equal(2, surface.CustomLayer.Strokes.Count);
            Assert.Same(kept, surface.CustomLayer.Strokes[1]); // undo restores the same instances
        });
    }


    [Fact]
    public async Task Wet_renderer_gating_is_re_evaluated_after_pen_up()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            Assert.True(surface.ActiveRenderer.Enabled);

            surface.BeginPenStroke(stylusId: 3, hasPressure: true);
            vm.Tools.ActiveTool = AppTool.Eraser; // changed while the stroke is live: gating is deferred
            Assert.True(surface.ActiveRenderer.Enabled);

            surface.FinishPenStroke();
            Assert.False(surface.ActiveRenderer.Enabled); // corrected right at the lift

            vm.Tools.ActiveTool = AppTool.Pen;
            Assert.True(surface.ActiveRenderer.Enabled);
        });
    }
    [Fact]
    public async Task Pen_tool_does_not_start_an_erase_unless_the_back_end_touches()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            Assert.False(surface.BeginEraseGesture(inverted: false, stylusId: 1));
            Assert.Null(vm.ActiveErase);
            Assert.False(surface.IsInking);
        });
    }

    [Fact]
    public async Task Eraser_cursor_follows_tool_size_and_zoom()
    {
        await WithDocumentAsync(vm =>
        {
            var surface = CreateSurface(vm);
            surface.Scale = 2;
            var cursor = surface.EraserCursor;

            surface.UpdateEraserCursor(new Point(100, 100), inverted: false);
            Assert.Equal(Visibility.Collapsed, cursor.Visibility); // pen tool, tip side

            surface.UpdateEraserCursor(new Point(100, 100), inverted: true); // back end hovering
            Assert.Equal(Visibility.Visible, cursor.Visibility);
            var diameter = Units.MmToPt(vm.Tools.EraserSizeMm) * 2;
            Assert.Equal(diameter, cursor.Width, 9);
            Assert.Equal(100 - (diameter / 2), Canvas.GetLeft(cursor), 9);

            vm.Tools.EraserSizeMm = 10;
            Assert.Equal(Units.MmToPt(10) * 2, cursor.Width, 9);

            surface.Scale = 4; // same page point, twice as far in DIPs
            Assert.Equal(Units.MmToPt(10) * 4, cursor.Width, 9);
            Assert.Equal(200 - (cursor.Width / 2), Canvas.GetLeft(cursor), 9);

            var fill = Assert.IsAssignableFrom<SolidColorBrush>(((Ellipse)cursor).Fill);
            Assert.Equal(0.2, fill.Opacity, 9);
            Assert.True(fill.Color.A > 0); // the accent colour resolved (not a transparent cursor)
            Assert.Equal(1, ((Ellipse)cursor).StrokeThickness, 9);

            surface.HideEraserCursor(); // StylusOutOfRange
            Assert.Equal(Visibility.Collapsed, cursor.Visibility);

            surface.UpdateEraserCursor(new Point(100, 100), inverted: true);
            Assert.Equal(Visibility.Visible, cursor.Visibility);
            surface.OnPenLeft(); // StylusLeave while hovering, no erase gesture live

            Assert.Equal(Visibility.Collapsed, cursor.Visibility);

            vm.Tools.ActiveTool = AppTool.Eraser;
            surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            Assert.Equal(Visibility.Visible, cursor.Visibility); // mouse hover with the eraser tool

            vm.Tools.ActiveTool = AppTool.Pen;
            Assert.Equal(Visibility.Collapsed, cursor.Visibility);
        });
    }

    [Fact]
    public async Task Document_view_keeps_each_page_on_its_own_surface_across_go_to_page()
    {
        var path = Path.Combine(_dir, "fake.pdf");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path }, scheduler: new ManualScheduler());
        new FakePageSource((200, 300), (400, 500)).InstallOn(vm);
        await vm.OpenPdfCommand.ExecuteAsync(null);

        WpfTestThread.Run(() =>
        {
            var view = new DocumentView { DataContext = vm };
            LayOut(view, 1000, 1000);
            view.FlushEditorLoads(); // the fake source loads synchronously
            var first = view.SurfaceFor(0)!;
            Assert.True(first.Editor!.CommitStroke(_line, InkTool.Pen, vm.Tools.Color, 1));
            var page1 = first.Editor.Page;
            Assert.Single(first.CustomLayer.Strokes);

            Assert.True(vm.GoToPageCommand.ExecuteAsync(2).IsCompletedSuccessfully);
            LayOut(view, 1000, 1000);
            WpfTestThread.Drain();
            var second = view.SurfaceFor(1)!;
            Assert.Equal(400, second.Page!.WidthPt);
            Assert.Empty(second.DisplayStrokes);
            Assert.Empty(second.CustomLayer.Strokes);

            Assert.True(vm.GoToPageCommand.ExecuteAsync(1).IsCompletedSuccessfully);
            LayOut(view, 1000, 1000);
            WpfTestThread.Drain();
            first = view.SurfaceFor(0)!;
            Assert.Same(page1, first.Page);
            Assert.Same(page1.Strokes, first.DisplayStrokes);
            Assert.Single(first.CustomLayer.Strokes);
        });
    }

    [Fact]
    public async Task Document_view_scroll_is_journalled_by_the_view_model_once_settled()
    {
        var path = Path.Combine(_dir, "fake.pdf");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var store = new RecoveryStore(Path.Combine(_dir, "recovery"));
        var dialogs = new FakeFileDialogService { NextPath = path, SaveChangesAnswer = SaveChangesAnswer.DontSave };
        var scheduler = new ManualScheduler();
        var vm = new MainViewModel(dialogs, store, scheduler: scheduler);
        new FakePageSource((600, 800), (600, 800), (600, 800)).InstallOn(vm);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.True(vm.Editor!.CommitStroke(_line, InkTool.Pen, vm.Tools.Color, 1)); // dirty: views are journalled

        WpfTestThread.Run(() =>
        {
            var view = new DocumentView { DataContext = vm };
            LayOut(view, 400, 300);
            vm.ZoomVm.Zoom = 1;
            LayOut(view, 400, 300);
            view.Viewport.ScrollToVerticalOffset(((800 + 8) * Units.DipPerPt) + 150);
            LayOut(view, 400, 300);
            WpfTestThread.Drain();
            Assert.Equal(1, vm.Viewport.Position.PageIndex);
            scheduler.Advance(MainViewModel.ViewSettleDelay);
        });

        var last = store.Find(path)!.Contents.Entries.OfType<DreamBooks.Core.Journal.ViewEntry>().Last();
        Assert.Equal(1, last.PageIndex);
        Assert.Equal(1, last.Zoom, 9);
        Assert.Equal(150 / Units.DipPerPt, last.ScrollY, 6); // points inside the page, not DIPs
        Assert.False(last.FitWidth);
        Assert.True(await vm.CanCloseAsync());
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

    private static InkSurface CreateSurface(MainViewModel vm) => new() { ViewModel = vm, Editor = vm.Editor };

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
