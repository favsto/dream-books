using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DreamBooks.App.Services;
using Microsoft.Win32;

namespace DreamBooks.App.Views;

/// <summary>
/// The notices at the top centre (FR-84, FR-86): the view model's <c>Notice</c> and <c>JournalWarning</c>, each line
/// shown only with a text, in a floating panel with a close button. Shown while there is a notice and it was not closed;
/// a notice that changes (or a new warning) shows it again. Each line is a live region and is announced when it changes
/// (NFR-31). Pen rule: only the surface hit-tests and it carries the inert stylus plug-in; the pen presses the close
/// button, inks around it, and inks there at once once it is closed.
/// </summary>
public partial class NoticeBar : UserControl
{
    private bool _listening;
    private (string? Notice, string? Warning) _current;
    private (string? Notice, string? Warning)? _dismissed;

    public NoticeBar()
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

    /// <summary>The Windows transparency setting (NFR-32); tests inject it.</summary>
    internal Func<bool> TransparencyEnabled { get; set; } = TransparencySetting.IsEnabled;

    /// <summary>True while the panel is shown.</summary>
    internal bool IsShown => Visibility == Visibility.Visible;

    /// <summary>
    /// The notices now (null or empty: none): each line shows with a text, and the panel with a line unless exactly these
    /// were closed.
    /// </summary>
    internal void Show(string? notice, string? warning)
    {
        notice = string.IsNullOrEmpty(notice) ? null : notice;
        warning = string.IsNullOrEmpty(warning) ? null : warning;
        _current = (notice, warning);
        SyncText(NoticeText, notice); // when driven without the bindings (tests); never replaces them
        SyncText(WarningText, warning);

        NoticeText.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
        if (notice is null && warning is null)
        {
            _dismissed = null;
        }

        var shown = (notice is not null || warning is not null) && _dismissed != _current;
        Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        Surface.IsHitTestVisible = shown;
    }

    /// <summary>The close button: hidden until a notice changes.</summary>
    internal void Dismiss()
    {
        _dismissed = _current;
        Show(_current.Notice, _current.Warning);
    }

    /// <summary>Opaque theme brushes when Windows transparency effects are off (NFR-32), the tinted layers otherwise.</summary>
    internal void ApplyTransparency() => Surface.IsOpaque = !TransparencyEnabled();

    private static void SyncText(TextBlock line, string? text)
    {
        if (!string.Equals(string.IsNullOrEmpty(line.Text) ? null : line.Text, text, StringComparison.Ordinal))
        {
            line.SetCurrentValue(TextBlock.TextProperty, text ?? string.Empty);
        }
    }

    private void OnTextUpdated(object? sender, DataTransferEventArgs e)
    {
        if (NoticeText is null || WarningText is null || Surface is null)
        {
            return; // still being built
        }

        Show(NoticeText.Text, WarningText.Text);
        if (IsShown && sender is TextBlock { Text.Length: > 0 } line)
        {
            var peer = UIElementAutomationPeer.FromElement(line) ?? UIElementAutomationPeer.CreatePeerForElement(line);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        var hadKeyboard = IsKeyboardFocusWithin;
        Dismiss();
        if (hadKeyboard)
        {
            System.Windows.Input.Keyboard.ClearFocus(); // the keys go back to the window and the document
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyTransparency); // raised on another thread
}
