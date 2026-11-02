namespace DreamBooks.App.Tests;

/// <summary>
/// Runs the tests that switch the application's theme alone: the theme lives in the shared test application's
/// resources, and a test pumping the dispatcher (Drain, PumpUntil) also runs work other tests queued on the WPF test
/// thread, so a parallel test could see the dark theme, or switch it back, in the middle of another.
/// </summary>
[CollectionDefinition(nameof(ThemeCollection), DisableParallelization = true)]
public sealed class ThemeCollection;
