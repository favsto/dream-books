namespace DreamBooks.Core.Journal;

/// <summary>Raised when the journal could not be written; the exception never carries document content.</summary>
public sealed class JournalFaultedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
