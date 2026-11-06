namespace DreamBooks.App.ViewModels;

/// <summary>The tool buttons of the toolbar (UI-02). <see cref="Selection"/> stays disabled until phase 5.</summary>
public enum ToolbarTool
{
    Pen,
    Highlighter,
    Eraser,

    /// <summary>The lasso: visible, never active until phase 5.</summary>
    Selection,
}
