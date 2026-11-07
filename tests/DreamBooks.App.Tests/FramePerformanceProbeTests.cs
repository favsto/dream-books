using DreamBooks.App.Services;

namespace DreamBooks.App.Tests;

/// <summary>The optional frame-time probe that measures NFR-01 and NFR-03 on the device (off unless asked for).</summary>
public sealed class FramePerformanceProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-perf-" + Guid.NewGuid().ToString("N"));

    public FramePerformanceProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("yes", false)]
    public void The_probe_is_off_unless_the_variable_is_1(string? value, bool expected)
        => Assert.Equal(expected, FramePerformanceProbe.IsRequested(value));

    [Fact]
    public void The_report_lists_startup_and_frame_numbers_and_nothing_else()
    {
        var started = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
        var now = started.AddMilliseconds(800);
        var clock = 0.0;
        var path = Path.Combine(_dir, "perf.txt");
        var probe = new FramePerformanceProbe(started, () => now, () => clock, path);

        probe.OnFrame(); // the first frame: 800 ms after the process started
        for (var i = 0; i < 100; i++)
        {
            clock += 16.67;
            probe.OnFrame();
        }

        clock += 1000; // idle: not a slow frame
        probe.OnFrame();
        clock += 70; // one clear hitch
        probe.OnFrame();
        probe.Write();

        var text = File.ReadAllText(path);
        Assert.Contains("startup to first frame: 800 ms", text);
        Assert.Contains("frames: 101", text); // 100 steady + the 70 ms one
        Assert.Contains("over 50 ms: 1", text);
        Assert.Contains("median: 16.7 ms", text);
        Assert.DoesNotContain(_dir, text); // no paths, no document names (NFR-12)
    }

    [Fact]
    public void Frames_during_a_pinch_are_reported_apart_from_the_rest()
    {
        var started = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
        var clock = 0.0;
        var path = Path.Combine(_dir, "perf.txt");
        var probe = new FramePerformanceProbe(started, () => started, () => clock, path);

        probe.OnFrame();
        for (var i = 0; i < 50; i++)
        {
            clock += 16.67; // plain scrolling: smooth
            probe.OnFrame();
        }

        for (var i = 0; i < 20; i++)
        {
            clock += 40; // pinching: slow
            probe.NoteZoom();
            probe.OnFrame();
        }

        probe.Write();

        var text = File.ReadAllText(path);
        Assert.Contains("pinch frames: 20", text);
        Assert.Contains("pinch over 25 ms: 20", text);
        Assert.Contains("other over 25 ms: 0", text);
        Assert.Contains("other frames: 50", text);
    }

    [Fact]
    public void Timings_are_kept_only_while_pinching_and_listed_by_name()
    {
        var started = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
        var clock = 0.0;
        var path = Path.Combine(_dir, "perf.txt");
        var probe = new FramePerformanceProbe(started, () => started, () => clock, path);

        probe.Record("layout", 99); // no pinch yet: not kept
        probe.NoteZoom();
        probe.Record("layout", 10);
        probe.Record("layout", 30);
        probe.Record("surfaces", 4);
        clock += 500; // the pinch is over
        probe.Record("layout", 99);
        probe.Write();

        var text = File.ReadAllText(path);
        Assert.Contains("pinch time layout: 2 calls", text);
        Assert.Contains("slowest 30.0 ms", text);
        Assert.Contains("pinch time surfaces: 1 calls", text);
    }

    [Fact]
    public void Writing_to_an_impossible_path_never_throws()
    {
        var probe = new FramePerformanceProbe(DateTime.UtcNow, () => DateTime.UtcNow, () => 0, Path.Combine(_dir, "no", "such\0dir", "perf.txt"));
        probe.OnFrame();

        var exception = Record.Exception(probe.Write);

        Assert.Null(exception);
    }
}
