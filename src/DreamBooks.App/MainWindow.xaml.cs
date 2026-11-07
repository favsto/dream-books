using DreamBooks.App.Resources;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>The non-client hit test answer "client" (HTCLIENT).</summary>
    internal const int HitTestClient = 1;

    private const int _wmNcHitTest = 0x0084;

    private readonly MainViewModel _viewModel = new(
        new FileDialogService(), new RecoveryStore(RecoveryRoot()), new ViewPositionStore(PositionsRoot()));

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
    /// <param name="recents">The recent documents (FR-03) for the start screen, or null (no start screen).</param>
    /// <param name="covers">The cover cache of the recent documents, or null (cards keep their placeholder).</param>
    /// <param name="settings">
    /// The app settings (FR-110): the tools and the toolbar take the remembered values and every change is remembered;
    /// null in tests that do not use the settings file.
    /// </param>
    internal MainWindow(ThemeService? theme, SettingsService? settings = null, RecentFilesService? recents = null, CoverService? covers = null)
    {
        _theme = theme;
        InitializeComponent();
        DataContext = _viewModel;
        if (settings is not null)
        {
            settings.AttachTools(_viewModel.Tools);
            settings.AttachToolbar(_viewModel.Toolbar);
            AttachSettingsPanel(settings);

            // A settings file that cannot be written shows a notice until a later write works (FR-110). Raised on a
            // thread-pool thread, like the journal's faults: shown on this window's thread.
            void OnSettingsSaveStatus(object? sender, EventArgs e) =>
                Dispatcher.BeginInvoke(() => CurrentViewModel.SettingsWarning = settings.Warning);
            settings.SaveStatusChanged += OnSettingsSaveStatus;
            Closed += (_, _) => settings.SaveStatusChanged -= OnSettingsSaveStatus;
            _viewModel.SettingsWarning = settings.Warning;
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
        DocumentViewer.SingleTapped += (_, position) => OnDocumentSingleTap(position);

        // Ctrl+M opens and closes the marks list (FR-103; free in 3a), Ctrl+Shift+P the page thumbnails (UI-04; Ctrl+P is
        // left to printing, the shortcut every Windows reader expects for it). Each switches to its tab when the other is
        // shown. Added here: the panel's command is not reachable from the window's input bindings in XAML.
        InputBindings.Add(new KeyBinding(MarksPanel.ToggleCommand, Key.M, ModifierKeys.Control) { CommandParameter = MarksTab });
        InputBindings.Add(new KeyBinding(MarksPanel.ToggleCommand, Key.P, ModifierKeys.Control | ModifierKeys.Shift)
        {
            CommandParameter = PagesTab,
        });

        // The main menu's entries for the left-edge panel's tabs and the search (UI-03): the same commands as Ctrl+M,
        // Ctrl+Shift+P and Ctrl+F. Open, Save and Save as bind the view model's commands in XAML.
        MainMenu.MarksItem.Command = MarksPanel.OpenTabCommand;
        MainMenu.MarksItem.CommandParameter = MarksTab;
        MainMenu.PagesItem.Command = MarksPanel.OpenTabCommand;
        MainMenu.PagesItem.CommandParameter = PagesTab;
        MainMenu.SearchItem.Command = new RelayCommand(() => FocusSearchField(CurrentViewModel.Search));

        // The page thumbnails: bitmaps from the document view's coordinator (the current one), the ink as the marks list
        // and the rail read it (FR-103); both in the coordinator's low-priority queue, never ahead of a page render.
        var thumbnails = new PageThumbnailCache(
            (pageIndex, ct) => DocumentViewer.Bitmaps.RenderThumbnailAsync(pageIndex, ct),
            (pageIndex, ct) => DataContext is MainViewModel vm
                ? DocumentViewer.Bitmaps.RunWhenIdleAsync(token => vm.LoadPageInkAsync(pageIndex, token), ct)
                : Task.FromResult<PageInk?>(null));
        thumbnails.SetPaused(true); // until the Pages tab is shown
        if (recents is not null)
        {
            AttachStart(recents, covers, thumbnails.GetBitmapAsync);
        }

        PagesList.Thumbnails = thumbnails;
        MarksList.PageBitmaps = thumbnails; // the marks rows draw the page under their ink from the same queue and cache
        DocumentViewer.ThumbnailScaleChanged += (_, _) => thumbnails.ClearBitmaps(); // rendered for the old scale
        DocumentViewer.ThumbnailRetryRequested += (_, _) => thumbnails.RetryMissing(); // the file is back after a save

        // The indicator chip and the navigation buttons (UI-05, FR-09, FR-102) show and hide with the rail, and keep it
        // shown while they are used. "Nessun altro segno" beside the buttons is a live region: Narrator reads it when it
        // appears (NFR-31), and it shows the buttons.
        DocumentViewer.Rail.ShownChanged += (_, _) => OnRailShownChanged();
        IndicatorChip.HoldChanged += (_, _) => OnOverlayHoldChanged();
        NavButtons.HoldChanged += (_, _) => OnOverlayHoldChanged();
        NavButtons.StatusShown += (_, _) => OnMarkStatusShown();
        OnRailShownChanged();

        // Esc or the clear button emptied the search: the keyboard goes back to the document (FR-13).
        _viewModel.Search.FocusDocumentRequested += (_, _) => FocusDocument();

        // The pen on the title bar writes instead of dragging the window (UI-06, FR-30): the bar's parts let the pen
        // through, and the window's hook answers the non-client hit test for the pen before WPF UI's (added on the first
        // render; hooks added later run first). The parts are changed once laid out, when every part of its template (the
        // window buttons among them) is in the tree.
        // Also when it is loaded, and when its template is applied again (a new style builds new parts at the same size,
        // which SizeChanged would miss).
        AppTitleBar.SizeChanged += (_, _) => { TitleBarPenInput.LetPenThrough(AppTitleBar); StyleWindowButtons(); };
        AppTitleBar.Loaded += (_, _) => { TitleBarPenInput.LetPenThrough(AppTitleBar); StyleWindowButtons(); };
        var titleBarTemplate = DependencyPropertyDescriptor.FromProperty(Control.TemplateProperty, typeof(Control));
        void OnTitleBarTemplateChanged(object? sender, EventArgs e)
        {
            AppTitleBar.ApplyTemplate(); // the new parts now, so they let the pen through before the next press
            TitleBarPenInput.LetPenThrough(AppTitleBar);
            StyleWindowButtons();
        }

        titleBarTemplate.AddValueChanged(AppTitleBar, OnTitleBarTemplateChanged);
        Closed += (_, _) => titleBarTemplate.RemoveValueChanged(AppTitleBar, OnTitleBarTemplateChanged); // a static table
        AppTitleBar.MaximizeActionOverride = (_, _) => ToggleMaximize();
        ContentRendered += OnFirstContentRendered;
    }

    /// <summary>
    /// Gives the three window buttons of the title bar their Italian names and the full height of the bar (a target at least 40 DIP, running to the window's top edge),
    /// lined up with the pill behind them (UI-06, NFR-31). WPF UI names them PART_MinimizeButton, PART_MaximizeButton and
    /// PART_CloseButton.
    /// </summary>
    internal void StyleWindowButtons()
    {
        var height = (double)FindResource("TitleBarHeight");
        foreach (var button in TitleBarPenInput.Buttons(AppTitleBar))
        {
            var name = button.Name switch
            {
                "PART_MinimizeButton" => Strings.TitleBarMinimize,
                "PART_MaximizeButton" => Strings.TitleBarMaximize,
                "PART_CloseButton" => Strings.TitleBarClose,
                _ => null,
            };
            if (name is null)
            {
                continue;
            }

            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Height = height;
            button.VerticalAlignment = VerticalAlignment.Top;
            button.Margin = new Thickness(0); // the targets run to the top and right edges of the window (the corner convention)
        }
    }

    private long _lastMaximizeToggle;

    /// <summary>
    /// The maximise button: maximises or restores, once per click. The device test saw the button maximise and fall back
    /// (and later do nothing): one click reached WPF UI's action twice, through the button and through the non-client
    /// hit test. A second call within <see cref="MaximizeDebounce"/> of the first is the same click.
    /// </summary>
    internal void ToggleMaximize()
    {
        var now = Environment.TickCount64;
        if (now - _lastMaximizeToggle < MaximizeDebounce.TotalMilliseconds)
        {
            return;
        }

        _lastMaximizeToggle = now;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    internal static readonly TimeSpan MaximizeDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>The settings panel's view model; null when the window has no settings service (tests).</summary>
    internal SettingsViewModel? SettingsVm { get; private set; }

    private void AttachSettingsPanel(SettingsService settings)
    {
        var vm = new SettingsViewModel(settings);
        SettingsVm = vm;
        SettingsView.DataContext = vm;
        MainMenu.SettingsItem.Command = vm.OpenCommand;
        InputBindings.Add(new KeyBinding(vm.OpenCommand, Key.OemComma, ModifierKeys.Control));
    }

    private CoverService? _covers;

    /// <summary>The start screen's view model; null when the window has no recent-documents service (tests).</summary>
    internal StartViewModel? Start => StartScreen.DataContext as StartViewModel;

    /// <summary>
    /// The start screen (FR-03): the recent documents as covers, shown while no document is open or being opened. The
    /// covers come from the cover cache, and a document that opens stores its first page there; "Apri con" and a file
    /// dropped on the window open through the normal open path.
    /// </summary>
    private void AttachStart(RecentFilesService recents, CoverService? covers, Func<int, CancellationToken, Task<System.Windows.Media.Imaging.BitmapSource?>> firstPage)
    {
        _covers = covers;
        _viewModel.Recents = recents;
        var start = new StartViewModel(
            recents,
            () => _viewModel.OpenPdfCommand.ExecuteAsync(null),
            path => _viewModel.OpenRecentAsync(path),
            fitCount: 4);
        StartScreen.DataContext = start;
        if (covers is not null)
        {
            start.CoverProvider = async (path, ct) => await covers.GetAsync(path, ct);
            covers.Stored += (_, path) => Dispatcher.BeginInvoke(() => start.ReloadCover(path));
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Session) && _viewModel.Session is { } session)
                {
                    _ = StoreCoverAsync(covers, session.Path, firstPage);
                }
            };
        }

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsStartScreenVisible))
            {
                SyncStartScreen(recents);
            }
        };
        SyncStartScreen(recents);
        MainMenu.SetRecents(recents.Entries, path => _ = _viewModel.OpenRecentAsync(path));
        recents.Changed += (_, _) => MainMenu.SetRecents(recents.Entries, path => _ = _viewModel.OpenRecentAsync(path));
    }

    private void SyncStartScreen(RecentFilesService recents)
    {
        var show = _viewModel.IsStartScreenVisible;
        StartScreen.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            _ = recents.RefreshAvailabilityAsync(CancellationToken.None); // off the UI thread: a dead network drive never freezes the window
        }
    }

    /// <summary>The first page of the document that just opened becomes its cover (low-priority render; a few tries while the viewer starts).</summary>
    private static async Task StoreCoverAsync(
        CoverService covers, string path, Func<int, CancellationToken, Task<System.Windows.Media.Imaging.BitmapSource?>> firstPage)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var bitmap = await firstPage(0, CancellationToken.None);
            if (bitmap is not null)
            {
                await covers.StoreAsync(path, bitmap, CancellationToken.None);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _ = OpenDroppedFileAsync(paths);
        }

        e.Handled = true;
    }

    /// <summary>
    /// A file dropped on the window: the first PDF opens (the normal open path, with its question about unsaved changes);
    /// anything else says "Questo non è un PDF" and changes nothing.
    /// </summary>
    internal async Task OpenDroppedFileAsync(IReadOnlyList<string> paths)
    {
        var pdf = paths.FirstOrDefault(p => string.Equals(System.IO.Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf is null)
        {
            _viewModel.ShowError(Strings.StartNotAPdf);
            return;
        }

        await CurrentViewModel.OpenRecentAsync(pdf);
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
        TitleNamePill.IsOpaque = !TransparencyEnabled();
        TitleButtonsPill.IsOpaque = !TransparencyEnabled();
        SettingsView.ApplyTransparency(TransparencyEnabled());
    }

    /// <summary>
    /// The window's answer to the non-client hit test at <paramref name="position"/> (in the window content's
    /// coordinates): "client" for the pen on the title bar's drag area, so the pen writes there instead of dragging the
    /// window (UI-06, FR-30); null otherwise (the mouse and fingers, the window buttons, the rest of the window), left
    /// to WPF UI and Windows.
    /// </summary>
    internal int? TitleBarHitTest(Point position, bool isPen)
    {
        if (!isPen || AppTitleBar.ActualWidth <= 0)
        {
            return null;
        }

        var bar = AppTitleBar.TransformToAncestor(ShellRoot).TransformBounds(new Rect(AppTitleBar.RenderSize));
        if (!bar.Contains(position))
        {
            return null;
        }

        foreach (var button in TitleBarPenInput.Buttons(AppTitleBar))
        {
            if (button.TransformToAncestor(ShellRoot).TransformBounds(new Rect(button.RenderSize)).Contains(position))
            {
                return null; // the window buttons work for the pen too
            }
        }

        return HitTestClient;
    }

    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;

        // After every ContentRendered handler, WPF UI's title bar hook among them: WPF UI 4.3.0 adds its hook once, in
        // TitleBar.OnWindowContentRendered, and HwndSource calls the hooks added last first. Check this again when
        // WPF UI is updated: if it adds its hook later or again, ours would no longer run first.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            (System.Windows.Interop.HwndSource.FromVisual(this) as System.Windows.Interop.HwndSource)?.AddHook(OnTitleBarHitTest));
    }

    private IntPtr OnTitleBarHitTest(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != _wmNcHitTest || !TitleBarPenInput.IsPenInput())
        {
            return IntPtr.Zero;
        }

        // The screen point in lParam: signed 16-bit x and y (negative on monitors left of or above the primary one).
        var value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        Point position;
        try
        {
            position = ShellRoot.PointFromScreen(screen);
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero; // no presentation source yet
        }

        if (TitleBarHitTest(position, isPen: true) is not { } answer)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return answer;
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
    private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The file the app was started with ("Apri con"); set before the window is shown. It wins over the restore (FR-86).</summary>
    internal string? StartupFile { get; set; }

    /// <summary>
    /// Completes when the start-up action (the file, the restored session, or nothing: the start screen) has finished,
    /// whether it worked or not. The splash closes on it; there is no minimum time.
    /// </summary>
    internal Task StartupCompleted => _startup.Task;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            switch (StartupPlan.Decide(StartupFile, restorable: _viewModel.Recovery is not null))
            {
                case StartupAction.OpenFile:
                    await _viewModel.OpenStartupFileAsync(StartupFile!);
                    break;
                case StartupAction.Restore:
                    await _viewModel.TryRestoreLastSessionAsync();
                    break;
            }
        }
#pragma warning disable CA1031 // A failed recovery or open leaves the journal on disk; the app starts on the start screen.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            _startup.TrySetResult();
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
    /// The search keys (FR-13, FR-105) come first: Ctrl+F opens the search pill and gives its field the keyboard, and
    /// F3 / Shift+F3 go to the next / previous result from anywhere; in the search pill's field Enter / Shift+Enter do
    /// the same and Esc clears the search and closes the pill (the keyboard goes back to the document), as does Esc
    /// elsewhere outside a text field while a search is shown or the pill is open. The indicator chip's page field keeps
    /// its own Enter and Esc. Before all of them, Esc closes the open main menu (UI-03).
    /// </summary>
    internal bool RouteKey(Key key, ModifierKeys modifiers, object? source)
    {
        if (key == Key.Escape && modifiers == ModifierKeys.None && SettingsView.IsOpen)
        {
            SettingsVm?.CloseCommand.Execute(null);
            return true;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None && MainMenu.IsOpen)
        {
            MainMenu.Close();
            return true;
        }

        // Esc closes the open toolbar flyout first (UI-02), except in the search field and the chip's page field, which
        // keep their own Esc.
        var viewModel = CurrentViewModel;
        if (key == Key.Escape && modifiers == ModifierKeys.None && viewModel.Toolbar.OpenPanel != OptionsPanel.None
            && !(source is DependencyObject origin && (IsInSearchField(origin) || IsWithin(origin, IndicatorChip.GoToPageBox))))
        {
            viewModel.Toolbar.ClosePanel();
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
        var search = CurrentViewModel.Search;
        var inField = source is DependencyObject element && IsInSearchField(element); // also a part of its template
        switch (key)
        {
            case Key.F when modifiers == ModifierKeys.Control:
                FocusSearchField(search);
                return true;
            case Key.F3 when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                Run(modifiers == ModifierKeys.Shift ? search.PreviousCommand : search.NextCommand);
                return true;
            case Key.Enter when inField && modifiers is ModifierKeys.None or ModifierKeys.Shift:
                Run(modifiers == ModifierKeys.Shift ? search.PreviousCommand : search.NextCommand);
                return true;
            case Key.Escape when modifiers == ModifierKeys.None
                                 && (inField || (!panelOpen && source is not TextBoxBase
                                                 && (search.QueryText.Length > 0 || search.IsOpen))):
                // Empties the field and closes the pill; the view model asks for the document's focus.
                search.ClearCommand.Execute(null);
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

    /// <summary>Ctrl+F: the search pill opens and its field takes the keyboard, the text selected (FR-13).</summary>
    private void FocusSearchField(SearchViewModel search)
    {
        SearchFocusRequests++;
        search.Open();
        SearchBar.FocusField();
    }

    /// <summary>The window's view model as bound now (tests may give the window another one).</summary>
    private MainViewModel CurrentViewModel => DataContext as MainViewModel ?? _viewModel;

    /// <summary>True inside the search pill's field (also a part of its template).</summary>
    private bool IsInSearchField(DependencyObject element) => IsWithin(element, SearchBar.Field);

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

    /// <summary>
    /// A finger's single tap on the document (<see cref="DocumentView.SingleTapped"/>, viewport coordinates): on an empty
    /// area it shows or hides the toolbar and closes its option panel (section 4); on a floating element it does
    /// nothing. A press that closed a panel from outside never gets here (it is taken), nor does the pen.
    /// </summary>
    internal void OnDocumentSingleTap(Point viewportPoint)
    {
        var point = DocumentViewer.Viewport.TranslatePoint(viewportPoint, PageHost);
        if (InputTargetAt(PageHost, point) is not { } hit || !IsWithin(hit, DocumentViewer))
        {
            return; // on a floating element (toolbar, chip, buttons, menu, notices), or outside the document
        }

        CurrentViewModel.Toolbar.FingerTapOnEmptyArea();
    }

    /// <summary>
    /// The visual input would reach at <paramref name="point"/>: a hit test that skips what input routing skips
    /// (elements that are not hit-testable or not visible, with their children).
    /// </summary>
    private static DependencyObject? InputTargetAt(Visual root, Point point)
    {
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(
            root,
            candidate => candidate is UIElement { IsHitTestVisible: false } or UIElement { Visibility: not Visibility.Visible }
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result =>
            {
                hit = result.VisualHit;
                return HitTestResultBehavior.Stop;
            },
            new PointHitTestParameters(point));
        return hit;
    }

    /// <summary>The marks' status says something: announced as a live region, and shown with the buttons and the rail.</summary>
    private void OnMarkStatusShown()
    {
        StatusAnnouncements++;
        var status = NavButtons.StatusText;
        var peer = UIElementAutomationPeer.FromElement(status) ?? UIElementAutomationPeer.CreatePeerForElement(status);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        DocumentViewer.Rail.NotifyActivity();
    }

    /// <summary>The rail showed, started to fade out, hid, or got or lost a document: the chip and the buttons follow.</summary>
    private void OnRailShownChanged()
    {
        var rail = DocumentViewer.Rail;
        IndicatorChip.SetShown(rail.IsShownForOverlays, rail.HasDocument);
        NavButtons.SetShown(rail.IsShownForOverlays, rail.HasDocument);
    }

    /// <summary>The chip or the buttons are in use (hovered, the page field open, the keyboard on them): the rail stays.</summary>
    private void OnOverlayHoldChanged() =>
        DocumentViewer.Rail.SetExternalHold(IndicatorChip.IsHolding || NavButtons.IsHolding);

    /// <summary>
    /// A press at <paramref name="position"/> (page host coordinates) over the document: the open left-edge panel, the
    /// open toolbar flyout and the open main menu each decide (<see cref="EdgePanel.RoutePress"/>,
    /// <see cref="ToolbarView.RoutePress"/>, <see cref="MainMenuButton.RoutePress"/>); true when any takes it. Outside
    /// them they close; fingers and the mouse are taken, the pen inks. On the left-edge panel's sheet the pen closes it
    /// and is taken (no stroke); on the flyout, the toolbar and the menu the pen presses the controls like a finger, on
    /// purpose (UI-02, UI-03). Separate so tests can drive it.
    /// </summary>
    internal bool RoutePagePress(Point position, PointerKind kind)
    {
        var panel = MarksPanel.RoutePress(PageHost.TranslatePoint(position, MarksPanel), kind);
        var flyout = FloatingToolbar.RoutePress(PageHost.TranslatePoint(position, FloatingToolbar), kind);
        var menu = MainMenu.RoutePress(PageHost.TranslatePoint(position, MainMenu), kind);
        var chip = IndicatorChip.RoutePress(PageHost.TranslatePoint(position, IndicatorChip), kind); // closes its open field
        var settingsPanel = SettingsView.RoutePress(PageHost.TranslatePoint(position, SettingsView), kind);
        if (!(panel || flyout || menu || chip || settingsPanel))
        {
            return false;
        }

        // The dismissing press that lands on another floating control (the toolbar, the navigation buttons, the chip, the
        // menu button) has closed what was open and then goes on to that control: the first press acts (device test of
        // 4a). The pen on the left-edge panel's sheet keeps its own rule (it closes it and is taken, no stroke).
        return (panel && kind == PointerKind.Pen) || !IsOnFloatingControl(position);
    }

    /// <summary>True when the topmost element at <paramref name="position"/> (page host coordinates) is part of a floating surface.</summary>
    private bool IsOnFloatingControl(Point position)
    {
        for (DependencyObject? current = VisualTreeHelper.HitTest(PageHost, position)?.VisualHit;
             current is not null && !ReferenceEquals(current, PageHost);
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (current is FloatingSurface)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The pen's press: outside the open panel or flyout it closes it and inks; on the panel's sheet it closes it and is
    /// taken; on the flyout it is left to the flyout's controls.
    /// </summary>
    private void OnPageHostStylusDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Stylus
            && RoutePagePress(e.GetPosition(PageHost), PointerKind.Pen))
        {
            e.Handled = true;
        }
    }

}
