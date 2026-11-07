using System.Windows;

namespace DreamBooks.App.Views;

/// <summary>
/// The splash (phase 4b): the app's icon, its name and a short tagline while the app starts. It is created before the
/// heavy start-up work and closed by the app as soon as the window is ready: it never waits for a minimum time.
/// </summary>
public partial class SplashWindow : Window
{
    private bool _reducedMotion;

    public SplashWindow()
    {
        InitializeComponent();
        ReducedMotion = !SystemParameters.ClientAreaAnimation;
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
