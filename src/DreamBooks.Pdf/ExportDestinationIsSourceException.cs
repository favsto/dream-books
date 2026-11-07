namespace DreamBooks.Pdf;

/// <summary>An export was asked to write over the file it reads (the same file under another spelling). Nothing was written; the message holds no path.</summary>
public sealed class ExportDestinationIsSourceException : IOException
{
    public ExportDestinationIsSourceException()
        : base("The export destination is the source file.")
    {
    }
}
