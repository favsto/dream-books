using System.Windows;

namespace DreamBooks.App.Views;

/// <summary>
/// A fling gliding on after the finger lifted: a velocity (DIP/ms, the direction the content moves) that slows at a
/// constant deceleration, integrated once per rendered frame (<see cref="Step"/>), so the motion follows the screen's
/// frames exactly. The integration is exact for constant deceleration (the last step ends where the speed reaches 0).
/// </summary>
internal sealed class FlingGlide
{
    /// <summary>The first frame after the lift moves by one 60 Hz frame (its predecessor's time is unknown).</summary>
    internal static readonly TimeSpan FirstStep = TimeSpan.FromMilliseconds(1000.0 / 60);

    /// <summary>After a stall (no frame for a while) the glide moves at most this much time in one frame.</summary>
    internal static readonly TimeSpan MaxStep = TimeSpan.FromMilliseconds(100);

    private Vector _direction;
    private double _speed;
    private double _deceleration;
    private TimeSpan? _last;

    public bool IsActive => _speed > 0;

    /// <summary>The current velocity in DIP/ms; zero when stopped.</summary>
    public Vector Velocity => _direction * _speed;

    public void Start(Vector velocity, double deceleration)
    {
        var speed = velocity.Length;
        if (!double.IsFinite(speed) || speed <= 0 || !double.IsFinite(deceleration) || deceleration <= 0)
        {
            Stop();
            return;
        }

        _direction = velocity / speed;
        _speed = speed;
        _deceleration = deceleration;
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
        var stopIn = _speed / _deceleration;
        double distance;
        if (dt >= stopIn)
        {
            distance = _speed * stopIn / 2;
            var direction = _direction;
            Stop();
            return direction * distance;
        }

        var next = _speed - (_deceleration * dt);
        distance = (_speed + next) / 2 * dt;
        _speed = next;
        return _direction * distance;
    }
}
