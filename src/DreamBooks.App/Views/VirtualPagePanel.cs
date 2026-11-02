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
/// Children are placed at <see cref="ArrangedRect"/>: the exact rect snapped to whole device pixels on screen.
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
    private double _dpiScale = 1;
    private Vector _scrollOffset;

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

    /// <summary>Device pixels per DIP of the screen the panel is on (1.5 at 150%).</summary>
    public double DpiScale => _dpiScale;

    /// <summary>
    /// The scroll viewer's current offsets (DIPs). Pages are placed so that their origin on screen falls on a whole
    /// device pixel at these offsets; changing them re-arranges the pages (sizes stay, only the snap of the origin moves).
    /// </summary>
    public Vector ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            if (value != _scrollOffset)
            {
                _scrollOffset = value;
                InvalidateArrange();
            }
        }
    }

    /// <summary>Sets what the panel lays out; re-measures only when something changed.</summary>
    public void Configure(DocumentLayout? layout, double zoom, double viewportWidthDip, double dpiScale = 1)
    {
        var width = double.IsFinite(viewportWidthDip) && viewportWidthDip > 0 ? viewportWidthDip : 0;
        if (!double.IsFinite(zoom) || zoom <= 0)
        {
            zoom = _zoom;
        }

        var dpi = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        if (ReferenceEquals(layout, _layout) && zoom == _zoom && width == _viewportWidthDip && dpi == _dpiScale)
        {
            return;
        }

        _layout = layout;
        _zoom = zoom;
        _viewportWidthDip = width;
        _dpiScale = dpi;
        InvalidateMeasure();
        InvalidateArrange();
    }

    /// <summary>
    /// Where a page's surface is actually placed: its <see cref="PageRect"/> with the size rounded to whole device
    /// pixels and the origin snapped so that, at <see cref="ScrollOffset"/>, it lands on a whole device pixel of the
    /// viewport (each at most half a pixel away). The bitmap then maps 1:1 onto screen pixels (NFR-07) while the
    /// geometry, the scroll extent and the ink scale stay exact.
    /// </summary>
    public Rect ArrangedRect(int pageIndex)
    {
        var exact = PageRect(pageIndex);
        if (exact.IsEmpty)
        {
            return exact;
        }

        var dpi = _dpiScale;
        double Snap(double value) => Math.Round(value * dpi) / dpi; // as RenderSizePolicy rounds the bitmap size
        return new Rect(
            Snap(exact.X - _scrollOffset.X) + _scrollOffset.X,
            Snap(exact.Y - _scrollOffset.Y) + _scrollOffset.Y,
            Snap(exact.Width),
            Snap(exact.Height));
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
            var rect = ArrangedRect(GetPageIndex(child)); // only its size counts here
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
            var rect = ArrangedRect(GetPageIndex(child));
            child.Arrange(rect.IsEmpty ? default : rect);
        }

        return finalSize;
    }
}
