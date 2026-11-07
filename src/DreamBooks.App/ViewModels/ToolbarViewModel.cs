using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Settings;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The floating toolbar (UI-02): Pen, Highlighter, Eraser, Selection (the lasso, FR-50), Undo, Redo, Bookmark
/// (disabled until bookmarks exist); the option panels; and its automatic visibility (section 4).
/// <list type="bullet">
/// <item>A tap on an inactive tool activates it and closes any panel; a tap on the active tool opens its panel, or
/// closes it when open (the lasso has none).</item>
/// <item>The active tool is <see cref="ToolsViewModel.ActiveTool"/>: a change made elsewhere is followed (a panel of
/// another tool closes); the pen's back end does not change it (FR-30).</item>
/// <item>Visibility: <see cref="PenDown"/>/<see cref="PenUp"/> (the pen writing or erasing) and
/// <see cref="FingerTapOnEmptyArea"/> drive a <see cref="ToolbarVisibilityState"/>; its timed changes are awaited
/// through the injected scheduler, whose time must be the injected clock's. When the writing retracts the toolbar,
/// the open panel closes, and nothing reopens it automatically.</item>
/// </list>
/// </summary>
public sealed class ToolbarViewModel : ObservableObject
{
    private readonly IDelayScheduler _scheduler;
    private OptionsPanel _openPanel;
    private ToolbarPhase _phase;
    private IDisposable? _pendingChange;

    /// <param name="tools">The tools and their settings.</param>
    /// <param name="undo">The document's undo command.</param>
    /// <param name="redo">The document's redo command.</param>
    /// <param name="currentEditor">The current page's editor, for "Clear all on this page"; null when none.</param>
    /// <param name="scheduler">Waits for the timed visibility changes.</param>
    /// <param name="clock">The time, in the scheduler's terms.</param>
    public ToolbarViewModel(
        ToolsViewModel tools,
        ICommand undo,
        ICommand redo,
        Func<PageEditor?> currentEditor,
        IDelayScheduler scheduler,
        Func<TimeSpan> clock)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(redo);
        ArgumentNullException.ThrowIfNull(currentEditor);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(clock);
        Tools = tools;
        _scheduler = scheduler;
        Visibility = new ToolbarVisibilityState(clock);
        _phase = Visibility.Phase;
        Pen = new PenOptionsViewModel(tools);
        Eraser = new EraserOptionsViewModel(tools, currentEditor);
        TapToolCommand = new RelayCommand<ToolbarTool>(TapTool, IsEnabled);
        ClosePanelCommand = new RelayCommand(ClosePanel);
        var disabled = new RelayCommand(static () => { }, static () => false);
        Buttons =
        [
            new(ToolbarButtonKind.Pen, Strings.ToolPen, new RelayCommand(() => TapTool(ToolbarTool.Pen)), IsTool: true),
            new(ToolbarButtonKind.Highlighter, Strings.ToolHighlighter, new RelayCommand(() => TapTool(ToolbarTool.Highlighter)), IsTool: true),
            new(ToolbarButtonKind.Eraser, Strings.ToolEraser, new RelayCommand(() => TapTool(ToolbarTool.Eraser)), IsTool: true),
            new(ToolbarButtonKind.Selection, Strings.ToolSelection, new RelayCommand(() => TapTool(ToolbarTool.Selection)), IsTool: true),
            new(ToolbarButtonKind.Undo, Strings.Undo, undo, IsTool: false),
            new(ToolbarButtonKind.Redo, Strings.Redo, redo, IsTool: false),
            new(ToolbarButtonKind.Bookmark, Strings.ToolBookmark, disabled, IsTool: false),
        ];
        tools.PropertyChanged += OnToolsChanged;
    }

    public ToolsViewModel Tools { get; }

    /// <summary>The seven buttons, left to right.</summary>
    public IReadOnlyList<ToolbarButton> Buttons { get; }

    public PenOptionsViewModel Pen { get; }

    public HighlighterOptionsViewModel Highlighter => Tools.Highlighter;

    public EraserOptionsViewModel Eraser { get; }

    /// <summary>The automatic visibility (read by the view for the retract fade).</summary>
    public ToolbarVisibilityState Visibility { get; }

    /// <summary>Taps a tool button: activates an inactive tool, toggles the panel of the active one.</summary>
    public IRelayCommand<ToolbarTool> TapToolCommand { get; }

    /// <summary>Closes the open panel (Esc, a tap outside, a pen press on it).</summary>
    public IRelayCommand ClosePanelCommand { get; }

    /// <summary>The tool new pen strokes use, as the toolbar shows it (the back end's eraser aside).</summary>
    public ToolbarTool ActiveTool => Tools.ActiveTool switch
    {
        AppTool.Highlighter => ToolbarTool.Highlighter,
        AppTool.Eraser => ToolbarTool.Eraser,
        AppTool.Selection => ToolbarTool.Selection,
        _ => ToolbarTool.Pen,
    };

    public OptionsPanel OpenPanel
    {
        get => _openPanel;
        private set => SetProperty(ref _openPanel, value);
    }

    /// <summary>Visible, retracting or hidden; raised when it changes.</summary>
    public ToolbarPhase Phase
    {
        get => _phase;
        private set => SetProperty(ref _phase, value);
    }

    private ToolbarEdge _edge = ToolbarEdge.Bottom;

    /// <summary>The window edge the toolbar sits on (UI-02, FR-110): the settings set it, and a drag by the grip does.</summary>
    public ToolbarEdge Edge
    {
        get => _edge;
        set
        {
            if (_edge != value && Enum.IsDefined(value))
            {
                _edge = value;
                OnPropertyChanged();
                ClosePanel(); // an open flyout belongs to the old place
            }
        }
    }

    /// <summary>"Toolbar fixed" (section 4): always visible.</summary>
    public bool IsToolbarFixed
    {
        get => Visibility.IsFixed;
        set
        {
            if (Visibility.IsFixed != value)
            {
                Visibility.IsFixed = value;
                OnPropertyChanged();
                Update();
            }
        }
    }

    /// <summary>Windows animations off (NFR-32): the retract is instant.</summary>
    public bool ReducedMotion
    {
        get => Visibility.ReducedMotion;
        set
        {
            if (Visibility.ReducedMotion != value)
            {
                Visibility.ReducedMotion = value;
                OnPropertyChanged();
                Update();
            }
        }
    }

    public void TapTool(ToolbarTool tool)
    {
        if (!IsEnabled(tool))
        {
            return;
        }

        if (tool != ActiveTool)
        {
            OpenPanel = OptionsPanel.None;
            Tools.ActiveTool = tool switch
            {
                ToolbarTool.Highlighter => AppTool.Highlighter,
                ToolbarTool.Eraser => AppTool.Eraser,
                ToolbarTool.Selection => AppTool.Selection,
                _ => AppTool.Pen,
            };
            return;
        }

        var panel = PanelOf(tool);
        OpenPanel = OpenPanel == panel ? OptionsPanel.None : panel;
    }

    public void ClosePanel()
    {
        Eraser.CancelClearPageCommand.Execute(null);
        OpenPanel = OptionsPanel.None;
    }

    /// <summary>The pen started writing or erasing on the page.</summary>
    public void PenDown()
    {
        Visibility.PenDown();
        Update();
    }

    /// <summary>The pen's stroke or eraser gesture ended.</summary>
    public void PenUp()
    {
        Visibility.PenUp();
        Update();
    }

    /// <summary>A finger tap on an empty area of the document: the panels close and the toolbar shows or hides.</summary>
    public void FingerTapOnEmptyArea()
    {
        ClosePanel();
        Visibility.FingerTap();
        Update();
    }

    private static bool IsEnabled(ToolbarTool tool) => Enum.IsDefined(tool);

    private static OptionsPanel PanelOf(ToolbarTool tool) => tool switch
    {
        ToolbarTool.Highlighter => OptionsPanel.Highlighter,
        ToolbarTool.Eraser => OptionsPanel.Eraser,
        ToolbarTool.Selection => OptionsPanel.None, // the lasso has no option panel; the action bar is its UI
        _ => OptionsPanel.Pen,
    };

    /// <summary>Publishes the state now and waits for its next timed change.</summary>
    private void Update()
    {
        _pendingChange?.Dispose();
        _pendingChange = null;
        if (Visibility.IsRetractedByWriting && OpenPanel != OptionsPanel.None)
        {
            ClosePanel();
        }

        Phase = Visibility.Phase;
        if (Visibility.TimeToNextChange is { } wait)
        {
            _pendingChange = _scheduler.Schedule(wait, () =>
            {
                _pendingChange = null;
                Update();
            });
        }
    }

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolsViewModel.ActiveTool))
        {
            return;
        }

        OnPropertyChanged(nameof(ActiveTool));
        if (OpenPanel != OptionsPanel.None && OpenPanel != PanelOf(ActiveTool))
        {
            OpenPanel = OptionsPanel.None;
        }
    }
}
