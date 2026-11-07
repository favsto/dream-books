namespace DreamBooks.App.ViewModels;

public enum AppTool
{
    Pen,
    Eraser,

    /// <summary>Drawn by the pen like <see cref="Pen"/>; the pen's back end still erases (FR-30, FR-41).</summary>
    Highlighter,

    /// <summary>The lasso: the pen draws a loop that selects strokes instead of inking (FR-50).</summary>
    Selection,
}
