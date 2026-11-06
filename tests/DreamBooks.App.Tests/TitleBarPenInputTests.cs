using DreamBooks.App.Views;

namespace DreamBooks.App.Tests;

/// <summary>
/// Whether the title bar's hit test sees the pen (UI-06, FR-30): the strongest evidence first, "the pen is in range"
/// only when Windows says nothing, so a finger, the mouse or the touchpad drag the window while the pen hovers.
/// </summary>
public class TitleBarPenInputTests
{
    private const int _mouse = 0x02;
    private const int _touch = 0x04;
    private const int _touchpad = 0x10;
    private const int _keyboard = 0x01;
    private const long _penSignature = 0xFF515700;
    private const long _touchSignature = 0xFF515780;

    [Theory]
    [InlineData(TitleBarPenInput.PenDevice, 0L, false, true)] // the pen, as Windows reports it
    [InlineData(TitleBarPenInput.PenDevice, 0L, true, true)]
    [InlineData(_mouse, 0L, true, false)] // the mouse while the pen hovers in range
    [InlineData(_mouse, _penSignature, true, false)] // a known source wins over the signature
    [InlineData(_touch, 0L, true, false)] // a finger while the pen hovers
    [InlineData(_touchpad, 0L, true, false)] // the touchpad while the pen hovers
    [InlineData(_keyboard, 0L, true, false)]
    [InlineData(TitleBarPenInput.UnavailableDevice, _penSignature, false, true)] // a mouse message made by the pen
    [InlineData(TitleBarPenInput.UnavailableDevice, _touchSignature, true, false)] // one made by a finger
    [InlineData(TitleBarPenInput.UnavailableDevice, 0L, true, true)] // nothing known: the pen in range decides
    [InlineData(TitleBarPenInput.UnavailableDevice, 0L, false, false)]
    [InlineData(TitleBarPenInput.UnavailableDevice, 0x12345678L, false, false)] // no signature, no pen
    public void The_pen_is_decided_from_the_strongest_evidence(int device, long extraInfo, bool penInRange, bool expected)
    {
        var asked = false;
        Assert.Equal(expected, TitleBarPenInput.Decide(device, extraInfo, () =>
        {
            asked = true;
            return penInRange;
        }));

        // "In range" is only asked when Windows says nothing about the message.
        Assert.Equal(device == TitleBarPenInput.UnavailableDevice && (extraInfo & 0xFFFFFF00) != _penSignature, asked);
    }
}
