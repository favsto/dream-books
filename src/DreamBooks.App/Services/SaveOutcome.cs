namespace DreamBooks.App.Services;

/// <summary>How a save request ended.</summary>
public enum SaveOutcome
{
    /// <summary>The file on disk now holds the snapshot's changes.</summary>
    Saved,

    /// <summary>The user declined a question or a dialog, or the save was cancelled; nothing was written.</summary>
    Cancelled,

    /// <summary>The document may not be rewritten (encrypted or restricted); nothing was written.</summary>
    Refused,

    /// <summary>Something went wrong; the original (or the Save as target) is untouched.</summary>
    Failed,
}
