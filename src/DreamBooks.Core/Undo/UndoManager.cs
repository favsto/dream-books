namespace DreamBooks.Core.Undo;

public sealed class UndoManager
{
    private readonly int _capacity;
    private readonly LinkedList<IUndoableCommand> _undo = new();
    private readonly Stack<IUndoableCommand> _redo = new();

    public UndoManager(int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        _capacity = capacity;
    }

    public event EventHandler? StateChanged;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Execute(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Do();
        _undo.AddLast(command);
        if (_undo.Count > _capacity)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undo.Last is not { } node)
        {
            return false;
        }

        node.Value.Undo();
        _undo.RemoveLast();
        _redo.Push(node.Value);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var command))
        {
            return false;
        }

        command.Do();
        _undo.AddLast(command);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0)
        {
            return;
        }

        _undo.Clear();
        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
