using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Commands;

/// <summary>A command that changes the strokes of exactly one page.</summary>
public interface IPageCommand : IUndoableCommand
{
    int PageIndex { get; }
}
