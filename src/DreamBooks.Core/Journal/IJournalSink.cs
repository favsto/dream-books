namespace DreamBooks.Core.Journal;

/// <summary>Where journal lines go. Called synchronously on the command path; a line never contains a newline.</summary>
public interface IJournalSink
{
    void Append(string line);
}
