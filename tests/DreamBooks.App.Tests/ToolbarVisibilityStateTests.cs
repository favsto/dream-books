using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.ViewModels;

namespace DreamBooks.App.Tests;

/// <summary>The toolbar's automatic visibility (section 4 table, section 3 animations, NFR-32), on a fake clock.</summary>
public class ToolbarVisibilityStateTests
{
    private TimeSpan _now;
    private readonly ToolbarVisibilityState _state;

    public ToolbarVisibilityStateTests() => _state = new ToolbarVisibilityState(() => _now);

    private void Advance(int ms) => _now += TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Timings_follow_the_requirements()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300), ToolbarVisibilityState.WritingBeforeRetract);
        Assert.Equal(TimeSpan.FromMilliseconds(200), ToolbarVisibilityState.RetractDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(600), ToolbarVisibilityState.PenUpBeforeShow);
    }

    [Fact]
    public void Writing_for_299_ms_keeps_the_toolbar_and_300_ms_retracts_it_over_200_ms()
    {
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
        _state.PenDown();

        Advance(299);
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
        Assert.Equal(1, _state.Opacity);
        Assert.Equal(TimeSpan.FromMilliseconds(1), _state.TimeToNextChange);

        Advance(1);
        Assert.Equal(ToolbarPhase.Retracting, _state.Phase);
        Assert.True(_state.IsRetractedByWriting);

        Advance(100);
        Assert.Equal(ToolbarPhase.Retracting, _state.Phase);
        Assert.InRange(_state.Opacity, 0.01, 0.99);

        Advance(100);
        Assert.Equal(ToolbarPhase.Hidden, _state.Phase);
        Assert.Equal(0, _state.Opacity);
    }

    [Fact]
    public void A_short_stroke_never_retracts_the_toolbar()
    {
        _state.PenDown();
        Advance(299);
        _state.PenUp();

        Advance(5000);
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
    }

    [Fact]
    public void Short_strokes_close_together_add_up_to_the_300_ms() // handwriting: letters with short lifts
    {
        _state.PenDown();
        Advance(200);
        _state.PenUp();
        Advance(150); // lifted for less than 600 ms: the same writing
        _state.PenDown();

        Advance(99);
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
        Advance(1);
        Assert.Equal(ToolbarPhase.Retracting, _state.Phase);
    }

    [Fact]
    public void A_pause_of_600_ms_starts_the_count_again()
    {
        _state.PenDown();
        Advance(200);
        _state.PenUp();
        Advance(600);
        _state.PenDown();

        Advance(200);
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
        Advance(100);
        Assert.Equal(ToolbarPhase.Retracting, _state.Phase);
    }

    [Fact]
    public void After_pen_up_it_stays_hidden_for_599_ms_and_shows_at_600_ms()
    {
        _state.PenDown();
        Advance(1000);
        _state.PenUp();

        Advance(599);
        Assert.Equal(ToolbarPhase.Hidden, _state.Phase);
        Assert.Equal(TimeSpan.FromMilliseconds(1), _state.TimeToNextChange);

        Advance(1);
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
        Assert.Equal(1, _state.Opacity);
        Assert.False(_state.IsRetractedByWriting);
        Assert.Null(_state.TimeToNextChange);
    }

    [Fact]
    public void Writing_again_before_600_ms_keeps_it_hidden()
    {
        _state.PenDown();
        Advance(1000);
        _state.PenUp();
        Advance(500);
        _state.PenDown();

        Advance(5000);
        Assert.Equal(ToolbarPhase.Hidden, _state.Phase);
    }

    [Fact]
    public void A_finger_tap_on_an_empty_area_toggles_the_toolbar()
    {
        _state.FingerTap();
        Assert.Equal(ToolbarPhase.Retracting, _state.Phase);
        Advance(200);
        Assert.Equal(ToolbarPhase.Hidden, _state.Phase);

        _state.FingerTap();
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);

        _state.FingerTap();
        Advance(200);
        Assert.Equal(ToolbarPhase.Hidden, _state.Phase);
    }

    [Fact]
    public void A_finger_tap_shows_a_toolbar_the_writing_retracted()
    {
        _state.PenDown();
        Advance(1000);
        _state.PenUp();
        Advance(100);

        _state.FingerTap();

        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
        Advance(1000);
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
    }

    [Fact]
    public void A_toolbar_hidden_by_a_tap_stays_hidden_after_writing()
    {
        _state.FingerTap();
        _state.PenDown();
        Advance(1000);
        _state.PenUp();

        Advance(1000);

        Assert.Equal(ToolbarPhase.Hidden, _state.Phase);
    }

    [Fact]
    public void Toolbar_fixed_keeps_it_visible_while_writing_and_against_a_tap()
    {
        _state.IsFixed = true;

        _state.PenDown();
        Advance(1000);
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
        Assert.True(_state.IsRetractedByWriting); // the panels still close
        _state.PenUp();

        _state.FingerTap();
        Advance(500);
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
    }

    [Fact]
    public void Turning_fixed_on_shows_a_hidden_toolbar()
    {
        _state.FingerTap();
        Advance(200);

        _state.IsFixed = true;

        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
        _state.IsFixed = false;
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
    }

    [Fact]
    public void Reduced_motion_retracts_instantly()
    {
        _state.ReducedMotion = true;
        _state.PenDown();

        Advance(299);
        Assert.Equal(ToolbarPhase.Visible, _state.Phase);
        Advance(1);
        Assert.Equal(ToolbarPhase.Hidden, _state.Phase);
        Assert.Equal(0, _state.Opacity);

        _state.PenUp();
        Advance(600);
        _state.FingerTap();
        Assert.Equal(ToolbarPhase.Hidden, _state.Phase); // the tap hides at once too
    }

    // The view model drives the state with the scheduler (whose time is the clock) and closes the panels.
    [Fact]
    public void Panels_close_when_the_writing_retracts_the_toolbar_and_never_reopen_automatically()
    {
        var scheduler = new ManualScheduler();
        var toolbar = new ToolbarViewModel(
            new ToolsViewModel(), new RelayCommand(() => { }), new RelayCommand(() => { }), () => null, scheduler, () => scheduler.Now);
        toolbar.TapTool(ToolbarTool.Pen);
        Assert.Equal(OptionsPanel.Pen, toolbar.OpenPanel);

        toolbar.PenDown();
        scheduler.Advance(299);
        Assert.Equal(OptionsPanel.Pen, toolbar.OpenPanel);
        Assert.Equal(ToolbarPhase.Visible, toolbar.Phase);

        scheduler.Advance(1);
        Assert.Equal(OptionsPanel.None, toolbar.OpenPanel);
        Assert.Equal(ToolbarPhase.Retracting, toolbar.Phase);
        scheduler.Advance(200);
        Assert.Equal(ToolbarPhase.Hidden, toolbar.Phase);

        toolbar.PenUp();
        scheduler.Advance(599);
        Assert.Equal(ToolbarPhase.Hidden, toolbar.Phase);
        scheduler.Advance(1);
        Assert.Equal(ToolbarPhase.Visible, toolbar.Phase);
        Assert.Equal(OptionsPanel.None, toolbar.OpenPanel);
        Assert.Equal(0, scheduler.Pending);
    }

    [Fact]
    public void Panels_close_when_writing_with_the_toolbar_fixed()
    {
        var scheduler = new ManualScheduler();
        var toolbar = new ToolbarViewModel(
            new ToolsViewModel(), new RelayCommand(() => { }), new RelayCommand(() => { }), () => null, scheduler, () => scheduler.Now)
        {
            IsToolbarFixed = true,
        };
        toolbar.TapTool(ToolbarTool.Pen);

        toolbar.PenDown();
        scheduler.Advance(300);

        Assert.Equal(OptionsPanel.None, toolbar.OpenPanel);
        Assert.Equal(ToolbarPhase.Visible, toolbar.Phase);
    }

    [Fact]
    public void A_finger_tap_closes_the_panels_and_toggles_the_toolbar()
    {
        var scheduler = new ManualScheduler();
        var toolbar = new ToolbarViewModel(
            new ToolsViewModel(), new RelayCommand(() => { }), new RelayCommand(() => { }), () => null, scheduler, () => scheduler.Now);
        var phases = new List<ToolbarPhase>();
        toolbar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolbarViewModel.Phase))
            {
                phases.Add(toolbar.Phase);
            }
        };
        toolbar.TapTool(ToolbarTool.Pen);

        toolbar.FingerTapOnEmptyArea();
        Assert.Equal(OptionsPanel.None, toolbar.OpenPanel);
        scheduler.Advance(200);
        Assert.Equal(ToolbarPhase.Hidden, toolbar.Phase);

        toolbar.FingerTapOnEmptyArea();
        Assert.Equal(ToolbarPhase.Visible, toolbar.Phase);
        Assert.Equal([ToolbarPhase.Retracting, ToolbarPhase.Hidden, ToolbarPhase.Visible], phases);
    }
}
