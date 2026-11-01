using System.Windows;
using DreamBooks.App.Views;

namespace DreamBooks.App.Tests;

/// <summary>
/// The finger fling's physics (FR-05, NFR-03): the launch speed from the release speed (a mild gain above a threshold,
/// a cap, nothing below a minimum), constant deceleration, and the per-frame integration the view runs.
/// </summary>
public sealed class FlingGlideTests
{
    private static readonly FlingSettings _settings = new(
        Deceleration: 0.002, MinSpeed: 0.2, GainThreshold: 1, GainPerSpeed: 0.1, MaxSpeed: 16);

    private static readonly TimeSpan _frame = TimeSpan.FromMilliseconds(1000.0 / 60);

    [Theory]
    [InlineData(1, 1, 250)]
    [InlineData(3, 3.6, 3240)]
    [InlineData(8, 13.6, 46240)]
    [InlineData(12, 16, 64000)] // capped
    public void Release_speed_gives_the_documented_launch_speed_and_distance(double release, double launch, double distance)
    {
        Assert.Equal(launch, _settings.LaunchSpeed(release), 9);
        Assert.Equal(distance, _settings.DistanceDip(release), 6);
    }

    [Fact]
    public void A_slow_lift_does_not_glide_and_bad_input_is_ignored()
    {
        Assert.Equal(0, _settings.LaunchSpeed(0.19));
        Assert.Equal(0, _settings.LaunchSpeed(double.NaN));
        Assert.Equal(default, _settings.LaunchVelocity(new Vector(0.1, -0.1)));
        Assert.Equal(default, _settings.LaunchVelocity(new Vector(double.PositiveInfinity, 1)));
        Assert.Equal(0.5, _settings.LaunchSpeed(0.5), 9); // no gain below the threshold
    }

    [Fact]
    public void Launch_velocity_keeps_the_direction_of_the_release()
    {
        var launch = _settings.LaunchVelocity(new Vector(3, -4)); // 5 DIP/ms
        Assert.Equal(_settings.LaunchSpeed(5), launch.Length, 9);
        Assert.Equal(0.6, launch.X / launch.Length, 9);
        Assert.Equal(-0.8, launch.Y / launch.Length, 9);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void Frames_add_up_to_the_distance_and_every_frame_moves_less_than_the_one_before(double release)
    {
        var glide = new FlingGlide();
        glide.Start(_settings.LaunchVelocity(new Vector(0, -release)), _settings.Deceleration);
        var now = TimeSpan.FromSeconds(3);
        var total = new Vector();
        var previous = double.MaxValue;
        var frames = 0;
        while (glide.IsActive)
        {
            var step = glide.Step(now);
            now += _frame;
            frames++;
            Assert.True(step.X == 0 && step.Y <= 0, $"frame {frames}: {step}");
            Assert.True(step.Length <= previous + 1e-9, $"frame {frames} sped up");
            previous = step.Length;
            total += step;
            Assert.True(frames < 2000);
        }

        Assert.Equal(-_settings.DistanceDip(release), total.Y, 6);
        var expectedMs = _settings.LaunchSpeed(release) / _settings.Deceleration;
        Assert.InRange(frames * _frame.TotalMilliseconds, expectedMs - (2 * _frame.TotalMilliseconds), expectedMs + (2 * _frame.TotalMilliseconds));
    }

    [Fact]
    public void The_first_frame_moves_at_once_and_repeated_or_stopped_frames_do_not()
    {
        var glide = new FlingGlide();
        Assert.False(glide.IsActive);
        Assert.Equal(default, glide.Step(TimeSpan.FromSeconds(1)));

        glide.Start(new Vector(0, 2), 0.002);
        var first = glide.Step(TimeSpan.FromSeconds(1));
        Assert.True(first.Y > 30, $"{first}"); // about one frame at 2 DIP/ms: no frame without motion after the lift
        Assert.Equal(default, glide.Step(TimeSpan.FromSeconds(1))); // the same frame time again
        Assert.Equal(default, glide.Step(TimeSpan.FromSeconds(0.5))); // a clock going back

        glide.Stop();
        Assert.False(glide.IsActive);
        Assert.Equal(default, glide.Velocity);
        Assert.Equal(default, glide.Step(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void A_long_stall_between_frames_moves_at_most_one_capped_step()
    {
        var glide = new FlingGlide();
        glide.Start(new Vector(0, 10), 0.002);
        glide.Step(TimeSpan.FromSeconds(1));
        var step = glide.Step(TimeSpan.FromSeconds(3)); // two seconds without a frame
        Assert.True(step.Y <= 10 * FlingGlide.MaxStep.TotalMilliseconds, $"{step}");
        Assert.True(glide.IsActive);
    }

    [Fact]
    public void Settings_come_from_the_theme_tokens_and_a_missing_one_is_an_error()
    {
        var values = new Dictionary<string, object>
        {
            ["FlingDeceleration"] = 0.002,
            ["FlingMinSpeed"] = 0.2,
            ["FlingGainThreshold"] = 1.0,
            ["FlingGainPerSpeed"] = 0.1,
            ["FlingMaxSpeed"] = 16.0,
        };
        Assert.Equal(_settings, FlingSettings.From(key => values.GetValueOrDefault(key)));

        values.Remove("FlingMaxSpeed");
        Assert.Throws<InvalidOperationException>(() => FlingSettings.From(key => values.GetValueOrDefault(key)));
    }
}
