using System.Windows;
using System.Windows.Controls;

namespace DreamBooks.App.Views;

/// <summary>
/// The material of the floating toolbar and the option flyouts (section 3): a tinted translucency with the two-layer
/// shadow, diffuse and contact. WPF gives an element one effect, so the surface is two rounded layers behind the
/// content, each with one shadow and half the tint (<c>PanelLayerBrush</c>, 65% together); with Windows transparency
/// effects off (<see cref="IsOpaque"/>, NFR-32) both layers take the opaque panel brush. The content is the children
/// added after the layers (in XAML: the surface's children). The surface itself has no background: only the rounded
/// layers hit-test, so the pen and fingers outside its visible bounds reach what lies under it.
/// </summary>
public sealed class FloatingSurface : Grid
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(FloatingSurface),
        new PropertyMetadata(default(CornerRadius), (d, e) => ((FloatingSurface)d).ApplyCornerRadius()));

    public static readonly DependencyProperty IsOpaqueProperty = DependencyProperty.Register(
        nameof(IsOpaque), typeof(bool), typeof(FloatingSurface),
        new PropertyMetadata(false, (d, e) => ((FloatingSurface)d).ApplyMaterial()));

    public FloatingSurface()
    {
        DiffuseLayer = new Border();
        ContactLayer = new Border();
        Children.Add(DiffuseLayer);
        Children.Add(ContactLayer);
        ApplyCornerRadius();
        ApplyMaterial();
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Opaque theme brushes (Windows transparency effects off, NFR-32).</summary>
    public bool IsOpaque
    {
        get => (bool)GetValue(IsOpaqueProperty);
        set => SetValue(IsOpaqueProperty, value);
    }

    /// <summary>The layer with the diffuse shadow (blur 32 px).</summary>
    internal Border DiffuseLayer { get; }

    /// <summary>The layer with the contact shadow (blur 4 px).</summary>
    internal Border ContactLayer { get; }

    private void ApplyCornerRadius()
    {
        DiffuseLayer.CornerRadius = CornerRadius;
        ContactLayer.CornerRadius = CornerRadius;
    }

    private void ApplyMaterial()
    {
        var brush = IsOpaque ? "PanelTintBrushOpaque" : "PanelLayerBrush";
        DiffuseLayer.SetResourceReference(Border.BackgroundProperty, brush);
        ContactLayer.SetResourceReference(Border.BackgroundProperty, brush);
        DiffuseLayer.SetResourceReference(EffectProperty, "PanelDiffuseShadow");
        ContactLayer.SetResourceReference(EffectProperty, "PanelContactShadow");
    }
}
