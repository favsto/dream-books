using System.Windows;

namespace DreamBooks.App.Views;

/// <summary>
/// A fling gliding on after the finger lifted: a velocity (DIP/ms, the direction the content moves) whose speed decays
/// exponentially, <c>v(t) = v0·e^(−t/τ)</c>, until it falls below a minimum, integrated once per rendered frame
/// (<see cref="Step"/>) so the motion follows the screen's frames. The integration is exact for any frame length
/// (<c>τ·v·(1 − e^(−dt/τ))</c> per step; the last step ends where the speed reaches the minimum), so the distance and
/// duration do not depend on the frame rate.
/// </summary>
internal sealed class FlingGlide
{
    /// <summary>The first frame after the lift moves by one 60 Hz frame (its predecessor's time is unknown).</summary>
    internal static readonly TimeSpan FirstStep = TimeSpan.FromMilliseconds(1000.0 / 60);

    /// <summary>After a stall (no frame for a while) the glide moves at most this much time in one frame.</summary>
    internal static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(100);

    private Vector _direction;
    private double _speed;
    private double _timeConstantMs;
    private double _minSpeed;
    private TimeSpan? _last;

    public bool IsActive => _speed > 0;

    /// <summary>The current velocity in DIP/ms; zero when stopped.</summary>
    public Vector Velocity => _direction * _speed;

    /// <summary>Starts from <paramref name="velocity"/>; nothing when it is not faster than <paramref name="minSpeed"/>.</summary>
    public void Start(Vector velocity, double timeConstantMs, double minSpeed)
    {
        var speed = velocity.Length;
        if (!double.IsFinite(speed) || !double.IsFinite(timeConstantMs) || timeConstantMs <= 0
            || !double.IsFinite(minSpeed) || minSpeed <= 0 || speed <= minSpeed)
        {
            Stop();
            return;
        }

        _direction = velocity / speed;
        _speed = speed;
        _timeConstantMs = timeConstantMs;
        _minSpeed = minSpeed;
        _last = null;
    }

    public void Stop()
    {
        _speed = 0;
        _direction = default;
        _last = null;
    }

    /// <summary>
    /// The frame at <paramref name="now"/> (any monotonic clock): the translation since the previous frame. Nothing
    /// when stopped or when the time did not advance.
    /// </summary>
    public Vector Step(TimeSpan now)
    {
        if (!IsActive)
        {
            return default;
        }

        var elapsed = _last is { } last ? now - last : FirstStep;
        if (elapsed <= TimeSpan.Zero)
        {
            return default;
        }

        _last = now;
        var dt = Math.Min(elapsed.TotalMilliseconds, MaxStep.TotalMilliseconds);
        var tau = _timeConstantMs;
        var stopIn = tau * Math.Log(_speed / _minSpeed);
        if (dt >= stopIn)
        {
            var direction = _direction;
            var rest = tau * (_speed - _minSpeed);
            Stop();
            return direction * rest;
        }

        var next = _speed * Math.Exp(-dt / tau);
        var distance = tau * (_speed - next);
        _speed = next;
        return _direction * distance;
    }
}
