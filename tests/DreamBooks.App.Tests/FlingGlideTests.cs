using System.Windows;
using DreamBooks.App.Views;

namespace DreamBooks.App.Tests;

/// <summary>
/// The finger fling's physics (FR-05, NFR-03): like a phone, the speed decays exponentially, v(t) = v0·e^(−t/τ), until
/// it falls below the minimum; the launch speed is the release speed, capped, and a release after the finger rested is
/// no fling. The per-frame integration is exact, so the distance does not depend on the frame rate.
/// </summary>
public sealed class FlingGlideTests
{
    private static readonly FlingSettings _settings = new(DecayTimeConstantMs: 325, MinSpeed: 0.05, MaxSpeed: 10);

    [Theory]
    [InlineData(1, 1, 308.75, 973.61)]
    [InlineData(2, 2, 633.75, 1198.89)]
    [InlineData(3, 3, 958.75, 1330.66)]
    [InlineData(4, 4, 1283.75, 1424.16)]
    [InlineData(8, 8, 2583.75, 1649.43)]
    [InlineData(10, 10, 3233.75, 1721.95)]
    [InlineData(25, 10, 3233.75, 1721.95)] // a spike is capped
    public void Release_speed_gives_the_documented_launch_distance_and_duration(
        double release, double launch, double distance, double durationMs)
    {
        Assert.Equal(launch, _settings.LaunchSpeed(release), 9);
        Assert.Equal(distance, _settings.DistanceDip(release), 6);
        Assert.InRange(_settings.DurationMs(release), durationMs - 0.1, durationMs + 0.1);
        Assert.True(_settings.DurationMs(release) < 2000, "nothing glides longer than about 2 s");
    }

    [Fact]
    public void A_slow_lift_does_not_glide_and_bad_input_is_ignored()
    {
        Assert.Equal(0, _settings.LaunchSpeed(0.05));
        Assert.Equal(0, _settings.LaunchSpeed(double.NaN));
        Assert.Equal(0, _settings.DistanceDip(0.01));
        Assert.Equal(default, _settings.LaunchVelocity(new Vector(0.03, -0.03)));
        Assert.Equal(default, _settings.LaunchVelocity(new Vector(double.PositiveInfinity, 1)));
        Assert.Equal(0.5, _settings.LaunchSpeed(0.5), 9); // no gain
    }

    [Fact]
    public void A_release_after_the_finger_rested_is_no_fling()
    {
        var release = new Vector(0, -3);
        Assert.Equal(release, _settings.LaunchVelocity(release, restedFor: TimeSpan.FromMilliseconds(50)));
        Assert.Equal(release, _settings.LaunchVelocity(release, restedFor: FlingSettings.StaleRelease));
        Assert.Equal(default, _settings.LaunchVelocity(release, restedFor: FlingSettings.StaleRelease + TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void Launch_velocity_keeps_the_direction_and_a_spike_is_capped()
    {
        var launch = _settings.LaunchVelocity(new Vector(30, -40)); // 50 DIP/ms: a digitizer spike
        Assert.Equal(10, launch.Length, 9);
        Assert.Equal(0.6, launch.X / launch.Length, 9);
        Assert.Equal(-0.8, launch.Y / launch.Length, 9);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(1, 60)]
    [InlineData(1, 144)]
    [InlineData(3, 30)]
    [InlineData(3, 60)]
    [InlineData(3, 144)]
    [InlineData(8, 30)]
    [InlineData(8, 60)]
    [InlineData(8, 144)]
    public void The_distance_and_duration_do_not_depend_on_the_frame_rate(double release, double hz)
    {
        var frame = TimeSpan.FromSeconds(1 / hz);
        var glide = new FlingGlide();
        glide.Start(_settings.LaunchVelocity(new Vector(0, -release)), _settings.DecayTimeConstantMs, _settings.MinSpeed);
        var now = TimeSpan.FromSeconds(3);
        var total = new Vector();
        var previous = double.MaxValue;
        var frames = 0;
        while (glide.IsActive)
        {
            var step = glide.Step(now);
            now += frame;
            frames++;
            Assert.True(step.X == 0 && step.Y <= 0, $"frame {frames}: {step}");
            Assert.True(step.Length <= previous + 1e-9 || frames == 2, $"frame {frames} sped up"); // the first frame is nominal
            previous = step.Length;
            total += step;
            Assert.True(frames < 2000);
        }

        var expected = _settings.DistanceDip(release);
        Assert.InRange(-total.Y, expected * 0.99, expected * 1.01);
        var elapsedMs = frames * frame.TotalMilliseconds;
        var slack = (2 * frame.TotalMilliseconds) + FlingGlide.FirstStep.TotalMilliseconds;
        Assert.InRange(elapsedMs, _settings.DurationMs(release) - slack, _settings.DurationMs(release) + slack);
    }

    [Fact]
    public void Speed_decays_exponentially()
    {
        var glide = new FlingGlide();
        glide.Start(new Vector(0, 4), 325, 0.05);
        glide.Step(TimeSpan.FromSeconds(1)); // the first, nominal frame
        var speedAfterFirst = glide.Velocity.Length;
        for (var ms = 25; ms <= 325; ms += 25)
        {
            glide.Step(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(ms));
        }

        Assert.Equal(speedAfterFirst / Math.E, glide.Velocity.Length, 9); // one time constant later: 1/e
    }

    [Fact]
    public void The_first_frame_moves_at_once_and_repeated_or_stopped_frames_do_not()
    {
        var glide = new FlingGlide();
        Assert.False(glide.IsActive);
        Assert.Equal(default, glide.Step(TimeSpan.FromSeconds(1)));

        glide.Start(new Vector(0, 2), 325, 0.05);
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
        glide.Start(new Vector(0, 10), 325, 0.05);
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
            ["FlingDecayTimeConstant"] = 325.0,
            ["FlingMinSpeed"] = 0.05,
            ["FlingMaxSpeed"] = 10.0,
        };
        Assert.Equal(_settings, FlingSettings.From(key => values.GetValueOrDefault(key)));

        values.Remove("FlingMaxSpeed");
        Assert.Throws<InvalidOperationException>(() => FlingSettings.From(key => values.GetValueOrDefault(key)));
    }
}
