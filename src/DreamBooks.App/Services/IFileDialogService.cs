namespace DreamBooks.App.Services;

/// <summary>The dialogs the view models need, kept behind an interface so they can be scripted in tests.</summary>
public interface IFileDialogService
{
    /// <summary>Asks the user for a PDF; null if cancelled.</summary>
    string? PickPdf();

    /// <summary>Asks where to save a PDF (Save as), proposing <paramref name="suggestedFileName"/>; null if cancelled.</summary>
    string? PickSavePdf(string suggestedFileName);

    /// <summary>Asks where to save a page image (PNG); null when the user cancelled.</summary>
    string? PickSavePng(string suggestedFileName);

    /// <summary>The document has unsaved changes: save them, drop them, or stay (FR-85).</summary>
    SaveChangesAnswer AskSaveChanges();

    /// <summary>The file changed on disk since it was opened: save elsewhere, overwrite, or stop.</summary>
    FileChangedAnswer AskFileChanged();

    /// <summary>Saving invalidates the document's digital signatures: true to save anyway.</summary>
    bool ConfirmSignatureInvalidation();

    /// <summary>
    /// The journal of unsaved changes was written for a different version of the file: apply it anyway, discard
    /// it, or cancel (keep it, open without it). Cancel is the safe default.
    /// </summary>
    JournalMismatchAnswer AskJournalMismatch();

    /// <summary>
    /// Unsaved changes exist for <paramref name="documentPath"/>, which is no longer there: true to discard them,
    /// false (the default) to keep them.
    /// </summary>
    bool AskDiscardOrphanJournal(string documentPath);

    /// <summary>Shows a message the user must acknowledge.</summary>
    void ShowMessage(string text);
}
