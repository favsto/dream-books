using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Settings;

/// <summary>
/// The app-wide settings (FR-110), immutable. <see cref="Default"/> holds the factory values. Colours are RGB; the
/// highlighter colour always carries the highlighter alpha.
/// </summary>
public sealed record AppSettings
{
    public const double MinPenWidthMm = 0.25;
    public const double MaxPenWidthMm = 5;
    public const double MinEraserSizeMm = 2;
    public const double MaxEraserSizeMm = 20;
    public const double MinHighlighterAngleDeg = 0;
    public const double MaxHighlighterAngleDeg = 180;

    public const double DefaultPenWidthMm = 0.5;
    public const double DefaultEraserSizeMm = 6;
    public const double DefaultHighlighterAngleDeg = 45;

    // Declared before Default: static initialisers run in textual order and Default reads it.
    public static InkColor DefaultPenColor { get; } = InkColor.FromRgb(0x00, 0x00, 0x00);

    public static AppSettings Default { get; } = new();

    public ThemeMode ThemeMode { get; init; } = ThemeMode.System;

    /// <summary>Accent colour override as <c>#RRGGBB</c>, or null to follow Windows.</summary>
    public string? AccentOverride { get; init; }

    public EraserMode EraserMode { get; init; } = EraserMode.WholeStroke;

    public PressureCurve PressureCurve { get; init; } = PressureCurve.Firm;

    public SmoothingLevel Smoothing { get; init; } = SmoothingLevel.Strong;

    public bool ToolbarFixed { get; init; }

    public ToolbarEdge ToolbarEdge { get; init; } = ToolbarEdge.Bottom;

    public InkColor PenColor { get; init; } = DefaultPenColor;

    public double PenWidthMm { get; init; } = DefaultPenWidthMm;

    public InkColor HighlighterColor { get; init; } = HighlighterPresets.Yellow;

    public bool HighlighterChisel { get; init; }

    public double HighlighterAngleDeg { get; init; } = DefaultHighlighterAngleDeg;

    public bool HighlighterStraightLine { get; init; }

    public double EraserSizeMm { get; init; } = DefaultEraserSizeMm;

    public CustomPalette CustomPalette { get; init; } = CustomPalette.Empty;
}
