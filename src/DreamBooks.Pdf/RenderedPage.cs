namespace DreamBooks.Pdf;

/// <summary>
/// A rendered page as 32-bit BGRA pixels (premultiplied-free, opaque white background).
/// Rows are <see cref="Stride"/> bytes apart; <see cref="Bgra"/> holds Stride * PixelHeight bytes.
/// </summary>
public sealed record RenderedPage(int PixelWidth, int PixelHeight, int Stride, byte[] Bgra);
