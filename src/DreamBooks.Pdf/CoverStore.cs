namespace DreamBooks.Pdf;

/// <summary>
/// The small page-1 pictures of the recent documents (FR-03): <c>&lt;root&gt;\covers\&lt;DocumentKey&gt;.png</c>, kept
/// under a byte budget (the oldest files go first). Cover files hold only a picture, no path. Nothing here throws: an
/// unusable folder reads as "no cover" and writes are skipped.
/// </summary>
public sealed class CoverStore
{
    public const long DefaultMaxBytes = 8L * 1024 * 1024;

    private const string _folderName = "covers";
    private const string _extension = ".png";

    private readonly string _folder;
    private readonly long _maxBytes;

    public CoverStore(string rootDirectory, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _folder = Path.Combine(rootDirectory, _folderName);
        _maxBytes = maxBytes;
    }

    /// <summary>The cover's PNG bytes, or null when there is none (or it cannot be read).</summary>
    public byte[]? TryLoad(string documentPath)
    {
        try
        {
            var file = FileFor(documentPath);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return null;
        }
    }

    /// <summary>Stores a cover, then deletes the oldest covers while the folder is over the byte budget.</summary>
    public void Save(string documentPath, byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(FileFor(documentPath), png);
            Trim();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
        }
    }

    public void Delete(string documentPath)
    {
        try
        {
            File.Delete(FileFor(documentPath));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
        }
    }

    public void Clear()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder, "*" + _extension))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private string FileFor(string documentPath) => Path.Combine(_folder, RecoveryStore.DocumentKey(documentPath) + _extension);

    private void Trim()
    {
        var files = new DirectoryInfo(_folder).EnumerateFiles("*" + _extension)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total > _maxBytes)
            {
                file.Delete();
            }
        }
    }
}
