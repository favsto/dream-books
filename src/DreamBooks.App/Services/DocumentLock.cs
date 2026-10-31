using System.IO;
using DreamBooks.Pdf;

namespace DreamBooks.App.Services;

/// <summary>
/// Single writer per document (FR-84): while a document is open, this process holds
/// <c>&lt;recovery root&gt;\&lt;DocumentKey&gt;\lock</c> open without sharing, so a second process opening the same
/// document is refused and never touches its journal. Different documents lock different files.
/// </summary>
/// <remarks>
/// A lock file rather than a named mutex: a mutex is owned by a thread and must be released on that thread,
/// which async continuations do not guarantee; a file handle has no thread affinity. The OS closes the handle
/// when the process dies (crash, kill), so a stale lock never survives its owner; the file is opened
/// delete-on-close, so a normal release removes it as well.
/// </remarks>
public sealed class DocumentLock : IDisposable
{
    internal const string LockFileName = "lock";

    private const int _sharingViolation = 32;
    private const int _lockViolation = 33;
    private const int _attempts = 3;

    private readonly string _folder;
    private FileStream? _stream;

    private DocumentLock(FileStream stream, string folder)
    {
        _stream = stream;
        _folder = folder;
    }

    /// <summary>
    /// Takes the document's lock, or returns null when another holder (another process) has it.
    /// Other I/O failures (e.g. the recovery folder cannot be written) throw.
    /// </summary>
    public static DocumentLock? TryAcquire(string recoveryRoot, string documentPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(recoveryRoot);
        var folder = Path.Combine(recoveryRoot, RecoveryStore.DocumentKey(documentPath));
        var file = Path.Combine(folder, LockFileName);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var stream = new FileStream(
                    file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
                return new DocumentLock(stream, folder);
            }
            catch (DirectoryNotFoundException) when (attempt < _attempts)
            {
                // The journal's folder was removed between creating it and opening the lock: try again.
            }
            catch (IOException ex) when (IsHeldElsewhere(ex))
            {
                return null;
            }
            catch (UnauthorizedAccessException) when (attempt < _attempts)
            {
                // A lock file whose holder is just closing it is "delete pending" for a moment.
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException) when (File.Exists(file))
            {
                return null;
            }
        }
    }

    /// <summary>Releases the lock (the file goes away with the handle). Idempotent.</summary>
    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }

        stream.Dispose();
        try
        {
            if (!Directory.EnumerateFileSystemEntries(_folder).Any())
            {
                Directory.Delete(_folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: an empty folder is harmless.
        }
    }

    private static bool IsHeldElsewhere(IOException ex) =>
        (ex.HResult & 0xFFFF) is _sharingViolation or _lockViolation;
}
