using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DreamBooks.App.Views;

namespace DreamBooks.App.Tests;

/// <summary>
/// Maximising the window keeps it maximised for at least a second, and restore works (UI-06; the device test of 4a saw
/// the window grow to full screen and fall back). The window is really shown here, for a moment and out of the taskbar,
/// because the state change belongs to the native window. Not reproduced in-process: the bug of the device test needs
/// the device's input (touch or pen) or a document; these tests guard the three paths that can be driven.
/// </summary>
[Collection(nameof(ThemeCollection))]
public sealed class MaximiseTests
{
    private const int _wmSysCommand = 0x0112;
    private const int _wmNcLButtonDblClk = 0x00A3;
    private const int _scMaximize = 0xF030;
    private const int _scRestore = 0xF120;
    private const int _htCaption = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            WpfTestThread.PumpUntil(() => true);
            Thread.Sleep(10);
        }
    }

    private static void WithShownWindow(Action<MainWindow, IntPtr, List<WindowState>> test) =>
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow { ShowInTaskbar = false, Left = 100, Top = 100 };
            var states = new List<WindowState>();
            window.StateChanged += (_, _) => states.Add(window.WindowState);
            try
            {
                window.Show();
                Pump(300);
                test(window, new WindowInteropHelper(window).Handle, states);
            }
            finally
            {
                window.Close();
            }
        });

    private static void AssertStaysMaximisedThenRestores(MainWindow window, IntPtr hwnd, List<WindowState> states)
    {
        Pump(1000);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.Equal([WindowState.Maximized], states); // changed once, nothing changed it back
        SendMessage(hwnd, _wmSysCommand, _scRestore, IntPtr.Zero);
        Pump(300);
        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    [Fact]
    public void The_maximise_button_keeps_the_window_maximised()
    {
        WithShownWindow((window, hwnd, states) =>
        {
            var button = TitleBarPenInput.Buttons(window.AppTitleBar).First(b => b.Name == "PART_MaximizeButton");
            button.Command.Execute(button.CommandParameter);
            AssertStaysMaximisedThenRestores(window, hwnd, states);
        });
    }

    [Fact]
    public void The_maximise_system_command_keeps_the_window_maximised()
    {
        WithShownWindow((window, hwnd, states) =>
        {
            SendMessage(hwnd, _wmSysCommand, _scMaximize, IntPtr.Zero); // Win+Up, snap layouts
            AssertStaysMaximisedThenRestores(window, hwnd, states);
        });
    }

    [Fact]
    public void A_double_click_on_the_title_bar_keeps_the_window_maximised()
    {
        WithShownWindow((window, hwnd, states) =>
        {
            SendMessage(hwnd, _wmNcLButtonDblClk, _htCaption, IntPtr.Zero);
            AssertStaysMaximisedThenRestores(window, hwnd, states);
        });
    }
}
