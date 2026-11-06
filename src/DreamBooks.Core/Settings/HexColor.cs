using System.Globalization;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Settings;

/// <summary>Parsing and formatting of <c>#RRGGBB</c> colours, the only colour text the settings file holds.</summary>
public static class HexColor
{
    /// <summary>Normalises <c>#RRGGBB</c> (any case, the '#' optional) to upper-case <c>#RRGGBB</c>; false for anything else.</summary>
    public static bool TryNormalize(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (text is null)
        {
            return false;
        }

        var digits = text.StartsWith('#') ? text[1..] : text;
        if (digits.Length != 6 || !digits.All(Uri.IsHexDigit))
        {
            return false;
        }

        normalized = "#" + digits.ToUpperInvariant();
        return true;
    }

    public static bool TryParse(string? text, byte alpha, out InkColor color)
    {
        color = default;
        if (!TryNormalize(text, out var hex))
        {
            return false;
        }

        color = new InkColor(
            alpha,
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>The <c>#RRGGBB</c> text of a colour; the alpha is not part of it.</summary>
    public static string Format(InkColor color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
}
