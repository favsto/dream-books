using CommunityToolkit.Mvvm.ComponentModel;
using DreamBooks.Core.Model;
using DreamBooks.Core.Settings;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The user's custom colours (UI-02), one palette shared by the pen and the highlighter: at most
/// <see cref="CustomPalette.MaxColors"/>, oldest first, the oldest dropping when a new one comes (the rules of
/// <see cref="CustomPalette"/>). Colours are hues: the alpha of a colour added is ignored.
/// </summary>
public sealed class CustomPaletteViewModel : ObservableObject
{
    private CustomPalette _palette = CustomPalette.Empty;

    /// <summary>The palette; raises a change when replaced by a different one.</summary>
    public CustomPalette Palette
    {
        get => _palette;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _palette, value))
            {
                PaletteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Raised after <see cref="Palette"/> changed.</summary>
    public event EventHandler? PaletteChanged;

    /// <summary>The colours, oldest first, with <paramref name="alpha"/>.</summary>
    public IReadOnlyList<InkColor> ColorsWithAlpha(byte alpha) =>
        _palette.Colors
            .Select(hex => HexColor.TryParse(hex, alpha, out var color) ? color : (InkColor?)null)
            .OfType<InkColor>()
            .ToArray();

    public void Add(InkColor color) => Palette = _palette.Add(HexColor.Format(color));

    public void Remove(InkColor color) => Palette = _palette.Remove(HexColor.Format(color));
}
