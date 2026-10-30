namespace DreamBooks.Pdf;

/// <summary>Why a PDF could not be opened (NFR-11).</summary>
public enum PdfOpenError
{
    FileNotFound,
    InvalidFormat,
    PasswordRequired,
    UnsupportedSecurity,
    Unknown,
}
