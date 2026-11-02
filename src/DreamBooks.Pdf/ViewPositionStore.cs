using System.Globalization;
using System.Text.Json;
using DreamBooks.Core.Layout;

namespace DreamBooks.Pdf;

/// <summary>
/// Remembers where the user was in each document: one small ASCII JSON file per document,
/// <c>&lt;root&gt;\&lt;DocumentKey&gt;.json</c>, outside the user's folders. Files hold no path or document text.
/// Format: <c>{"v":1,"page","y","x","zoom","fit","saved"}</c>; <c>fit</c> (fit-width mode) is optional on read, a file
/// without it reads as not fit. Loading never throws; saving reports every failure through <see cref="Failed"/>
/// instead of throwing.
/// </summary>
public sealed class ViewPositionStore
{
    private const int _formatVersion = 1;
    private const string _extension = ".json";
    private const string _tempExtension = ".tmp";

    /// <summary>A temporary file older than this is an orphan of a crashed save (a save lasts milliseconds).</summary>
    private static readonly TimeSpan _orphanTempAge = TimeSpan.FromDays(1);

    private readonly string _root;
    private readonly Func<DateTime> _clock;

    public ViewPositionStore(string rootDirectory, Func<DateTime>? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        _root = rootDirectory;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Raised when a write or delete fails. The exception carries no document content.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>Never throws: an unusable path or unreadable file reads as "nothing remembered".</summary>
    public StoredPosition? TryLoad(string documentPath)
    {
        string file;
        try
        {
            file = FilePath(documentPath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return ReadFile(file);
    }

    /// <summary>
    /// Remembers a position and whether the zoom follows the window width. Never throws: failures, expected (I/O) or
    /// not, are reported through <see cref="Failed"/>.
    /// </summary>
    public void Save(string documentPath, ViewPosition position, bool fitWidth = false)
    {
        if (!IsValid(position))
        {
            return;
        }

        string? temp = null;
        try
        {
            var file = FilePath(documentPath);
            temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + _tempExtension);
            Directory.CreateDirectory(_root);
            File.WriteAllBytes(temp, Serialize(position, fitWidth, _clock()));
            Commit(temp, file);
        }
#pragma warning disable CA1031 // Remembering the position is best effort: every failure is reported, none thrown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (temp is not null)
            {
                TryDelete(temp);
            }

            Report(ex);
        }
    }

    public void Delete(string documentPath)
    {
        try
        {
            File.Delete(FilePath(documentPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Removes entries older than <paramref name="maxAge"/> (default 365 days), files that do not parse, the oldest entries
    /// beyond <paramref name="maxEntries"/>, and temporary files older than a day (left by a crash during a save). A file
    /// that cannot be read right now (held by a writer or a scanner) is left alone.
    /// </summary>
    public void Prune(int maxEntries = 500, TimeSpan? maxAge = null)
    {
        var limit = maxAge ?? TimeSpan.FromDays(365);
        string[] files;
        string[] temps;
        try
        {
            files = Directory.GetFiles(_root, "*" + _extension);
            temps = Directory.GetFiles(_root, "*" + _tempExtension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var now = _clock();
        var keep = new List<(string File, DateTime Saved)>();
        foreach (var file in files)
        {
            var (stored, readable) = ReadFileOutcome(file);
            if (!readable)
            {
                continue; // in use: maybe a save is replacing it right now
            }

            if (stored is null || now - stored.Value.SavedUtc > limit)
            {
                TryDelete(file);
            }
            else
            {
                keep.Add((file, stored.Value.SavedUtc));
            }
        }

        foreach (var (file, _) in keep.OrderByDescending(k => k.Saved).Skip(Math.Max(0, maxEntries)))
        {
            TryDelete(file);
        }

        foreach (var temp in temps)
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(temp) > _orphanTempAge)
                {
                    TryDelete(temp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsValid(ViewPosition p) =>
        p.PageIndex >= 0 && double.IsFinite(p.YPt) && double.IsFinite(p.ColumnXPt) && double.IsFinite(p.Zoom) && p.Zoom > 0;

    private static byte[] Serialize(ViewPosition p, bool fitWidth, DateTime savedUtc)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", _formatVersion);
            writer.WriteNumber("page", p.PageIndex);
            writer.WriteNumber("y", p.YPt);
            writer.WriteNumber("x", p.ColumnXPt);
            writer.WriteNumber("zoom", p.Zoom);
            writer.WriteBoolean("fit", fitWidth); // optional on read: older files have no "fit" (not fit)
            writer.WriteString("saved", savedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static StoredPosition? ReadFile(string file) => ReadFileOutcome(file).Stored;

    /// <summary>The position in a file (null when it does not parse), and whether the file could be read at all.</summary>
    private static (StoredPosition? Stored, bool Readable) ReadFileOutcome(string file)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, false);
        }

        try
        {
            return (Parse(bytes), true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return (null, true);
        }
    }

    private static StoredPosition? Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("v", out var v) || !v.TryGetInt32(out var version) || version != _formatVersion
            || !root.TryGetProperty("page", out var pageEl) || !pageEl.TryGetInt32(out var page)
            || !TryNumber(root, "y", out var y)
            || !TryNumber(root, "x", out var x)
            || !TryNumber(root, "zoom", out var zoom)
            || !root.TryGetProperty("saved", out var savedEl) || savedEl.ValueKind != JsonValueKind.String
            || !DateTime.TryParse(savedEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var saved))
        {
            return null;
        }

        var position = new ViewPosition(page, y, x, zoom);
        var fit = root.TryGetProperty("fit", out var fitEl) && fitEl.ValueKind == JsonValueKind.True;
        return IsValid(position) ? new StoredPosition(position, saved, fit) : null;
    }

    private static bool TryNumber(JsonElement root, string name, out double value)
    {
        value = 0;
        return root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value) && double.IsFinite(value);
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

    private string FilePath(string documentPath) => Path.Combine(_root, RecoveryStore.DocumentKey(documentPath) + _extension);

    private void Report(Exception ex) => Failed?.Invoke(this, new IOException("View position store I/O failed (" + ex.GetType().Name + ")."));
}
