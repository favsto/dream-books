namespace DreamBooks.App.Services;

/// <summary>The answer to "the unsaved changes were made on a file that changed since" (FR-86).</summary>
public enum JournalMismatchAnswer
{
    ApplyAnyway,
    Discard,
    Cancel,
}
