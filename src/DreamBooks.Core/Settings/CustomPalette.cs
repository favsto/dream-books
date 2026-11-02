namespace DreamBooks.Core.Settings;

/// <summary>
/// The user's custom colours, shared by the pen and the highlighter (UI-02): at most <see cref="MaxColors"/>
/// <c>#RRGGBB</c> entries, oldest first. Immutable: every change returns a new palette.
/// </summary>
public sealed class CustomPalette : IEquatable<CustomPalette>
{
    public const int MaxColors = 12;

    public static CustomPalette Empty { get; } = new([]);

    private readonly string[] _colors;

    private CustomPalette(string[] colors) => _colors = colors;

    /// <summary>The colours, oldest first.</summary>
    public IReadOnlyList<string> Colors => _colors;

    public int Count => _colors.Length;

    /// <summary>Builds a palette from text, skipping invalid entries and applying the <see cref="Add"/> rules in order.</summary>
    public static CustomPalette From(IEnumerable<string?> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        var palette = Empty;
        foreach (var color in colors)
        {
            if (HexColor.TryNormalize(color, out _))
            {
                palette = palette.Add(color!);
            }
        }

        return palette;
    }

    /// <summary>
    /// Adds a colour as the newest. An existing equal colour (case-insensitive) moves to the newest slot instead of
    /// duplicating; beyond <see cref="MaxColors"/> the oldest drops. An invalid colour changes nothing.
    /// </summary>
    public CustomPalette Add(string hex)
    {
        if (!HexColor.TryNormalize(hex, out var normalized))
        {
            return this;
        }

        var list = _colors.Where(c => c != normalized).ToList();
        list.Add(normalized);
        if (list.Count > MaxColors)
        {
            list.RemoveRange(0, list.Count - MaxColors);
        }

        return new CustomPalette([.. list]);
    }

    public CustomPalette Remove(string hex) =>
        HexColor.TryNormalize(hex, out var normalized) && Contains(normalized)
            ? new CustomPalette(_colors.Where(c => c != normalized).ToArray())
            : this;

    public bool Contains(string hex) => HexColor.TryNormalize(hex, out var normalized) && _colors.Contains(normalized);

    public bool Equals(CustomPalette? other) => other is not null && _colors.AsSpan().SequenceEqual(other._colors);

    public override bool Equals(object? obj) => Equals(obj as CustomPalette);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var c in _colors)
        {
            hash.Add(c, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
