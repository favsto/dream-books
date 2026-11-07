using System.Globalization;
using System.IO;
using System.Text;
using DreamBooks.Core.Rendering;

namespace DreamBooks.App.Services;

/// <summary>
/// An optional measurement of NFR-01 (cold start) and NFR-03 (60 fps while scrolling and zooming) on the device. Off
/// unless the environment variable <see cref="Variable"/> is <c>1</c>. It counts frames and times only, and writes a
/// short text report when the app exits: no document name, path or content (NFR-12), no network (NFR-40). Never throws.
/// </summary>
public sealed class FramePerformanceProbe
{
    /// <summary>The environment variable that turns the probe on.</summary>
    public const string Variable = "DREAMBOOKS_PERF";

    private readonly DateTime _processStartUtc;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<double> _monotonicMs;
    private readonly string _path;
    // A frame drawn within this long after a pinch step belongs to the pinch.
    private const double _pinchWindowMs = 100;

    private readonly FrameTimeStats _stats = new();
    private readonly FrameTimeStats _pinchStats = new();
    private readonly FrameTimeStats _otherStats = new();
    private double _lastZoomMs = double.NegativeInfinity;
    private double? _lastFrameMs;
    private double? _firstFrameMs;
    private double? _readyMs;

    /// <param name="processStartUtc">When the process started.</param>
    /// <param name="utcNow">The time now (tests inject it).</param>
    /// <param name="monotonicMs">A monotonic clock in milliseconds, for the frame intervals.</param>
    /// <param name="path">Where <see cref="Write"/> puts the report.</param>
    public FramePerformanceProbe(DateTime processStartUtc, Func<DateTime> utcNow, Func<double> monotonicMs, string path)
    {
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(monotonicMs);
        ArgumentException.ThrowIfNullOrEmpty(path);
        _processStartUtc = processStartUtc;
        _utcNow = utcNow;
        _monotonicMs = monotonicMs;
        _path = path;
    }

    /// <summary>The running probe, or null when it is off (the views report a pinch step to it).</summary>
    public static FramePerformanceProbe? Active { get; set; }

    /// <summary>True only for the value <c>1</c>.</summary>
    public static bool IsRequested(string? value) => value == "1";

    /// <summary>The report file: <c>perf.txt</c> beside the app's other files under LocalAppData.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DreamBooks", "perf.txt");

    /// <summary>A frame was drawn (CompositionTarget.Rendering).</summary>
    public void OnFrame()
    {
        _firstFrameMs ??= (_utcNow() - _processStartUtc).TotalMilliseconds;
        var now = _monotonicMs();
        _stats.AddFrame(now);
        if (_lastFrameMs is { } previous && now >= previous)
        {
            // The interval belongs to the frame that ended it.
            (now - _lastZoomMs <= _pinchWindowMs ? _pinchStats : _otherStats).AddInterval(now - previous);
        }

        _lastFrameMs = now;
    }

    /// <summary>A pinch step was applied: the frames drawn soon after count as pinch frames in the report.</summary>
    public void NoteZoom() => _lastZoomMs = _monotonicMs();

    /// <summary>The main window finished starting up and painted: the start screen or the document is there.</summary>
    public void MarkReady() => _readyMs ??= (_utcNow() - _processStartUtc).TotalMilliseconds;

    /// <summary>Writes the report, replacing the last one. A failure is swallowed: a measurement must not break the app.</summary>
    public void Write()
    {
        try
        {
            var summary = _stats.Summary();
            var inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            text.AppendLine("Dream Books frame report (numbers only)");
            text.AppendLine(inv, $"startup to first frame: {_firstFrameMs ?? 0:0} ms");
            text.AppendLine(inv, $"startup to window ready: {_readyMs ?? 0:0} ms");
            text.AppendLine(inv, $"frames: {summary.Frames}");
            text.AppendLine(inv, $"continuous motion: {summary.Seconds:0.0} s");
            text.AppendLine(inv, $"median: {summary.MedianMs:0.0} ms");
            text.AppendLine(inv, $"95th percentile: {summary.P95Ms:0.0} ms");
            text.AppendLine(inv, $"99th percentile: {summary.P99Ms:0.0} ms");
            text.AppendLine(inv, $"slowest: {summary.MaxMs:0.0} ms");
            text.AppendLine(inv, $"over 25 ms: {summary.Over25Ms}");
            text.AppendLine(inv, $"over 50 ms: {summary.Over50Ms}");
            AppendPart(text, "pinch", _pinchStats.Summary());
            AppendPart(text, "other", _otherStats.Summary());
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // nothing to do: the report is a convenience
        }
    }

    private static void AppendPart(StringBuilder text, string name, FrameSummary part)
    {
        var inv = CultureInfo.InvariantCulture;
        text.AppendLine(inv, $"{name} frames: {part.Frames}");
        text.AppendLine(inv, $"{name} median: {part.MedianMs:0.0} ms");
        text.AppendLine(inv, $"{name} 95th percentile: {part.P95Ms:0.0} ms");
        text.AppendLine(inv, $"{name} slowest: {part.MaxMs:0.0} ms");
        text.AppendLine(inv, $"{name} over 25 ms: {part.Over25Ms}");
        text.AppendLine(inv, $"{name} over 50 ms: {part.Over50Ms}");
    }
}
