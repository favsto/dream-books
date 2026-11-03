using System.Runtime.CompilerServices;
using System.Windows;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Services;

namespace DreamBooks.App.Tests;

/// <summary>
/// A closed window is garbage: closing it (and opening another) never keeps the old window, its view model or its
/// document in memory. The only thing that held a closed test window was WPF UI's title bar on the static theme event,
/// released on Unloaded (<see cref="TestWindows.CloseForTests"/>, which a shown window gets from WPF when it closes).
/// </summary>
[Collection(nameof(MarkIntegrationCollection))] // alone: the GC sees only this test's windows go
public sealed class MainWindowLifetimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-lifetime-" + Guid.NewGuid().ToString("N"));

    public MainWindowLifetimeTests() => Directory.CreateDirectory(_dir);

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
    public async Task Closed_windows_with_a_document_are_collected_window_after_window()
    {
        var references = new List<WeakReference>();
        for (var i = 0; i < 3; i++)
        {
            references.AddRange(await OpenUseAndCloseAsync(pages: 1 + (i * 7)));
        }

        // One more after them, not checked: the most recently closed window (with its view model and document) is now
        // and then still reachable for a while after the test closed it, from outside every static field, the
        // dispatcher's queue and its timers (a reflection walk from all of them never reaches it), and is collected
        // later; what matters is that closed windows never pile up.
        _ = await OpenUseAndCloseAsync(pages: 3);
        Collect(references);
        Assert.True(!references.Any(r => r.IsAlive), "alive: " + string.Join(",", references.Select((r, i) => r.IsAlive ? $"{i}:{r.Target?.GetType().Name}" : "")));
    }

    [Fact]
    public void A_closed_empty_window_is_collected()
    {
        WeakReference? reference = null;
        WpfTestThread.Run(() =>
        {
            reference = BuildAndClose();
            _ = BuildAndClose(); // the most recent one is not checked (see above)
        });
        Collect([reference!]);
        Assert.False(reference!.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BuildAndClose()
    {
        var window = new MainWindow();
        ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
        window.CloseForTests();
        return new WeakReference(window);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference[]> OpenUseAndCloseAsync(int pages)
    {
        var dialogs = new FakeFileDialogService();
        var vm = new MainViewModel(dialogs, scheduler: new ManualScheduler()); // its own: pending work never outlives the test
        var path = Path.Combine(_dir, $"doc{pages}.pdf");
        File.WriteAllBytes(path, [1, 2, 3]);
        dialogs.NextPath = path;
        vm.OpenDocument = (opened, _) =>
        {
            var source = new FakePageSource(Enumerable.Repeat((612.0, 792.0), pages).ToArray());
            return Task.FromResult(new OpenedDocument(source, source, opened, new FakeViewerHandle([])));
        };
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Editor);

        WeakReference[] references = [];
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow { DataContext = vm };
            window.DocumentViewer.BitmapRenderer = new FakeBitmapRenderer();
            window.DocumentViewer.Rail.AnimationsOverride = false;
            ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
            ScrollRailTests.Settle(window.DocumentViewer);
            vm.Toolbar.TapTool(ToolbarTool.Pen); // the pen flyout opens
            references = [new(window), new(window.DocumentViewer), new(window.FloatingToolbar), new(vm.Session!)];
            window.CloseForTests();
        });
        return references;
    }

    /// <summary>
    /// Collects until the references are gone or 5 s passed. A window closed a moment ago can still be reachable for a
    /// short while from a timer of its own that is already due (the bitmap coordinator's settle, the editor loads): the
    /// dispatcher holds a started timer until it ticks. That is not a leak; a leak stays past the timeout.
    /// </summary>
    private static void Collect(IReadOnlyCollection<WeakReference> references)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            WpfTestThread.Run(() => WpfTestThread.PumpUntil(() => true));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!references.Any(r => r.IsAlive))
            {
                return;
            }

            Thread.Sleep(50);
        }
        while (watch.ElapsedMilliseconds < 5000);
    }
}
