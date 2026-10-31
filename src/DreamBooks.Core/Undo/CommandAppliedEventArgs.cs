namespace DreamBooks.Core.Undo;

/// <summary>Raised by <see cref="UndoManager"/> after a command was executed, undone or redone.</summary>
public sealed class CommandAppliedEventArgs(CommandAction action, IUndoableCommand command) : EventArgs
{
    public CommandAction Action { get; } = action;

    public IUndoableCommand Command { get; } = command;
}
