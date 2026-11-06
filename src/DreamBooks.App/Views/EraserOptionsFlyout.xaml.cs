using System.Windows.Controls;

namespace DreamBooks.App.Views;

/// <summary>
/// The eraser's option panel (UI-02), hosted and animated by <see cref="ToolbarView"/>; its data context is the
/// <see cref="ViewModels.ToolbarViewModel"/>.
/// </summary>
public partial class EraserOptionsFlyout : UserControl
{
    public EraserOptionsFlyout() => InitializeComponent();
}
