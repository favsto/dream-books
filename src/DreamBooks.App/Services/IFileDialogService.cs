namespace DreamBooks.App.Services;

public interface IFileDialogService
{
    /// <summary>Asks the user for a PDF; null if cancelled.</summary>
    string? PickPdf();

    /// <summary>Asks a yes/no question; true means yes.</summary>
    bool Confirm(string message);
}
