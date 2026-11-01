using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Pdf;

namespace DreamBooks.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new(
        new FileDialogService(), new RecoveryStore(RecoveryRoot()), new ViewPositionStore(PositionsRoot()));

    // Set once the view model allowed closing, so the second Close() goes through.
    private bool _closeAllowed;
    private bool _closeCheckRunning;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
        DocumentViewer.LeftEdgeSwiped += (_, _) => MarksPanel.Open(); // a finger swipe from the left edge (UI-04)
    }

    /// <summary>The marks list hosted by <see cref="MarksPanel"/> (not named in XAML: the panel is a user control).</summary>
    internal MarksListView MarksList => (MarksListView)MarksPanel.Body!;

    /// <summary>Journals and document locks: <c>%LOCALAPPDATA%\DreamBooks\recovery</c> (FR-84), outside the user's folders.</summary>
    internal static string RecoveryRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DreamBooks", "recovery");

    /// <summary>Last position per document: <c>%LOCALAPPDATA%\DreamBooks\positions</c> (FR-04), outside the user's folders.</summary>
    internal static string PositionsRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DreamBooks", "positions");

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

    /// <summary>
    /// PgUp, PgDn, Home and End anywhere in the window move the document through the view model (FR-10), before the
    /// scroll viewer's own handling; never while typing in a field or on a slider.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!e.Handled && RouteKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, e.OriginalSource))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// The window's key routing (separate so tests can drive it): true when a navigation key was taken for the
    /// document, or Esc for the open left-edge panel (it closes). Keys pressed inside the open panel (arrows, PgUp/PgDn,
    /// Home/End in the list) stay the panel's. Everything else (Alt+Left for Back, Alt+Up/Alt+Down for the marks, the
    /// shortcuts) is left to the key bindings.
    /// </summary>
    internal bool RouteKey(Key key, ModifierKeys modifiers, object? source)
    {
        if (MarksPanel.IsOpen)
        {
            if (key == Key.Escape && modifiers == ModifierKeys.None)
            {
                MarksPanel.Close(); // Esc closes the left-edge panel (UI-04)
                return true;
            }

            if (source is DependencyObject element && IsWithin(element, MarksPanel))
            {
                return false;
            }
        }

        return DocumentViewer.HandleNavigationKey(key, modifiers, source);
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A real mouse press over the document while the left-edge panel is open (FR-30: the pen has its own).</summary>
    private void OnPageHostMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is null && MarksPanel.RoutePress(e.GetPosition(MarksPanel), PointerKind.Mouse))
        {
            e.Handled = true;
        }
    }

    /// <summary>A finger press over the document while the panel is open: outside it, a light dismiss.</summary>
    private void OnPageHostTouchDown(object? sender, TouchEventArgs e)
    {
        if (Stylus.CurrentStylusDevice?.TabletDevice?.Type == TabletDeviceType.Stylus)
        {
            return; // the pen's own touch events: routed by its stylus press
        }

        if (MarksPanel.RoutePress(e.GetTouchPoint(MarksPanel).Position, PointerKind.Finger))
        {
            e.Handled = true; // not promoted to the mouse, never panned
        }
    }

    /// <summary>The pen's press: outside the open panel it closes it and inks; on it, it closes it and is taken.</summary>
    private void OnPageHostStylusDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Stylus
            && MarksPanel.RoutePress(e.GetPosition(MarksPanel), PointerKind.Pen))
        {
            e.Handled = true;
        }
    }

    /// <summary>The panel's go-to-page field (FR-09): Enter jumps to a valid page and clears it; Esc clears it.</summary>
    private void OnGoToPageFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_viewModel.Viewport.TryGoToPage(GoToPageField.Text))
            {
                GoToPageField.Clear();
            }
            else
            {
                GoToPageField.SelectAll(); // invalid input is ignored
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            GoToPageField.Clear();
        }
    }

    /// <summary>Fit width (FR-07): turns the mode on, fitting the widest page; nothing to do when it is already on.</summary>
    private void OnFitWidthClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.Viewport.IsFitWidth)
        {
            _viewModel.Viewport.ToggleFitWidth(null);
        }
    }
}
