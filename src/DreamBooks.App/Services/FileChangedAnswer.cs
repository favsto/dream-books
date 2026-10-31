namespace DreamBooks.App.Services;

/// <summary>The answer to "the file changed on disk since it was opened".</summary>
public enum FileChangedAnswer
{
    SaveAs,
    OverwriteAnyway,
    Cancel,
}
