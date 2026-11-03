using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Pdf;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using WindowBackdrop = Wpf.Ui.Controls.WindowBackdrop;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace DreamBooks.App;

public partial class MainWindow : FluentWindow
{
    /// <summary>The left-edge panel's tabs: the marks list and the page thumbnails.</summary>
    internal const int MarksTab = 0;

    internal const int PagesTab = 1;

    private readonly MainViewModel _viewModel = new(
        new FileDialogService(), new RecoveryStore(RecoveryRoot()), new ViewPositionStore(PositionsRoot()));

    private static readonly DependencyPropertyDescriptor _statusText =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    private readonly ThemeService? _theme;

    private int? _dismissTouchId;

    // Set once the view model allowed closing, so the second Close() goes through.
    private bool _closeAllowed;
    private bool _closeCheckRunning;

    public MainWindow()
        : this(null)
    {
    }

    /// <summary>
    /// The window with the app's theme service (null in tests that do not switch themes): after each theme change the
    /// backdrop is applied again, as WPF UI's theme manager re-applies the main window's backdrop itself.
    /// </summary>
    /// <param name="theme">The app's theme service, or null.</param>
    /// <param name="settings">
    /// The app settings (FR-110): the tools and the toolbar take the remembered values and every change is remembered;
    /// null in tests that do not use the settings file.
    /// </param>
    internal MainWindow(ThemeService? theme, SettingsService? settings = null)
    {
        _theme = theme;
        InitializeComponent();
        DataContext = _viewModel;
        if (settings is not null)
        {
            settings.AttachTools(_viewModel.Tools);
            settings.AttachToolbar(_viewModel.Toolbar);
        }

        Loaded += OnLoaded;
        ApplyBackdrop();
        if (theme is not null)
        {
            theme.ThemeChanged += OnThemeChanged;
            Closed += (_, _) => theme.ThemeChanged -= OnThemeChanged;
        }

        // Windows "Transparency effects" (NFR-32) switched while the app runs: Mica or the solid brush.
        SourceInitialized += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            // The theme was applied before this window had a handle: give it the theme's dark or light frame and
            // Mica tint now (WPF UI does it itself on later theme changes).
            WindowBackgroundManager.UpdateBackground(this, ApplicationThemeManager.GetAppTheme(), WindowBackdropType);
        };
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        DocumentViewer.LeftEdgeSwiped += (_, _) => MarksPanel.Open(); // a finger swipe from the left edge (UI-04)

        // Ctrl+M opens and closes the marks list (FR-103; free in 3a), Ctrl+Shift+P the page thumbnails (UI-04; Ctrl+P is
        // left to printing, the shortcut every Windows reader expects for it). Each switches to its tab when the other is
        // shown. Added here: the panel's command is not reachable from the window's input bindings in XAML.
        InputBindings.Add(new KeyBinding(MarksPanel.ToggleCommand, Key.M, ModifierKeys.Control) { CommandParameter = MarksTab });
        InputBindings.Add(new KeyBinding(MarksPanel.ToggleCommand, Key.P, ModifierKeys.Control | ModifierKeys.Shift)
        {
            CommandParameter = PagesTab,
        });

        // The page thumbnails: bitmaps from the document view's coordinator (the current one), the ink as the marks list
        // and the rail read it (FR-103); both in the coordinator's low-priority queue, never ahead of a page render.
        var thumbnails = new PageThumbnailCache(
            (pageIndex, ct) => DocumentViewer.Bitmaps.RenderThumbnailAsync(pageIndex, ct),
            (pageIndex, ct) => DataContext is MainViewModel vm
                ? DocumentViewer.Bitmaps.RunWhenIdleAsync(token => vm.LoadPageInkAsync(pageIndex, token), ct)
                : Task.FromResult<PageInk?>(null));
        thumbnails.SetPaused(true); // until the Pages tab is shown
        PagesList.Thumbnails = thumbnails;
        DocumentViewer.ThumbnailScaleChanged += (_, _) => thumbnails.ClearBitmaps(); // rendered for the old scale
        DocumentViewer.ThumbnailRetryRequested += (_, _) => thumbnails.RetryMissing(); // the file is back after a save

        // "Nessun altro segno" is a live region: Narrator reads it when it appears (NFR-31).
        _statusText.AddValueChanged(MarkStatusText, OnMarkStatusTextChanged);
        Closed += (_, _) => _statusText.RemoveValueChanged(MarkStatusText, OnMarkStatusTextChanged);

        // Esc or the clear button emptied the search: the keyboard goes back to the document (FR-13).
        _viewModel.Search.FocusDocumentRequested += (_, _) => FocusDocument();

    }

    /// <summary>The marks list hosted by <see cref="MarksPanel"/> (not named in XAML: the panel is a user control).</summary>
    internal MarksListView MarksList => (MarksListView)MarksPanel.Body!;

    /// <summary>The page thumbnails hosted by <see cref="MarksPanel"/> as its second tab.</summary>
    internal PagesListView PagesList => (PagesListView)MarksPanel.SecondBody!;

    /// <summary>How many times Ctrl+F asked for the search field's focus (tests: an unshown window cannot take it).</summary>
    internal int SearchFocusRequests { get; private set; }

    /// <summary>How many times the keyboard was given back to the document after the search was cleared (tests).</summary>
    internal int DocumentFocusRequests { get; private set; }

    /// <summary>How many times the marks status was announced as a live region (tests).</summary>
    internal int StatusAnnouncements { get; private set; }

    /// <summary>The Windows "Transparency effects" setting (NFR-32); replaceable by tests.</summary>
    internal Func<bool> TransparencyEnabled { get; set; } = TransparencySetting.IsEnabled;

    /// <summary>Whether Windows can draw Mica (Windows 11); replaceable by tests.</summary>
    internal Func<bool> MicaSupported { get; set; } = () => WindowBackdrop.IsSupported(WindowBackdropType.Mica);

    /// <summary>
    /// The window's backdrop (section 3, NFR-32): Mica behind a transparent window and a translucent grey around the
    /// pages; with Windows "Transparency effects" off, or without Mica (Windows 10), no backdrop and solid theme brushes
    /// instead (window and the area around the pages). The theme service learns the backdrop, as WPF UI's theme manager
    /// re-applies it on each theme change.
    /// </summary>
    internal void ApplyBackdrop()
    {
        var transparent = TransparencyEnabled() && MicaSupported();
        var backdrop = transparent ? WindowBackdropType.Mica : WindowBackdropType.None;
        if (_theme is not null)
        {
            _theme.Backdrop = backdrop;
        }

        // The background first: FluentWindow applies the backdrop when the type changes (with a handle), and Mica needs
        // the window transparent (WPF UI's style background is opaque).
        if (transparent)
        {
            Background = Brushes.Transparent;
        }
        else
        {
            SetResourceReference(BackgroundProperty, "WindowBackgroundOpaqueBrush");
        }

        WindowBackdropType = backdrop;
        DocumentViewer.ApplyTransparency(transparent);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyBackdrop();

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyBackdrop); // raised on another thread

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
        // Esc closes the open toolbar flyout first (UI-02), except in the search field, which keeps its own Esc.
        if (key == Key.Escape && modifiers == ModifierKeys.None && _viewModel.Toolbar.OpenPanel != OptionsPanel.None
            && !(source is DependencyObject origin && IsWithin(origin, SearchField)))
        {
            _viewModel.Toolbar.ClosePanel();
            return true;
        }

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
        if (e.StylusDevice is null && RoutePagePress(e.GetPosition(PageHost), PointerKind.Mouse))
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
        if (RouteDismissTouchDown(e.GetTouchPoint(PageHost).Position, device.Id, () => PageHost.CaptureTouch(device)))
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
    /// A finger down at <paramref name="position"/> (page host coordinates, the panel's too): when it closes the open panel
    /// or an open flyout from outside, <paramref name="capture"/> holds it on the page host and the press is taken (true).
    /// Separate so tests can drive it.
    /// </summary>
    internal bool RouteDismissTouchDown(Point position, int touchId, Func<bool> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!RoutePagePress(position, PointerKind.Finger))
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

    /// <summary>
    /// A press at <paramref name="position"/> (page host coordinates) over the document: the open left-edge panel and
    /// the open toolbar flyout each decide (<see cref="EdgePanel.RoutePress"/>, <see cref="ToolbarView.RoutePress"/>);
    /// true when either takes it. Outside them both close; fingers and the mouse are taken, the pen inks. The pen on
    /// either closes it and is taken (no stroke). Separate so tests can drive it.
    /// </summary>
    internal bool RoutePagePress(Point position, PointerKind kind)
    {
        var panel = MarksPanel.RoutePress(PageHost.TranslatePoint(position, MarksPanel), kind);
        var flyout = FloatingToolbar.RoutePress(PageHost.TranslatePoint(position, FloatingToolbar), kind);
        return panel || flyout;
    }

    /// <summary>The pen's press: outside the open panel or flyout it closes it and inks; on it, it closes it and is taken.</summary>
    private void OnPageHostStylusDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Stylus
            && RoutePagePress(e.GetPosition(PageHost), PointerKind.Pen))
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
