using System.Windows.Controls;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Media;

namespace DreamBooks.App.Views;

/// <summary>
/// The invisible area over the rail's knob (its bounds plus <c>RailKnobPenSlop</c>) that the pen can grab while the
/// rail is shown (UI-07). It hit-tests, so the pen's events go to it and not to the page under it, and it carries a
/// stylus plug-in that does nothing: WPF's real-time stylus targets the topmost element with plug-ins under the pen,
/// so the page's wet-ink renderer never starts a stroke there. Hidden (not hit-testable) when the rail is hidden,
/// so the pen inks at the right edge as before.
/// </summary>
internal sealed class RailKnobGrip : Border
{
    public RailKnobGrip()
    {
        Background = Brushes.Transparent;
        Focusable = false;
        StylusPlugIns.Add(new InertPlugIn());
    }

    /// <summary>True when the grip carries its plug-in (what keeps the page's wet ink off).</summary>
    internal bool HasStylusPlugIn => StylusPlugIns.Count > 0;

    /// <summary>Claims the pen for the grip on the real-time stylus thread; draws nothing.</summary>
    private sealed class InertPlugIn : StylusPlugIn
    {
    }
}
