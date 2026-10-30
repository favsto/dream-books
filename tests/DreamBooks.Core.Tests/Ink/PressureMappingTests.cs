using DreamBooks.Core.Ink;

namespace DreamBooks.Core.Tests.Ink;

public class PressureMappingTests
{
    [Theory]
    [InlineData(PressureCurve.Linear)]
    [InlineData(PressureCurve.Soft)]
    [InlineData(PressureCurve.Firm)]
    public void Width_spans_0_3x_to_1_5x(PressureCurve c)
    {
        Assert.Equal(0.3, PressureMapping.WidthAt(1, 0f, c), 9);
        Assert.Equal(1.5, PressureMapping.WidthAt(1, 1f, c), 9);
    }

    [Theory]
    [InlineData(PressureCurve.Linear)]
    [InlineData(PressureCurve.Soft)]
    [InlineData(PressureCurve.Firm)]
    public void Curves_are_monotonic(PressureCurve c)
    {
        var previous = PressureMapping.Apply(c, 0f);
        for (var i = 1; i <= 1000; i++)
        {
            var current = PressureMapping.Apply(c, i / 1000f);
            Assert.True(current >= previous);
            previous = current;
        }
    }

    [Fact]
    public void Soft_is_thicker_and_Firm_thinner_than_Linear_at_half_pressure()
    {
        var linear = PressureMapping.WidthAt(1, 0.5f, PressureCurve.Linear);
        Assert.True(PressureMapping.WidthAt(1, 0.5f, PressureCurve.Soft) > linear);
        Assert.True(PressureMapping.WidthAt(1, 0.5f, PressureCurve.Firm) < linear);
    }

    [Fact]
    public void Missing_pressure_uses_base_width()
    {
        Assert.Equal(2, PressureMapping.WidthAt(2, null, PressureCurve.Linear));
    }

    [Fact]
    public void Pressure_outside_range_is_clamped()
    {
        Assert.Equal(0.3, PressureMapping.WidthAt(1, -0.2f, PressureCurve.Linear), 9);
        Assert.Equal(1.5, PressureMapping.WidthAt(1, 1.4f, PressureCurve.Linear), 9);
    }
}
