using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Tests.Undo;

internal sealed class ThrowingCommand : IUndoableCommand
{
    public bool ThrowOnDo { get; set; }

    public bool ThrowOnUndo { get; set; }

    public int DoCount { get; private set; }

    public string Description => "throwing";

    public void Do()
    {
        DoCount++;
        if (ThrowOnDo)
        {
            throw new InvalidOperationException("do failed");
        }
    }

    public void Undo()
    {
        if (ThrowOnUndo)
        {
            throw new InvalidOperationException("undo failed");
        }
    }
}
