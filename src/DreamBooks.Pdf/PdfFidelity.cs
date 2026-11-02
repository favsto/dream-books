using System.Globalization;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf;

/// <summary>
/// Compares a PDF with its rewritten copy (FR-82, NFR-10): page count and sizes; per page the
/// extracted text, link and other annotation counts by subtype, page-object counts by type
/// (form XObjects included recursively); the outline entry count and the metadata fields; and
/// a low-resolution render of the first, middle and last page. Reports only counts and
/// difference categories, never document text or metadata values (NFR-12).
/// <see cref="Compare"/> must run inside <see cref="PdfiumThread.RunAsync{T}"/>; <see cref="CompareAsync"/> runs the
/// same check as a series of short jobs on that thread.
/// </summary>
internal static class PdfFidelity
{
    /// <summary>Width in pixels of the comparison renders.</summary>
    internal const int RenderWidth = 200;

    /// <summary>Largest accepted mean absolute difference per channel, in 1/255 units.</summary>
    internal const double MaxMeanPixelDifference = 2.0;

    /// <summary>
    /// How long one job of <see cref="CompareAsync"/> compares pages before it gives the PDFium thread back: work
    /// queued meanwhile (a page render, a stroke load) waits at most about this long (NFR-03, NFR-04).
    /// </summary>
    internal static readonly TimeSpan DefaultSlice = TimeSpan.FromMilliseconds(15);

    private const double _pageSizeTolerance = 0.01;

    private static readonly string[] _metadataTags = ["Title", "Author", "Subject", "Keywords", "Creator", "Producer"];

    internal static FidelityReport Compare(string originalPath, string rewrittenPath)
    {
        if (!PdfiumThread.IsPdfiumThread)
        {
            throw new InvalidOperationException("PdfFidelity must run on the PDFium thread.");
        }

        using Comparison comparison = Comparison.Open(originalPath, rewrittenPath);
        while (!comparison.Step(Timeout.InfiniteTimeSpan))
        {
        }

        return comparison.Finish();
    }

    /// <summary>
    /// <see cref="Compare"/> in short jobs on the PDFium thread: one opens both documents, each next one compares pages
    /// for about <paramref name="slice"/> (at least one page; <see cref="TimeSpan.Zero"/>: one page per job), the last
    /// one reads the outline and metadata. The next job is queued only when the previous one is done, so other work
    /// queued meanwhile runs between them. Both documents are closed whatever happens. <paramref name="afterSlice"/>
    /// (tests) runs on the PDFium thread after each page slice.
    /// </summary>
    internal static async Task<FidelityReport> CompareAsync(
        string originalPath, string rewrittenPath, TimeSpan slice, Action? afterSlice, CancellationToken ct)
    {
        Comparison comparison = await PdfiumThread.RunAsync(() => Comparison.Open(originalPath, rewrittenPath), ct)
            .ConfigureAwait(false);
        try
        {
            while (!await PdfiumThread.RunAsync(
                () =>
                {
                    bool done = comparison.Step(slice);
                    afterSlice?.Invoke();
                    return done;
                },
                ct).ConfigureAwait(false))
            {
            }

            return await PdfiumThread.RunAsync(comparison.Finish, ct).ConfigureAwait(false);
        }
        finally
        {
            await PdfiumThread.RunAsync(comparison.Dispose, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One comparison in progress: both documents open, the totals so far and the next page. Every member runs on the
    /// PDFium thread; the comparison owns both documents until <see cref="Dispose"/>.
    /// </summary>
    private sealed class Comparison : IDisposable
    {
        private readonly PdfiumDocument _original;
        private readonly PdfiumDocument _rewritten;
        private readonly List<string> _differences = [];
        private readonly List<string> _summary = [];
        private readonly SortedSet<int> _renderedPages = [];
        private readonly SortedDictionary<int, int> _annotsA = [];
        private readonly SortedDictionary<int, int> _annotsB = [];
        private readonly SortedDictionary<int, int> _objectsA = [];
        private readonly SortedDictionary<int, int> _objectsB = [];
        private readonly int _pages;
        private long _textA;
        private long _textB;
        private int _next;

        private Comparison(PdfiumDocument original, PdfiumDocument rewritten)
        {
            _original = original;
            _rewritten = rewritten;
            _summary.Add(Inv($"pages {original.PageCount} vs {rewritten.PageCount}"));
            if (original.PageCount != rewritten.PageCount)
            {
                _differences.Add(Inv($"page-count {original.PageCount} vs {rewritten.PageCount}"));
            }

            _pages = Math.Min(original.PageCount, rewritten.PageCount);
            if (_pages > 0)
            {
                _renderedPages.Add(0);
                _renderedPages.Add(_pages / 2);
                _renderedPages.Add(_pages - 1);
            }
        }

        public static Comparison Open(string originalPath, string rewrittenPath)
        {
            if (!PdfiumThread.IsPdfiumThread)
            {
                throw new InvalidOperationException("PdfFidelity must run on the PDFium thread.");
            }

            PdfiumDocument original = PdfiumDocument.Open(originalPath);
            try
            {
                return new Comparison(original, PdfiumDocument.Open(rewrittenPath));
            }
            catch
            {
                original.Dispose();
                throw;
            }
        }

        /// <summary>Compares pages for about <paramref name="slice"/> (at least one); true when every page is done.</summary>
        public bool Step(TimeSpan slice)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            while (_next < _pages)
            {
                ComparePage(_original, _rewritten, _next, _renderedPages.Contains(_next), _differences, _summary,
                    ref _textA, ref _textB, _annotsA, _annotsB, _objectsA, _objectsB);
                _next++;
                if (slice != Timeout.InfiniteTimeSpan && System.Diagnostics.Stopwatch.GetElapsedTime(start) >= slice)
                {
                    break;
                }
            }

            return _next >= _pages;
        }

        /// <summary>The totals, outline and metadata: the report. Call once every page is done.</summary>
        public FidelityReport Finish()
        {
            _summary.Add(Inv($"text characters {_textA} vs {_textB}"));
            _summary.Add($"annotations by subtype {Format(_annotsA, SubtypeName)} vs {Format(_annotsB, SubtypeName)}");
            _summary.Add($"page objects by type {Format(_objectsA, ObjectTypeName)} vs {Format(_objectsB, ObjectTypeName)}");

            int outlineA = CountOutlineEntries(_original.Handle);
            int outlineB = CountOutlineEntries(_rewritten.Handle);
            _summary.Add(Inv($"outline entries {outlineA} vs {outlineB}"));
            if (outlineA != outlineB)
            {
                _differences.Add(Inv($"outline-entries {outlineA} vs {outlineB}"));
            }

            var presentTags = new List<string>();
            foreach (string tag in _metadataTags)
            {
                string a = ReadMetaText(_original.Handle, tag);
                string b = ReadMetaText(_rewritten.Handle, tag);
                if (a.Length > 0)
                {
                    presentTags.Add(tag);
                }

                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    _differences.Add(Inv($"metadata {tag} differs (length {a.Length} vs {b.Length})"));
                }
            }

            _summary.Add($"metadata fields present: {(presentTags.Count == 0 ? "none" : string.Join(", ", presentTags))}");
            return new FidelityReport(_differences) { Summary = _summary };
        }

        public void Dispose()
        {
            _rewritten.Dispose();
            _original.Dispose();
        }
    }

    /// <summary>Extracted text of a loaded page. PDFium thread only.</summary>
    internal static unsafe string ReadPageText(nint page)
    {
        nint textPage = PdfiumText.FPDFText_LoadPage(page);
        if (textPage == 0)
        {
            throw new InvalidOperationException("PDFium could not load the page text.");
        }

        try
        {
            int count = PdfiumText.FPDFText_CountChars(textPage);
            if (count <= 0)
            {
                return string.Empty;
            }

            char[] buffer = new char[count + 1];
            int written;
            fixed (char* p = buffer)
            {
                written = PdfiumText.FPDFText_GetText(textPage, 0, count, p);
            }

            return new string(buffer, 0, Math.Clamp(written - 1, 0, count));
        }
        finally
        {
            PdfiumText.FPDFText_ClosePage(textPage);
        }
    }

    private static void ComparePage(
        PdfiumDocument original,
        PdfiumDocument rewritten,
        int index,
        bool render,
        List<string> differences,
        List<string> summary,
        ref long textA,
        ref long textB,
        SortedDictionary<int, int> annotsA,
        SortedDictionary<int, int> annotsB,
        SortedDictionary<int, int> objectsA,
        SortedDictionary<int, int> objectsB)
    {
        int label = index + 1;
        PdfiumNative.FPDF_GetPageSizeByIndexF(original.Handle, index, out PdfiumNative.FS_SIZEF sizeA);
        PdfiumNative.FPDF_GetPageSizeByIndexF(rewritten.Handle, index, out PdfiumNative.FS_SIZEF sizeB);
        if (Math.Abs(sizeA.Width - sizeB.Width) > _pageSizeTolerance
            || Math.Abs(sizeA.Height - sizeB.Height) > _pageSizeTolerance)
        {
            differences.Add(Inv($"page {label}: size {sizeA.Width}x{sizeA.Height} vs {sizeB.Width}x{sizeB.Height}"));
        }

        nint pageA = original.LoadPage(index);
        try
        {
            nint pageB = rewritten.LoadPage(index);
            try
            {
                string a = ReadPageText(pageA);
                string b = ReadPageText(pageB);
                textA += a.Length;
                textB += b.Length;
                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    differences.Add(Inv($"page {label}: text differs (length {a.Length} vs {b.Length})"));
                }

                SortedDictionary<int, int> pageAnnotsA = CountAnnotations(pageA);
                SortedDictionary<int, int> pageAnnotsB = CountAnnotations(pageB);
                AddInto(annotsA, pageAnnotsA);
                AddInto(annotsB, pageAnnotsB);
                foreach (int subtype in pageAnnotsA.Keys.Union(pageAnnotsB.Keys))
                {
                    int ca = pageAnnotsA.GetValueOrDefault(subtype);
                    int cb = pageAnnotsB.GetValueOrDefault(subtype);
                    if (ca != cb)
                    {
                        string what = subtype == PdfiumNative.FPDF_ANNOT_LINK
                            ? "link annotations"
                            : $"annotations {SubtypeName(subtype)}";
                        differences.Add(Inv($"page {label}: {what} {ca} vs {cb}"));
                    }
                }

                SortedDictionary<int, int> pageObjectsA = CountPageObjects(pageA);
                SortedDictionary<int, int> pageObjectsB = CountPageObjects(pageB);
                AddInto(objectsA, pageObjectsA);
                AddInto(objectsB, pageObjectsB);
                foreach (int type in pageObjectsA.Keys.Union(pageObjectsB.Keys))
                {
                    int ca = pageObjectsA.GetValueOrDefault(type);
                    int cb = pageObjectsB.GetValueOrDefault(type);
                    if (ca != cb)
                    {
                        differences.Add(Inv($"page {label}: page objects {ObjectTypeName(type)} {ca} vs {cb}"));
                    }
                }

                if (render)
                {
                    ComparePixels(pageA, pageB, sizeA, label, differences, summary);
                }
            }
            finally
            {
                rewritten.ClosePage(pageB);
            }
        }
        finally
        {
            original.ClosePage(pageA);
        }
    }

    private static void ComparePixels(
        nint pageA, nint pageB, PdfiumNative.FS_SIZEF size, int label, List<string> differences, List<string> summary)
    {
        int height = size.Width > 0 && size.Height > 0
            ? Math.Clamp((int)Math.Round(RenderWidth * size.Height / size.Width), 1, RenderWidth * 20)
            : RenderWidth;
        RenderedPage a = PdfRenderer.RenderLoadedPage(pageA, RenderWidth, height);
        RenderedPage b = PdfRenderer.RenderLoadedPage(pageB, RenderWidth, height);

        double[] means = MeanAbsoluteDifferencePerChannel(a, b);
        string formatted = Inv($"B {means[0]:0.###} G {means[1]:0.###} R {means[2]:0.###} A {means[3]:0.###}");
        summary.Add($"page {label.ToString(CultureInfo.InvariantCulture)}: pixel mean abs diff (1/255) {formatted}");
        if (means.Any(m => m > MaxMeanPixelDifference))
        {
            differences.Add($"page {label.ToString(CultureInfo.InvariantCulture)}: pixels differ, mean abs diff (1/255) {formatted}");
        }
    }

    internal static double[] MeanAbsoluteDifferencePerChannel(RenderedPage a, RenderedPage b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
        {
            throw new ArgumentException("The renders must have the same size.", nameof(b));
        }

        var sums = new long[4];
        for (int y = 0; y < a.PixelHeight; y++)
        {
            int rowA = y * a.Stride;
            int rowB = y * b.Stride;
            for (int x = 0; x < a.PixelWidth * 4; x++)
            {
                sums[x & 3] += Math.Abs(a.Bgra[rowA + x] - b.Bgra[rowB + x]);
            }
        }

        double pixels = (double)a.PixelWidth * a.PixelHeight;
        return [sums[0] / pixels, sums[1] / pixels, sums[2] / pixels, sums[3] / pixels];
    }

    internal static SortedDictionary<int, int> CountAnnotations(nint page)
    {
        var counts = new SortedDictionary<int, int>();
        int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            nint annotation = PdfiumNative.FPDFPage_GetAnnot(page, i);
            int subtype = -1;
            if (annotation != 0)
            {
                subtype = PdfiumNative.FPDFAnnot_GetSubtype(annotation);
                PdfiumNative.FPDFPage_CloseAnnot(annotation);
            }

            counts[subtype] = counts.GetValueOrDefault(subtype) + 1;
        }

        return counts;
    }

    private static SortedDictionary<int, int> CountPageObjects(nint page)
    {
        var counts = new SortedDictionary<int, int>();
        int count = PdfiumNative.FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++)
        {
            CountObject(PdfiumNative.FPDFPage_GetObject(page, i), counts, depth: 0);
        }

        return counts;
    }

    private static void CountObject(nint pageObject, SortedDictionary<int, int> counts, int depth)
    {
        int type = pageObject == 0 ? -1 : PdfiumNative.FPDFPageObj_GetType(pageObject);
        counts[type] = counts.GetValueOrDefault(type) + 1;
        if (type != PdfiumNative.FPDF_PAGEOBJ_FORM || depth >= 32)
        {
            return;
        }

        int children = PdfiumNative.FPDFFormObj_CountObjects(pageObject);
        for (int i = 0; i < children; i++)
        {
            CountObject(PdfiumNative.FPDFFormObj_GetObject(pageObject, (uint)i), counts, depth + 1);
        }
    }

    /// <summary>Counts every outline entry at every level, guarding against cycles.</summary>
    internal static int CountOutlineEntries(nint document)
    {
        var visited = new HashSet<nint>();
        var pending = new Stack<nint>();
        nint first = PdfiumNative.FPDFBookmark_GetFirstChild(document, 0);
        if (first != 0)
        {
            pending.Push(first);
        }

        while (pending.Count > 0)
        {
            nint bookmark = pending.Pop();
            if (!visited.Add(bookmark))
            {
                continue;
            }

            nint sibling = PdfiumNative.FPDFBookmark_GetNextSibling(document, bookmark);
            if (sibling != 0)
            {
                pending.Push(sibling);
            }

            nint child = PdfiumNative.FPDFBookmark_GetFirstChild(document, bookmark);
            if (child != 0)
            {
                pending.Push(child);
            }
        }

        return visited.Count;
    }

    internal static unsafe string ReadMetaText(nint document, string tag)
    {
        uint length = PdfiumNative.FPDF_GetMetaText(document, tag, null, 0);
        if (length <= 2)
        {
            return string.Empty;
        }

        char[] buffer = new char[length / 2];
        fixed (char* p = buffer)
        {
            PdfiumNative.FPDF_GetMetaText(document, tag, p, length);
        }

        return new string(buffer, 0, buffer.Length - 1);
    }

    private static void AddInto(SortedDictionary<int, int> total, SortedDictionary<int, int> page)
    {
        foreach ((int key, int value) in page)
        {
            total[key] = total.GetValueOrDefault(key) + value;
        }
    }

    private static string Format(SortedDictionary<int, int> counts, Func<int, string> name) =>
        counts.Count == 0
            ? "{}"
            : "{" + string.Join(", ", counts.Select(kv => Inv($"{name(kv.Key)}={kv.Value}"))) + "}";

    private static string SubtypeName(int subtype) => subtype switch
    {
        -1 => "unreadable",
        0 => "Unknown",
        1 => "Text",
        2 => "Link",
        3 => "FreeText",
        4 => "Line",
        5 => "Square",
        6 => "Circle",
        7 => "Polygon",
        8 => "PolyLine",
        9 => "Highlight",
        10 => "Underline",
        11 => "Squiggly",
        12 => "StrikeOut",
        13 => "Stamp",
        14 => "Caret",
        15 => "Ink",
        16 => "Popup",
        17 => "FileAttachment",
        PdfiumNative.FPDF_ANNOT_WIDGET => "Widget",
        _ => Inv($"subtype{subtype}"),
    };

    private static string ObjectTypeName(int type) => type switch
    {
        -1 => "unreadable",
        0 => "unknown",
        1 => "text",
        2 => "path",
        3 => "image",
        4 => "shading",
        5 => "form",
        _ => Inv($"type{type}"),
    };

    private static string Inv(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
