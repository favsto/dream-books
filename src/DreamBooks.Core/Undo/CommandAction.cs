namespace DreamBooks.Core.Undo;

/// <summary>What <see cref="UndoManager"/> just did with a command.</summary>
public enum CommandAction
{
    Executed,
    Undone,
    Redone,
}
