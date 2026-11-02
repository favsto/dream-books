namespace DreamBooks.Core.Model;

/// <summary>The six highlighter colours (FR-41), translucent so text stays readable.</summary>
public static class HighlighterPresets
{
    public const byte Alpha = 89;

    public static InkColor Yellow { get; } = new(Alpha, 255, 235, 0);

    public static InkColor Green { get; } = new(Alpha, 64, 220, 70);

    public static InkColor LightBlue { get; } = new(Alpha, 80, 200, 255);

    public static InkColor Pink { get; } = new(Alpha, 255, 90, 170);

    public static InkColor Orange { get; } = new(Alpha, 255, 150, 30);

    public static InkColor Purple { get; } = new(Alpha, 170, 100, 255);

    public static IReadOnlyList<InkColor> All { get; } = [Yellow, Green, LightBlue, Pink, Orange, Purple];
}
