using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DreamBooks.App.Services;
using Microsoft.Win32;

namespace DreamBooks.App.Views;

/// <summary>
/// The navigation buttons above the indicator chip: Back (<see cref="ViewModels.DocumentViewportViewModel.BackCommand"/>,
/// FR-09) and previous / next mark (<see cref="ViewModels.MarksViewModel"/>, FR-102), enabled as their commands are,
/// and the marks' status beside them while there is one (<see cref="StatusShown"/>: the window announces it and shows
/// the buttons). They show and hide with the rail like the chip (<see cref="SetShown"/>, fading in
/// <c>NavigationFadeDuration</c>, instant when Windows animations are off, NFR-32) and hold it while hovered or on the
/// keyboard (<see cref="IsHolding"/>). Pen rule: only the round surfaces hit-test, each with the inert stylus plug-in;
/// the pen presses the buttons and inks around them; hidden, they take no input at once.
/// </summary>
public partial class NavigationButtons : UserControl
{
    private bool _shown;
    private bool _available;
    private bool _hovered;
    private bool _keyboardHold;
    private bool _statusShown;
    private bool _listening;

    public NavigationButtons()
    {
        InitializeComponent();
        Surfaces = [StatusSurface, BackSurface, PreviousMarkSurface, NextMarkSurface];
        Row.Opacity = 0;
        SyncHitTest();
        Visibility = Visibility.Collapsed; // until the window says there is a document
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        IsKeyboardFocusWithinChanged += (_, e) =>
            SetKeyboardHold((bool)e.NewValue && InputManager.Current.MostRecentInputDevice is KeyboardDevice);
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

    /// <summary>Raised when <see cref="IsHolding"/> changes: the window holds the rail (and these buttons) shown.</summary>
    internal event EventHandler? HoldChanged;

    /// <summary>Raised when the marks' status appears (or changes) with a text: the window announces it and shows it.</summary>
    internal event EventHandler? StatusShown;

    /// <summary>Windows client-area animations (NFR-32); tests inject it.</summary>
    internal Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    /// <summary>The Windows transparency setting (NFR-32); tests inject it.</summary>
    internal Func<bool> TransparencyEnabled { get; set; } = TransparencySetting.IsEnabled;

    /// <summary>The status pill and the three round buttons' surfaces, left to right.</summary>
    internal IReadOnlyList<FloatingSurface> Surfaces { get; }

    /// <summary>True while in use: hovered, the keyboard on them, or the marks' status shown (it lasts longer than the idle delay).</summary>
    internal bool IsHolding => _hovered || _keyboardHold || _statusShown;

    internal bool IsShown => _shown && _available;

    /// <summary>The duration of the last fade started (tests).</summary>
    internal TimeSpan? LastFadeDuration { get; private set; }

    /// <summary>
    /// Shown with the rail (<paramref name="shown"/>) when there is a document (<paramref name="available"/>): they fade
    /// in; otherwise they take no input at once and fade out (collapsed at once without a document).
    /// </summary>
    internal void SetShown(bool shown, bool available)
    {
        var was = IsShown;
        _shown = shown;
        _available = available;
        Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        SyncHitTest();
        if (IsShown != was || !available)
        {
            var duration = FloatingFade.Run(Row, IsShown ? 1 : 0, "NavigationFadeDuration", available && AnimationsEnabled());
            LastFadeDuration = duration ?? LastFadeDuration;
        }
    }

    /// <summary>The mouse (or the pen in the air) is over the buttons: the rail stays.</summary>
    internal void SetHover(bool hovering)
    {
        if (hovering != _hovered)
        {
            _hovered = hovering;
            HoldChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The keyboard reached the buttons (or left them): shown while it is there.</summary>
    internal void SetKeyboardHold(bool held)
    {
        if (held != _keyboardHold)
        {
            _keyboardHold = held;
            HoldChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Opaque theme brushes when Windows transparency effects are off (NFR-32), the tinted layers otherwise.</summary>
    internal void ApplyTransparency()
    {
        var opaque = !TransparencyEnabled();
        foreach (var surface in Surfaces)
        {
            surface.IsOpaque = opaque;
        }
    }

    private void SyncHitTest()
    {
        foreach (var surface in Surfaces)
        {
            surface.IsHitTestVisible = IsShown;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyTransparency); // raised on another thread

    private void OnStatusTextUpdated(object? sender, DataTransferEventArgs e)
    {
        if (StatusSurface is null || StatusText is null)
        {
            return; // still being built
        }

        var shown = !string.IsNullOrEmpty(StatusText.Text);
        StatusSurface.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown)
        {
            StatusShown?.Invoke(this, EventArgs.Empty);
        }

        if (shown != _statusShown)
        {
            _statusShown = shown; // the buttons (and the rail) stay while the status is on screen
            HoldChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
