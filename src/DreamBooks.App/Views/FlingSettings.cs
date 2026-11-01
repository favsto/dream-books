using System.Windows;

namespace DreamBooks.App.Views;

/// <summary>
/// How a finger fling glides on after the lift (FR-05, NFR-03), from the theme tokens <c>FlingDecayTimeConstant</c>
/// (ms), <c>FlingMinSpeed</c> and <c>FlingMaxSpeed</c> (DIP/ms), the only place these values live.
/// </summary>
/// <remarks>
/// Like a phone, the speed decays exponentially, <c>v(t) = v0·e^(−t/τ)</c>, and the glide ends when it falls below
/// <see cref="MinSpeed"/>: it covers <c>τ·(v0 − vmin)</c> in <c>τ·ln(v0 / vmin)</c>. The launch speed is the release
/// speed (no gain), capped at <see cref="MaxSpeed"/> so a digitizer spike cannot throw the document away; a release
/// after the finger rested longer than <see cref="StaleRelease"/> is no fling.
/// </remarks>
internal readonly record struct FlingSettings(double DecayTimeConstantMs, double MinSpeed, double MaxSpeed)
{
    /// <summary>A finger that rested this long before lifting has no fling, whatever velocity WPF still reports.</summary>
    public static readonly TimeSpan StaleRelease = TimeSpan.FromMilliseconds(100);

    /// <summary>The settings from the theme dictionary; a missing or non-numeric token is an error.</summary>
    public static FlingSettings From(Func<string, object?> find)
    {
        ArgumentNullException.ThrowIfNull(find);
        return new FlingSettings(
            Require(find, "FlingDecayTimeConstant"), Require(find, "FlingMinSpeed"), Require(find, "FlingMaxSpeed"));
    }

    /// <summary>The speed the glide starts with for a release speed (0: no glide).</summary>
    public double LaunchSpeed(double releaseSpeed) =>
        double.IsFinite(releaseSpeed) && releaseSpeed > MinSpeed ? Math.Min(MaxSpeed, releaseSpeed) : 0;

    /// <summary>
    /// The launch velocity for the finger's velocity at the lift: same direction, launch speed; nothing when the finger
    /// had rested for longer than <see cref="StaleRelease"/> before lifting.
    /// </summary>
    public Vector LaunchVelocity(Vector release, TimeSpan restedFor = default)
    {
        if (restedFor > StaleRelease)
        {
            return default;
        }

        var speed = release.Length;
        var launch = LaunchSpeed(speed);
        return launch > 0 ? release * (launch / speed) : default;
    }

    /// <summary>How far a glide launched from <paramref name="releaseSpeed"/> travels, in DIPs.</summary>
    public double DistanceDip(double releaseSpeed)
    {
        var launch = LaunchSpeed(releaseSpeed);
        return launch > 0 ? DecayTimeConstantMs * (launch - MinSpeed) : 0;
    }

    /// <summary>How long a glide launched from <paramref name="releaseSpeed"/> lasts, in milliseconds.</summary>
    public double DurationMs(double releaseSpeed)
    {
        var launch = LaunchSpeed(releaseSpeed);
        return launch > 0 ? DecayTimeConstantMs * Math.Log(launch / MinSpeed) : 0;
    }

    private static double Require(Func<string, object?> find, string key) =>
        find(key) is double value && double.IsFinite(value) && value > 0
            ? value
            : throw new InvalidOperationException($"The theme resource '{key}' is missing.");
}
