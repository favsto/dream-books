namespace DreamBooks.App.Services;

/// <summary>
/// Runs an action once after a delay, on the scheduler's own thread (the UI thread in the app). View models debounce
/// through it so tests can drive time by hand instead of sleeping. <see cref="Schedule"/> and disposing its result may
/// be called from any thread (a background scan schedules the marks list refresh).
/// </summary>
public interface IDelayScheduler
{
    /// <summary>Schedules <paramref name="action"/>; disposing the result before it runs cancels it.</summary>
    IDisposable Schedule(TimeSpan delay, Action action);
}
