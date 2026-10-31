namespace DreamBooks.Pdf;

/// <summary>
/// The written copy did not match what was expected, so it was deleted and nothing was replaced
/// (NFR-10). The message names only the failed check, never document content or paths (NFR-12).
/// </summary>
public sealed class SaveVerificationException : Exception
{
    public SaveVerificationException(string check)
        : base($"The saved copy failed verification ({check}).")
    {
        Check = check;
    }

    public SaveVerificationException(string check, Exception innerException)
        : base($"The saved copy failed verification ({check}).", innerException)
    {
        Check = check;
    }

    /// <summary>Which check failed, e.g. "page count" or "strokes on page 3".</summary>
    public string Check { get; }
}
