using DreamBooks.Core.Journal;

namespace DreamBooks.Core.Tests.Journal;

/// <summary>Collects appended lines in memory; can be told to throw instead.</summary>
internal sealed class ListSink : IJournalSink
{
    public List<string> Lines { get; } = [];

    /// <summary>When set, every append throws it and records nothing.</summary>
    public Exception? Failure { get; set; }

    public void Append(string line)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        Lines.Add(line);
    }
}
