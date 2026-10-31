using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Journal;

/// <summary>
/// Reads and writes journal lines: one JSON object per line, printable ASCII only (non-ASCII escaped as
/// <c>\uXXXX</c>), stroke payloads embedded as the same JSON objects <see cref="StrokeJson"/> stores in the PDF.
/// Parsing never throws: a corrupt or torn line parses to null. Entries record effects on strokes (add, erase)
/// and the view; there are no symbolic undo/redo entries (an undo or redo is journalled as its effect).
/// </summary>
public static class JournalCodec
{
    public const int CurrentFormatVersion = 1;

    private static readonly JavaScriptEncoder _asciiEncoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin);

    private static readonly JsonDocumentOptions _documentOptions = new()
    {
        MaxDepth = 32,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static string SerializeHeader(JournalHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentException.ThrowIfNullOrEmpty(header.Path);
        return Write(w =>
        {
            w.WriteString("k", "hdr");
            w.WriteNumber("fv", header.FormatVersion);
            w.WriteString("path", header.Path);
            w.WriteString("fp", header.Fingerprint.ToToken());
        });
    }

    /// <summary>Serializes an entry. Throws <see cref="ArgumentException"/> for values that cannot be written (non-finite numbers).</summary>
    public static string SerializeEntry(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry switch
        {
            AddEntry add => Write(w =>
            {
                w.WriteString("k", "add");
                w.WriteNumber("p", add.PageIndex);
                w.WriteStartArray("s");
                foreach (var stroke in add.Strokes)
                {
                    w.WriteRawValue(StrokeJson.Serialize(stroke));
                }

                w.WriteEndArray();
            }),
            EraseEntry erase => Write(w =>
            {
                w.WriteString("k", "erase");
                w.WriteNumber("p", erase.PageIndex);
                w.WriteStartArray("rm");
                foreach (var id in erase.RemovedIds)
                {
                    w.WriteStringValue(id.ToString("N", CultureInfo.InvariantCulture));
                }

                w.WriteEndArray();
                w.WriteStartArray("add");
                foreach (var added in erase.Added)
                {
                    w.WriteStartObject();
                    w.WriteNumber("i", added.Index);
                    w.WritePropertyName("s");
                    w.WriteRawValue(StrokeJson.Serialize(added.Stroke));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }),
            ViewEntry view => SerializeView(view),
            _ => throw new ArgumentException("Unknown journal entry type.", nameof(entry)),
        };
    }

    /// <summary>Parses a header line; null if the line is not a well-formed header.</summary>
    public static JournalHeader? ParseHeader(string line)
    {
        return ParseObject<JournalHeader>(line, root =>
        {
            if (!TryReadProperties(root, out var props) || !IsKind(props, "hdr"))
            {
                return null;
            }

            if (!TryGetInt(props, "fv", out var version) || version < 1
                || !props.TryGetValue("path", out var path) || path.ValueKind != JsonValueKind.String
                || !props.TryGetValue("fp", out var fp) || fp.ValueKind != JsonValueKind.String
                || !DocumentFingerprint.TryParse(fp.GetString()!, out var fingerprint))
            {
                return null;
            }

            var pathText = path.GetString();
            return string.IsNullOrEmpty(pathText) ? null : new JournalHeader(version, pathText, fingerprint);
        });
    }

    /// <summary>Parses an entry line; null for a corrupt or torn line, a header line or an unknown kind.</summary>
    public static JournalEntry? ParseEntry(string line)
    {
        return ParseObject<JournalEntry>(line, root =>
        {
            if (!TryReadProperties(root, out var props)
                || !props.TryGetValue("k", out var kind) || kind.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return kind.GetString() switch
            {
                "add" => ReadAdd(props),
                "erase" => ReadErase(props),
                "view" => ReadView(props),
                _ => null,
            };
        });
    }

    /// <summary>
    /// Parses a whole journal. Blank lines are ignored. The first non-blank line must be the header; an
    /// unparseable last line is a torn tail and is dropped; other unparseable lines are skipped and counted.
    /// Without a header, or with an unsupported format version, no entry is returned and every entry line
    /// counts as skipped (a lone unparseable line is reported as a torn tail instead).
    /// </summary>
    public static JournalContents Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var nonBlank = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (nonBlank.Count == 0)
        {
            return new JournalContents(null, [], false, 0);
        }

        var header = ParseHeader(nonBlank[0]);
        if (header is null)
        {
            var onlyLine = nonBlank.Count == 1;
            return new JournalContents(null, [], onlyLine, onlyLine ? 0 : nonBlank.Count);
        }

        if (header.FormatVersion != CurrentFormatVersion)
        {
            return new JournalContents(header, [], false, nonBlank.Count - 1);
        }

        var entries = new List<JournalEntry>(nonBlank.Count - 1);
        var skipped = 0;
        var torn = false;
        for (var i = 1; i < nonBlank.Count; i++)
        {
            if (ParseEntry(nonBlank[i]) is { } entry)
            {
                entries.Add(entry);
            }
            else if (i == nonBlank.Count - 1)
            {
                torn = true;
            }
            else
            {
                skipped++;
            }
        }

        return new JournalContents(header, entries.AsReadOnly(), torn, skipped);
    }

    private static string SerializeView(ViewEntry view)
    {
        if (!double.IsFinite(view.Zoom) || !double.IsFinite(view.ScrollX) || !double.IsFinite(view.ScrollY))
        {
            throw new ArgumentException("View values must be finite.", nameof(view));
        }

        return Write(w =>
        {
            w.WriteString("k", "view");
            w.WriteNumber("p", view.PageIndex);
            w.WriteNumber("z", view.Zoom);
            w.WriteNumber("x", view.ScrollX);
            w.WriteNumber("y", view.ScrollY);
        });
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = _asciiEncoder, Indented = false }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static T? ParseObject<T>(string line, Func<JsonElement, T?> read)
        where T : class
    {
        try
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(line, _documentOptions);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? read(doc.RootElement) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Collects the properties of an object; false if a name appears twice.</summary>
    private static bool TryReadProperties(JsonElement element, out Dictionary<string, JsonElement> props)
    {
        props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            if (!props.TryAdd(prop.Name, prop.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsKind(Dictionary<string, JsonElement> props, string kind)
        => props.TryGetValue("k", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == kind;

    private static bool TryGetInt(Dictionary<string, JsonElement> props, string name, out int value)
    {
        value = 0;
        return props.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
    }

    private static bool TryGetPage(Dictionary<string, JsonElement> props, out int page)
        => TryGetInt(props, "p", out page) && page >= 0;

    private static bool TryGetDouble(Dictionary<string, JsonElement> props, string name, out double value)
    {
        value = 0;
        return props.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.Number
            && e.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static InkStroke? ReadStroke(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = StrokeJson.Parse(element.GetRawText());
        return result.Status == StrokeParseStatus.Ok ? result.Stroke : null;
    }

    private static AddEntry? ReadAdd(Dictionary<string, JsonElement> props)
    {
        if (!TryGetPage(props, out var page)
            || !props.TryGetValue("s", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var strokes = new List<InkStroke>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            if (ReadStroke(element) is not { } stroke)
            {
                return null;
            }

            strokes.Add(stroke);
        }

        return new AddEntry(page, strokes.AsReadOnly());
    }

    private static EraseEntry? ReadErase(Dictionary<string, JsonElement> props)
    {
        if (!TryGetPage(props, out var page)
            || !props.TryGetValue("rm", out var removedArray) || removedArray.ValueKind != JsonValueKind.Array
            || !props.TryGetValue("add", out var addedArray) || addedArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var removed = new List<Guid>(removedArray.GetArrayLength());
        foreach (var element in removedArray.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || !Guid.TryParseExact(element.GetString(), "N", out var id))
            {
                return null;
            }

            removed.Add(id);
        }

        var added = new List<IndexedStroke>(addedArray.GetArrayLength());
        foreach (var element in addedArray.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !TryReadProperties(element, out var item)
                || !TryGetInt(item, "i", out var index) || index < 0
                || !item.TryGetValue("s", out var payload)
                || ReadStroke(payload) is not { } stroke)
            {
                return null;
            }

            added.Add(new IndexedStroke(index, stroke));
        }

        return new EraseEntry(page, removed.AsReadOnly(), added.AsReadOnly());
    }

    private static ViewEntry? ReadView(Dictionary<string, JsonElement> props)
    {
        if (!TryGetPage(props, out var page)
            || !TryGetDouble(props, "z", out var zoom) || zoom <= 0
            || !TryGetDouble(props, "x", out var x)
            || !TryGetDouble(props, "y", out var y))
        {
            return null;
        }

        return new ViewEntry(page, zoom, x, y);
    }
}
