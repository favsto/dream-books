namespace DreamBooks.App.ViewModels;

public enum AppTool
{
    Pen,
    Eraser,

    /// <summary>Drawn by the pen like <see cref="Pen"/>; the pen's back end still erases (FR-30, FR-41).</summary>
    Highlighter,
}
