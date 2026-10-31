using System.Security.Cryptography;
using System.Text;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Journal;

namespace DreamBooks.Pdf;

/// <summary>
/// Journals of unsaved changes, kept outside the user's folders: <c>&lt;root&gt;\&lt;DocumentKey&gt;\journal.jsonl</c>.
/// </summary>
public sealed class RecoveryStore
{
    internal const string JournalFileName = "journal.jsonl";

    private readonly string _root;

    public RecoveryStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        _root = rootDirectory;
    }

    /// <summary>The folder holding one sub-folder per document key.</summary>
    public string RootDirectory => _root;

    /// <summary>Lowercase hex of the first 16 bytes of SHA-256 over the normalised, upper-cased full path.</summary>
    public static string DocumentKey(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        var normalised = Path.GetFullPath(fullPath).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Opens the document's journal for appending, creating it with a header line if missing. An existing
    /// journal (restore after a crash) keeps its header and is appended to.
    /// </summary>
    public IJournalSession Begin(string documentPath, DocumentFingerprint fingerprint)
    {
        var fullPath = Path.GetFullPath(documentPath);
        var file = JournalPath(fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var header = new JournalHeader(JournalCodec.CurrentFormatVersion, fullPath, fingerprint);
        return new JournalSession(file, JournalCodec.SerializeHeader(header));
    }

    /// <summary>All journals, most recently written first. Damaged files are returned with a null header.</summary>
    public IReadOnlyList<RecoveredJournal> FindAll()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        List<string> folders;
        try
        {
            folders = Directory.GetDirectories(_root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var result = new List<RecoveredJournal>();
        foreach (var folder in folders)
        {
            var found = Read(Path.Combine(folder, JournalFileName));
            if (found is not null)
            {
                result.Add(found);
            }
        }

        return result.OrderByDescending(j => j.LastWriteUtc).ToList();
    }

    public RecoveredJournal? Find(string documentPath) => Read(JournalPath(Path.GetFullPath(documentPath)));

    /// <summary>Removes the document's journal and its folder. Idempotent.</summary>
    public void Delete(string documentPath) => JournalSession.DeleteFiles(JournalPath(Path.GetFullPath(documentPath)), purgeDamaged: true);

    private string JournalPath(string fullPath) => Path.Combine(_root, DocumentKey(fullPath), JournalFileName);

    private static RecoveredJournal? Read(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var lastWrite = File.GetLastWriteTimeUtc(file);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return new RecoveredJournal(file, JournalCodec.Parse(lines), lastWrite);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RecoveredJournal(file, new JournalContents(null, [], false, 0), File.GetLastWriteTimeUtc(file), Unreadable: true);
        }
    }
}
