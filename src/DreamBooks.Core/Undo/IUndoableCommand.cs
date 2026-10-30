namespace DreamBooks.Core.Undo;

public interface IUndoableCommand
{
    string Description { get; }

    void Do();

    void Undo();
}
