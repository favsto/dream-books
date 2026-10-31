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

    /// <summary>
    /// Raised after a command was executed, undone or redone, before <see cref="StateChanged"/>.
    /// Handlers must not throw; if one does, the exception propagates to the caller of
    /// Execute/Undo/Redo after <see cref="StateChanged"/> has still been raised.
    /// </summary>
    public event EventHandler<CommandAppliedEventArgs>? CommandApplied;

    public event EventHandler? StateChanged;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>The command the next <see cref="Undo"/> would revert, or null.</summary>
    public IUndoableCommand? PeekUndo() => _undo.Last?.Value;

    /// <summary>The command the next <see cref="Redo"/> would reapply, or null.</summary>
    public IUndoableCommand? PeekRedo() => _redo.TryPeek(out var command) ? command : null;

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
        Applied(CommandAction.Executed, command);
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
        Applied(CommandAction.Undone, node.Value);
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPeek(out var command))
        {
            return false;
        }

        command.Do();
        _redo.Pop();
        _undo.AddLast(command);
        Applied(CommandAction.Redone, command);
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

    private void Applied(CommandAction action, IUndoableCommand command)
    {
        try
        {
            CommandApplied?.Invoke(this, new CommandAppliedEventArgs(action, command));
        }
        finally
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
