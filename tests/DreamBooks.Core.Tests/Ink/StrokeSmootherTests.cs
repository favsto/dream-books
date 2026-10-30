using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Ink;

public class StrokeSmootherTests
{
    private static InkPoint P(double x, double y, float? pressure = 0.5f, long t = 0) =>
        new(x, y, pressure, null, null, null, t);

    private static List<InkPoint> Wave()
    {
        var list = new List<InkPoint>();
        for (var i = 0; i < 40; i++)
        {
            list.Add(P(i * 0.8, 5 * Math.Sin(i * 0.3), 0.5f, i * 8L));
        }

        return list;
    }

    [Fact]
    public void Off_passes_through_every_input_point_in_order()
    {
        var input = Wave();
        var output = StrokeSmoother.Smooth(input, SmoothingLevel.Off);
        var next = 0;
        foreach (var o in output)
        {
            if (next < input.Count && o == input[next])
            {
                next++;
            }
        }

        Assert.Equal(input.Count, next);
    }

    [Theory]
    [InlineData(SmoothingLevel.Off)]
    [InlineData(SmoothingLevel.Light)]
    [InlineData(SmoothingLevel.Strong)]
    public void Endpoints_are_preserved(SmoothingLevel l)
    {
        var input = Wave();
        var output = StrokeSmoother.Smooth(input, l);
        Assert.Equal(input[0], output[0]);
        Assert.Equal(input[^1], output[^1]);
    }

    [Theory]
    [InlineData(SmoothingLevel.Off)]
    [InlineData(SmoothingLevel.Light)]
    [InlineData(SmoothingLevel.Strong)]
    public void Consecutive_points_are_at_most_half_a_point_apart(SmoothingLevel l)
    {
        var output = StrokeSmoother.Smooth(Wave(), l);
        for (var i = 1; i < output.Count; i++)
        {
            Assert.True(PointD.Distance(output[i - 1].Position, output[i].Position) <= 0.5 + 1e-9);
        }
    }

    [Fact]
    public void Strong_keeps_sharp_corner()
    {
        var input = new List<InkPoint>();
        for (var i = 0; i <= 10; i++)
        {
            input.Add(P(i, 0));
        }

        for (var i = 1; i <= 10; i++)
        {
            input.Add(P(10, i));
        }

        var output = StrokeSmoother.Smooth(input, SmoothingLevel.Strong);
        Assert.Contains(output, o => PointD.Distance(o.Position, new PointD(10, 0)) < 0.05);
    }

    [Fact]
    public void Single_point_returns_single_point()
    {
        var output = StrokeSmoother.Smooth([P(3, 4)], SmoothingLevel.Strong);
        Assert.Single(output);
        Assert.Equal(P(3, 4), output[0]);
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Empty(StrokeSmoother.Smooth([], SmoothingLevel.Light));
    }

    [Theory]
    [InlineData(SmoothingLevel.Off)]
    [InlineData(SmoothingLevel.Light)]
    [InlineData(SmoothingLevel.Strong)]
    public void Duplicate_points_produce_no_NaN(SmoothingLevel l)
    {
        var input = new List<InkPoint> { P(0, 0), P(0, 0), P(0, 0), P(5, 0), P(5, 0), P(5, 5), P(5, 5) };
        var output = StrokeSmoother.Smooth(input, l);
        Assert.All(output, o =>
        {
            Assert.False(double.IsNaN(o.X) || double.IsNaN(o.Y));
            Assert.False(double.IsInfinity(o.X) || double.IsInfinity(o.Y));
        });
    }

    [Fact]
    public void Pressure_is_interpolated_between_samples()
    {
        var input = new List<InkPoint> { P(0, 0, 0f, 0), P(10, 0, 1f, 100) };
        var output = StrokeSmoother.Smooth(input, SmoothingLevel.Off);
        Assert.True(output.Count > 2);
        for (var i = 1; i < output.Count; i++)
        {
            Assert.True(output[i].Pressure >= output[i - 1].Pressure);
            Assert.True(output[i].TimestampMs >= output[i - 1].TimestampMs);
        }

        var mid = output[output.Count / 2];
        Assert.InRange(mid.Pressure!.Value, 0.4f, 0.6f);
    }

    [Fact]
    public void Missing_pressure_stays_missing()
    {
        var input = new List<InkPoint> { P(0, 0, null), P(10, 0, null), P(20, 5, null) };
        var output = StrokeSmoother.Smooth(input, SmoothingLevel.Light);
        Assert.All(output, o => Assert.Null(o.Pressure));
    }

    [Fact]
    public void Light_reduces_jitter()
    {
        var input = new List<InkPoint>();
        for (var i = 0; i < 30; i++)
        {
            input.Add(P(i, i % 2 == 0 ? 0.3 : -0.3));
        }

        var output = StrokeSmoother.Smooth(input, SmoothingLevel.Light);
        Assert.True(output.Skip(1).SkipLast(1).Max(o => Math.Abs(o.Y)) < 0.3);
    }

    [Fact]
    public void Long_stroke_is_handled()
    {
        var input = new List<InkPoint>();
        for (var i = 0; i < 20000; i++)
        {
            input.Add(P(i * 0.3, Math.Sin(i * 0.01) * 20));
        }

        var output = StrokeSmoother.Smooth(input, SmoothingLevel.Strong);
        Assert.True(output.Count >= input.Count);
    }

    [Theory]
    [InlineData(SmoothingLevel.Off)]
    [InlineData(SmoothingLevel.Light)]
    [InlineData(SmoothingLevel.Strong)]
    public void Dot_keeps_max_pressure(SmoothingLevel level)
    {
        var output = StrokeSmoother.Smooth([P(5, 5, 0.1f, 0), P(5, 5, 0.5f, 8), P(5, 5, 0.9f, 16)], level);

        var point = Assert.Single(output);
        Assert.Equal(0.9f, point.Pressure);
        Assert.Equal(16, point.TimestampMs);
    }

    [Fact]
    public void Duplicate_cluster_inside_stroke_keeps_max_pressure()
    {
        var output = StrokeSmoother.Smooth([P(0, 0, 0.1f), P(0, 0, 0.9f), P(0, 0.001, 0.5f), P(5, 0, 0.3f)], SmoothingLevel.Off);

        Assert.Equal(0.9f, output[0].Pressure);
    }

    [Fact]
    public void Trailing_duplicate_cluster_carries_max_pressure()
    {
        var output = StrokeSmoother.Smooth([P(0, 0, 0.2f), P(5, 0, 0.1f, 0), P(5, 0, 0.8f, 9)], SmoothingLevel.Off);

        Assert.Equal(0.8f, output[^1].Pressure);
        Assert.Equal(9, output[^1].TimestampMs);
    }

    [Fact]
    public void Null_pressure_cluster_stays_null()
    {
        var output = StrokeSmoother.Smooth([P(5, 5, null), P(5, 5, null)], SmoothingLevel.Off);

        Assert.Null(Assert.Single(output).Pressure);
    }
}
