namespace DreamBooks.App.ViewModels;

/// <summary>
/// When the scroll rail shows and how opaque it is (UI-07, NFR-32): any activity (scroll, wheel, hover or touch near
/// the edge) fades it in (<see cref="RailTimings.FadeIn"/>), it stays while activity continues or while it is held
/// (drag, hover, typing a page number), and <see cref="RailTimings.Idle"/> after the last activity it fades out
/// (<see cref="RailTimings.FadeOut"/>); both fades ease out. With <see cref="ReducedMotion"/> (Windows animations
/// off) every change is instant. Pure: the time comes from the injected clock and is read on demand, so the view
/// asks for <see cref="Phase"/> and <see cref="Opacity"/> each frame and for <see cref="TimeToNextChange"/> to wait.
/// </summary>
public sealed class RailVisibilityState
{
    private readonly Func<TimeSpan> _clock;
    private bool _shown;
    private bool _held;
    private TimeSpan _fadeInStart;
    private double _fadeInFrom;
    private TimeSpan _lastActivity;

    public RailVisibilityState(Func<TimeSpan> clock, RailTimings timings)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        Timings = timings;
    }

    public RailTimings Timings { get; set; }

    /// <summary>True when Windows animations are off: fades become instant changes.</summary>
    public bool ReducedMotion { get; set; }

    /// <summary>While held the rail stays fully visible; the idle delay counts again from the release.</summary>
    public bool IsHeld
    {
        get => _held;
        set
        {
            if (value == _held)
            {
                return;
            }

            if (value)
            {
                Activity();
            }
            else
            {
                _lastActivity = _clock();
            }

            _held = value;
        }
    }

    public RailPhase Phase => Evaluate(_clock()).Phase;

    /// <summary>0 (hidden) to 1 (visible), eased.</summary>
    public double Opacity => Evaluate(_clock()).Opacity;

    /// <summary>Time until the phase changes by itself; null when hidden or held.</summary>
    public TimeSpan? TimeToNextChange
    {
        get
        {
            var now = _clock();
            var (phase, _) = Evaluate(now);
            return phase switch
            {
                RailPhase.FadingIn => FadeInEnd - now,
                RailPhase.Visible when !_held => FadeOutStart - now,
                RailPhase.FadingOut => FadeOutStart + FadeOutDuration - now,
                _ => null,
            };
        }
    }

    private TimeSpan FadeInDuration => ReducedMotion ? TimeSpan.Zero : Timings.FadeIn;

    private TimeSpan FadeOutDuration => ReducedMotion ? TimeSpan.Zero : Timings.FadeOut;

    private TimeSpan FadeInEnd => _fadeInStart + FadeInDuration;

    private TimeSpan FadeOutStart =>
        _lastActivity + Timings.Idle > FadeInEnd ? _lastActivity + Timings.Idle : FadeInEnd;

    /// <summary>Something happened near the rail: it shows (from its current opacity) and the idle delay restarts.</summary>
    public void Activity()
    {
        var now = _clock();
        var (phase, opacity) = Evaluate(now);
        if (phase is RailPhase.Hidden or RailPhase.FadingOut)
        {
            _fadeInStart = now;
            _fadeInFrom = opacity;
            _shown = true;
        }

        _lastActivity = now;
    }

    /// <summary>Hidden at once (no document any more); a later activity shows it again.</summary>
    public void Hide()
    {
        _shown = false;
        _held = false;
    }

    private static double EaseOut(double progress)
    {
        var rest = 1 - Math.Clamp(progress, 0, 1);
        return 1 - (rest * rest * rest);
    }

    private (RailPhase Phase, double Opacity) Evaluate(TimeSpan now)
    {
        if (!_shown)
        {
            return (RailPhase.Hidden, 0);
        }

        if (now < FadeInEnd)
        {
            var progress = (now - _fadeInStart) / FadeInDuration;
            return (RailPhase.FadingIn, _fadeInFrom + ((1 - _fadeInFrom) * EaseOut(progress)));
        }

        if (_held || now < FadeOutStart)
        {
            return (RailPhase.Visible, 1);
        }

        var fadeOutEnd = FadeOutStart + FadeOutDuration;
        if (now < fadeOutEnd)
        {
            return (RailPhase.FadingOut, 1 - EaseOut((now - FadeOutStart) / FadeOutDuration));
        }

        return (RailPhase.Hidden, 0);
    }
}
