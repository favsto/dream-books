using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf;

/// <summary>
/// Maps between the model frame and PDF default user space for one page (FR-26, FR-81).
/// </summary>
/// <remarks>
/// The model frame is the page as PDFium displays it: the visible box (CropBox clipped to the
/// MediaBox) with /Rotate applied, origin top-left, y down, 1 unit = 1 point. User space is the
/// PDF default user space (y up) in which annotation rects, ink lists and appearance streams are
/// written. With the visible box [L B R T] and r clockwise quarter turns:
/// <code>
/// r = 0:  user = (L + mx, T - my)     model = (ux - L, T - uy)
/// r = 1:  user = (L + my, B + mx)     model = (uy - B, ux - L)
/// r = 2:  user = (R - mx, B + my)     model = (R - ux, uy - B)
/// r = 3:  user = (R - my, T - mx)     model = (T - uy, R - ux)
/// </code>
/// Computed in double from the box and rotation PDFium reports, so the round trip is exact;
/// the rendered-pixel tests verify it against PDFium's own display matrix.
/// </remarks>
internal sealed class PageGeometry
{
    private readonly double _left;
    private readonly double _bottom;
    private readonly double _right;
    private readonly double _top;
    private readonly int _rotation;

    private PageGeometry(double left, double bottom, double right, double top, int rotation)
    {
        _left = left;
        _bottom = bottom;
        _right = right;
        _top = top;
        _rotation = rotation;
        double width = right - left;
        double height = top - bottom;
        Dimensions = rotation % 2 == 0 ? new PageDimensions(width, height) : new PageDimensions(height, width);
    }

    /// <summary>Visible page size in points, after rotation, as PDFium renders it.</summary>
    public PageDimensions Dimensions { get; }

    /// <summary>Reads the visible box and rotation of a loaded page. PDFium thread only.</summary>
    public static PageGeometry From(nint page)
    {
        if (page == 0)
        {
            throw new ArgumentException("The page handle is null.", nameof(page));
        }

        if (PdfiumNative.FPDF_GetPageBoundingBox(page, out PdfiumNative.FS_RECTF box) == 0)
        {
            throw new InvalidOperationException("PDFium could not read the page box.");
        }

        int rotation = PdfiumNative.FPDFPage_GetRotation(page);
        if (rotation is < 0 or > 3)
        {
            throw new InvalidOperationException("PDFium could not read the page rotation.");
        }

        return new PageGeometry(
            Math.Min(box.Left, box.Right),
            Math.Min(box.Bottom, box.Top),
            Math.Max(box.Left, box.Right),
            Math.Max(box.Bottom, box.Top),
            rotation);
    }

    /// <summary>Converts a model point (top-left origin, y down) to PDF user space (y up).</summary>
    public PointD ModelToUser(PointD model) => _rotation switch
    {
        0 => new PointD(_left + model.X, _top - model.Y),
        1 => new PointD(_left + model.Y, _bottom + model.X),
        2 => new PointD(_right - model.X, _bottom + model.Y),
        _ => new PointD(_right - model.Y, _top - model.X),
    };

    /// <summary>Converts a PDF user-space point (y up) to the model frame (top-left origin, y down).</summary>
    public PointD UserToModel(PointD user) => _rotation switch
    {
        0 => new PointD(user.X - _left, _top - user.Y),
        1 => new PointD(user.Y - _bottom, user.X - _left),
        2 => new PointD(_right - user.X, user.Y - _bottom),
        _ => new PointD(_top - user.Y, _right - user.X),
    };

    /// <summary>
    /// The user-space bounds of a model rectangle (model y down, so <paramref name="top"/> is the
    /// smaller y), returned as a PDF rect with Bottom &lt; Top.
    /// </summary>
    public (double Left, double Bottom, double Right, double Top) RectToUser(
        double left, double top, double right, double bottom)
    {
        PointD a = ModelToUser(new PointD(left, top));
        PointD b = ModelToUser(new PointD(right, bottom));
        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }
}
