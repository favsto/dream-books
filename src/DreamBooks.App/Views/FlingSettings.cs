using System.Windows;

namespace DreamBooks.App.Views;

/// <summary>
/// How a finger fling glides on after the lift (FR-05, NFR-03), from the theme tokens <c>FlingDeceleration</c>,
/// <c>FlingMinSpeed</c>, <c>FlingGainThreshold</c>, <c>FlingGainPerSpeed</c> and <c>FlingMaxSpeed</c> (the only place
/// these values live). Speeds are in DIP/ms, the deceleration in DIP/ms².
/// </summary>
/// <remarks>
/// The launch speed is the release speed times <c>1 + GainPerSpeed × (speed − GainThreshold)</c> above the threshold
/// (a mild gain: a hard flick travels proportionally further than a gentle one, so a book can be crossed by a few
/// flicks), capped at <see cref="MaxSpeed"/>; below <see cref="MinSpeed"/> a lift just stops. The glide then slows at
/// a constant <see cref="Deceleration"/>: it covers <c>launch² / (2 × deceleration)</c> in <c>launch / deceleration</c>
/// milliseconds.
/// </remarks>
internal readonly record struct FlingSettings(
    double Deceleration, double MinSpeed, double GainThreshold, double GainPerSpeed, double MaxSpeed)
{
    /// <summary>The settings from the theme dictionary; a missing or non-numeric token is an error.</summary>
    public static FlingSettings From(Func<string, object?> find)
    {
        ArgumentNullException.ThrowIfNull(find);
        return new FlingSettings(
            Require(find, "FlingDeceleration"),
            Require(find, "FlingMinSpeed"),
            Require(find, "FlingGainThreshold"),
            Require(find, "FlingGainPerSpeed"),
            Require(find, "FlingMaxSpeed"));
    }

    /// <summary>The speed the glide starts with for a release speed (0: no glide).</summary>
    public double LaunchSpeed(double releaseSpeed)
    {
        if (!double.IsFinite(releaseSpeed) || releaseSpeed < MinSpeed || releaseSpeed <= 0)
        {
            return 0;
        }

        var gain = 1 + (GainPerSpeed * Math.Max(0, releaseSpeed - GainThreshold));
        return Math.Min(MaxSpeed, releaseSpeed * gain);
    }

    /// <summary>The launch velocity for the finger's velocity at the lift: same direction, launch speed.</summary>
    public Vector LaunchVelocity(Vector release)
    {
        var speed = release.Length;
        var launch = LaunchSpeed(speed);
        return launch > 0 ? release * (launch / speed) : default;
    }

    /// <summary>How far a glide launched from <paramref name="releaseSpeed"/> travels, in DIPs.</summary>
    public double DistanceDip(double releaseSpeed)
    {
        var launch = LaunchSpeed(releaseSpeed);
        return Deceleration > 0 ? launch * launch / (2 * Deceleration) : 0;
    }

    private static double Require(Func<string, object?> find, string key) =>
        find(key) is double value && double.IsFinite(value) && value >= 0
            ? value
            : throw new InvalidOperationException($"The theme resource '{key}' is missing.");
}
