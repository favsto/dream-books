using DreamBooks.App.Services;

namespace DreamBooks.App.Tests;

/// <summary>Scripted answers for every dialog, and a record of what was asked or shown.</summary>
internal sealed class FakeFileDialogService : IFileDialogService
{
    public string? NextPath { get; set; }

    /// <summary>What <see cref="PickSavePdf"/> returns; null means the user cancelled.</summary>
    public string? NextSavePath { get; set; }

    /// <summary>What <see cref="PickSavePng"/> returns; null means the user cancelled.</summary>
    public string? NextPngPath { get; set; }

    public List<string> SuggestedPngNames { get; } = [];

    public SaveChangesAnswer SaveChangesAnswer { get; set; } = SaveChangesAnswer.Cancel;

    public FileChangedAnswer FileChangedAnswer { get; set; } = FileChangedAnswer.Cancel;

    public bool ConfirmSignature { get; set; }

    public List<string> SuggestedNames { get; } = [];

    public int SaveChangesAsks { get; private set; }

    public int FileChangedAsks { get; private set; }

    public int SignatureAsks { get; private set; }

    public List<string> Messages { get; } = [];

    public string? PickPdf() => NextPath;

    public string? PickSavePng(string suggestedFileName)
    {
        SuggestedPngNames.Add(suggestedFileName);
        return NextPngPath;
    }

    public string? PickSavePdf(string suggestedFileName)
    {
        SuggestedNames.Add(suggestedFileName);
        return NextSavePath;
    }

    public SaveChangesAnswer AskSaveChanges()
    {
        SaveChangesAsks++;
        return SaveChangesAnswer;
    }

    public FileChangedAnswer AskFileChanged()
    {
        FileChangedAsks++;
        return FileChangedAnswer;
    }

    public bool ConfirmSignatureInvalidation()
    {
        SignatureAsks++;
        return ConfirmSignature;
    }

    public JournalMismatchAnswer MismatchAnswer { get; set; } = JournalMismatchAnswer.Cancel;

    public int MismatchAsks { get; private set; }

    public bool DiscardOrphan { get; set; }

    public List<string> OrphanAsks { get; } = [];

    public JournalMismatchAnswer AskJournalMismatch()
    {
        MismatchAsks++;
        return MismatchAnswer;
    }

    public bool AskDiscardOrphanJournal(string documentPath)
    {
        OrphanAsks.Add(documentPath);
        return DiscardOrphan;
    }

    /// <summary>When set, <see cref="ShowMessage"/> records the text and then throws it.</summary>
    public Exception? ShowMessageFailure { get; set; }

    /// <summary>Runs inside <see cref="ShowMessage"/>, as if the modal popup were open.</summary>
    public Action<string>? WhileShowingMessage { get; set; }

    public void ShowMessage(string text)
    {
        Messages.Add(text);
        WhileShowingMessage?.Invoke(text);
        if (ShowMessageFailure is { } failure)
        {
            throw failure;
        }
    }
}
