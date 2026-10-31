using System.ComponentModel;
using System.IO;
using System.Windows;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Pdf;

namespace DreamBooks.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new(new FileDialogService(), new RecoveryStore(RecoveryRoot()));

    // Set once the view model allowed closing, so the second Close() goes through.
    private bool _closeAllowed;
    private bool _closeCheckRunning;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    /// <summary>Journals and document locks: <c>%LOCALAPPDATA%\DreamBooks\recovery</c> (FR-84), outside the user's folders.</summary>
    internal static string RecoveryRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DreamBooks", "recovery");

    /// <summary>
    /// Startup recovery (FR-86), once the window is up so its questions have an owner: reopens the document of
    /// the most recent journal with its unsaved changes. Never on the pen path; never crashes the app.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _viewModel.TryRestoreLastSessionAsync();
        }
#pragma warning disable CA1031 // A failed recovery leaves the journal on disk; the app starts empty.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// FR-85: asks about unsaved changes before closing. The check is asynchronous (it may wait for a save),
    /// so the first close is cancelled and the window closes again once the view model allows it.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeAllowed || e.Cancel)
        {
            return;
        }

        if (_closeCheckRunning)
        {
            e.Cancel = true; // a second close request while the question or the save is still running
            return;
        }

        var check = _viewModel.CanCloseAsync();
        if (SynchronousCloseDecision(check) is { } allowed)
        {
            // Answered synchronously (no changes, Don't save, Cancel): decide on this very close.
            e.Cancel = !allowed;
            _closeAllowed = allowed;
            return;
        }

        e.Cancel = true;
        _closeCheckRunning = true;
        _ = CloseWhenAllowedAsync(check);
    }

    /// <summary>
    /// The decision of a close check that already finished, or null while it still runs. A faulted or
    /// cancelled check keeps the window open: never crash (or close) with unsaved changes.
    /// </summary>
    internal static bool? SynchronousCloseDecision(Task<bool> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!check.IsCompleted)
        {
            return null;
        }

        if (!check.IsCompletedSuccessfully)
        {
            _ = check.Exception; // observed, so it is not reported as unobserved later
            return false;
        }

        return check.Result;
    }

    private async Task CloseWhenAllowedAsync(Task<bool> check)
    {
        bool allowed;
        try
        {
            allowed = await check;
        }
#pragma warning disable CA1031 // Never close (or crash) on an unexpected failure: the changes stay open.
        catch (Exception)
#pragma warning restore CA1031
        {
            allowed = false;
        }
        finally
        {
            _closeCheckRunning = false;
        }

        if (allowed)
        {
            _closeAllowed = true;
            await Dispatcher.InvokeAsync(Close); // not from inside the cancelled Closing event
        }
    }

    private void OnFitWidthClick(object sender, RoutedEventArgs e) =>
        _viewModel.ZoomVm.FitWidth(PageViewer.FitWidthViewport, _viewModel.PageSize.WidthPt);
}
