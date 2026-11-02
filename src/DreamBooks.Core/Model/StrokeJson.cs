using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace DreamBooks.Core.Model;

/// <summary>
/// Versioned JSON form of a single <see cref="InkStroke"/> as stored inside the PDF (FR-83).
/// Output is pure printable ASCII; coordinates are PDF page points.
/// </summary>
public static class StrokeJson
{
    public const int CurrentVersion = 1;

    private const int _maxPayloadChars = 16 * 1024 * 1024;
    private const int _maxPoints = 1_000_000;

    private static readonly JavaScriptEncoder _asciiEncoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin);

    private static readonly JsonDocumentOptions _documentOptions = new()
    {
        MaxDepth = 32,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>Serializes a stroke. Throws <see cref="ArgumentException"/> if a coordinate or optional value is not finite.</summary>
    public static string Serialize(InkStroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        if (!double.IsFinite(stroke.BaseWidthPt))
        {
            throw new ArgumentException("Stroke width must be finite.", nameof(stroke));
        }

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = _asciiEncoder, Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", CurrentVersion);
            w.WriteString("id", stroke.Id.ToString("N", CultureInfo.InvariantCulture));
            w.WriteString("tool", stroke.Tool == InkTool.Highlighter ? "highlighter" : "pen");
            var c = stroke.Color;
            w.WriteString("color", string.Create(CultureInfo.InvariantCulture, $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"));
            w.WriteNumber("w", stroke.BaseWidthPt);
            w.WriteStartArray("pts");
            foreach (var p in stroke.Points)
            {
                WritePoint(w, p);
            }

            w.WriteEndArray();
            if (stroke.Chisel is { } chisel)
            {
                w.WriteStartObject("chisel");
                w.WriteNumber("angle", chisel.AngleDeg);
                w.WriteNumber("ratio", chisel.Ratio);
                w.WriteEndObject();

            }
            w.WriteEndObject();
        }

        return Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Parses a stroke payload. Never throws: any problem is reported through the status.</summary>
    public static StrokeParseResult Parse(string json)
    {
        try
        {
            if (json is null || json.Length > _maxPayloadChars)
            {
                return Corrupt();
            }

            using var doc = JsonDocument.Parse(json, _documentOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Corrupt();
            }

            var version = ReadVersion(root);
            if (version is null or < 1)
            {
                return Corrupt();
            }

            if (version > CurrentVersion)
            {
                return new StrokeParseResult(StrokeParseStatus.UnsupportedVersion, null);
            }

            var stroke = ReadStroke(root);
            return stroke is null ? Corrupt() : new StrokeParseResult(StrokeParseStatus.Ok, stroke);
        }
        catch (Exception)
        {
            return Corrupt();
        }
    }

    /// <summary>Returns a quoted JSON string literal in which every non-ASCII character is a \uXXXX escape.</summary>
    internal static string EscapeString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "\"" + _asciiEncoder.Encode(value) + "\"";
    }

    private static StrokeParseResult Corrupt() => new(StrokeParseStatus.Corrupt, null);

    private static void WritePoint(Utf8JsonWriter w, InkPoint p)
    {
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)
            || !IsFiniteOrNull(p.Pressure) || !IsFiniteOrNull(p.TiltX) || !IsFiniteOrNull(p.TiltY) || !IsFiniteOrNull(p.TipAngle))
        {
            throw new ArgumentException("Stroke point values must be finite.");
        }

        w.WriteStartObject();
        w.WriteNumber("x", p.X);
        w.WriteNumber("y", p.Y);
        WriteOptional(w, "p", p.Pressure);
        WriteOptional(w, "tx", p.TiltX);
        WriteOptional(w, "ty", p.TiltY);
        WriteOptional(w, "ta", p.TipAngle);
        w.WriteNumber("t", p.TimestampMs);
        w.WriteEndObject();
    }

    private static bool IsFiniteOrNull(float? value) => value is null || float.IsFinite(value.Value);

    private static void WriteOptional(Utf8JsonWriter w, string name, float? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
    }

    private static int? ReadVersion(JsonElement root)
    {
        int? version = null;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name != "v")
            {
                continue;
            }

            if (version is not null || prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var v))
            {
                return null;
            }

            version = v;
        }

        return version;
    }

    private static InkStroke? ReadStroke(JsonElement root)
    {
        Guid? id = null;
        InkTool? tool = null;
        InkColor? color = null;
        double? width = null;
        JsonElement? pts = null;
        ChiselTip? chisel = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prop in root.EnumerateObject())
        {
            var name = prop.Name;
            if (name is "v" or "id" or "tool" or "color" or "w" or "pts" or "chisel" && !seen.Add(name))
            {
                return null;
            }

            switch (name)
            {
                case "id":
                    if (prop.Value.ValueKind != JsonValueKind.String
                        || !Guid.TryParseExact(prop.Value.GetString(), "N", out var g))
                    {
                        return null;
                    }

                    id = g;
                    break;
                case "tool":
                    tool = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() switch
                    {
                        "pen" => InkTool.Pen,
                        "highlighter" => InkTool.Highlighter,
                        _ => null,
                    }
                    : null;
                    if (tool is null)
                    {
                        return null;
                    }

                    break;
                case "color":
                    color = ParseColor(prop.Value);
                    if (color is null)
                    {
                        return null;
                    }

                    break;
                case "w":
                    if (!TryReadDouble(prop.Value, out var w) || w <= 0)
                    {
                        return null;
                    }

                    width = w;
                    break;
                case "pts":
                    if (prop.Value.ValueKind != JsonValueKind.Array)
                    {
                        return null;
                    }

                    pts = prop.Value;
                    break;
                case "chisel":
                    chisel = ReadChisel(prop.Value);
                    if (chisel is null)
                    {
                        return null;
                    }

                    break;
            }
        }

        if (id is null || tool is null || color is null || width is null || pts is null)
        {
            return null;
        }

        var count = pts.Value.GetArrayLength();
        if (count is 0 or > _maxPoints)
        {
            return null;
        }

        var points = new InkPoint[count];
        var i = 0;
        foreach (var element in pts.Value.EnumerateArray())
        {
            if (!TryReadPoint(element, out points[i++]))
            {
                return null;
            }
        }

        return new InkStroke(id.Value, tool.Value, color.Value, width.Value, points, chisel);
    }

    private static ChiselTip? ReadChisel(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        double? angle = null, ratio = null;
        foreach (var prop in element.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "angle":
                    if (angle is not null || !TryReadDouble(prop.Value, out var a)) { return null; }
                    angle = a;
                    break;
                case "ratio":
                    if (ratio is not null || !TryReadDouble(prop.Value, out var r)) { return null; }
                    ratio = r;
                    break;
            }
        }

        if (angle is not { } ang || ratio is not { } rat || ang is < 0 or > 180 || rat is < 0 or > 1)
        {
            return null;
        }

        return new ChiselTip(ang, rat);
    }

    private static InkColor? ParseColor(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var s = element.GetString();
        if (s is not { Length: 9 } || s[0] != '#'
            || !uint.TryParse(s.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var argb))
        {
            return null;
        }

        return new InkColor((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    private static bool TryReadPoint(JsonElement element, out InkPoint point)
    {
        point = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        double? x = null, y = null;
        float? p = null, tx = null, ty = null, ta = null;
        long? t = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name is "x" or "y" or "p" or "tx" or "ty" or "ta" or "t" && !seen.Add(prop.Name))
            {
                return false;
            }

            switch (prop.Name)
            {
                case "x":
                    if (!TryReadDouble(prop.Value, out var xv)) { return false; }
                    x = xv;
                    break;
                case "y":
                    if (!TryReadDouble(prop.Value, out var yv)) { return false; }
                    y = yv;
                    break;
                case "p":
                    if (!TryReadFloat(prop.Value, out var pv)) { return false; }
                    p = pv;
                    break;
                case "tx":
                    if (!TryReadFloat(prop.Value, out var txv)) { return false; }
                    tx = txv;
                    break;
                case "ty":
                    if (!TryReadFloat(prop.Value, out var tyv)) { return false; }
                    ty = tyv;
                    break;
                case "ta":
                    if (!TryReadFloat(prop.Value, out var tav)) { return false; }
                    ta = tav;
                    break;
                case "t":
                    if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt64(out var tv)) { return false; }
                    t = tv;
                    break;
            }
        }

        if (x is null || y is null || t is null)
        {
            return false;
        }

        point = new InkPoint(x.Value, y.Value, p, tx, ty, ta, t.Value);
        return true;
    }

    private static bool TryReadDouble(JsonElement e, out double value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static bool TryReadFloat(JsonElement e, out float value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetSingle(out value) && float.IsFinite(value);
    }
}
