using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DreamBooks.App.Views;

/// <summary>
/// The pen on the title bar (UI-06, FR-30: the pen draws). WPF UI's <c>TitleBar</c> answers the window's non-client
/// hit test with "caption" over its whole area, so a pen press there would drag the window. The window answers first
/// for the pen (<see cref="IsPenInput"/>) with "client" on the drag area, and <see cref="LetPenThrough"/> makes the
/// bar's parts other than the window buttons invisible to WPF's hit test, so the pen reaches the page under it. WPF
/// UI's own checks (the bar's and the buttons' bounds and <see cref="UIElement.IsHitTestVisible"/>) are unchanged:
/// the mouse and fingers still drag the window, and the buttons work for every pointer.
/// </summary>
internal static class TitleBarPenInput
{
    /// <summary>The mouse message came from a pen or touch device (Windows' "MI_WP_SIGNATURE").</summary>
    private const long _penOrTouchSignature = 0xFF515700;
    private const long _signatureMask = 0xFFFFFF00;

    /// <summary>Set in the extra information of a mouse message that came from touch, not from the pen.</summary>
    private const long _touchFlag = 0x80;

    /// <summary>IMDT_PEN of INPUT_MESSAGE_DEVICE_TYPE.</summary>
    private const int _penDeviceType = 0x08;

    /// <summary>
    /// True when the input being processed comes from the pen: Windows says so (the message source, or the signature of
    /// a mouse message made from the pen), or WPF has the pen in range over the window (hovering before a press).
    /// </summary>
    public static bool IsPenInput()
    {
        if (NativeMethods.GetCurrentInputMessageSource(out var source) && source.DeviceType == _penDeviceType)
        {
            return true;
        }

        var extra = NativeMethods.GetMessageExtraInfo().ToInt64();
        if ((extra & _signatureMask) == _penOrTouchSignature && (extra & _touchFlag) == 0)
        {
            return true;
        }

        return Stylus.CurrentStylusDevice is { InRange: true, TabletDevice.Type: TabletDeviceType.Stylus };
    }

    /// <summary>The window buttons of <paramref name="titleBar"/> that are laid out.</summary>
    public static IEnumerable<ButtonBase> Buttons(DependencyObject titleBar) =>
        Descendants(titleBar).OfType<ButtonBase>().Where(b => b.Visibility == Visibility.Visible && b.ActualWidth > 0);

    /// <summary>
    /// Makes the parts of the bar's template that hold no window button invisible to WPF's hit test, and takes the
    /// background off the parts that hold one (the bar is transparent: nothing changes on screen), so the pen passes
    /// through everywhere but on the buttons. The bar itself stays hit-test visible for WPF UI.
    /// </summary>
    public static void LetPenThrough(DependencyObject titleBar)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(titleBar); i++)
        {
            var child = VisualTreeHelper.GetChild(titleBar, i);
            if (child is ButtonBase)
            {
                continue;
            }

            if (!Descendants(child).OfType<ButtonBase>().Any())
            {
                if (child is UIElement element)
                {
                    element.IsHitTestVisible = false;
                }

                continue;
            }

            switch (child)
            {
                case Panel panel:
                    panel.Background = null;
                    break;
                case Border border:
                    border.Background = null;
                    break;
                case Control control:
                    control.Background = null;
                    break;
            }

            LetPenThrough(child);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputMessageSource
    {
        public int DeviceType;
        public int OriginId;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr GetMessageExtraInfo();

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCurrentInputMessageSource(out InputMessageSource source);
    }
}
