namespace DreamBooks.Pdf;

/// <summary>Chooses bitmap sizes for page renders, capping memory use at high zoom.</summary>
public static class RenderSizePolicy
{
    /// <summary>Maximum pixels per rendered page (about 96 MB as BGRA).</summary>
    public const long MaxPixels = 24_000_000;

    private const double _dipPerPoint = 96.0 / 72.0;

    public static (int Width, int Height) Compute(PageSize page, double zoom, double dpiScale)
    {
        RequirePositive(page.WidthPt, nameof(page));
        RequirePositive(page.HeightPt, nameof(page));
        RequirePositive(zoom, nameof(zoom));
        RequirePositive(dpiScale, nameof(dpiScale));

        double scale = _dipPerPoint * zoom * dpiScale;
        double w = page.WidthPt * scale;
        double h = page.HeightPt * scale;

        if (w * h > MaxPixels)
        {
            double factor = Math.Sqrt(MaxPixels / (w * h));
            w *= factor;
            h *= factor;
        }

        int width = Math.Max(1, (int)Math.Round(w));
        int height = Math.Max(1, (int)Math.Round(h));

        if ((long)width * height > MaxPixels)
        {
            width = Math.Max(1, (int)Math.Floor(w));
            height = Math.Max(1, (int)Math.Floor(h));
        }

        return (width, height);
    }

    private static void RequirePositive(double value, string paramName)
    {
        if (!(value > 0) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be a positive finite number.");
        }
    }
}
