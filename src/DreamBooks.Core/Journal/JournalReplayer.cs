using DreamBooks.Core.Commands;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Journal;

/// <summary>
/// Re-applies a journal to a freshly opened session through the normal stroke commands on its undo history
/// (FR-84, FR-86, FR-63). Each add or erase effect becomes one ordinary command, so after a restore the undo
/// history is the flat list of those effects: undoing them all returns to the file's content. The previous
/// undo/redo stacks are not reproduced; the strokes are.
/// </summary>
/// <remarks>
/// The session must have no <see cref="JournalRecorder"/> attached; attach one afterwards. Bad data never
/// throws: entries that cannot be applied are skipped and counted. A journal without header or with an
/// unsupported format version is not replayed at all. Only pages referenced by applied entries are loaded.
/// Cancellation throws <see cref="OperationCanceledException"/> and leaves the session partly replayed:
/// the caller should discard it. Failures of the page source propagate.
/// </remarks>
public sealed class JournalReplayer
{
    public async Task<ReplayResult> ReplayAsync(DocumentSession session, JournalContents contents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(contents);
        ct.ThrowIfCancellationRequested();

        if (!contents.IsSupportedVersion)
        {
            return new ReplayResult(0, contents.Entries.Count, null);
        }

        var applied = 0;
        var skipped = 0;
        ViewEntry? lastView = null;
        foreach (var entry in contents.Entries)
        {
            ct.ThrowIfCancellationRequested();
            switch (entry)
            {
                case ViewEntry view:
                    lastView = view;
                    continue;
                case AddEntry add when IsPage(session, add.PageIndex):
                    {
                        var (ok, missed) = await ApplyAddAsync(session, add, ct);
                        Count(ok, missed);
                        break;
                    }

                case EraseEntry erase when IsPage(session, erase.PageIndex):
                    {
                        var (ok, missed) = await ApplyEraseAsync(session, erase, ct);
                        Count(ok, missed);
                        break;
                    }

                default:
                    skipped++; // out-of-range page or unknown entry type
                    break;
            }
        }

        return new ReplayResult(applied, skipped, lastView);

        void Count(bool ok, int missed)
        {
            if (ok)
            {
                applied++;
            }
            else
            {
                skipped++;
            }

            skipped += missed;
        }
    }

    private static bool IsPage(DocumentSession session, int pageIndex) => pageIndex >= 0 && pageIndex < session.PageCount;

    private static async Task<(bool Applied, int Missed)> ApplyAddAsync(DocumentSession session, AddEntry add, CancellationToken ct)
    {
        var page = await session.EnsurePageAsync(add.PageIndex, ct);
        var present = page.Strokes.Select(s => s.Id).ToHashSet();
        var strokes = add.Strokes.Where(s => present.Add(s.Id)).ToArray();
        var duplicates = add.Strokes.Count - strokes.Length;
        if (strokes.Length == 0 && add.Strokes.Count > 0)
        {
            return (false, 0); // every stroke is already on the page: skip the whole entry
        }

        session.Undo.Execute(new AddStrokesCommand(page, strokes));
        return (true, duplicates);
    }

    private static async Task<(bool Applied, int Missed)> ApplyEraseAsync(DocumentSession session, EraseEntry erase, CancellationToken ct)
    {
        var page = await session.EnsurePageAsync(erase.PageIndex, ct);
        var before = page.Strokes;
        var present = before.Select(s => s.Id).ToHashSet();
        var removed = erase.RemovedIds.ToHashSet();
        var missing = removed.Count(id => !present.Contains(id));

        var after = new List<InkStroke>(before.Count + erase.Added.Count);
        after.AddRange(before.Where(s => !removed.Contains(s.Id)));
        var kept = after.Select(s => s.Id).ToHashSet();
        var duplicates = 0;
        foreach (var added in erase.Added.OrderBy(a => a.Index))
        {
            if (!kept.Add(added.Stroke.Id))
            {
                duplicates++;
                continue;
            }

            after.Insert(Math.Min(added.Index, after.Count), added.Stroke);
        }

        if (after.Count == before.Count && after.Select(s => s.Id).SequenceEqual(before.Select(s => s.Id)))
        {
            // Nothing left to do (every removed Id was gone and nothing could be added): skip the whole entry.
            return (false, 0);
        }

        session.Undo.Execute(new EraseStrokesCommand(page, before, after));
        return (true, missing + duplicates);
    }
}
