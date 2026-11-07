namespace DreamBooks.Core.Rendering;

/// <summary>
/// Frame intervals of continuous motion (scrolling, zooming), to check NFR-03 on the device. WPF draws only when
/// something changes, so a long gap between two frames means nothing moved, not a slow frame: gaps longer than
/// <c>continuousGapMs</c> are skipped. Keeps only the most recent <c>maxIntervals</c> intervals, so a long session
/// uses bounded memory. Pure and time-agnostic: the caller passes monotonic timestamps in milliseconds.
/// </summary>
public sealed class FrameTimeStats
{
    private readonly double _continuousGapMs;
    private readonly int _maxIntervals;
    private readonly Queue<double> _intervals = new();
    private double? _last;

    public FrameTimeStats(double continuousGapMs = 250, int maxIntervals = 20_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(continuousGapMs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxIntervals, 0);
        _continuousGapMs = continuousGapMs;
        _maxIntervals = maxIntervals;
    }

    /// <summary>A frame was drawn at <paramref name="timestampMs"/>; a timestamp that goes backwards is ignored.</summary>
    public void AddFrame(double timestampMs)
    {
        if (_last is { } last)
        {
            if (timestampMs < last)
            {
                return;
            }

            AddInterval(timestampMs - last);
        }

        _last = timestampMs;
    }

    /// <summary>
    /// One frame interval, for a caller that sorts frames into several series and so tracks the timestamps itself. An
    /// idle gap (longer than the continuous gap) or a negative value is ignored.
    /// </summary>
    public void AddInterval(double intervalMs)
    {
        if (intervalMs < 0 || intervalMs > _continuousGapMs)
        {
            return;
        }

        _intervals.Enqueue(intervalMs);
        if (_intervals.Count > _maxIntervals)
        {
            _intervals.Dequeue();
        }
    }

    public FrameSummary Summary()
    {
        if (_intervals.Count == 0)
        {
            return new FrameSummary(0, 0, 0, 0, 0, 0, 0, 0);
        }

        var sorted = _intervals.Order().ToArray();
        return new FrameSummary(
            sorted.Length,
            Percentile(sorted, 0.5),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1],
            sorted.Count(i => i > 25),
            sorted.Count(i => i > 50),
            sorted.Sum() / 1000);
    }

    /// <summary>The nearest-rank percentile of an ascending array.</summary>
    private static double Percentile(double[] sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}
