namespace DreamBooks.Core.Ink;

/// <summary>What a press on the selection grabs (FR-51).</summary>
public enum SelectionHandle
{
    /// <summary>Not on the selection.</summary>
    None,

    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,

    /// <summary>Inside the selection rectangle, away from the corners: a move.</summary>
    Inside,
}
