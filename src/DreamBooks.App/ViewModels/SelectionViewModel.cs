using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The lasso selection on one page (FR-50, FR-51). A drag or a resize only changes <see cref="Preview"/>; the page
/// is touched once, at <see cref="Commit"/>, as a single undo step. The selection clears itself when anything else
/// changes the page's strokes (undo, erase, another tool) and when the current page is no longer the one selected on.
/// </summary>
public sealed class SelectionViewModel : ObservableObject
{
    private readonly Func<PageEditor?> _currentEditor;
    private readonly StrokeClipboard _clipboard;
    private PageEditor? _editor;
    private SelectionState _state;
    private IReadOnlyList<InkStroke> _selected = [];
    private IReadOnlyList<InkStroke> _preview = [];
    private double _dx;
    private double _dy;
    private double _factor = 1;
    private PointD _anchor;
    private bool _applying;

    public SelectionViewModel(Func<PageEditor?> currentEditor, StrokeClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(currentEditor);
        ArgumentNullException.ThrowIfNull(clipboard);
        _currentEditor = currentEditor;
        _clipboard = clipboard;
        RecolorCommand = new RelayCommand<InkColor>(Recolor, _ => HasSelection);
        CopyCommand = new RelayCommand(Copy, () => HasSelection);
        DeleteCommand = new RelayCommand(Delete, () => HasSelection);
        PasteCommand = new RelayCommand(Paste, () => CanPaste);
        _clipboard.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(CanPaste));
            PasteCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Gives the selection a colour (FR-51); the parameter is the <see cref="InkColor"/>.</summary>
    public IRelayCommand<InkColor> RecolorCommand { get; }

    public IRelayCommand CopyCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    public IRelayCommand PasteCommand { get; }

    public SelectionState State => _state;

    public bool HasSelection => _state != SelectionState.None;

    /// <summary>The selected strokes as they are on the page.</summary>
    public IReadOnlyList<InkStroke> Selected => _selected;

    /// <summary>What to draw for the selection: the strokes as they would be after the drag in progress.</summary>
    public IReadOnlyList<InkStroke> Preview => _preview;

    /// <summary>The bounds of <see cref="Preview"/>, or null when nothing is selected.</summary>
    public RectD? Bounds => SelectionGeometry.Bounds(_preview);

    public bool CanPaste => !_clipboard.IsEmpty;

    /// <summary>Selects the strokes the loop encloses; a loop that encloses none clears the selection.</summary>
    public void SelectLoop(IReadOnlyList<PointD> loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        var editor = _currentEditor();
        if (editor is null)
        {
            Clear();
            return;
        }

        var hit = SelectionGeometry.StrokesInLoop(editor.Page.Strokes, loop);
        if (hit.Count == 0)
        {
            Clear();
            return;
        }

        SetSelection(editor, hit);
    }

    public void BeginMove()
    {
        if (_state is SelectionState.Selected)
        {
            _dx = 0;
            _dy = 0;
            SetState(SelectionState.Moving);
        }
    }

    /// <summary>Sets the drag offset from where the drag began, in page points.</summary>
    public void MoveBy(double dx, double dy)
    {
        if (_state is not SelectionState.Moving || _editor is null)
        {
            return;
        }

        _dx = dx;
        _dy = dy;
        SetPreview(StrokeTransform.Translate(_selected, dx, dy, _editor.Page.WidthPt, _editor.Page.HeightPt));
    }

    public void BeginResize(PointD anchor)
    {
        if (_state is SelectionState.Selected)
        {
            _anchor = anchor;
            _factor = 1;
            SetState(SelectionState.Resizing);
        }
    }

    /// <summary>Sets the resize factor from where the resize began; the page limits and the minimum apply.</summary>
    public void ScaleTo(double factor)
    {
        if (_state is not SelectionState.Resizing || _editor is null)
        {
            return;
        }

        _factor = factor;
        SetPreview(StrokeTransform.Scale(_selected, factor, _anchor, _editor.Page.WidthPt, _editor.Page.HeightPt));
    }

    /// <summary>Applies the drag or resize in progress to the page as one undo step.</summary>
    public void Commit()
    {
        if (_state is not (SelectionState.Moving or SelectionState.Resizing) || _editor is not { } editor || !IsCurrent(editor))
        {
            Clear();
            return;
        }

        var ids = _selected.Select(s => s.Id).ToArray();
        var moving = _state == SelectionState.Moving;
        IReadOnlyList<Guid> result;
        _applying = true;
        try
        {
            result = moving ? editor.MoveStrokes(ids, _dx, _dy) : editor.ScaleStrokes(ids, _factor, _anchor);
        }
        finally
        {
            _applying = false;
        }

        Reselect(editor, result);
    }

    /// <summary>Drops the drag or resize in progress; the page was never touched.</summary>
    public void Cancel()
    {
        if (_state is SelectionState.Moving or SelectionState.Resizing)
        {
            SetPreview(_selected);
            SetState(SelectionState.Selected);
        }
    }

    public void Recolor(InkColor color) => Apply(editor => editor.RecolorStrokes(Ids(), color));

    public void Delete()
    {
        if (!HasSelection || _editor is not { } editor || !IsCurrent(editor))
        {
            Clear();
            return;
        }

        _applying = true;
        try
        {
            editor.DeleteStrokes(Ids());
        }
        finally
        {
            _applying = false;
        }

        Clear();
    }

    public void Copy()
    {
        if (HasSelection)
        {
            _clipboard.Set(_selected);
        }
    }

    /// <summary>Pastes the clipboard on the current page and selects the copies.</summary>
    public void Paste()
    {
        var editor = _currentEditor();
        if (editor is null || _clipboard.IsEmpty)
        {
            return;
        }

        IReadOnlyList<Guid> result;
        _applying = true;
        try
        {
            result = editor.PasteStrokes(_clipboard);
        }
        finally
        {
            _applying = false;
        }

        Reselect(editor, result);
    }

    public void Clear()
    {
        Detach();
        _selected = [];
        _preview = [];
        _dx = 0;
        _dy = 0;
        _factor = 1;
        SetState(SelectionState.None);
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Bounds));
    }

    private void Apply(Func<PageEditor, IReadOnlyList<Guid>> edit)
    {
        if (!HasSelection || _editor is not { } editor || !IsCurrent(editor))
        {
            Clear();
            return;
        }

        IReadOnlyList<Guid> result;
        _applying = true;
        try
        {
            result = edit(editor);
        }
        finally
        {
            _applying = false;
        }

        Reselect(editor, result);
    }

    private IReadOnlyCollection<Guid> Ids() => _selected.Select(s => s.Id).ToArray();

    private bool IsCurrent(PageEditor editor) => ReferenceEquals(_currentEditor(), editor);

    private void Reselect(PageEditor editor, IReadOnlyList<Guid> ids)
    {
        var strokes = editor.Strokes(ids.ToArray());
        if (strokes.Count == 0)
        {
            Clear();
            return;
        }

        SetSelection(editor, strokes);
    }

    private void SetSelection(PageEditor editor, IReadOnlyList<InkStroke> strokes)
    {
        Detach();
        _editor = editor;
        _editor.Page.StrokesChanged += OnStrokesChanged;
        _selected = strokes;
        _preview = strokes;
        _dx = 0;
        _dy = 0;
        _factor = 1;
        SetState(SelectionState.Selected);
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Bounds));
    }

    private void SetPreview(IReadOnlyList<InkStroke> preview)
    {
        _preview = preview;
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Bounds));
    }

    private void SetState(SelectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(HasSelection));
        RecolorCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private void Detach()
    {
        if (_editor is not null)
        {
            _editor.Page.StrokesChanged -= OnStrokesChanged;
            _editor = null;
        }
    }

    private void OnStrokesChanged(object? sender, EventArgs e)
    {
        if (!_applying)
        {
            Clear();
        }
    }
}
