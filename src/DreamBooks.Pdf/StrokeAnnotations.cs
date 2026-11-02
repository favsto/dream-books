using System.Globalization;
using System.Text;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf;

/// <summary>
/// Writes, reads and removes the app's strokes as PDF ink annotations with a private JSON
/// payload (FR-81, FR-83). PDFium thread only: every method asserts it.
/// </summary>
/// <remarks>
/// An app annotation is <c>/Subtype /Ink</c> with the <see cref="PayloadKey"/> entry. The payload
/// (<see cref="StrokeJson"/>) is the source of truth and holds model coordinates; the appearance
/// stream and /InkList are for other readers. Handle rule: every FPDF_ANNOTATION obtained from
/// CreateAnnot or GetAnnot is closed exactly once, in a finally. A removal (by index) happens only
/// after the handle used to classify that annotation has been closed; the half-built annotation
/// removed by <see cref="Write"/> on failure is the one exception, closed right after.
/// </remarks>
internal static class StrokeAnnotations
{
    public const string PayloadKey = "DRBK_Data";

    /// <summary>Value of /T on every app annotation.</summary>
    public const string Author = "Dream Books";

    // The saved appearance always uses the default pen feel (controller ruling): reopening
    // recomputes the on-screen outline from the raw points with the live settings.
    private const PressureCurve _appearanceCurve = PressureCurve.Firm;
    private const SmoothingLevel _appearanceSmoothing = SmoothingLevel.Strong;

    private const int _maxInkListPoints = 100;
    private const double _rectPadding = 1;

    /// <summary>The appearance operator that selects the ExtGState PDFium's SetAP adds for /CA (opacity spike).</summary>
    private const string _selectOpacityState = "/GS gs\n";

    /// <summary>
    /// Adds <paramref name="stroke"/> to the page as one ink annotation. A highlighter (FR-40) is written
    /// transparent by route A of <see cref="HighlighterWriteRoute"/>: /CA from its colour alpha and an
    /// appearance filled along its constant-width or chisel outline (FR-42, FR-46) that first selects the
    /// /GS state. Pen strokes stay opaque, written exactly as before.
    /// </summary>
    public static void Write(nint page, PageGeometry geometry, InkStroke stroke)
    {
        EnsurePdfiumThread();
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(stroke);
        if (page == 0)
        {
            throw new ArgumentException("The page handle is null.", nameof(page));
        }

        // Everything that can fail in managed code is computed before the annotation exists.
        string payload = StrokeJson.Serialize(stroke);
        byte alpha = stroke.Tool == InkTool.Highlighter ? stroke.Color.A : (byte)255;
        (string appearance, PdfiumNative.FS_RECTF rect) = BuildAppearance(geometry, stroke, alpha);
        PdfiumNative.FS_POINTF[] centreline = BuildCentreline(geometry, stroke);
        string date = DateTime.UtcNow.ToString("'D:'yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture);

        nint annot = PdfiumNative.FPDFPage_CreateAnnot(page, PdfiumNative.FPDF_ANNOT_INK);
        if (annot == 0)
        {
            throw new InvalidOperationException("PDFium could not create the ink annotation.");
        }

        bool written = false;
        try
        {
            // Call order matters (spike notes): Rect before SetAP, Color and Border before SetAP
            // (SetColor fails once an AP exists, SetBorder deletes it), SetAP last. An alpha below 255 writes
            // /CA, and SetAP then adds the /GS ExtGState that a highlighter's appearance selects.
            Check(SetRect(annot, rect), "SetRect");
            Check(PdfiumNative.FPDFAnnot_SetColor(
                annot, PdfiumNative.FPDFANNOT_COLORTYPE_Color, stroke.Color.R, stroke.Color.G, stroke.Color.B, alpha) != 0,
                "SetColor");
            Check(PdfiumNative.FPDFAnnot_SetBorder(annot, 0, 0, (float)stroke.BaseWidthPt) != 0, "SetBorder");
            Check(AddInkStroke(annot, centreline), "AddInkStroke");
            Check(PdfiumAnnotations.SetString(annot, "NM", stroke.Id.ToString("N", CultureInfo.InvariantCulture)), "NM");
            Check(PdfiumAnnotations.SetString(annot, "M", date), "M");
            Check(PdfiumAnnotations.SetString(annot, "T", Author), "T");
            Check(PdfiumAnnotations.SetString(annot, PayloadKey, payload), PayloadKey);
            Check(PdfiumNative.FPDFAnnot_SetFlags(annot, PdfiumNative.FPDF_ANNOT_FLAG_PRINT) != 0, "SetFlags");
            Check(PdfiumAnnotations.SetAppearance(annot, appearance), "SetAP");
            written = true;
        }
        finally
        {
            if (!written)
            {
                // Do not leave a half-built annotation on the page.
                int index = PdfiumNative.FPDFPage_GetAnnotCount(page) - 1;
                if (index >= 0)
                {
                    PdfiumNative.FPDFPage_RemoveAnnot(page, index);
                }
            }

            PdfiumNative.FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>Reads the app's strokes on the page without modifying it.</summary>
    public static StrokeReadResult Read(nint page, PageGeometry geometry)
    {
        EnsurePdfiumThread();
        ArgumentNullException.ThrowIfNull(geometry);

        var strokes = new List<InkStroke>();
        var unreadable = new List<int>();
        int foreign = 0;
        int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            nint annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
            if (annot == 0)
            {
                foreign++;
                continue;
            }

            try
            {
                switch (Classify(annot))
                {
                    case (AnnotKind.App, InkStroke stroke, _):
                        strokes.Add(stroke);
                        break;
                    case (AnnotKind.Unreadable, _, _):
                        unreadable.Add(i);
                        break;
                    default:
                        foreign++;
                        break;
                }
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        }

        return new StrokeReadResult(strokes, unreadable, foreign);
    }

    /// <summary>
    /// True when the page holds at least one ink annotation carrying <see cref="PayloadKey"/>, readable or
    /// not. The cheap pre-check of the mark scan (FR-100, NFR-02): it reads only each annotation's subtype
    /// and the key's presence, never a payload, and stops at the first match.
    /// </summary>
    public static bool HasAppAnnotation(nint page)
    {
        EnsurePdfiumThread();
        int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            nint annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
            if (annot == 0)
            {
                continue;
            }

            bool ours;
            try
            {
                ours = PdfiumNative.FPDFAnnot_GetSubtype(annot) == PdfiumNative.FPDF_ANNOT_INK
                    && PdfiumNative.FPDFAnnot_HasKey(annot, PayloadKey) != 0;
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }

            if (ours)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes every readable app annotation whose payload Id is <paramref name="id"/>, so an erased
    /// stroke cannot come back through a duplicate. Matching is by payload Id only (/NM is ignored).
    /// Annotations with a newer or corrupt payload are never removed. Returns how many were removed.
    /// </summary>
    public static int RemoveById(nint page, Guid id) => RemoveByIds(page, new HashSet<Guid> { id });

    /// <summary>
    /// Removes, in one pass that parses each payload once, every readable app annotation whose
    /// payload Id is in <paramref name="ids"/> (all duplicates included; /NM is ignored). Ids not on
    /// the page are ignored; foreign annotations and annotations with a newer or corrupt payload are
    /// never removed. Returns how many were removed.
    /// </summary>
    public static int RemoveByIds(nint page, IReadOnlySet<Guid> ids)
    {
        EnsurePdfiumThread();
        ArgumentNullException.ThrowIfNull(ids);
        return ids.Count == 0 ? 0 : RemoveWhere(page, stroke => ids.Contains(stroke.Id));
    }

    /// <summary>
    /// The exact payload strings of the readable app annotations on the page, in annotation order,
    /// with their parsed strokes, plus the unreadable and foreign counts, in one pass that parses each
    /// payload once. Used to build and check a saved copy (NFR-10).
    /// </summary>
    internal static IReadOnlyList<(InkStroke Stroke, string Payload)> ReadAppPayloads(
        nint page, out int unreadableCount, out int foreignCount)
    {
        EnsurePdfiumThread();
        var payloads = new List<(InkStroke, string)>();
        unreadableCount = 0;
        foreignCount = 0;
        int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            nint annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
            if (annot == 0)
            {
                foreignCount++; // as in Read
                continue;
            }

            try
            {
                switch (Classify(annot))
                {
                    case (AnnotKind.App, InkStroke stroke, string payload):
                        payloads.Add((stroke, payload));
                        break;
                    case (AnnotKind.Unreadable, _, _):
                        unreadableCount++;
                        break;
                    default:
                        foreignCount++;
                        break;
                }
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        }

        return payloads;
    }

    /// <summary>Removes every app annotation whose payload parses; unreadable ones stay. Returns the count.</summary>
    public static int RemoveAllApp(nint page)
    {
        EnsurePdfiumThread();
        return RemoveWhere(page, _ => true);
    }

    /// <summary>Test counter: how many payloads have been parsed (PDFium thread only).</summary>
    internal static long PayloadParseCount { get; private set; }

    /// <summary>One pass from the end (indexes still to visit stay valid), each payload parsed once.</summary>
    private static int RemoveWhere(nint page, Func<InkStroke, bool> match)
    {
        int removed = 0;
        for (int i = PdfiumNative.FPDFPage_GetAnnotCount(page) - 1; i >= 0; i--)
        {
            nint annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
            if (annot == 0)
            {
                continue;
            }

            bool doomed;
            try
            {
                doomed = Classify(annot) is (AnnotKind.App, InkStroke stroke, _) && match(stroke);
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }

            if (doomed)
            {
                Remove(page, i);
                removed++;
            }
        }

        return removed;
    }

    private static void Remove(nint page, int index)
    {
        if (PdfiumNative.FPDFPage_RemoveAnnot(page, index) == 0)
        {
            throw new InvalidOperationException("PDFium could not remove the annotation.");
        }
    }

    private static (AnnotKind Kind, InkStroke? Stroke, string? Payload) Classify(nint annot)
    {
        if (PdfiumNative.FPDFAnnot_GetSubtype(annot) != PdfiumNative.FPDF_ANNOT_INK)
        {
            return (AnnotKind.Foreign, null, null);
        }

        string? payload = PdfiumAnnotations.GetString(annot, PayloadKey);
        if (payload is null)
        {
            return (AnnotKind.Foreign, null, null);
        }

        PayloadParseCount++;
        StrokeParseResult parsed = StrokeJson.Parse(payload);
        return parsed.Status == StrokeParseStatus.Ok && parsed.Stroke is not null
            ? (AnnotKind.App, parsed.Stroke, payload)
            : (AnnotKind.Unreadable, null, payload);
    }

    /// <summary>
    /// The filled contour in user space, coloured, and a rect that covers it. A translucent stroke
    /// (<paramref name="alpha"/> below 255) first selects the /GS state, or PDFium paints it opaque.
    /// </summary>
    private static (string Content, PdfiumNative.FS_RECTF Rect) BuildAppearance(
        PageGeometry geometry, InkStroke stroke, byte alpha)
    {
        IReadOnlyList<IReadOnlyList<PointD>> figures = stroke.Tool == InkTool.Highlighter
            ? [HighlighterGeometry.Outline(stroke)]
            : StrokeGeometry.BuildOutline(stroke, _appearanceCurve, _appearanceSmoothing);

        var content = new StringBuilder();
        if (alpha < 255)
        {
            content.Append(_selectOpacityState);
        }

        content.Append(Num(stroke.Color.R / 255.0)).Append(' ')
            .Append(Num(stroke.Color.G / 255.0)).Append(' ')
            .Append(Num(stroke.Color.B / 255.0)).Append(" rg\n");

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (IReadOnlyList<PointD> figure in figures)
        {
            for (int i = 0; i < figure.Count; i++)
            {
                PointD user = geometry.ModelToUser(figure[i]);
                minX = Math.Min(minX, user.X);
                minY = Math.Min(minY, user.Y);
                maxX = Math.Max(maxX, user.X);
                maxY = Math.Max(maxY, user.Y);
                content.Append(Num(user.X)).Append(' ').Append(Num(user.Y)).Append(i == 0 ? " m\n" : " l\n");
            }

            content.Append("h\n");
        }

        content.Append('f');

        if (!double.IsFinite(minX))
        {
            // No figure (cannot happen for a valid stroke): fall back to the points themselves.
            foreach (InkPoint p in stroke.Points)
            {
                PointD user = geometry.ModelToUser(p.Position);
                minX = Math.Min(minX, user.X);
                minY = Math.Min(minY, user.Y);
                maxX = Math.Max(maxX, user.X);
                maxY = Math.Max(maxY, user.Y);
            }
        }

        // The padding also absorbs the float rounding of FS_RECTF and of the written numbers;
        // the appearance BBox is set from this rect, so the whole contour stays inside it.
        var rect = new PdfiumNative.FS_RECTF
        {
            Left = (float)Math.Floor(minX - _rectPadding),
            Bottom = (float)Math.Floor(minY - _rectPadding),
            Right = (float)Math.Ceiling(maxX + _rectPadding),
            Top = (float)Math.Ceiling(maxY + _rectPadding),
        };
        return (content.ToString(), rect);
    }

    /// <summary>The stroke's points in user space, evenly decimated to at most 100 (first and last kept).</summary>
    private static PdfiumNative.FS_POINTF[] BuildCentreline(PageGeometry geometry, InkStroke stroke)
    {
        IReadOnlyList<InkPoint> points = stroke.Points;
        int count = Math.Min(points.Count, _maxInkListPoints);
        var result = new PdfiumNative.FS_POINTF[count];
        for (int i = 0; i < count; i++)
        {
            int source = count == 1 ? 0 : (int)Math.Round(i * (points.Count - 1) / (double)(count - 1));
            PointD user = geometry.ModelToUser(points[source].Position);
            result[i] = new PdfiumNative.FS_POINTF { X = (float)user.X, Y = (float)user.Y };
        }

        return result;
    }

    private static unsafe bool SetRect(nint annot, PdfiumNative.FS_RECTF rect) =>
        PdfiumNative.FPDFAnnot_SetRect(annot, &rect) != 0;

    private static unsafe bool AddInkStroke(nint annot, PdfiumNative.FS_POINTF[] points)
    {
        fixed (PdfiumNative.FS_POINTF* p = points)
        {
            return PdfiumNative.FPDFAnnot_AddInkStroke(annot, p, (nuint)points.Length) >= 0;
        }
    }

    private static void Check(bool ok, string step)
    {
        if (!ok)
        {
            throw new InvalidOperationException($"PDFium failed to write the ink annotation ({step}).");
        }
    }

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static void EnsurePdfiumThread()
    {
        if (!PdfiumThread.IsPdfiumThread)
        {
            throw new InvalidOperationException("Stroke annotations must be used on the PDFium thread.");
        }
    }

    private enum AnnotKind
    {
        Foreign,
        App,
        Unreadable,
    }
}
