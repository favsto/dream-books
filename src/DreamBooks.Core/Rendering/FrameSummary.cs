namespace DreamBooks.Core.Rendering;

/// <summary>What <see cref="FrameTimeStats"/> reports: how smooth the continuous motion was (NFR-03).</summary>
/// <param name="Frames">Frame intervals counted (the most recent ones, within the cap).</param>
/// <param name="MedianMs">The typical frame interval; about 16.7 at 60 fps.</param>
/// <param name="P95Ms">95% of the frames were at least this quick.</param>
/// <param name="P99Ms">99% of the frames were at least this quick.</param>
/// <param name="MaxMs">The slowest frame.</param>
/// <param name="Over25Ms">Frames slower than 25 ms (under 40 fps): visible as a stutter.</param>
/// <param name="Over50Ms">Frames slower than 50 ms (under 20 fps): a clear hitch.</param>
/// <param name="Seconds">Total time of the counted frames.</param>
public sealed record FrameSummary(
    int Frames, double MedianMs, double P95Ms, double P99Ms, double MaxMs, int Over25Ms, int Over50Ms, double Seconds);
