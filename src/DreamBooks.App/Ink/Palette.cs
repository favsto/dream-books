using DreamBooks.Core.Model;

namespace DreamBooks.App.Ink;

/// <summary>
/// The eight preset pen colours (FR-25), in toolbar order: black, dark blue, blue, red, green, orange, purple, grey.
/// The first one is the factory pen colour (<see cref="Core.Settings.AppSettings.DefaultPenColor"/>).
/// </summary>
internal static class Palette
{
    public static InkColor Black { get; } = InkColor.FromRgb(0x00, 0x00, 0x00);

    public static InkColor DarkBlue { get; } = InkColor.FromRgb(0x0D, 0x2B, 0x6B);

    public static InkColor Blue { get; } = InkColor.FromRgb(0x1F, 0x5B, 0xD8);

    public static InkColor Red { get; } = InkColor.FromRgb(0xD3, 0x2F, 0x2F);

    public static InkColor Green { get; } = InkColor.FromRgb(0x2E, 0x7D, 0x32);

    public static InkColor Orange { get; } = InkColor.FromRgb(0xEF, 0x6C, 0x00);

    public static InkColor Purple { get; } = InkColor.FromRgb(0x7B, 0x1F, 0xA2);

    public static InkColor Grey { get; } = InkColor.FromRgb(0x6B, 0x6B, 0x6B);

    public static IReadOnlyList<InkColor> Colors { get; } = [Black, DarkBlue, Blue, Red, Green, Orange, Purple, Grey];
}
