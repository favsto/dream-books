using System.Globalization;
using System.Security.Cryptography;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf;

/// <summary>
/// Saves by full rewrite to a verified temporary copy (FR-80, FR-82, NFR-10). On the PDFium thread
/// it opens a fresh PDFium instance of the source (never the viewer's, which rendering alters),
/// applies only the changed pages (erased Ids removed by payload Id; an added stroke replaces any
/// existing annotation with its Id), writes the whole file with FPDF_NO_INCREMENTAL to
/// <c>&lt;destination name&gt;.&lt;8 hex&gt;.drbk-tmp</c> next to the destination, then reopens that copy in
/// another fresh instance and checks it against what was read from the source before editing. Any
/// failure or cancellation deletes the copy; source and destination are only ever read. Never logs
/// or reports document content (NFR-12).
/// <para>
/// Then, unless disabled or the copy is above <see cref="PackingSizeLimit"/>, the verified copy is
/// re-packed by <see cref="PdfObjectStreamPacker"/> into a second temp copy with the same naming
/// pattern (NFR-20, FR-81). That packed copy is returned only if it passes the same verification plus a
/// <see cref="PdfFidelity"/> comparison with the plain copy (which is then deleted); on any other
/// outcome the packed copy is deleted and the plain verified copy is returned. If the plain copy
/// cannot be deleted, the packed copy is deleted and the plain one returned. When every delete
/// succeeds, exactly one temp file exists when the method returns and none after it throws; a
/// failed best-effort delete can leave an extra temp file behind.
/// </para>
/// </summary>
/// <remarks>
/// Writing and verifying the plain copy is ONE PDFium work item: it blocks every other PDFium
/// call, page rendering included, for its duration (seconds for a large e-book). Packing runs on
/// the thread pool; verifying the packed copy is a second PDFium work item. Cancellation is
/// honoured before it starts, between pages, around verification and inside packing, but not
/// inside <c>FPDF_SaveAsCopy</c>. The App shows a busy state while it runs and must not queue
/// renders behind it.
/// </remarks>
public sealed class PdfDocumentSaver : IDocumentWriter
{
    private const string _tempExtension = ".drbk-tmp";
    private const int _tempNameAttempts = 16;
    private const double _pageSizeTolerance = 0.01;

    /// <summary>Test seam: wraps the stream the copy is written to (e.g. to simulate a full disk).</summary>
    internal Func<Stream, Stream>? WrapTempStream { get; init; }

    /// <summary>Test seam: runs on the PDFium thread right after the copy is written, before verification.</summary>
    internal Action<string>? AfterTempWritten { get; init; }

    /// <summary>Test seam: runs on the PDFium thread after each changed page has been edited.</summary>
    internal Action<int>? AfterPageChanged { get; init; }

    /// <summary>Test seam: replaces <see cref="PdfObjectStreamPacker.Pack"/> (e.g. to make it throw).</summary>
    internal Func<string, string, CancellationToken, PackResult>? PackCopy { get; init; }

    /// <summary>Test seam: runs on the PDFium thread right after the packed copy is written, before its verification.</summary>
    internal Action<string>? AfterPackedWritten { get; init; }

    /// <summary>
    /// Test seam: replaces the deletion of a temp copy; returns true when the delete succeeded
    /// (e.g. to simulate a delete that fails, or one that succeeds while the name stays visible).
    /// </summary>
    internal Func<string, bool>? DeleteTemp { get; init; }

    /// <summary>Verified plain copies above this size are returned unpacked (the packer holds the whole file in memory).</summary>
    internal long PackingSizeLimit { get; init; } = DefaultPackingSizeLimit;

    /// <summary>Default for <see cref="PackingSizeLimit"/>: 300 MB.</summary>
    internal const long DefaultPackingSizeLimit = 300L * 1024 * 1024;

    private readonly bool _packObjects;

    /// <summary>Creates a saver.</summary>
    /// <param name="packObjects">
    /// When true (the default), the verified copy is re-packed into object streams and the packed
    /// copy is returned if it passes the same verification; on any problem the plain copy is returned.
    /// </param>
    public PdfDocumentSaver(bool packObjects = true)
    {
        _packObjects = packObjects;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Callers treat ANY exception other than cancellation as "save failed" (the original is
    /// untouched and no copy is left). Only <see cref="SaveVerificationException"/>,
    /// <see cref="PdfSaveRefusedException"/> and <see cref="PdfOpenException"/> have path-free
    /// messages; system exceptions (and every <see cref="Exception.InnerException"/>) may contain
    /// file paths, so the App must never display or log their messages.
    /// </remarks>
    /// <exception cref="PdfSaveRefusedException">The source is encrypted or restricted (no file is created).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A change names a page the source does not have (no file is created).</exception>
    /// <exception cref="ArgumentException">A page appears in two changes, or a change adds one Id twice.</exception>
    /// <exception cref="SaveVerificationException">The written copy did not match (it has been deleted).</exception>
    /// <exception cref="PdfOpenException">The source cannot be opened.</exception>
    /// <exception cref="IOException">
    /// Writing the copy failed, including <see cref="DirectoryNotFoundException"/>,
    /// <see cref="PathTooLongException"/> and a full disk (nothing is left behind).
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The destination folder cannot be written.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left behind.</exception>
    public Task<string> WriteVerifiedCopyAsync(
        string sourcePath, string destinationPath, IReadOnlyList<PageChange> changes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentNullException.ThrowIfNull(changes);

        string fullDestination = Path.GetFullPath(destinationPath);
        string fileName = Path.GetFileName(fullDestination);
        string? folder = Path.GetDirectoryName(fullDestination);
        if (fileName.Length == 0 || folder is null)
        {
            throw new ArgumentException("The destination must be a file path.", nameof(destinationPath));
        }

        var seen = new HashSet<int>();
        foreach (PageChange? change in changes)
        {
            ArgumentNullException.ThrowIfNull(change, nameof(changes));
            ArgumentNullException.ThrowIfNull(change.RemovedIds, nameof(changes));
            ArgumentNullException.ThrowIfNull(change.Added, nameof(changes));
            ArgumentOutOfRangeException.ThrowIfNegative(change.PageIndex, nameof(changes));
            if (!seen.Add(change.PageIndex))
            {
                throw new ArgumentException("Each page may appear in only one change.", nameof(changes));
            }

            if (change.Added.Select(s => s.Id).Distinct().Count() != change.Added.Count)
            {
                throw new ArgumentException("A change may add each stroke Id only once.", nameof(changes));
            }
        }

        PageChange[] snapshot = [.. changes];
        return WriteAndPackAsync(sourcePath, folder, fileName, snapshot, ct);
    }

    private async Task<string> WriteAndPackAsync(
        string sourcePath, string folder, string fileName, PageChange[] changes, CancellationToken ct)
    {
        (string plain, SourceFacts facts) = await PdfiumThread.RunAsync(
            () => Save(sourcePath, folder, fileName, changes, ct), ct).ConfigureAwait(false);
        return _packObjects ? await PackOrKeepAsync(plain, facts, folder, fileName, ct).ConfigureAwait(false) : plain;
    }

    /// <summary>
    /// The optional compaction step (NFR-20, FR-81). <paramref name="plain"/> is already verified. Packs it
    /// into a second temp copy off the PDFium thread, then verifies that copy afresh on the PDFium
    /// thread with the same checks plus <see cref="PdfFidelity.Compare"/> against the plain copy.
    /// Returns the packed copy (the plain one deleted) only if all of that succeeds; on ANY other
    /// outcome deletes the packed copy and returns the plain one unchanged. Only cancellation of
    /// <paramref name="ct"/> propagates, after deleting both copies.
    /// </summary>
    private async Task<string> PackOrKeepAsync(
        string plain, SourceFacts facts, string folder, string fileName, CancellationToken ct)
    {
        string? packed = null;
        try
        {
            if (new FileInfo(plain).Length > PackingSizeLimit)
            {
                return plain;
            }

            string target = NewTempPath(folder, fileName);
            packed = target;
            Func<string, string, CancellationToken, PackResult> pack = PackCopy ?? PdfObjectStreamPacker.Pack;
            await Task.Run(() => pack(plain, target, ct), ct).ConfigureAwait(false);

            await PdfiumThread.RunAsync(
                () =>
                {
                    AfterPackedWritten?.Invoke(target);
                    ct.ThrowIfCancellationRequested();
                    Verify(target, facts, ct);
                    ct.ThrowIfCancellationRequested();
                    if (!PdfFidelity.Compare(plain, target).IsFaithful)
                    {
                        throw new SaveVerificationException("the packed copy differs");
                    }
                },
                ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (!TryDelete(plain))
            {
                // Never leave two copies behind: the plain copy could not be deleted, so keep it
                // and drop the packed one. The decision rests on the delete result, not on
                // File.Exists: with legacy delete semantics (FAT/exFAT/SMB) a delete-pending file
                // still "exists" for a while although it is about to vanish.
                TryDelete(target);
                return plain;
            }

            return target;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DeleteBoth(plain, packed);
            throw;
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
            {
                // Whatever failed, the caller cancelled: only cancellation propagates.
                DeleteBoth(plain, packed);
                throw new OperationCanceledException("The save was cancelled.", ex, ct);
            }

            // Packing is an optimisation: no problem in it (packer refusal, a bug, I/O, a
            // verification mismatch, an OperationCanceledException not caused by ct) may fail a
            // save whose plain copy is verified.
            if (packed is not null)
            {
                TryDelete(packed);
            }

            return plain;
        }
    }

    private void DeleteBoth(string plain, string? packed)
    {
        if (packed is not null)
        {
            TryDelete(packed);
        }

        TryDelete(plain);
    }

    private (string Path, SourceFacts Facts) Save(
        string sourcePath, string folder, string fileName, PageChange[] changes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? temp = null;
        try
        {
            SourceFacts facts;
            using (PdfiumDocument source = PdfiumDocument.Open(sourcePath))
            {
                // Refusals come before any file exists.
                PdfDocumentInfo info = PdfDocumentInfo.Read(source.Handle);
                if (!info.CanBeRewritten)
                {
                    throw new PdfSaveRefusedException(info);
                }

                foreach (PageChange change in changes)
                {
                    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(change.PageIndex, source.PageCount, nameof(changes));
                }

                facts = ReadDocumentFacts(source);
                var expectations = new List<PageExpectation>(changes.Length);
                foreach (PageChange change in changes)
                {
                    expectations.Add(ApplyChange(source, change));
                    AfterPageChanged?.Invoke(change.PageIndex);
                    ct.ThrowIfCancellationRequested();
                }

                facts = facts with { Pages = expectations };
                string path = NewTempPath(folder, fileName);
                source.SaveFullRewrite(path, WrapTempStream); // deletes its own partial file on failure
                temp = path;
            }

            AfterTempWritten?.Invoke(temp);
            ct.ThrowIfCancellationRequested();
            Verify(temp, facts, ct);
            ct.ThrowIfCancellationRequested();
            return (temp, facts);
        }
        catch
        {
            if (temp is not null)
            {
                TryDelete(temp);
            }

            throw;
        }
    }

    /// <summary>Page sizes of every page and the text of the first and last page, read before editing.</summary>
    private static SourceFacts ReadDocumentFacts(PdfiumDocument document)
    {
        var sizes = new PdfiumNative.FS_SIZEF[document.PageCount];
        for (int i = 0; i < sizes.Length; i++)
        {
            if (PdfiumNative.FPDF_GetPageSizeByIndexF(document.Handle, i, out sizes[i]) == 0)
            {
                throw new InvalidOperationException("PDFium could not read a page size.");
            }
        }

        string firstText = sizes.Length > 0 ? ReadText(document, 0) : string.Empty;
        string lastText = sizes.Length > 1 ? ReadText(document, sizes.Length - 1) : firstText;
        return new SourceFacts(sizes, firstText, lastText, []);
    }

    /// <summary>
    /// Reads what the page holds, then edits it. The expectation is derived from the source page as
    /// read here and from the requested change, never from the edited page.
    /// </summary>
    private static PageExpectation ApplyChange(PdfiumDocument source, PageChange change)
    {
        nint page = source.LoadPage(change.PageIndex);
        try
        {
            var geometry = PageGeometry.From(page);
            IReadOnlyList<(Guid Id, string Payload)> payloads =
                StrokeAnnotations.ReadAppPayloads(page, out int unreadable, out int foreign);

            // An added stroke replaces every existing copy with its Id (ruling 14): its Id is
            // removed together with the erased ones, in a single pass.
            var addedIds = new HashSet<Guid>(change.Added.Select(s => s.Id));
            var toRemove = new HashSet<Guid>(change.RemovedIds);
            toRemove.UnionWith(addedIds);

            string[] added = [.. change.Added.Select(StrokeJson.Serialize)];
            List<string> expected = [.. payloads.Where(p => !toRemove.Contains(p.Id)).Select(p => p.Payload), .. added];
            expected.Sort(StringComparer.Ordinal);
            var gone = new HashSet<Guid>(change.RemovedIds.Where(id => !addedIds.Contains(id)));

            StrokeAnnotations.RemoveByIds(page, toRemove); // Ids absent from the page are ignored
            foreach (InkStroke stroke in change.Added)
            {
                StrokeAnnotations.Write(page, geometry, stroke);
            }

            return new PageExpectation(change.PageIndex, expected, added, addedIds, gone, unreadable, foreign);
        }
        finally
        {
            source.ClosePage(page);
        }
    }

    private static void Verify(string tempPath, SourceFacts facts, CancellationToken ct)
    {
        try
        {
            using PdfiumDocument copy = PdfiumDocument.Open(tempPath);
            if (copy.PageCount != facts.PageSizes.Length)
            {
                throw new SaveVerificationException("page count");
            }

            for (int i = 0; i < facts.PageSizes.Length; i++)
            {
                if (PdfiumNative.FPDF_GetPageSizeByIndexF(copy.Handle, i, out PdfiumNative.FS_SIZEF size) == 0
                    || Math.Abs(size.Width - facts.PageSizes[i].Width) > _pageSizeTolerance
                    || Math.Abs(size.Height - facts.PageSizes[i].Height) > _pageSizeTolerance)
                {
                    throw new SaveVerificationException(Inv($"size of page {i + 1}"));
                }
            }

            int pageCount = facts.PageSizes.Length;
            if (pageCount > 0)
            {
                if (!string.Equals(ReadText(copy, 0), facts.FirstPageText, StringComparison.Ordinal))
                {
                    throw new SaveVerificationException("text of the first page");
                }

                if (pageCount > 1
                    && !string.Equals(ReadText(copy, pageCount - 1), facts.LastPageText, StringComparison.Ordinal))
                {
                    throw new SaveVerificationException("text of the last page");
                }
            }

            foreach (PageExpectation expectation in facts.Pages)
            {
                ct.ThrowIfCancellationRequested();
                VerifyPage(copy, expectation);
            }
        }
        catch (Exception ex) when (ex is not (SaveVerificationException or OperationCanceledException or OutOfMemoryException))
        {
            throw new SaveVerificationException("the copy could not be read back", ex);
        }
    }

    private static void VerifyPage(PdfiumDocument copy, PageExpectation expected)
    {
        int label = expected.PageIndex + 1;
        nint page = copy.LoadPage(expected.PageIndex);
        try
        {
            // Read afresh from the copy, never from what was written.
            IReadOnlyList<(Guid Id, string Payload)> payloads =
                StrokeAnnotations.ReadAppPayloads(page, out int unreadable, out int foreign);

            if (foreign != expected.ForeignCount)
            {
                throw new SaveVerificationException(Inv($"other annotations on page {label}"));
            }

            if (unreadable != expected.UnreadableCount)
            {
                throw new SaveVerificationException(Inv($"unreadable strokes on page {label}"));
            }

            if (payloads.Any(p => expected.GoneIds.Contains(p.Id)))
            {
                throw new SaveVerificationException(Inv($"erased stroke still on page {label}"));
            }

            var idCounts = new Dictionary<Guid, int>();
            foreach ((Guid id, _) in payloads)
            {
                idCounts[id] = idCounts.GetValueOrDefault(id) + 1;
            }

            if (expected.AddedIds.Any(id => idCounts.GetValueOrDefault(id) != 1))
            {
                throw new SaveVerificationException(Inv($"duplicated stroke on page {label}"));
            }

            List<string> actual = [.. payloads.Select(p => p.Payload)];
            actual.Sort(StringComparer.Ordinal);
            if (!actual.SequenceEqual(expected.Payloads, StringComparer.Ordinal))
            {
                throw new SaveVerificationException(Inv($"strokes on page {label}"));
            }

            // Implied by the multiset check above, kept explicit: every new stroke is there verbatim.
            var present = new HashSet<string>(actual, StringComparer.Ordinal);
            if (!expected.AddedPayloads.All(present.Contains))
            {
                throw new SaveVerificationException(Inv($"new strokes on page {label}"));
            }
        }
        finally
        {
            copy.ClosePage(page);
        }
    }

    private static string ReadText(PdfiumDocument document, int pageIndex)
    {
        nint page = document.LoadPage(pageIndex);
        try
        {
            return PdfFidelity.ReadPageText(page);
        }
        finally
        {
            document.ClosePage(page);
        }
    }

    private static string NewTempPath(string folder, string fileName)
    {
        for (int i = 0; i < _tempNameAttempts; i++)
        {
            string path = Path.Combine(
                folder, $"{fileName}.{RandomNumberGenerator.GetHexString(8, lowercase: true)}{_tempExtension}");
            if (!File.Exists(path))
            {
                return path; // created with FileMode.CreateNew, so a race still cannot overwrite a file
            }
        }

        throw new IOException("No free name for the temporary copy.");
    }

    /// <summary>Best-effort delete of a temp copy; true only if the delete did not fail.</summary>
    private bool TryDelete(string path) => DeleteTemp is null ? DeleteFile(path) : DeleteTemp(path);

    /// <summary>Deletes <paramref name="path"/>; true only if <see cref="File.Delete"/> did not throw.</summary>
    internal static bool DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Inv(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the source held before editing: every page size, first/last page text, changed pages.</summary>
    private sealed record SourceFacts(
        PdfiumNative.FS_SIZEF[] PageSizes,
        string FirstPageText,
        string LastPageText,
        IReadOnlyList<PageExpectation> Pages);

    /// <summary>
    /// What a changed page must hold in the copy: the sorted multiset of app payload strings (source
    /// payloads minus erased and re-added Ids, plus the new strokes' <see cref="StrokeJson.Serialize"/>),
    /// the new payloads and Ids (each present exactly once), the erased Ids that must be gone, and
    /// the unchanged unreadable and foreign counts.
    /// </summary>
    private sealed record PageExpectation(
        int PageIndex,
        IReadOnlyList<string> Payloads,
        IReadOnlyList<string> AddedPayloads,
        IReadOnlySet<Guid> AddedIds,
        IReadOnlySet<Guid> GoneIds,
        int UnreadableCount,
        int ForeignCount);
}
