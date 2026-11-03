namespace DreamBooks.App.ViewModels;

/// <summary>
/// The toolbar's automatic visibility (section 4 table, section 3 animations, NFR-32). Pure: the time comes from the
/// injected clock and is read on demand, so the owner asks for <see cref="Phase"/> and waits
/// <see cref="TimeToNextChange"/> before asking again.
/// <list type="bullet">
/// <item>Writing: after <see cref="WritingBeforeRetract"/> of pen-down time the toolbar retracts, fading out over
/// <see cref="RetractDuration"/>. The pen-down time adds up over strokes separated by lifts shorter than
/// <see cref="PenUpBeforeShow"/> (handwriting is many short strokes); a longer lift starts the count again.</item>
/// <item>Pen up: a toolbar the writing retracted shows again <see cref="PenUpBeforeShow"/> after the last lift.</item>
/// <item>A finger tap on an empty area toggles it: a visible toolbar fades out, a hidden or retracting one shows at
/// once (and the writing count starts again). A toolbar hidden by a tap stays hidden after writing.</item>
/// <item><see cref="IsFixed"/> keeps it visible whatever happens; <see cref="IsRetractedByWriting"/> still says when
/// the writing would have retracted it, so the panels close as usual.</item>
/// <item><see cref="ReducedMotion"/> (Windows animations off): the fade is instant.</item>
/// </list>
/// </summary>
public sealed class ToolbarVisibilityState
{
    /// <summary>Pen-down time before the toolbar retracts (section 4).</summary>
    public static readonly TimeSpan WritingBeforeRetract = TimeSpan.FromMilliseconds(300);

    /// <summary>The retract fade (section 3).</summary>
    public static readonly TimeSpan RetractDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the pen stays lifted before a retracted toolbar shows again (section 4).</summary>
    public static readonly TimeSpan PenUpBeforeShow = TimeSpan.FromMilliseconds(600);

    private readonly Func<TimeSpan> _clock;
    private bool _fixed;

    // The writing: a session lasts from the first pen-down until the pen has been lifted for PenUpBeforeShow.
    private bool _sessionActive;
    private bool _penDown;
    private TimeSpan _downAt;
    private TimeSpan _lastUpAt;
    private TimeSpan _accumulated; // pen-down time of the session before the current stroke
    private TimeSpan? _retractAt; // when the session's pen-down time reaches WritingBeforeRetract, once known

    // A finger tap hid it.
    private bool _tapHidden;
    private TimeSpan _tapHiddenAt;

    public ToolbarVisibilityState(Func<TimeSpan> clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>"Toolbar fixed" (section 4): always visible. Turning it on also clears a tap's hiding.</summary>
    public bool IsFixed
    {
        get => _fixed;
        set
        {
            _fixed = value;
            if (value)
            {
                _tapHidden = false;
            }
        }
    }

    /// <summary>True when Windows animations are off: the fade is instant.</summary>
    public bool ReducedMotion { get; set; }

    public ToolbarPhase Phase => Evaluate(_clock()).Phase;

    /// <summary>1 (visible) to 0 (hidden), eased while retracting.</summary>
    public double Opacity => Evaluate(_clock()).Opacity;

    /// <summary>True from the moment the writing retracts the toolbar until the pen-up delay shows it (even when fixed).</summary>
    public bool IsRetractedByWriting => WritingHiddenSince(_clock()) is not null;

    /// <summary>Time until something changes by itself (retract start, fade end, show after pen-up); null when nothing will.</summary>
    public TimeSpan? TimeToNextChange
    {
        get
        {
            var now = _clock();
            TimeSpan? next = null;
            void Consider(TimeSpan at)
            {
                if (at > now && (next is null || at - now < next))
                {
                    next = at - now;
                }
            }

            if (_sessionActive)
            {
                if (_retractAt is { } retract)
                {
                    Consider(retract);
                    Consider(retract + FadeDuration);
                }

                if (!_penDown)
                {
                    Consider(_lastUpAt + PenUpBeforeShow);
                }
            }

            if (_tapHidden)
            {
                Consider(_tapHiddenAt + FadeDuration);
            }

            return next;
        }
    }

    private TimeSpan FadeDuration => ReducedMotion ? TimeSpan.Zero : RetractDuration;

    /// <summary>The pen touched the screen to write or erase.</summary>
    public void PenDown()
    {
        var now = _clock();
        EndSessionIfOver(now);
        if (_penDown)
        {
            return;
        }

        if (!_sessionActive)
        {
            _sessionActive = true;
            _accumulated = TimeSpan.Zero;
            _retractAt = null;
        }

        _penDown = true;
        _downAt = now;
        _retractAt ??= now + (WritingBeforeRetract - _accumulated);
    }

    /// <summary>The pen left the screen.</summary>
    public void PenUp()
    {
        if (!_penDown)
        {
            return;
        }

        var now = _clock();
        _penDown = false;
        _accumulated += now - _downAt;
        _lastUpAt = now;
        if (_retractAt is { } retract && retract > now)
        {
            _retractAt = null; // not reached: the next stroke goes on counting
        }
    }

    /// <summary>A finger tap on an empty area of the document: hides a visible toolbar, shows a hidden one.</summary>
    public void FingerTap()
    {
        var now = _clock();
        EndSessionIfOver(now);
        if (_fixed)
        {
            return;
        }

        if (Evaluate(now).Phase == ToolbarPhase.Visible)
        {
            _tapHidden = true;
            _tapHiddenAt = now;
            return;
        }

        _tapHidden = false;
        if (!_penDown)
        {
            EndSession();
        }
        else
        {
            _accumulated = TimeSpan.Zero;
            _downAt = now;
            _retractAt = now + WritingBeforeRetract;
        }
    }

    private static double EaseIn(double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        return p * p;
    }

    private void EndSessionIfOver(TimeSpan now)
    {
        if (_sessionActive && !_penDown && now >= _lastUpAt + PenUpBeforeShow)
        {
            EndSession();
        }
    }

    private void EndSession()
    {
        _sessionActive = false;
        _accumulated = TimeSpan.Zero;
        _retractAt = null;
    }

    private TimeSpan? WritingHiddenSince(TimeSpan now)
    {
        if (!_sessionActive || _retractAt is not { } retract || now < retract)
        {
            return null;
        }

        return !_penDown && now >= _lastUpAt + PenUpBeforeShow ? null : retract;
    }

    private (ToolbarPhase Phase, double Opacity) Evaluate(TimeSpan now)
    {
        if (_fixed)
        {
            return (ToolbarPhase.Visible, 1);
        }

        TimeSpan? since = WritingHiddenSince(now);
        if (_tapHidden && (since is null || _tapHiddenAt < since))
        {
            since = _tapHiddenAt;
        }

        if (since is not { } start)
        {
            return (ToolbarPhase.Visible, 1);
        }

        var fade = FadeDuration;
        if (now >= start + fade)
        {
            return (ToolbarPhase.Hidden, 0);
        }

        return (ToolbarPhase.Retracting, 1 - EaseIn((now - start) / fade));
    }
}
