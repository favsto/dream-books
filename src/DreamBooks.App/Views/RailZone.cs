namespace DreamBooks.App.Views;

/// <summary>
/// Which part of the shown scroll rail a point is over. For fingers and the mouse: the knob (a zone wider and taller
/// than it), the strip or the indicator. For the pen only the knob (its bounds plus a small slop); elsewhere it inks.
/// </summary>
internal enum RailZone
{
    None,
    Strip,
    Indicator,
    Knob,
}
