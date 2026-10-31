using DreamBooks.Core.Documents;

namespace DreamBooks.Core.Journal;

/// <summary>
/// The journal entries that bring the last saved file up to the session's current strokes (FR-84). Written
/// right after a save truncates or re-keys the journal, so changes made while the save ran (which are not in
/// the file) are not lost from the journal.
/// </summary>
public static class JournalCatchUp
{
    /// <summary>
    /// One <see cref="EraseEntry"/> per changed page of <paramref name="unsaved"/> (a snapshot taken after
    /// <see cref="DocumentSession.MarkSaved"/>): the baseline Ids no longer present, and the strokes not in the
    /// baseline with their index in the page's current list. Replayed in page order onto the saved file, they give
    /// back each page's current Id sequence. Empty when nothing is unsaved.
    /// </summary>
    public static IReadOnlyList<EraseEntry> Build(SaveSnapshot unsaved)
    {
        ArgumentNullException.ThrowIfNull(unsaved);
        var entries = new List<EraseEntry>(unsaved.Changes.Count);
        foreach (var change in unsaved.Changes)
        {
            var sequence = unsaved.Sequences[change.PageIndex];
            var position = new Dictionary<Guid, int>(sequence.Count);
            for (var i = 0; i < sequence.Count; i++)
            {
                position[sequence[i]] = i;
            }

            var added = change.Added
                .Select(stroke => new IndexedStroke(position[stroke.Id], stroke))
                .OrderBy(s => s.Index)
                .ToArray();
            entries.Add(new EraseEntry(change.PageIndex, change.RemovedIds.ToArray(), added));
        }

        return entries;
    }
}
