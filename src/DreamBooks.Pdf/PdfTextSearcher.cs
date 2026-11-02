using DreamBooks.Core.Geometry;
using DreamBooks.Core.Search;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf;

/// <summary>
/// Searches the text layer of one page with PDFium's text API (FR-13). PDFium thread only.
/// </summary>
/// <remarks>
/// The page's characters are read one by one with <c>FPDFText_GetUnicode</c> (one PDFium index per character,
/// generated spaces and line breaks included; a code point outside the BMP becomes a surrogate pair whose two
/// units map to the same index; an unknown character becomes U+FFFD, which never matches), folded with
/// <see cref="TextFolding.Fold"/> and searched with an ordinal, non-overlapping substring match. A hit covers the
/// PDFium characters from the one its first folded character came from to the one its last came from, so a match
/// that starts or ends inside a ligature highlights the whole glyph. Rectangles come from <c>FPDFText_CountRects</c>
/// (PDFium skips generated and empty characters and starts a new rectangle per text object); consecutive rectangles
/// on the same user-space line are merged, then converted to displayed page points with <see cref="PageGeometry"/>,
/// the conversion the strokes use. A hit with no rectangle (only empty glyphs) is dropped: nothing could show it.
/// Hyphenated words split across lines are not joined (documented limit). The page text lives only for the call.
/// Never logs or reports document content (NFR-12).
/// </remarks>
internal static class PdfTextSearcher
{
    private const int _cancelCheckInterval = 1024;
    private const char _unknown = '�';

    private static int _openHandles;

    /// <summary>Test seam: pages and text pages this class has open right now (0 between jobs).</summary>
    internal static int OpenHandleCount => Volatile.Read(ref _openHandles);

    /// <summary>
    /// Loads the page and its text page, searches it and closes both, on every path (cancellation and exceptions
    /// included). <paramref name="afterOpen"/> is a test seam run once both are open.
    /// </summary>
    /// <exception cref="InvalidOperationException">PDFium could not load the page or its text.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled during the search.</exception>
    internal static SearchPageResult Search(
        nint document, int pageIndex, string foldedQuery, int maxHits, CancellationToken ct, Action? afterOpen = null)
    {
        ArgumentNullException.ThrowIfNull(foldedQuery);
        ArgumentOutOfRangeException.ThrowIfNegative(maxHits);
        string query = TextFolding.FoldQuery(foldedQuery);
        ct.ThrowIfCancellationRequested();

        nint page = PdfiumNative.FPDF_LoadPage(document, pageIndex);
        if (page == 0)
        {
            throw new InvalidOperationException("PDFium could not load the page.");
        }

        Interlocked.Increment(ref _openHandles);
        try
        {
            PageGeometry geometry = PageGeometry.From(page);
            nint textPage = PdfiumText.FPDFText_LoadPage(page);
            if (textPage == 0)
            {
                throw new InvalidOperationException("PDFium could not load the page text.");
            }

            Interlocked.Increment(ref _openHandles);
            try
            {
                afterOpen?.Invoke();
                return SearchText(textPage, geometry, pageIndex, query, maxHits, ct);
            }
            finally
            {
                PdfiumText.FPDFText_ClosePage(textPage);
                Interlocked.Decrement(ref _openHandles);
            }
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
            Interlocked.Decrement(ref _openHandles);
        }
    }

    private static SearchPageResult SearchText(
        nint textPage, PageGeometry geometry, int pageIndex, string query, int maxHits, CancellationToken ct)
    {
        int count = PdfiumText.FPDFText_CountChars(textPage);
        if (count <= 0)
        {
            return new SearchPageResult(false, [], false);
        }

        if (query.Length == 0)
        {
            return new SearchPageResult(true, [], false);
        }

        (string source, int[] charOf) = ReadCharacters(textPage, count, ct);
        FoldedText folded = TextFolding.Fold(source);
        var hits = new List<SearchHit>();
        bool truncated = false;
        int from = 0;
        while (from <= folded.Folded.Length - query.Length)
        {
            int at = folded.Folded.IndexOf(query, from, StringComparison.Ordinal);
            if (at < 0)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
            int first = charOf[folded.SourceIndex[at]];
            int last = charOf[folded.SourceIndex[at + query.Length - 1]];
            List<SearchRect> rects = Rectangles(textPage, geometry, first, last - first + 1);
            if (rects.Count > 0)
            {
                if (hits.Count == maxHits)
                {
                    truncated = true;
                    break;
                }

                hits.Add(new SearchHit(pageIndex, rects));
            }

            from = at + query.Length;
        }

        return new SearchPageResult(true, hits, truncated);
    }

    /// <summary>The page text as UTF-16 and, per UTF-16 unit, the PDFium index of its character.</summary>
    private static (string Text, int[] CharOf) ReadCharacters(nint textPage, int count, CancellationToken ct)
    {
        var text = new char[count * 2];
        var charOf = new int[count * 2];
        int length = 0;
        for (int i = 0; i < count; i++)
        {
            if (i % _cancelCheckInterval == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            uint unicode = PdfiumText.FPDFText_GetUnicode(textPage, i);
            if (unicode is > 0xFFFF and <= 0x10FFFF)
            {
                string pair = char.ConvertFromUtf32((int)unicode);
                text[length] = pair[0];
                charOf[length++] = i;
                text[length] = pair[1];
                charOf[length++] = i;
            }
            else
            {
                // A lone surrogate value is kept as it is (PDFium may split a pair over two indexes).
                text[length] = unicode is 0 or > 0xFFFF ? _unknown : (char)unicode;
                charOf[length++] = i;
            }
        }

        return (new string(text, 0, length), charOf);
    }

    /// <summary>The displayed rectangles of <paramref name="count"/> characters from <paramref name="start"/>.</summary>
    private static List<SearchRect> Rectangles(nint textPage, PageGeometry geometry, int start, int count)
    {
        int rectCount = PdfiumText.FPDFText_CountRects(textPage, start, count);
        var user = new List<(double Left, double Bottom, double Right, double Top)>(Math.Max(rectCount, 0));
        for (int k = 0; k < rectCount; k++)
        {
            if (PdfiumText.FPDFText_GetRect(textPage, k, out double left, out double top, out double right, out double bottom) == 0)
            {
                continue;
            }

            var rect = (Math.Min(left, right), Math.Min(bottom, top), Math.Max(left, right), Math.Max(bottom, top));
            if (user.Count > 0 && OnSameLine(user[^1], rect))
            {
                var previous = user[^1];
                user[^1] = (Math.Min(previous.Left, rect.Item1), Math.Min(previous.Bottom, rect.Item2),
                    Math.Max(previous.Right, rect.Item3), Math.Max(previous.Top, rect.Item4));
            }
            else
            {
                user.Add(rect);
            }
        }

        var displayed = new List<SearchRect>(user.Count);
        foreach ((double left, double bottom, double right, double top) in user)
        {
            PointD a = geometry.UserToModel(new PointD(left, bottom));
            PointD b = geometry.UserToModel(new PointD(right, top));
            displayed.Add(new SearchRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
        }

        return displayed;
    }

    /// <summary>
    /// True when <paramref name="next"/> continues the horizontal user-space line of <paramref name="current"/>:
    /// their vertical extents overlap by at least half the smaller height, and it starts after the current one's
    /// start and not further than two line heights past its end. Text running any other way is simply not merged.
    /// </summary>
    private static bool OnSameLine(
        (double Left, double Bottom, double Right, double Top) current,
        (double Left, double Bottom, double Right, double Top) next)
    {
        double minHeight = Math.Min(current.Top - current.Bottom, next.Top - next.Bottom);
        double maxHeight = Math.Max(current.Top - current.Bottom, next.Top - next.Bottom);
        double overlap = Math.Min(current.Top, next.Top) - Math.Max(current.Bottom, next.Bottom);
        return minHeight > 0
            && overlap >= minHeight / 2
            && next.Left >= current.Left
            && next.Left - current.Right <= 2 * maxHeight;
    }
}
