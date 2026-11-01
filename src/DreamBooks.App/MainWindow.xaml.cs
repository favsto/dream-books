using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    private static readonly DependencyPropertyDescriptor _statusText =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    private int? _dismissTouchId;

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

        // Ctrl+M opens and closes the marks list (FR-103; free in 3a). Added here: the panel's command is not reachable
        // from the window's input bindings in XAML.
        InputBindings.Add(new KeyBinding(MarksPanel.ToggleCommand, Key.M, ModifierKeys.Control));

        // "Nessun altro segno" is a live region: Narrator reads it when it appears (NFR-31).
        _statusText.AddValueChanged(MarkStatusText, OnMarkStatusTextChanged);
        Closed += (_, _) => _statusText.RemoveValueChanged(MarkStatusText, OnMarkStatusTextChanged);

        // Esc or the clear button emptied the search: the keyboard goes back to the document (FR-13).
        _viewModel.Search.FocusDocumentRequested += (_, _) => FocusDocument();
    }

    /// <summary>The marks list hosted by <see cref="MarksPanel"/> (not named in XAML: the panel is a user control).</summary>
    internal MarksListView MarksList => (MarksListView)MarksPanel.Body!;

    /// <summary>How many times Ctrl+F asked for the search field's focus (tests: an unshown window cannot take it).</summary>
    internal int SearchFocusRequests { get; private set; }

    /// <summary>How many times the keyboard was given back to the document after the search was cleared (tests).</summary>
    internal int DocumentFocusRequests { get; private set; }

    /// <summary>How many times the marks status was announced as a live region (tests).</summary>
    internal int StatusAnnouncements { get; private set; }

    /// <summary>The finger that closed the panel from outside, held by the page host until it lifts; null otherwise.</summary>
    internal int? DismissTouchId => _dismissTouchId;

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
    /// The search keys (FR-13, FR-105) come first: Ctrl+F focuses the search field and F3 / Shift+F3 go to the next /
    /// previous result from anywhere; in the search field Enter / Shift+Enter do the same and Esc clears the search
    /// (the keyboard goes back to the document), as does Esc elsewhere outside a text field while a search is shown.
    /// </summary>
    internal bool RouteKey(Key key, ModifierKeys modifiers, object? source)
    {
        if (RouteSearchKey(key, modifiers, source, panelOpen: MarksPanel.IsOpen))
        {
            return true;
        }

        if (MarksPanel.IsOpen)
        {
            var inPanel = source is DependencyObject element && IsWithin(element, MarksPanel);
            if (key == Key.Escape && modifiers == ModifierKeys.None && (inPanel || source is not TextBoxBase))
            {
                MarksPanel.Close(); // Esc closes the left-edge panel (UI-04); a field outside it keeps its own Esc
                return true;
            }

            if (inPanel)
            {
                return false;
            }
        }

        return DocumentViewer.HandleNavigationKey(key, modifiers, source);
    }

    private bool RouteSearchKey(Key key, ModifierKeys modifiers, object? source, bool panelOpen)
    {
        var search = _viewModel.Search;
        var inField = source is DependencyObject element && IsWithin(element, SearchField); // also a part of its template
        switch (key)
        {
            case Key.F when modifiers == ModifierKeys.Control:
                FocusSearchField();
                return true;
            case Key.F3 when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                Run(modifiers == ModifierKeys.Shift ? search.PreviousCommand : search.NextCommand);
                return true;
            case Key.Enter when inField && modifiers is ModifierKeys.None or ModifierKeys.Shift:
                Run(modifiers == ModifierKeys.Shift ? search.PreviousCommand : search.NextCommand);
                return true;
            case Key.Escape when modifiers == ModifierKeys.None
                                 && (inField || (!panelOpen && source is not TextBoxBase && search.QueryText.Length > 0)):
                search.ClearCommand.Execute(null); // empties the field; the view model asks for the document's focus
                return true;
            default:
                return false;
        }
    }

    private static void Run(ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void FocusSearchField()
    {
        SearchFocusRequests++;
        SearchField.Focus();
        SearchField.SelectAll();
    }

    private void FocusDocument()
    {
        DocumentFocusRequests++;
        DocumentViewer.FocusDocument();
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

    /// <summary>
    /// A finger press over the document while the panel is open: outside it, a light dismiss. The finger is captured by
    /// the page host (not manipulation-enabled) until it lifts, so a drag never pans the page (same as the rail's
    /// press on DocumentView's host).
    /// </summary>
    private void OnPageHostTouchDown(object? sender, TouchEventArgs e)
    {
        if (Stylus.CurrentStylusDevice?.TabletDevice?.Type == TabletDeviceType.Stylus)
        {
            return; // the pen's own touch events: routed by its stylus press
        }

        var device = e.TouchDevice;
        if (RouteDismissTouchDown(e.GetTouchPoint(MarksPanel).Position, device.Id, () => PageHost.CaptureTouch(device)))
        {
            e.Handled = true; // not promoted to the mouse
        }
    }

    private void OnPageHostTouchMove(object? sender, TouchEventArgs e)
    {
        if (RouteDismissTouchMove(e.TouchDevice.Id))
        {
            e.Handled = true;
        }
    }

    private void OnPageHostTouchUp(object? sender, TouchEventArgs e)
    {
        var device = e.TouchDevice;
        if (RouteDismissTouchUp(device.Id, () => PageHost.ReleaseTouchCapture(device)))
        {
            e.Handled = true;
        }
    }

    private void OnPageHostLostTouchCapture(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice.Id == _dismissTouchId)
        {
            _dismissTouchId = null;
        }
    }

    /// <summary>
    /// A finger down at <paramref name="position"/> (panel coordinates): when it closes the open panel from outside,
    /// <paramref name="capture"/> holds it on the page host and the press is taken (true). Separate so tests can drive it.
    /// </summary>
    internal bool RouteDismissTouchDown(Point position, int touchId, Func<bool> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!MarksPanel.RoutePress(position, PointerKind.Finger))
        {
            return false;
        }

        if (capture())
        {
            _dismissTouchId = touchId;
        }

        return true;
    }

    /// <summary>The dismissing finger moves: taken (true), so nothing under it pans.</summary>
    internal bool RouteDismissTouchMove(int touchId) => touchId == _dismissTouchId;

    /// <summary>The dismissing finger lifts: <paramref name="release"/> lets it go; taken (true).</summary>
    internal bool RouteDismissTouchUp(int touchId, Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (touchId != _dismissTouchId)
        {
            return false;
        }

        _dismissTouchId = null;
        release();
        return true;
    }

    /// <summary>The status line's text changed: announced as a live region when it says something.</summary>
    private void OnMarkStatusTextChanged(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(MarkStatusText.Text))
        {
            return;
        }

        StatusAnnouncements++;
        var peer = UIElementAutomationPeer.FromElement(MarkStatusText) ?? UIElementAutomationPeer.CreatePeerForElement(MarkStatusText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
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
