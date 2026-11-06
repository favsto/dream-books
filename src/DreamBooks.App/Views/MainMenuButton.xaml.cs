using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DreamBooks.App.Services;
using Microsoft.Win32;

namespace DreamBooks.App.Views;

/// <summary>
/// The main menu (UI-03): a round floating button at the top left, below the title bar, and the panel of entries it
/// opens under it. The entries run the existing commands (Open, Save, Save as through the data context's view model;
/// Marks, Pages and Search get theirs from the window); Recent files, Export and Settings are disabled until phase 4b.
/// <list type="bullet">
/// <item>The button toggles the panel; an entry closes it, then runs its command; Esc (routed by the window) and a press
/// outside it (<see cref="RoutePress"/>) close it.</item>
/// <item>It appears like a toolbar flyout (fade and scale from <c>FlyoutOpenFromScale</c> in <c>FlyoutOpenDuration</c>,
/// ease-out; instant when Windows animations are off, NFR-32) and goes at once, so the pen inks there again
/// straight away. With transparency effects off the surfaces are opaque.</item>
/// <item>Opened from the keyboard, the keyboard moves to the first entry (the arrows and Tab cycle through them); closed
/// from inside, it goes back to the button.</item>
/// </list>
/// Pen rule (UI-02, FR-30): only the two surfaces hit-test, each with the inert stylus plug-in; on them the pen presses
/// the button and the entries like a finger or the mouse; a pen press outside closes the menu and inks.
/// </summary>
public partial class MainMenuButton : UserControl
{
    private bool _listening;
    private int _openVersion;

    public MainMenuButton()
    {
        InitializeComponent();
        PanelHost.RenderTransform = new ScaleTransform(1, 1);
        Items = [OpenItem, SaveItem, SaveAsItem, CloseItem, RecentItem, ExportItem, MarksItem, PagesItem, SearchItem, SettingsItem];
        Separators = [Separator1, Separator2, Separator3];
        EntryList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnEntryClick));
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

            Close(restoreFocus: false);
        };
        ApplyTransparency();
    }

    /// <summary>Windows client-area animations (NFR-32); tests inject it.</summary>
    internal Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    /// <summary>The Windows transparency setting (NFR-32); tests inject it.</summary>
    internal Func<bool> TransparencyEnabled { get; set; } = TransparencySetting.IsEnabled;

    /// <summary>The entries, top to bottom.</summary>
    internal IReadOnlyList<Button> Items { get; }

    /// <summary>The three separators: after Save as, after Export, after Search.</summary>
    internal IReadOnlyList<FrameworkElement> Separators { get; }

    /// <summary>True while the panel is open.</summary>
    internal bool IsOpen { get; private set; }

    /// <summary>How many recent documents the menu lists (the start screen has them all, FR-03).</summary>
    internal const int MaxRecents = 8;

    /// <summary>The recent documents under "File recenti" (enabled when there are any); choosing one calls <paramref name="open"/> and closes the menu.</summary>
    internal void SetRecents(IReadOnlyList<RecentEntry> entries, Action<string> open)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(open);
        RecentList.Children.Clear();
        foreach (var entry in entries.Take(MaxRecents))
        {
            var path = entry.Path;
            var row = new Button
            {
                Style = (Style)FindResource("MenuItemStyle"),
                Content = new TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(entry.FileName), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            System.Windows.Automation.AutomationProperties.SetName(row, entry.FileName);
            row.Click += (_, _) => open(path);
            RecentList.Children.Add(row);
        }

        RecentItem.IsEnabled = entries.Count > 0;
        if (entries.Count == 0)
        {
            RecentList.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The rows of the recent list, in order.</summary>
    internal IReadOnlyList<Button> RecentRows => RecentList.Children.OfType<Button>().ToList();

    /// <summary>True while the "File recenti" list is expanded.</summary>
    internal bool IsRecentListOpen => RecentList.Visibility == Visibility.Visible;

    /// <summary>The duration of the last opening animation (tests); null when it opened at once.</summary>
    internal TimeSpan? LastOpenDuration { get; set; }

    /// <summary>Opens the panel (nothing when it is open).</summary>
    internal void Open()
    {
        if (IsOpen)
        {
            return;
        }

        IsOpen = true;
        var version = ++_openVersion;
        var fromKeyboard = MenuButton.IsKeyboardFocused && InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        PanelHost.Visibility = Visibility.Visible;
        PanelHost.IsHitTestVisible = true;
        var scale = (ScaleTransform)PanelHost.RenderTransform;
        if (!AnimationsEnabled() || TryFindResource("FlyoutOpenDuration") is not TimeSpan duration || duration <= TimeSpan.Zero)
        {
            StopAnimations(scale);
            LastOpenDuration = null;
        }
        else
        {
            LastOpenDuration = duration;
            var from = TryFindResource("FlyoutOpenFromScale") as double? ?? 1;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var opacity = new DoubleAnimation(0, 1, new Duration(duration)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            var size = new DoubleAnimation(from, 1, new Duration(duration)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            PanelHost.Opacity = 1; // the base values under the animations: where they end
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            PanelHost.BeginAnimation(OpacityProperty, opacity);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, size);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, size);
        }

        if (fromKeyboard)
        {
            // Opened from the keyboard: it moves to the first entry that can take it, once laid out.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (IsOpen && version == _openVersion)
                {
                    EntryList.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                }
            });
        }
    }

    /// <summary>Closes the panel at once (nothing when it is closed); its keyboard focus goes back to the button.</summary>
    internal void Close() => Close(restoreFocus: true);

    /// <summary>The button: opens or closes the panel.</summary>
    internal void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    /// <summary>
    /// A press at <paramref name="position"/> (this control's coordinates) anywhere over the document. Returns true when
    /// it is taken. Closed: never taken. On the panel or on the button: left to their controls, for every pointer (the
    /// pen presses them too). Elsewhere: the menu closes; a finger or the mouse is taken (a light dismiss), the pen is not
    /// (it inks).
    /// </summary>
    internal bool RoutePress(Point position, PointerKind kind)
    {
        if (!IsOpen)
        {
            return false;
        }

        if (Contains(PanelSurface, position) || Contains(ButtonSurface, position))
        {
            return false;
        }

        Close(restoreFocus: false);
        return kind != PointerKind.Pen;
    }

    /// <summary>Opaque theme brushes when Windows transparency effects are off (NFR-32), the tinted layers otherwise.</summary>
    internal void ApplyTransparency()
    {
        var opaque = !TransparencyEnabled();
        ButtonSurface.IsOpaque = opaque;
        PanelSurface.IsOpaque = opaque;
    }

    private void Close(bool restoreFocus)
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        _openVersion++;
        var hadKeyboard = PanelHost.IsKeyboardFocusWithin;
        StopAnimations((ScaleTransform)PanelHost.RenderTransform);
        PanelHost.IsHitTestVisible = false; // at once: the pen and fingers reach the page there
        PanelHost.Visibility = Visibility.Collapsed;
        if (hadKeyboard)
        {
            if (restoreFocus)
            {
                MenuButton.Focus();
            }
            else
            {
                Keyboard.ClearFocus(); // the keys go back to the window (and the navigation keys to the document)
            }
        }
    }

    private void StopAnimations(ScaleTransform scale)
    {
        PanelHost.BeginAnimation(OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PanelHost.Opacity = 1;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
    }

    private bool Contains(FrameworkElement element, Point position)
    {
        if (element.Visibility != Visibility.Visible || !element.IsHitTestVisible)
        {
            return false;
        }

        if (element.ActualWidth <= 0)
        {
            UpdateLayout(); // opened in this very input event: lay it out first
        }

        return element.IsDescendantOf(this)
            && element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize)).Contains(position);
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => Toggle();

    // Raised before the entry's command runs (ButtonBase.OnClick): the menu is closed when the command starts, so a
    // dialog it opens, or the panel or the pill it shows, is not under the menu.
    private void OnEntryClick(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, RecentItem))
        {
            RecentList.Visibility = RecentList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            return; // "File recenti" opens its list; choosing a document closes the menu
        }

        Close(restoreFocus: true);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyTransparency); // raised on another thread
}
