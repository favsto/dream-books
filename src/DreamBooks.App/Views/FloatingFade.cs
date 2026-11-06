using System.Windows;
using System.Windows.Media.Animation;

namespace DreamBooks.App.Views;

/// <summary>
/// The opacity fade of the floating elements that show and hide (the indicator chip, the navigation buttons, the search
/// pill): from where the element is to the target over a theme duration, ease-out, or at once when Windows animations
/// are off (NFR-32). The base value is set to the target, so the element ends there when the animation is removed.
/// </summary>
internal static class FloatingFade
{
    /// <summary>
    /// Fades <paramref name="element"/> to <paramref name="to"/>; returns the duration used, or null when it was set at
    /// once (animations off, no duration, or already there).
    /// </summary>
    internal static TimeSpan? Run(FrameworkElement element, double to, string durationKey, bool animate)
    {
        var from = element.Opacity; // the current value, animated or not
        if (!animate || from == to || element.TryFindResource(durationKey) is not TimeSpan duration || duration <= TimeSpan.Zero)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = to;
            return null;
        }

        element.Opacity = to;
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(from, to, new Duration(duration))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        return duration;
    }
}
