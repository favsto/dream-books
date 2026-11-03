using CommunityToolkit.Mvvm.ComponentModel;
using DreamBooks.Core.Model;
using DreamBooks.Core.Settings;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The free colour picker of the pen and highlighter flyouts (UI-02 custom palette): a hue (0 to 360 degrees), a
/// saturation and a brightness (0 to 1, the square), and the <c>#RRGGBB</c> code, kept in step. A code being typed that
/// is not valid yet stays as typed (<see cref="IsHexValid"/> false) and the colour keeps its last valid value; a valid
/// one also stays as typed (no reformatting under the caret) and gives exactly its colour, until <see cref="CommitHex"/>
/// writes it as <c>#RRGGBB</c> (the field losing the keyboard, Enter, Add). Colours are opaque: the tool decides the alpha (the highlighter's stays 89).
/// </summary>
public sealed class ColorPickerViewModel : ObservableObject
{
    private double _hue;
    private double _saturation;
    private double _brightness;
    private string _hex = "#000000";
    private bool _isHexValid = true;
    private InkColor? _typed;

    /// <summary>The hue in degrees, wrapped into 0..360.</summary>
    public double Hue
    {
        get => _hue;
        set => SetHsv(Wrap(value), _saturation, _brightness);
    }

    /// <summary>The saturation (the square's horizontal axis), clamped to 0..1.</summary>
    public double Saturation
    {
        get => _saturation;
        set => SetHsv(_hue, Math.Clamp(value, 0, 1), _brightness);
    }

    /// <summary>The brightness (the square's vertical axis, 1 at the top), clamped to 0..1.</summary>
    public double Brightness
    {
        get => _brightness;
        set => SetHsv(_hue, _saturation, Math.Clamp(value, 0, 1));
    }

    /// <summary>The colour chosen, opaque.</summary>
    public InkColor Color => _typed ?? FromHsv(_hue, _saturation, _brightness);

    /// <summary>The pure hue (full saturation and brightness): the square's background.</summary>
    public InkColor HueColor => FromHsv(_hue, 1, 1);

    /// <summary>The <c>#RRGGBB</c> code; setting a valid one (the '#' optional, any case) moves the colour to it.</summary>
    public string Hex
    {
        get => _hex;
        set
        {
            value ??= string.Empty;
            if (HexColor.TryParse(value, 255, out var color))
            {
                var (h, s, v) = ToHsv(color);
                SetHsv(h, s, v, value, color);
                return;
            }

            SetProperty(ref _hex, value);
            IsHexValid = false;
        }
    }

    /// <summary>False while the typed code is not a valid colour.</summary>
    public bool IsHexValid
    {
        get => _isHexValid;
        private set => SetProperty(ref _isHexValid, value);
    }

    /// <summary>Writes a valid typed code in the usual form, <c>#RRGGBB</c>; an invalid one stays as typed.</summary>
    public void CommitHex()
    {
        if (IsHexValid && HexColor.TryNormalize(_hex, out var normalized))
        {
            SetProperty(ref _hex, normalized, nameof(Hex));
        }
    }

    /// <summary>Starts the picker from <paramref name="color"/> (the colour in use).</summary>
    public void Reset(InkColor color)
    {
        var (h, s, v) = ToHsv(color);
        SetHsv(h, s, v);
    }

    /// <summary>The opaque colour of a hue (degrees), saturation and brightness (0..1).</summary>
    public static InkColor FromHsv(double hue, double saturation, double brightness)
    {
        var h = Wrap(hue) / 60;
        var s = Math.Clamp(saturation, 0, 1);
        var v = Math.Clamp(brightness, 0, 1);
        var chroma = v * s;
        var x = chroma * (1 - Math.Abs((h % 2) - 1));
        var (r, g, b) = (int)Math.Floor(h) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        var m = v - chroma;
        return InkColor.FromRgb(Channel(r + m), Channel(g + m), Channel(b + m));
    }

    /// <summary>The hue (degrees), saturation and brightness (0..1) of a colour; a grey has hue 0.</summary>
    public static (double Hue, double Saturation, double Brightness) ToHsv(InkColor color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                hue = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                hue = 60 * (((r - g) / delta) + 4);
            }
        }

        return (Wrap(hue), max == 0 ? 0 : delta / max, max);
    }

    private static double Wrap(double degrees)
    {
        if (!double.IsFinite(degrees))
        {
            return 0;
        }

        var wrapped = degrees % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }

    private static byte Channel(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);

    private void SetHsv(double hue, double saturation, double brightness, string? typed = null, InkColor? typedColor = null)
    {
        _typed = typedColor;
        var hueChanged = SetProperty(ref _hue, hue, nameof(Hue));
        SetProperty(ref _saturation, saturation, nameof(Saturation));
        SetProperty(ref _brightness, brightness, nameof(Brightness));
        OnPropertyChanged(nameof(Color));
        if (hueChanged)
        {
            OnPropertyChanged(nameof(HueColor));
        }

        SetProperty(ref _hex, typed ?? HexColor.Format(Color), nameof(Hex));
        IsHexValid = true;
    }
}
