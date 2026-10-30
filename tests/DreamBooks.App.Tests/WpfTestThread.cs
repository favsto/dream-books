using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace DreamBooks.App.Tests;

/// <summary>
/// Runs test code on one shared STA thread with a WPF dispatcher and the app's theme resources
/// loaded (WPF allows one Application per process), without ever showing a window.
/// Exceptions thrown by the action, or by dispatcher work it queued, are rethrown on the test thread.
/// </summary>
internal static class WpfTestThread
{
    private static readonly Lazy<Dispatcher> _dispatcher = new(Start);
    private static readonly Uri _themeUri = new("/DreamBooks.App;component/Resources/Theme.xaml", UriKind.Relative);
    private static Exception? _unhandled;

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ExceptionDispatchInfo? failure = null;
        _dispatcher.Value.Invoke(() =>
        {
            _unhandled = null;
            try
            {
                action();
                Drain();
                if (_unhandled is not null)
                {
                    throw new InvalidOperationException("Unhandled dispatcher exception.", _unhandled);
                }
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        failure?.Throw();
    }

    /// <summary>Processes queued dispatcher work (bindings, layout) so its failures surface in the test.</summary>
    public static void Drain() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? startFailure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += (_, e) =>
            {
                _unhandled = e.Exception;
                e.Handled = true;
            };
            try
            {
                // Not `new App()`: its startup would open the real MainWindow (StartupUri). Merge the same
                // theme dictionary App.xaml merges instead.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = _themeUri });
            }
            catch (Exception ex)
            {
                startFailure = ex;
            }

            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test thread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (startFailure is not null)
        {
            throw new InvalidOperationException("Could not load the WPF application resources.", startFailure);
        }

        return dispatcher!;
    }
}
