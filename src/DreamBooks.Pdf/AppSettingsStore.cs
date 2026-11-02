using System.Text.Json;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Settings;

namespace DreamBooks.Pdf;

/// <summary>
/// Reads and writes the app settings (FR-110): <c>&lt;root&gt;\settings.json</c>, one small ASCII JSON file with no path
/// and no document text. Format: <c>{"v":1, "theme", "accent", ...}</c>. Loading never throws and works field by field:
/// a missing or invalid field takes its default, the others are kept; unknown keys are ignored; a missing or unknown
/// <c>v</c>, or a file that does not parse, gives all defaults. Saving writes a temporary file and swaps it in, and
/// reports every failure through <see cref="Failed"/> instead of throwing.
/// </summary>
public sealed class AppSettingsStore
{
    private const int _formatVersion = 1;
    private const string _fileName = "settings.json";
    private const string _tempExtension = ".tmp";

    private readonly string _root;

    public AppSettingsStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        _root = rootDirectory;
    }

    /// <summary>Raised when a write fails. The exception carries no path and no content.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>The production root: <c>%LOCALAPPDATA%\DreamBooks</c>.</summary>
    public static string DefaultRootDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DreamBooks");

    /// <summary>Never throws: anything unreadable reads as the defaults.</summary>
    public AppSettings Load()
    {
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
            return AppSettings.Default;
        }

        try
        {
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return AppSettings.Default;
        }
    }

    /// <summary>Never throws: failures, expected (I/O) or not, are reported through <see cref="Failed"/>.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? temp = null;
        try
        {
            var file = Path.Combine(_root, _fileName);
            temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + _tempExtension);
            Directory.CreateDirectory(_root);
            File.WriteAllBytes(temp, Serialize(settings));
            Commit(temp, file);
        }
#pragma warning disable CA1031 // Remembering the settings is best effort: every failure is reported, none thrown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (temp is not null)
            {
                TryDelete(temp);
            }

            Failed?.Invoke(this, new IOException("Settings store I/O failed (" + ex.GetType().Name + ")."));
        }
    }

    private static byte[] Serialize(AppSettings s)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", _formatVersion);
            writer.WriteString("theme", s.ThemeMode.ToString());
            if (s.AccentOverride is not null && HexColor.TryNormalize(s.AccentOverride, out var accent))
            {
                writer.WriteString("accent", accent);
            }
            else
            {
                writer.WriteNull("accent");
            }

            writer.WriteString("eraserMode", s.EraserMode.ToString());
            writer.WriteString("curve", s.PressureCurve.ToString());
            writer.WriteString("smoothing", s.Smoothing.ToString());
            writer.WriteBoolean("toolbarFixed", s.ToolbarFixed);
            writer.WriteString("toolbarEdge", s.ToolbarEdge.ToString());
            writer.WriteString("penColor", HexColor.Format(s.PenColor));
            writer.WriteNumber("penWidthMm", Sanitize(s.PenWidthMm, AppSettings.MinPenWidthMm, AppSettings.MaxPenWidthMm, AppSettings.DefaultPenWidthMm));
            writer.WriteString("highlighterColor", HexColor.Format(s.HighlighterColor));
            writer.WriteBoolean("highlighterChisel", s.HighlighterChisel);
            writer.WriteNumber("highlighterAngleDeg", Sanitize(s.HighlighterAngleDeg, AppSettings.MinHighlighterAngleDeg, AppSettings.MaxHighlighterAngleDeg, AppSettings.DefaultHighlighterAngleDeg));
            writer.WriteBoolean("highlighterStraightLine", s.HighlighterStraightLine);
            writer.WriteNumber("eraserSizeMm", Sanitize(s.EraserSizeMm, AppSettings.MinEraserSizeMm, AppSettings.MaxEraserSizeMm, AppSettings.DefaultEraserSizeMm));
            writer.WriteStartArray("palette");
            foreach (var color in s.CustomPalette.Colors)
            {
                writer.WriteStringValue(color);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Clamps into range; NaN or infinity (never valid JSON numbers) become the default.</summary>
    private static double Sanitize(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static AppSettings Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt32(out var version) || version != _formatVersion)
        {
            return AppSettings.Default;
        }

        var d = AppSettings.Default;
        return new AppSettings
        {
            ThemeMode = ReadEnum(root, "theme", d.ThemeMode),
            AccentOverride = ReadAccent(root, d.AccentOverride),
            EraserMode = ReadEnum(root, "eraserMode", d.EraserMode),
            PressureCurve = ReadEnum(root, "curve", d.PressureCurve),
            Smoothing = ReadEnum(root, "smoothing", d.Smoothing),
            ToolbarFixed = ReadBool(root, "toolbarFixed", d.ToolbarFixed),
            ToolbarEdge = ReadEnum(root, "toolbarEdge", d.ToolbarEdge),
            PenColor = ReadColor(root, "penColor", 255, d.PenColor),
            PenWidthMm = ReadNumber(root, "penWidthMm", AppSettings.MinPenWidthMm, AppSettings.MaxPenWidthMm, d.PenWidthMm),
            HighlighterColor = ReadColor(root, "highlighterColor", HighlighterPresets.Alpha, d.HighlighterColor),
            HighlighterChisel = ReadBool(root, "highlighterChisel", d.HighlighterChisel),
            HighlighterAngleDeg = ReadNumber(root, "highlighterAngleDeg", AppSettings.MinHighlighterAngleDeg, AppSettings.MaxHighlighterAngleDeg, d.HighlighterAngleDeg),
            HighlighterStraightLine = ReadBool(root, "highlighterStraightLine", d.HighlighterStraightLine),
            EraserSizeMm = ReadNumber(root, "eraserSizeMm", AppSettings.MinEraserSizeMm, AppSettings.MaxEraserSizeMm, d.EraserSizeMm),
            CustomPalette = ReadPalette(root, d.CustomPalette),
        };
    }

    private static T ReadEnum<T>(JsonElement root, string name, T fallback)
        where T : struct, Enum
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            && Enum.TryParse<T>(el.GetString(), ignoreCase: true, out var value)
            && Enum.IsDefined(value)
            && !int.TryParse(el.GetString(), out _))
        {
            return value;
        }

        return fallback;
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean()
            : fallback;

    private static double ReadNumber(JsonElement root, string name, double min, double max, double fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            && el.TryGetDouble(out var value) && double.IsFinite(value) && value >= min && value <= max
            ? value
            : fallback;

    private static InkColor ReadColor(JsonElement root, string name, byte alpha, InkColor fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            && HexColor.TryParse(el.GetString(), alpha, out var color)
            ? color
            : fallback;

    private static string? ReadAccent(JsonElement root, string? fallback)
    {
        if (!root.TryGetProperty("accent", out var el))
        {
            return fallback;
        }

        if (el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return el.ValueKind == JsonValueKind.String && HexColor.TryNormalize(el.GetString(), out var hex) ? hex : fallback;
    }

    private static CustomPalette ReadPalette(JsonElement root, CustomPalette fallback)
    {
        if (!root.TryGetProperty("palette", out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        var colors = new List<string?>();
        foreach (var item in el.EnumerateArray())
        {
            colors.Add(item.ValueKind == JsonValueKind.String ? item.GetString() : null);
        }

        return CustomPalette.From(colors);
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
}
