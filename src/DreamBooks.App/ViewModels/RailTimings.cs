namespace DreamBooks.App.ViewModels;

/// <summary>
/// The scroll rail's fade timings (UI-07): fade in, fade out, and how long the rail stays after the last activity.
/// The values live in the theme dictionary (<c>RailFadeInDuration</c>, <c>RailFadeOutDuration</c>,
/// <c>RailIdleDelay</c>).
/// </summary>
public readonly record struct RailTimings(TimeSpan FadeIn, TimeSpan FadeOut, TimeSpan Idle);
