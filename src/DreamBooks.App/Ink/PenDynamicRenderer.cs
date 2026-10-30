using System.Collections.Frozen;
using System.Windows.Input.StylusPlugIns;

namespace DreamBooks.App.Ink;

/// <summary>
/// The stock WPF wet-ink renderer (mode A), unchanged in how it draws, that only refuses input which
/// must never ink: touch (fingers, FR-30 / wpf-ui rules) and the pen's back end (Review Focus 3).
/// The filter runs on the real-time stylus thread, so it is effective even before the UI thread sees the
/// contact. The UI thread keeps the device sets current through <see cref="SetIgnoredDevices"/>.
/// </summary>
public class PenDynamicRenderer : DynamicRenderer
{
    private volatile FrozenSet<int> _ignoredTablets = FrozenSet<int>.Empty;
    private volatile FrozenSet<int> _ignoredStyluses = FrozenSet<int>.Empty;

    /// <summary>Called on the UI thread: touch tablets and inverted (eraser) stylus cursors.</summary>
    public void SetIgnoredDevices(IReadOnlyCollection<int> touchTabletIds, IReadOnlyCollection<int> invertedStylusIds)
    {
        ArgumentNullException.ThrowIfNull(touchTabletIds);
        ArgumentNullException.ThrowIfNull(invertedStylusIds);
        if (!_ignoredTablets.SetEquals(touchTabletIds))
        {
            _ignoredTablets = touchTabletIds.ToFrozenSet();
        }

        if (!_ignoredStyluses.SetEquals(invertedStylusIds))
        {
            _ignoredStyluses = invertedStylusIds.ToFrozenSet();
        }
    }

    protected bool IsIgnored(RawStylusInput rawStylusInput)
    {
        ArgumentNullException.ThrowIfNull(rawStylusInput);
        return _ignoredTablets.Contains(rawStylusInput.TabletDeviceId)
            || _ignoredStyluses.Contains(rawStylusInput.StylusDeviceId);
    }

    protected override void OnStylusDown(RawStylusInput rawStylusInput)
    {
        if (!IsIgnored(rawStylusInput))
        {
            base.OnStylusDown(rawStylusInput);
        }
    }

    protected override void OnStylusMove(RawStylusInput rawStylusInput)
    {
        if (!IsIgnored(rawStylusInput))
        {
            base.OnStylusMove(rawStylusInput);
        }
    }

    protected override void OnStylusUp(RawStylusInput rawStylusInput)
    {
        if (!IsIgnored(rawStylusInput))
        {
            base.OnStylusUp(rawStylusInput);
        }
    }
}
