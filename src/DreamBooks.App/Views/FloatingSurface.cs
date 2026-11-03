using System.Windows;
using System.Windows.Controls;
using System.Windows.Input.StylusPlugIns;

namespace DreamBooks.App.Views;

/// <summary>
/// The material of the floating toolbar and the option flyouts (section 3): a tinted translucency with the two-layer
/// shadow, diffuse and contact. WPF gives an element one effect, so the surface is two rounded layers behind the
/// content, each with one shadow and half the tint (<c>PanelLayerBrush</c>, 65% together); with Windows transparency
/// effects off (<see cref="IsOpaque"/>, NFR-32) both layers take the opaque panel brush. The content is the children
/// added after the layers (in XAML: the surface's children). The surface itself has no background: only the rounded
/// layers hit-test, so the pen and fingers outside its visible bounds reach what lies under it. While it is hit-test
/// visible it carries a stylus plug-in that does nothing (as <see cref="RailKnobGrip"/> does): WPF's real-time stylus
/// targets the topmost element with plug-ins under the pen, so a pen press on a button or a flyout control never starts
/// the page's wet ink under the surface. The plug-in's area is the surface's rectangle, so a pen landing just outside a
/// rounded corner gets no wet ink for that stroke (the stroke itself is still written). Not hit-test visible (retracting,
/// a closing or closed flyout), it drops the plug-in and the pen inks under it at once.
/// </summary>
public sealed class FloatingSurface : Grid
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(FloatingSurface),
        new PropertyMetadata(default(CornerRadius), (d, e) => ((FloatingSurface)d).ApplyCornerRadius()));

    public static readonly DependencyProperty IsOpaqueProperty = DependencyProperty.Register(
        nameof(IsOpaque), typeof(bool), typeof(FloatingSurface),
        new PropertyMetadata(false, (d, e) => ((FloatingSurface)d).ApplyMaterial()));

    private readonly InertPlugIn _plugIn = new();

    public FloatingSurface()
    {
        IsHitTestVisibleChanged += (_, _) => SyncPlugIn();
        DiffuseLayer = new Border();
        ContactLayer = new Border();
        Children.Add(DiffuseLayer);
        Children.Add(ContactLayer);
        ApplyCornerRadius();
        ApplyMaterial();
        SyncPlugIn();
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

    /// <summary>True when the surface carries its inert plug-in (what keeps the page's wet ink off under it).</summary>
    internal bool HasStylusPlugIn => StylusPlugIns.Contains(_plugIn);

    private void SyncPlugIn()
    {
        var has = StylusPlugIns.Contains(_plugIn);
        if (IsHitTestVisible && !has)
        {
            StylusPlugIns.Add(_plugIn);
        }
        else if (!IsHitTestVisible && has)
        {
            StylusPlugIns.Remove(_plugIn);
        }
    }

    /// <summary>A radius larger than half the shorter side (the theme's pill) is cut to it: a rounded band, not an ellipse.</summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ApplyCornerRadius();
    }

    private void ApplyCornerRadius()
    {
        var limit = Math.Min(ActualWidth, ActualHeight) / 2;
        var radius = limit > 0
            ? new CornerRadius(
                Math.Min(CornerRadius.TopLeft, limit), Math.Min(CornerRadius.TopRight, limit),
                Math.Min(CornerRadius.BottomRight, limit), Math.Min(CornerRadius.BottomLeft, limit))
            : CornerRadius;
        DiffuseLayer.CornerRadius = radius;
        ContactLayer.CornerRadius = radius;
    }

    private void ApplyMaterial()
    {
        var brush = IsOpaque ? "PanelTintBrushOpaque" : "PanelLayerBrush";
        DiffuseLayer.SetResourceReference(Border.BackgroundProperty, brush);
        ContactLayer.SetResourceReference(Border.BackgroundProperty, brush);
        DiffuseLayer.SetResourceReference(EffectProperty, "PanelDiffuseShadow");
        ContactLayer.SetResourceReference(EffectProperty, "PanelContactShadow");
    }

    /// <summary>Claims the pen for the surface on the real-time stylus thread; draws nothing.</summary>
    private sealed class InertPlugIn : StylusPlugIn
    {
    }
}
