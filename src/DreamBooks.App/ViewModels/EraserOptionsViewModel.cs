using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Settings;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The eraser's option panel (UI-02, FR-31, FR-32): whole stroke or part of the stroke, the size, and "Clear all on
/// this page". The mode is <see cref="ToolsViewModel.EraserMode"/>, the one mode used by the eraser tool and by the
/// pen's back end alike. Clearing takes two steps: <see cref="ClearPageCommand"/> asks
/// (<see cref="IsConfirmingClear"/>), <see cref="ConfirmClearPageCommand"/> clears the page that was current when
/// asked, as one undoable step (<see cref="PageEditor.ClearPage"/>), and only if it is still current.
/// </summary>
public sealed class EraserOptionsViewModel : ObservableObject
{
    private readonly ToolsViewModel _tools;
    private readonly Func<PageEditor?> _currentEditor;
    private PageEditor? _confirming;

    /// <param name="tools">The tools and their settings.</param>
    /// <param name="currentEditor">The current page's editor; null when there is none.</param>
    public EraserOptionsViewModel(ToolsViewModel tools, Func<PageEditor?> currentEditor)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(currentEditor);
        _tools = tools;
        _currentEditor = currentEditor;
        ClearPageCommand = new RelayCommand(RequestClear, CanClear);
        ConfirmClearPageCommand = new RelayCommand(ConfirmClear, () => IsConfirmingClear);
        CancelClearPageCommand = new RelayCommand(() => SetConfirming(null));
        tools.PropertyChanged += OnToolsChanged;
    }

    public double MinSizeMm => AppSettings.MinEraserSizeMm;

    public double MaxSizeMm => AppSettings.MaxEraserSizeMm;

    public EraserMode Mode
    {
        get => _tools.EraserMode;
        set => _tools.EraserMode = value;
    }

    public bool IsWholeStroke
    {
        get => Mode == EraserMode.WholeStroke;
        set
        {
            if (value)
            {
                Mode = EraserMode.WholeStroke;
            }
        }
    }

    public bool IsPartial
    {
        get => Mode == EraserMode.Partial;
        set
        {
            if (value)
            {
                Mode = EraserMode.Partial;
            }
        }
    }

    /// <summary>The eraser circle's diameter, clamped to 2..20 mm (FR-32).</summary>
    public double SizeMm
    {
        get => _tools.EraserSizeMm;
        set => _tools.EraserSizeMm = value;
    }

    /// <summary>True between the request to clear the page and its confirmation or cancellation.</summary>
    public bool IsConfirmingClear => _confirming is not null;

    /// <summary>Asks to clear the current page; enabled while it has strokes.</summary>
    public IRelayCommand ClearPageCommand { get; }

    /// <summary>Clears the page asked about (one undo step).</summary>
    public IRelayCommand ConfirmClearPageCommand { get; }

    public IRelayCommand CancelClearPageCommand { get; }

    /// <summary>The current page or its strokes changed (page change, undo, redo, a new stroke): re-reads the commands' state.</summary>
    public void Refresh() => ClearPageCommand.NotifyCanExecuteChanged();

    private bool CanClear() => _currentEditor() is { Page.Strokes.Count: > 0 };

    private void RequestClear()
    {
        if (_currentEditor() is { Page.Strokes.Count: > 0 } editor)
        {
            SetConfirming(editor);
        }
    }

    private void ConfirmClear()
    {
        var editor = _confirming;
        SetConfirming(null);
        if (editor is not null && ReferenceEquals(editor, _currentEditor()))
        {
            editor.ClearPage();
        }

        Refresh();
    }

    private void SetConfirming(PageEditor? editor)
    {
        if (ReferenceEquals(_confirming, editor))
        {
            return;
        }

        _confirming = editor;
        OnPropertyChanged(nameof(IsConfirmingClear));
        ConfirmClearPageCommand.NotifyCanExecuteChanged();
    }

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ToolsViewModel.EraserMode):
                OnPropertyChanged(nameof(Mode));
                OnPropertyChanged(nameof(IsWholeStroke));
                OnPropertyChanged(nameof(IsPartial));
                break;
            case nameof(ToolsViewModel.EraserSizeMm):
                OnPropertyChanged(nameof(SizeMm));
                break;
        }
    }
}
