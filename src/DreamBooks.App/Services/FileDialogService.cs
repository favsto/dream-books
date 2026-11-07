using System.Globalization;
using System.IO;
using DreamBooks.App.Resources;
using Microsoft.Win32;

namespace DreamBooks.App.Services;

/// <summary>Standard Windows file dialogs and small Italian choice dialogs (<see cref="ChoiceDialog"/>).</summary>
public sealed class FileDialogService : IFileDialogService
{
    /// <summary>Unsaved changes: Enter saves (FR-85).</summary>
    internal static ChoiceQuestion SaveChangesQuestion =>
        new(Strings.AskSaveChanges, [Strings.AnswerSave, Strings.AnswerDontSave, Strings.AnswerCancel], DefaultIndex: 0, CancelIndex: 2);

    /// <summary>The file changed on disk: Enter cancels, never overwrites.</summary>
    internal static ChoiceQuestion FileChangedQuestion =>
        new(Strings.AskFileChanged, [Strings.SaveAs, Strings.AnswerOverwriteAnyway, Strings.AnswerCancel], DefaultIndex: 2, CancelIndex: 2);

    /// <summary>Saving invalidates the signatures: Enter cancels, never invalidates.</summary>
    internal static ChoiceQuestion SignatureQuestion =>
        new(Strings.WarnSignatureInvalidation, [Strings.AnswerSaveAnyway, Strings.AnswerCancel], DefaultIndex: 1, CancelIndex: 1);

    /// <summary>The journal does not match the file: Enter cancels (keeps the journal, applies nothing).</summary>
    internal static ChoiceQuestion JournalMismatchQuestion =>
        new(Strings.AskJournalMismatch, [Strings.AnswerApplyAnyway, Strings.AnswerDiscard, Strings.AnswerCancel], DefaultIndex: 2, CancelIndex: 2);

    /// <summary>The journal's file is missing: Enter keeps the journal, never discards it.</summary>
    internal static ChoiceQuestion OrphanJournalQuestion(string documentPath) =>
        new(
            string.Format(CultureInfo.CurrentCulture, Strings.AskDiscardOrphanJournal, documentPath),
            [Strings.AnswerDiscard, Strings.AnswerKeep],
            DefaultIndex: 1,
            CancelIndex: 1);

    public string? PickPdf()
    {
        var dialog = new OpenFileDialog
        {
            Filter = Strings.PdfFileFilter,
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSavePng(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = Strings.PngFileFilter,
            FileName = Path.GetFileName(suggestedFileName),
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSavePdf(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = Strings.PdfFileFilter,
            FileName = Path.GetFileName(suggestedFileName),
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public SaveChangesAnswer AskSaveChanges() => ChoiceDialog.Ask(SaveChangesQuestion) switch
    {
        0 => SaveChangesAnswer.Save,
        1 => SaveChangesAnswer.DontSave,
        _ => SaveChangesAnswer.Cancel,
    };

    public FileChangedAnswer AskFileChanged() => ChoiceDialog.Ask(FileChangedQuestion) switch
    {
        0 => FileChangedAnswer.SaveAs,
        1 => FileChangedAnswer.OverwriteAnyway,
        _ => FileChangedAnswer.Cancel,
    };

    public bool ConfirmSignatureInvalidation() => ChoiceDialog.Ask(SignatureQuestion) == 0;

    public JournalMismatchAnswer AskJournalMismatch() => ChoiceDialog.Ask(JournalMismatchQuestion) switch
    {
        0 => JournalMismatchAnswer.ApplyAnyway,
        1 => JournalMismatchAnswer.Discard,
        _ => JournalMismatchAnswer.Cancel,
    };

    public bool AskDiscardOrphanJournal(string documentPath) => ChoiceDialog.Ask(OrphanJournalQuestion(documentPath)) == 0;

    public void ShowMessage(string text) =>
        ChoiceDialog.Ask(new ChoiceQuestion(text, [Strings.AnswerOk], DefaultIndex: 0, CancelIndex: 0));
}
