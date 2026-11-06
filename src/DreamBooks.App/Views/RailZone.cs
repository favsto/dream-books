namespace DreamBooks.App.Views;

/// <summary>
/// Which part of the shown scroll rail a point is over. For fingers and the mouse: the knob (a zone wider and taller
/// than it) or the strip. For the pen only the knob (its bounds plus a small slop); elsewhere it inks. The page
/// indicator is the window's chip (<see cref="PageIndicatorChip"/>), not a part of the rail.
/// </summary>
internal enum RailZone
{
    None,
    Strip,
    Knob,
}
