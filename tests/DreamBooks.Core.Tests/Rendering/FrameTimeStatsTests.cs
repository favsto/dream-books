using DreamBooks.Core.Rendering;

namespace DreamBooks.Core.Tests.Rendering;

public class FrameTimeStatsTests
{
    private static FrameTimeStats Frames(params double[] intervalsMs)
    {
        var stats = new FrameTimeStats();
        var now = 0.0;
        stats.AddFrame(now);
        foreach (var interval in intervalsMs)
        {
            now += interval;
            stats.AddFrame(now);
        }

        return stats;
    }

    [Fact]
    public void An_interval_added_directly_counts_like_one_between_two_frames()
    {
        var stats = new FrameTimeStats();
        stats.AddInterval(40);
        stats.AddInterval(300); // idle: skipped
        stats.AddInterval(-5); // nonsense: skipped

        var summary = stats.Summary();

        Assert.Equal(1, summary.Frames);
        Assert.Equal(40, summary.MaxMs);
        Assert.Equal(1, summary.Over25Ms);
    }

    [Fact]
    public void Steady_60_fps_has_a_16_7_ms_median_and_no_slow_frames()
    {
        var summary = Frames(Enumerable.Repeat(16.67, 120).ToArray()).Summary();

        Assert.Equal(120, summary.Frames);
        Assert.Equal(16.67, summary.MedianMs, 2);
        Assert.Equal(16.67, summary.P95Ms, 2);
        Assert.Equal(0, summary.Over25Ms);
        Assert.Equal(0, summary.Over50Ms);
        Assert.Equal(2.0, summary.Seconds, 1);
    }

    [Fact]
    public void An_idle_gap_between_two_bursts_is_not_a_slow_frame()
    {
        var stats = new FrameTimeStats();
        foreach (var t in new[] { 0.0, 16, 32, 1000, 1016 })
        {
            stats.AddFrame(t);
        }

        var summary = stats.Summary();

        Assert.Equal(3, summary.Frames); // 0→16, 16→32 and 1000→1016; 32→1000 is idle
        Assert.Equal(16, summary.MaxMs);
        Assert.Equal(0, summary.Over50Ms);
    }

    [Fact]
    public void Slow_frames_are_counted_by_threshold()
    {
        var summary = Frames(16, 16, 30, 60, 16).Summary();

        Assert.Equal(2, summary.Over25Ms); // 30 and 60
        Assert.Equal(1, summary.Over50Ms); // 60
        Assert.Equal(60, summary.MaxMs);
    }

    [Fact]
    public void P95_is_the_nearest_rank_value()
    {
        var intervals = Enumerable.Repeat(16.0, 94).Concat(Enumerable.Repeat(40.0, 6)).ToArray();

        var summary = Frames(intervals).Summary();

        Assert.Equal(40, summary.P95Ms);
        Assert.Equal(16, summary.MedianMs);
        Assert.Equal(40, summary.P99Ms);
    }

    [Fact]
    public void No_frames_gives_zeros()
    {
        var summary = new FrameTimeStats().Summary();

        Assert.Equal(0, summary.Frames);
        Assert.Equal(0, summary.MedianMs);
        Assert.Equal(0, summary.MaxMs);
    }

    [Fact]
    public void A_timestamp_that_goes_backwards_is_ignored()
    {
        var stats = new FrameTimeStats();
        foreach (var t in new[] { 100.0, 116, 90, 132 })
        {
            stats.AddFrame(t);
        }

        var summary = stats.Summary();

        Assert.Equal(2, summary.Frames); // 100→116 and 116→132
        Assert.Equal(16, summary.MaxMs);
    }

    [Fact]
    public void Frames_are_capped_so_a_long_session_uses_bounded_memory()
    {
        var stats = new FrameTimeStats(maxIntervals: 1000);
        for (var i = 0; i < 5000; i++)
        {
            stats.AddFrame(i * 16.0);
        }

        Assert.Equal(1000, stats.Summary().Frames);
    }
}
