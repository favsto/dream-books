using System.Globalization;
using System.Text.Json;
using DreamBooks.Core.Recent;

namespace DreamBooks.Pdf;

/// <summary>
/// Reads and writes the list of recently opened documents (FR-03): <c>&lt;root&gt;\recent.json</c>, one small ASCII JSON
/// file. It holds document paths, so it is a file of its own and never part of the settings (FR-110). Format:
/// <c>{"v":1,"items":[{"path","opened","page","pages"}]}</c>, newest first. Loading never throws: a file that is
/// missing, does not parse or has another version gives an empty list, and an invalid entry is skipped. Saving writes a
/// temporary file, flushes it and swaps it in; every failure goes to <see cref="Failed"/> (without a path) instead of
/// throwing.
/// </summary>
public sealed class RecentFilesStore
{
    private const int _formatVersion = 1;
    private const string _fileName = "recent.json";
    private const string _tempExtension = ".tmp";

    /// <summary>A temporary file older than this was left by a save that never finished: swept at load.</summary>
    private static readonly TimeSpan _orphanAge = TimeSpan.FromHours(1);

    private readonly string _root;

    public RecentFilesStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        _root = rootDirectory;
    }

    /// <summary>Raised when a write fails. The exception carries no path and no content.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>The production root: <c>%LOCALAPPDATA%\DreamBooks</c>, the settings file's folder.</summary>
    public static string DefaultRootDirectory() => AppSettingsStore.DefaultRootDirectory();

    /// <summary>Never throws. First sweeps the temporary files of saves that never finished.</summary>
    public RecentFilesList Load()
    {
        SweepOrphanedTemporaryFiles();
        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                Path.Combine(_root, _fileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new RecentFilesList();
        }

        try
        {
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return new RecentFilesList();
        }
    }

    /// <summary>Never throws: failures, expected (I/O) or not, are reported through <see cref="Failed"/>.</summary>
    public void Save(RecentFilesList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        string? temp = null;
        try
        {
            var file = Path.Combine(_root, _fileName);
            temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + _tempExtension);
            Directory.CreateDirectory(_root);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Serialize(list));
                stream.Flush(flushToDisk: true);
            }

            Commit(temp, file);
        }
#pragma warning disable CA1031 // Remembering the recents is best effort: every failure is reported, none thrown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (temp is not null)
            {
                TryDelete(temp);
            }

            Failed?.Invoke(this, new IOException("Recent files store I/O failed (" + ex.GetType().Name + ")."));
        }
    }

    private static byte[] Serialize(RecentFilesList list)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", _formatVersion);
            writer.WriteStartArray("items");
            foreach (var item in list.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("path", item.Path);
                writer.WriteString("opened", item.LastOpenedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                writer.WriteNumber("page", item.Page);
                writer.WriteNumber("pages", item.PageCount);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static RecentFilesList Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt32(out var version) || version != _formatVersion
            || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return new RecentFilesList();
        }

        var files = new List<RecentFile>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && TryReadEntry(item, out var file))
            {
                files.Add(file);
            }
        }

        return new RecentFilesList(files);
    }

    private static bool TryReadEntry(JsonElement item, out RecentFile file)
    {
        file = null!;
        if (!item.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(path.GetString())
            || !item.TryGetProperty("opened", out var opened) || opened.ValueKind != JsonValueKind.String
            || !DateTime.TryParse(opened.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            || !item.TryGetProperty("page", out var page) || !page.TryGetInt32(out var pageIndex)
            || !item.TryGetProperty("pages", out var pages) || !pages.TryGetInt32(out var pageCount))
        {
            return false;
        }

        file = new RecentFile(path.GetString()!, when.ToUniversalTime(), pageIndex, pageCount);
        return true;
    }

    private static void Commit(string temp, string file)
    {
        // A reader holding the file open without delete sharing can make the swap fail briefly: retry for a short while.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Replace(temp, file, null);
                }
                else
                {
                    File.Move(temp, file);
                }

                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

    private void SweepOrphanedTemporaryFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - _orphanAge;
            foreach (var temp in Directory.EnumerateFiles(_root, "*" + _tempExtension, SearchOption.TopDirectoryOnly))
            {
                // Only the names the stores make (a GUID in 32 hex digits), so nothing else in the folder is touched.
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(temp), "N", out _)
                    && File.GetLastWriteTimeUtc(temp) < cutoff)
                {
                    TryDelete(temp);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
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
        }
    }
}
