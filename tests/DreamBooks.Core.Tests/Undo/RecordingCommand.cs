using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Tests.Undo;

internal sealed class RecordingCommand(string name, List<string> log) : IUndoableCommand
{
    public string Description => name;

    public void Do() => log.Add($"do{name}");

    public void Undo() => log.Add($"undo{name}");
}
