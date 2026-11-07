using System.Windows;
using System.Windows.Media;

namespace DreamBooks.App.Views;

/// <summary>
/// The splash (phase 4b): the app's icon, its name and a short tagline while the app starts. It is created before the
/// heavy start-up work and closed by the app as soon as the window is ready: it never waits for a minimum time.
/// </summary>
public partial class SplashWindow : Window
{
    private bool _reducedMotion;

    public SplashWindow()
        : this(dark: true)
    {
    }

    /// <param name="dark">The dark look (the theme the app will use is dark) or the light one.</param>
    public SplashWindow(bool dark)
    {
        InitializeComponent();
        ReducedMotion = !SystemParameters.ClientAreaAnimation;
        ApplyTheme(dark);
    }

    /// <summary>
    /// The main window is up: the splash steps back from the top of the screen. A start-up question (a restored file that was
    /// moved, a journal that does not match) is a dialog of the main window and must not open hidden under the splash while
    /// start-up waits for the answer.
    /// </summary>
    internal void YieldToMainWindow() => Topmost = false;

    /// <summary>The splash takes the dark or the light look: the brushes of that look stand in for the ones the window uses.</summary>
    private void ApplyTheme(bool dark)
    {
        var look = dark ? "Dark" : "Light";
        foreach (var part in new[] { "Background", "Text", "Subtext" })
        {
            Resources[$"Splash{part}Brush"] = (Brush)FindResource($"Splash{look}{part}Brush");
        }
    }

    /// <summary>Windows animations off (NFR-32): the indicator stands still instead of running.</summary>
    internal bool ReducedMotion
    {
        get => _reducedMotion;
        set
        {
            _reducedMotion = value;
            Indicator.IsIndeterminate = !value;
        }
    }
}
