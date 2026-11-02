using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Layout;

/// <summary>
/// Vertical stack of pages centred in a column as wide as the widest page. Pure math in page points
/// and DIPs; no UI types. Page lookups are binary searches over precomputed tops.
/// </summary>
public sealed class DocumentLayout
{
    private readonly double[] _tops;
    private readonly double[] _widths;
    private readonly double _gapPt;

    public DocumentLayout(IReadOnlyList<PageDimensions> pages, double gapPt = LayoutConstants.PageGapPt)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            throw new ArgumentException("A layout needs at least one page.", nameof(pages));
        }

        if (!double.IsFinite(gapPt) || gapPt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gapPt));
        }

        _gapPt = gapPt;
        _tops = new double[pages.Count];
        _widths = new double[pages.Count];
        double y = 0;
        double widest = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            PageDimensions p = pages[i];
            if (!double.IsFinite(p.WidthPt) || !double.IsFinite(p.HeightPt) || p.WidthPt <= 0 || p.HeightPt <= 0)
            {
                throw new ArgumentException($"Page {i} has an invalid size.", nameof(pages));
            }

            _tops[i] = y;
            _widths[i] = p.WidthPt;
            y += p.HeightPt + gapPt;
            widest = Math.Max(widest, p.WidthPt);
        }

        TotalHeightPt = y;
        ColumnWidthPt = widest;
    }

    public int PageCount => _tops.Length;

    public double ColumnWidthPt { get; }

    public double TotalHeightPt { get; }

    public double PageTopPt(int i) => _tops[i];

    public double PageLeftPt(int i) => (ColumnWidthPt - _widths[i]) / 2;

    /// <summary>Page at a document y; a gap belongs to the page below it. Clamped to valid pages.</summary>
    public int PageAt(double yPt)
    {
        // Largest k with top(k) - gap <= y.
        int lo = 0;
        int hi = _tops.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_tops[mid] - _gapPt <= yPt)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    public (int First, int Last) VisibleRange(double topPt, double heightPt)
    {
        int first = PageAt(topPt);
        double bottom = topPt + heightPt;

        // Largest k with top(k) < bottom (a page starting exactly at the bottom edge is not visible).
        int lo = first;
        int hi = _tops.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_tops[mid] < bottom)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return (first, lo);
    }

    public (int First, int Last) RetainedRange(double topPt, double heightPt, int margin = 2)
    {
        (int first, int last) = VisibleRange(topPt, heightPt);
        return (Math.Max(0, first - margin), Math.Min(_tops.Length - 1, last + margin));
    }

    public double FitWidthZoom(double viewportWidthDip)
        => Math.Clamp(viewportWidthDip / (ColumnWidthPt * Units.DipPerPt), LayoutConstants.MinZoom, LayoutConstants.MaxZoom);

    public double ExtentWidthDip(double zoom, double viewportWidthDip)
        => Math.Max(viewportWidthDip, ColumnWidthPt * zoom * Units.DipPerPt);

    public double ExtentHeightDip(double zoom) => TotalHeightPt * zoom * Units.DipPerPt;

    public ViewPosition ToPosition(double offsetXDip, double offsetYDip, double viewportWidthDip, double zoom)
    {
        ValidateZoom(zoom);
        double scale = zoom * Units.DipPerPt;
        double yPt = offsetYDip / scale;
        int page = PageAt(yPt);
        double margin = Margin(zoom, viewportWidthDip);
        double columnX = (offsetXDip + (viewportWidthDip / 2) - margin) / scale;
        return new ViewPosition(page, yPt - _tops[page], columnX, zoom);
    }

    public (double OffsetXDip, double OffsetYDip) ToOffsets(ViewPosition p, double viewportWidthDip, double viewportHeightDip)
    {
        ValidateZoom(p.Zoom);
        double scale = p.Zoom * Units.DipPerPt;
        double extentW = ExtentWidthDip(p.Zoom, viewportWidthDip);
        double extentH = ExtentHeightDip(p.Zoom);
        double x = (p.ColumnXPt * scale) + Margin(p.Zoom, viewportWidthDip) - (viewportWidthDip / 2);
        double y = (_tops[p.PageIndex] + p.YPt) * scale;
        return (
            Math.Clamp(x, 0, Math.Max(0, extentW - viewportWidthDip)),
            Math.Clamp(y, 0, Math.Max(0, extentH - viewportHeightDip)));
    }

    /// <summary>
    /// New position at <paramref name="newZoom"/> keeping the document point under the anchor (viewport
    /// DIPs) fixed. The result is not clamped to the scrollable extent; <see cref="ToOffsets"/> does that.
    /// </summary>
    public ViewPosition ZoomAt(ViewPosition current, double newZoom, double anchorXDip, double anchorYDip, double viewportWidthDip)
    {
        ValidateZoom(current.Zoom);
        newZoom = double.IsNaN(newZoom) ? current.Zoom : Math.Clamp(newZoom, LayoutConstants.MinZoom, LayoutConstants.MaxZoom);
        double oldScale = current.Zoom * Units.DipPerPt;
        double newScale = newZoom * Units.DipPerPt;
        double fromCentre = anchorXDip - (viewportWidthDip / 2);
        double anchorYPt = _tops[current.PageIndex] + current.YPt + (anchorYDip / oldScale);
        double anchorColX = current.ColumnXPt + (fromCentre / oldScale);
        double topYPt = anchorYPt - (anchorYDip / newScale);
        double centreColX = anchorColX - (fromCentre / newScale);
        int page = PageAt(topYPt);
        return new ViewPosition(page, topYPt - _tops[page], centreColX, newZoom);
    }

    private static void ValidateZoom(double zoom)
    {
        if (!double.IsFinite(zoom) || zoom <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), zoom, "Zoom must be a positive finite number.");
        }
    }

    // Left margin of the column inside the extent when the column is narrower than the viewport.
    private double Margin(double zoom, double viewportWidthDip)
        => (ExtentWidthDip(zoom, viewportWidthDip) - (ColumnWidthPt * zoom * Units.DipPerPt)) / 2;
}
