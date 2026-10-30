using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>The eight preset ink colours (FR-25).</summary>
internal static class Palette
{
    public static IReadOnlyList<InkColor> Colors { get; } =
    [
        InkColor.FromRgb(0x00, 0x00, 0x00),
        InkColor.FromRgb(0x5A, 0x5A, 0x5A),
        InkColor.FromRgb(0x1F, 0x5B, 0xD8),
        InkColor.FromRgb(0xD3, 0x2F, 0x2F),
        InkColor.FromRgb(0x2E, 0x7D, 0x32),
        InkColor.FromRgb(0xEF, 0x6C, 0x00),
        InkColor.FromRgb(0x7B, 0x1F, 0xA2),
        InkColor.FromRgb(0x6D, 0x4C, 0x41),
    ];
}
