using System.Windows.Input;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// One toolbar button (UI-02): what it is, its Italian name for screen readers (NFR-31), its command (disabled buttons
/// have a command that cannot execute) and whether it is a tool (which shows the active indicator).
/// </summary>
public sealed record ToolbarButton(ToolbarButtonKind Kind, string Name, ICommand Command, bool IsTool);
