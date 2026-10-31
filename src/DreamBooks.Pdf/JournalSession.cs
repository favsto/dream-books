using System.Text;
using DreamBooks.Core.Journal;

namespace DreamBooks.Pdf;

/// <summary>
/// Threading model: <see cref="Append"/> takes only <c>_gate</c> and does one synchronous write and flush to the OS
/// (survives a killed process). The fsync runs on a single coalescing background worker that holds
/// <c>_syncGate</c> (never <c>_gate</c>) while syncing, so Append is never blocked by the disk.
/// Truncate, Delete and Dispose take <c>_syncGate</c> first, then <c>_gate</c>, so the handle is never closed mid-fsync.
/// </summary>
internal sealed class JournalSession : IJournalSession
{
    internal const string DamagedPrefix = "journal.damaged-";

    private static readonly UTF8Encoding _utf8 = new(false);

    private readonly object _gate = new();
    private readonly object _syncGate = new();
    private readonly string _file;
    private FileStream? _stream;
    private bool _closed;
    private bool _faulted;
    private bool _syncRequested;
    private bool _workerRunning;
    private Task _worker = Task.CompletedTask;
    private Exception? _syncFault;

    public JournalSession(string file, string headerLine)
    {
        _file = file;
        _stream = Open(file, headerLine);
    }

    public string FilePath => _file;

    /// <summary>Test hook invoked inside Truncate right after the stream is closed.</summary>
    internal Action? AfterStreamClosedForTest { get; set; }

    /// <summary>Test hook replacing <c>File.Replace(temp, journal, null)</c> inside Truncate.</summary>
    internal Action<string, string>? ReplaceForTest { get; set; }

    public void Append(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.AsSpan().ContainsAny('\n', '\r'))
        {
            throw new ArgumentException("A journal line must not contain a line break.", nameof(line));
        }

        var bytes = _utf8.GetBytes(line + "\n");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            ThrowIfFaulted();
            _stream!.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            _syncRequested = true;
            if (!_workerRunning)
            {
                _workerRunning = true;
                _worker = Task.Run(SyncLoop);
            }
        }
    }

    public void Truncate(JournalHeader newHeader) => Truncate(newHeader, []);

    public void Truncate(JournalHeader newHeader, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var headerLine = JournalCodec.SerializeHeader(newHeader);
        var content = new StringBuilder(headerLine).Append('\n');
        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (line.AsSpan().ContainsAny('\n', '\r'))
            {
                throw new ArgumentException("A journal line must not contain a line break.", nameof(lines));
            }

            content.Append(line).Append('\n');
        }

        var headerBytes = _utf8.GetBytes(content.ToString());
        lock (_syncGate)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                ThrowIfFaulted();
                var temp = _file + ".tmp";
                _stream!.Dispose();
                _stream = null;
                AfterStreamClosedForTest?.Invoke();
                try
                {
                    // File.Replace may fail with error 1176 (ERROR_UNABLE_TO_MOVE_REPLACEMENT) after removing the old
                    // journal: the catch then moves the complete temp into place, so the new content is never lost.
                    using (var tmp = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        tmp.Write(headerBytes, 0, headerBytes.Length);
                        tmp.Flush(flushToDisk: true);
                    }

                    if (File.Exists(_file))
                    {
                        (ReplaceForTest ?? ((t, f) => File.Replace(t, f, null)))(temp, _file);
                    }
                    else
                    {
                        File.Move(temp, _file);
                    }

                    _syncRequested = false;
                }
                catch when (!File.Exists(_file) && File.Exists(temp) && TryMove(temp, _file))
                {
                    // The old journal was removed but the new one not moved in: it is now, so this is a success.
                    _syncRequested = false;
                }
                catch
                {
                    TryDelete(temp);
                    Reopen(headerLine);
                    throw;
                }

                Reopen(headerLine);
            }
        }
    }

    public void Delete()
    {
        lock (_syncGate)
        {
            lock (_gate)
            {
                _closed = true;
                _stream?.Dispose();
                _stream = null;
                DeleteFiles(_file, purgeDamaged: false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        Exception? fault;
        lock (_syncGate)
        {
            lock (_gate)
            {
                worker = _worker;
                if (!_closed)
                {
                    _closed = true;
                    try
                    {
                        _stream?.Flush(flushToDisk: true);
                    }
                    finally
                    {
                        _stream?.Dispose();
                        _stream = null;
                    }
                }

                fault = _syncFault;
                _syncFault = null;
            }
        }

        await worker.ConfigureAwait(false);
        if (fault is not null)
        {
            throw new IOException("Flushing the recovery journal to disk failed.", fault);
        }
    }

    /// <summary>Deletes the journal (retrying; failure throws), then leftovers and the folder (best effort).</summary>
    internal static void DeleteFiles(string file, bool purgeDamaged)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(file);
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 2)
                {
                    throw new IOException("The recovery journal could not be deleted.");
                }

                Thread.Sleep(50);
            }
        }

        TryDelete(file + ".tmp");
        try
        {
            var dir = Path.GetDirectoryName(file)!;
            if (Directory.Exists(dir))
            {
                foreach (var damaged in purgeDamaged ? Directory.GetFiles(dir, DamagedPrefix + "*") : [])
                {
                    TryDelete(damaged);
                }

                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: leftovers and an empty folder are harmless.
        }
    }

    private static bool TryMove(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private void ThrowIfFaulted()
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The recovery journal is unavailable after a failed reopen.");
        }
    }

    private void Reopen(string headerLine)
    {
        try
        {
            _stream = Open(_file, headerLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _stream = null;
            _faulted = true;
        }
    }

    /// <summary>
    /// Opens for append. An existing journal with a valid current-version header is kept (a torn last line is
    /// terminated first); anything else is moved aside and a fresh journal with the given header line starts.
    /// </summary>
    private static FileStream Open(string file, string headerLine)
    {
        if (File.Exists(file) && !HasCurrentHeader(file))
        {
            var name = $"{DamagedPrefix}{DateTime.UtcNow:yyyyMMddHHmmss}-{Random.Shared.Next(0x10000):x4}.jsonl";
            File.Move(file, Path.Combine(Path.GetDirectoryName(file)!, name));
        }

        var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1);
        try
        {
            if (stream.Length == 0)
            {
                var bytes = _utf8.GetBytes(headerLine + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            else
            {
                stream.Seek(-1, SeekOrigin.End);
                var last = stream.ReadByte();
                stream.Seek(0, SeekOrigin.End);
                if (last != '\n')
                {
                    stream.WriteByte((byte)'\n');
                    stream.Flush();
                }
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool HasCurrentHeader(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, _utf8);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return JournalCodec.ParseHeader(line.Trim()) is { } h && h.FormatVersion == JournalCodec.CurrentFormatVersion;
            }
        }

        return false;
    }

    private void SyncLoop()
    {
        while (true)
        {
            lock (_syncGate)
            {
                FileStream? stream;
                lock (_gate)
                {
                    if (!_syncRequested || _closed)
                    {
                        _syncRequested = false;
                        _workerRunning = false;
                        return;
                    }

                    _syncRequested = false;
                    stream = _stream;
                }

                // Safe without _gate: writes are unbuffered (bufferSize 1), so Flush(true) only fsyncs, and
                // _syncGate keeps Truncate, Delete and Dispose from closing the handle meanwhile.
                try
                {
                    stream?.Flush(flushToDisk: true);
                }
                catch (Exception ex)
                {
                    // The data already reached the OS on Append; remember the first failure for DisposeAsync.
                    lock (_gate)
                    {
                        _syncFault ??= ex;
                    }
                }
            }
        }
    }
}
