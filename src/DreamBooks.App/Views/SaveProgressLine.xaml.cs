using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DreamBooks.App.Views;

/// <summary>
/// The save progress line under the title bar (FR-89): shown only while <see cref="IsActive"/> (bound to the view
/// model's <c>IsSaving</c>). An accent segment <c>SaveProgressBarFraction</c> of the width slides across in
/// <c>SaveProgressCycleDuration</c>, over and over; with Windows animations off (NFR-32) the line is still and full. It
/// never hit-tests, so the pen writes under it.
/// </summary>
public partial class SaveProgressLine : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(SaveProgressLine),
        new PropertyMetadata(false, (d, _) => ((SaveProgressLine)d).Apply()));

    public SaveProgressLine()
    {
        InitializeComponent();
        Track.SizeChanged += (_, _) =>
        {
            if (IsActive)
            {
                Apply(); // the segment and its path follow the width
            }
        };
    }

    /// <summary>A save is running.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Windows client-area animations (NFR-32); tests inject it.</summary>
    internal Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    /// <summary>True while the segment slides.</summary>
    internal bool IsAnimating { get; private set; }

    private void Apply()
    {
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        Slide.X = 0;
        IsAnimating = false;
        if (!IsActive)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        var width = Track.ActualWidth;
        if (!AnimationsEnabled() || width <= 0
            || TryFindResource("SaveProgressCycleDuration") is not TimeSpan cycle || cycle <= TimeSpan.Zero)
        {
            // Still: the whole line (also until it is laid out; SizeChanged starts the slide then).
            Bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            Bar.ClearValue(WidthProperty);
            return;
        }

        var fraction = TryFindResource("SaveProgressBarFraction") as double? ?? 0.3;
        var segment = width * fraction;
        Bar.HorizontalAlignment = HorizontalAlignment.Left;
        Bar.Width = segment;
        IsAnimating = true;
        Slide.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(-segment, width, new Duration(cycle))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                RepeatBehavior = RepeatBehavior.Forever,
            });
    }
}
