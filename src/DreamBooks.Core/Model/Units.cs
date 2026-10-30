namespace DreamBooks.Core.Model;

public static class Units
{
    public const double DipPerPt = 96.0 / 72.0;

    public static double MmToPt(double mm) => mm / 25.4 * 72.0;

    public static double PtToMm(double pt) => pt / 72.0 * 25.4;
}
