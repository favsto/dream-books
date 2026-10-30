namespace DreamBooks.Core.Model;

public readonly record struct InkColor(byte A, byte R, byte G, byte B)
{
    public static InkColor FromRgb(byte r, byte g, byte b) => new(255, r, g, b);
}
