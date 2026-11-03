using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DreamBooks.App.Services;
using Microsoft.Win32;

namespace DreamBooks.App.Views;

/// <summary>
/// The search pill at the top centre (FR-13, FR-105), the view of the <see cref="ViewModels.SearchViewModel"/>: the
/// field (<see cref="ViewModels.SearchViewModel.QueryText"/>), the count, previous / next and close (the clear command),
/// and the search's messages in a small panel under it while there are any. Shown while <see cref="IsOpen"/> (bound
/// to the view model's): it fades in (<c>FlyoutOpenDuration</c>, instant when Windows animations are off, NFR-32) and
/// goes at once when closed, so the pen inks there again straight away. <see cref="FocusField"/> gives the field the
/// keyboard (Ctrl+F). Pen rule: only its surfaces hit-test, each with the inert stylus plug-in; the pen uses the field
/// and the buttons, and inks around them.
/// </summary>
public partial class SearchPill : UserControl
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(SearchPill),
        new PropertyMetadata(false, (d, e) => ((SearchPill)d).ApplyOpen((bool)e.NewValue)));

    private bool _listening;

    public SearchPill()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyTransparency();
            if (!_listening)
            {
                _listening = true;
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            }
        };
        Unloaded += (_, _) =>
        {
            if (_listening)
            {
                _listening = false;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            }
        };
        ApplyTransparency();
    }

    /// <summary>Shown (the view model's <c>IsOpen</c>).</summary>
    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>Windows client-area animations (NFR-32); tests inject it.</summary>
    internal Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    /// <summary>The Windows transparency setting (NFR-32); tests inject it.</summary>
    internal Func<bool> TransparencyEnabled { get; set; } = TransparencySetting.IsEnabled;

    /// <summary>Gives the field the keyboard with its text selected, once the pill is laid out (Ctrl+F).</summary>
    internal void FocusField() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsOpen)
            {
                Field.Focus();
                Field.SelectAll();
            }
        });

    /// <summary>Opaque theme brushes when Windows transparency effects are off (NFR-32), the tinted layers otherwise.</summary>
    internal void ApplyTransparency()
    {
        var opaque = !TransparencyEnabled();
        Surface.IsOpaque = opaque;
        MessageSurface.IsOpaque = opaque;
    }

    private void ApplyOpen(bool open)
    {
        if (!open)
        {
            Visibility = Visibility.Collapsed; // at once: the pen inks there again
            FloatingFade.Run(this, 1, "FlyoutOpenDuration", animate: false);
            return;
        }

        FloatingFade.Run(this, 0, "FlyoutOpenDuration", animate: false);
        Visibility = Visibility.Visible;
        FloatingFade.Run(this, 1, "FlyoutOpenDuration", AnimationsEnabled());
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyTransparency); // raised on another thread

    /// <summary>A message came or went: each line shows only with a text, the panel only with a line.</summary>
    private void OnMessageUpdated(object? sender, DataTransferEventArgs e)
    {
        if (FailedPagesText is null || NoTextText is null || StatusText is null || MessageSurface is null)
        {
            return; // still being built
        }

        var any = false;
        foreach (var line in new[] { FailedPagesText, NoTextText, StatusText })
        {
            var shown = !string.IsNullOrEmpty(line.Text);
            line.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            any |= shown;
        }

        MessageSurface.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }
}
