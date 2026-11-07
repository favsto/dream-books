using System.Windows;
using System.Windows.Controls;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Views;

/// <summary>
/// The settings panel (FR-110, 4b): a centred floating surface over the document. Shown while
/// <see cref="SettingsViewModel.IsOpen"/>; a press outside it closes it (<see cref="RoutePress"/>): a finger or the mouse is
/// taken (a light dismiss), the pen is not (it writes). Inside the surface the pen presses the controls like a finger.
/// </summary>
public partial class SettingsPanel : UserControl
{
    /// <summary>A mid grey, readable on the light and the dark panel, for the pressure preview's stroke.</summary>
    private static readonly InkColor _previewColor = InkColor.FromRgb(0x80, 0x80, 0x80);

    public SettingsPanel()
    {
        InitializeComponent();
        Preview.InkColor = _previewColor;
        Preview.WidthMm = 1.5;
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsViewModel old)
            {
                old.PropertyChanged -= OnViewModelChanged;
            }

            if (e.NewValue is SettingsViewModel vm)
            {
                vm.PropertyChanged += OnViewModelChanged;
                Sync(vm);
            }
        };
    }

    /// <summary>The panel's surface (its bounds are where the pen presses the controls).</summary>
    internal FloatingSurface PanelSurface => Surface;

    /// <summary>True while the panel is shown.</summary>
    internal bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Opaque theme brushes when Windows transparency effects are off (NFR-32), the tinted layers otherwise.</summary>
    internal void ApplyTransparency(bool transparent) => Surface.IsOpaque = !transparent;

    /// <summary>
    /// A press at <paramref name="position"/> (this view's coordinates) anywhere over the document. Closed: never taken.
    /// On the surface: left to its controls, for every pointer. Elsewhere: the panel closes; a finger or the mouse is taken,
    /// the pen is not (it writes).
    /// </summary>
    internal bool RoutePress(Point position, PointerKind kind)
    {
        if (!IsOpen || DataContext is not SettingsViewModel vm)
        {
            return false;
        }

        var bounds = Surface.TransformToAncestor(this).TransformBounds(new Rect(Surface.RenderSize));
        if (bounds.Contains(position))
        {
            return false;
        }

        vm.CloseCommand.Execute(null);
        return kind != PointerKind.Pen;
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.IsOpen) && sender is SettingsViewModel vm)
        {
            Sync(vm);
        }
    }

    private void Sync(SettingsViewModel vm)
    {
        Visibility = vm.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        if (vm.IsOpen)
        {
            CloseButton.Focus();
        }
    }
}
