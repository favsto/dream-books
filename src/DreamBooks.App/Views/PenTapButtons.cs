using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DreamBooks.App.Views;

/// <summary>
/// Pen taps on the buttons of a floating bar (the toolbar pill, the lasso's action bar) click them from the pen's own
/// events, so the first tap after writing acts (UI-02, FR-51). On the Surface the first pen tap on a bar after a stroke
/// was lost: WPF's emulation of the mouse from the pen, which the buttons relied on, had to be woken by one tap before
/// the next one clicked. Here a pen press on an enabled button and its lift over the same button click it (the same
/// click as the mouse's, through the button's automation peer); the mouse button events promoted from the pen are
/// taken before the buttons see them, so a tap never clicks twice. The pen's events themselves are left unhandled:
/// WPF's own pen and mouse bookkeeping goes on as before. Fingers and the mouse press the buttons as before.
/// </summary>
internal sealed class PenTapButtons
{
    private readonly UIElement _root;
    private Button? _pressed;
    private int _stylusId;

    private PenTapButtons(UIElement root) => _root = root;

    /// <summary>The button the pen is pressing, or null.</summary>
    internal Button? Pressed => _pressed;

    /// <summary>Clicks a button; tests replace it to count clicks without a dispatcher round.</summary>
    internal Action<Button> Click { get; set; } = InvokeClick;

    /// <summary>Handles the pen taps on every button inside <paramref name="root"/>.</summary>
    public static PenTapButtons Attach(UIElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var taps = new PenTapButtons(root);
        root.AddHandler(UIElement.PreviewStylusDownEvent, new StylusDownEventHandler(taps.OnStylusDown));
        root.AddHandler(UIElement.PreviewStylusUpEvent, new StylusEventHandler(taps.OnStylusUp));
        root.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(taps.OnPromotedMouseButton));
        root.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(taps.OnPromotedMouseButton));
        return taps;
    }

    /// <summary>The pen came down on <paramref name="target"/> (an element inside the bar): presses its button, if enabled.</summary>
    internal bool PenDown(DependencyObject? target, int stylusId)
    {
        _pressed = ButtonAt(target) is { IsEnabled: true } button ? button : null;
        _stylusId = stylusId;
        return _pressed is not null;
    }

    /// <summary>The pen lifted over <paramref name="target"/>: clicks the pressed button when the lift is still on it.</summary>
    internal bool PenUp(DependencyObject? target, int stylusId)
    {
        if (_pressed is not { } button || stylusId != _stylusId)
        {
            return false;
        }

        _pressed = null;
        if (!ReferenceEquals(ButtonAt(target), button) || !button.IsEnabled)
        {
            return false;
        }

        Click(button);
        return true;
    }

    /// <summary>The button <paramref name="target"/> belongs to, inside the bar, or null.</summary>
    internal Button? ButtonAt(DependencyObject? target)
    {
        for (var current = target; current is not null && !ReferenceEquals(current, _root); current = Parent(current))
        {
            if (current is Button button)
            {
                return button;
            }
        }

        return null;
    }

    private static DependencyObject? Parent(DependencyObject current) =>
        current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);

    private static bool IsPen(StylusDevice? device) => device?.TabletDevice?.Type == TabletDeviceType.Stylus;

    private static void InvokeClick(Button button)
    {
        if (UIElementAutomationPeer.CreatePeerForElement(button) is IInvokeProvider peer)
        {
            peer.Invoke(); // ButtonBase.OnClick: the Click event, then the command
        }
    }

    private void OnStylusDown(object sender, StylusDownEventArgs e)
    {
        if (IsPen(e.StylusDevice))
        {
            PenDown(e.OriginalSource as DependencyObject, e.StylusDevice.Id);
        }
    }

    private void OnStylusUp(object sender, StylusEventArgs e)
    {
        if (IsPen(e.StylusDevice))
        {
            var position = e.GetPosition(_root);
            PenUp(_root.InputHitTest(position) as DependencyObject, e.StylusDevice.Id);
        }
    }

    // The pen's taps on the buttons are clicked by the pen's events above: the mouse events WPF promotes from them
    // never reach the buttons (a click lost, or a second click). Real mouse and finger presses go on.
    private void OnPromotedMouseButton(object sender, MouseButtonEventArgs e)
    {
        if (IsPen(e.StylusDevice) && ButtonAt(e.OriginalSource as DependencyObject) is not null)
        {
            e.Handled = true;
        }
    }
}
