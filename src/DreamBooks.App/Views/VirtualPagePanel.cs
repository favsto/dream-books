using System.Windows;
using System.Windows.Controls;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Views;

/// <summary>
/// The whole document as one tall panel: its size is the document extent of <see cref="DocumentLayout"/> at the
/// current zoom (FR-05), and its only children are the page surfaces of the retained window (NFR-05), each placed at
/// its page's rect via the attached <see cref="PageIndexProperty"/>. Pages are centred in a column as wide as the
/// widest page; the column is centred in the viewport when narrower than it. DIPs = points × zoom × 96/72 (FR-26).
/// </summary>
public sealed class VirtualPagePanel : Panel
{
    /// <summary>The page a child shows; -1 (or out of range) arranges it nowhere.</summary>
    public static readonly DependencyProperty PageIndexProperty = DependencyProperty.RegisterAttached(
        "PageIndex", typeof(int), typeof(VirtualPagePanel),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    private DocumentLayout? _layout;
    private double _zoom = 1;
    private double _viewportWidthDip;

    public static int GetPageIndex(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (int)element.GetValue(PageIndexProperty);
    }

    public static void SetPageIndex(UIElement element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(PageIndexProperty, value);
    }

    /// <summary>The layout shown, or null (empty panel).</summary>
    public DocumentLayout? Layout => _layout;

    /// <summary>DIPs per page point.</summary>
    public double Scale => _zoom * Units.DipPerPt;

    /// <summary>Sets what the panel lays out; re-measures only when something changed.</summary>
    public void Configure(DocumentLayout? layout, double zoom, double viewportWidthDip)
    {
        var width = double.IsFinite(viewportWidthDip) && viewportWidthDip > 0 ? viewportWidthDip : 0;
        if (!double.IsFinite(zoom) || zoom <= 0)
        {
            zoom = _zoom;
        }

        if (ReferenceEquals(layout, _layout) && zoom == _zoom && width == _viewportWidthDip)
        {
            return;
        }

        _layout = layout;
        _zoom = zoom;
        _viewportWidthDip = width;
        InvalidateMeasure();
        InvalidateArrange();
    }

    /// <summary>A page's size in points, from the layout (the column width minus twice its centring margin).</summary>
    public static Size PageSizePt(DocumentLayout layout, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var width = layout.ColumnWidthPt - (2 * layout.PageLeftPt(pageIndex));
        var bottom = pageIndex + 1 < layout.PageCount ? layout.PageTopPt(pageIndex + 1) : layout.TotalHeightPt;
        var height = bottom - layout.PageTopPt(pageIndex) - LayoutConstants.PageGapPt;
        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    /// <summary>Where a page sits in the panel, in DIPs; empty without a layout or for an index out of range.</summary>
    public Rect PageRect(int pageIndex)
    {
        if (_layout is not { } layout || pageIndex < 0 || pageIndex >= layout.PageCount)
        {
            return Rect.Empty;
        }

        var scale = Scale;
        var margin = (layout.ExtentWidthDip(_zoom, _viewportWidthDip) - (layout.ColumnWidthPt * scale)) / 2;
        var size = PageSizePt(layout, pageIndex);
        return new Rect(
            margin + (layout.PageLeftPt(pageIndex) * scale),
            layout.PageTopPt(pageIndex) * scale,
            size.Width * scale,
            size.Height * scale);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var rect = PageRect(GetPageIndex(child));
            child.Measure(rect.IsEmpty ? default : rect.Size);
        }

        return _layout is { } layout
            ? new Size(layout.ExtentWidthDip(_zoom, _viewportWidthDip), layout.ExtentHeightDip(_zoom))
            : default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var rect = PageRect(GetPageIndex(child));
            child.Arrange(rect.IsEmpty ? default : rect);
        }

        return finalSize;
    }
}
