using DreamBooks.Core.Commands;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Journal;

/// <summary>
/// Writes one journal line with the effect on strokes of every stroke command the session's undo history
/// executes, undoes or redoes, plus view lines on request (FR-84). An executed or redone add is an
/// <see cref="AddEntry"/>; an undone add is an <see cref="EraseEntry"/> of its Ids; an erase (or its undo) is the
/// <see cref="EraseEntry"/> turning the current list into the next one. Effects refer to stroke Ids only, so they
/// replay correctly even when the command they undo is older than the journal (e.g. before a save truncated it).
/// Commands of other types are not journalled.
/// </summary>
/// <remarks>
/// Runs on the command path (the UI thread) and never throws out of the undo history's event: a failing sink
/// raises <see cref="Faulted"/> once per run of consecutive failures and sets <see cref="IsFaulted"/>; later
/// commands are still attempted and the first successful append clears <see cref="IsFaulted"/>.
/// Attach it only after any replay, so the replay is not journalled again.
/// </remarks>
public sealed class JournalRecorder : IDisposable
{
    private readonly DocumentSession _session;
    private readonly IJournalSink _sink;
    private bool _disposed;

    public JournalRecorder(DocumentSession session, IJournalSink sink)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sink);
        _session = session;
        _sink = sink;
        _session.Undo.CommandApplied += OnCommandApplied;
    }

    /// <summary>Raised on the first failure after a success (or after attaching).</summary>
    public event EventHandler<JournalFaultedEventArgs>? Faulted;

    /// <summary>True while the last append attempt failed.</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>Appends a view line. Debouncing is the caller's job. Ignored after <see cref="Dispose"/>.</summary>
    public void RecordView(int pageIndex, double zoom, double scrollX, double scrollY, bool? fitWidth = null)
    {
        if (_disposed)
        {
            return;
        }

        Append(new ViewEntry(pageIndex, zoom, scrollX, scrollY, fitWidth));
    }

    /// <summary>Stops recording. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Undo.CommandApplied -= OnCommandApplied;
    }

    /// <summary>
    /// The effect of replacing <paramref name="from"/> with <paramref name="to"/>: Ids of <paramref name="from"/>
    /// missing from <paramref name="to"/>, and strokes of <paramref name="to"/> not in <paramref name="from"/>
    /// with their index in <paramref name="to"/>.
    /// </summary>
    private static EraseEntry Replacement(int pageIndex, IReadOnlyList<InkStroke> from, IReadOnlyList<InkStroke> to)
    {
        var fromIds = from.Select(s => s.Id).ToHashSet();
        var toIds = to.Select(s => s.Id).ToHashSet();
        var removed = from.Where(s => !toIds.Contains(s.Id)).Select(s => s.Id).ToArray();
        var added = to
            .Select((stroke, index) => new IndexedStroke(index, stroke))
            .Where(s => !fromIds.Contains(s.Stroke.Id))
            .ToArray();
        return new EraseEntry(pageIndex, removed, added);
    }

    private void OnCommandApplied(object? sender, CommandAppliedEventArgs e)
    {
        if (e.Command is not (AddStrokesCommand or EraseStrokesCommand))
        {
            return;
        }

        var undone = e.Action == CommandAction.Undone;
        JournalEntry entry = e.Command switch
        {
            AddStrokesCommand add when undone => new EraseEntry(add.PageIndex, add.Strokes.Select(s => s.Id).ToArray(), []),
            AddStrokesCommand add => new AddEntry(add.PageIndex, add.Strokes),
            EraseStrokesCommand erase when undone => Replacement(erase.PageIndex, erase.After, erase.Before),
            EraseStrokesCommand erase => Replacement(erase.PageIndex, erase.Before, erase.After),
            _ => throw new InvalidOperationException("Unreachable."),
        };

        Append(entry);
    }

    private void Append(JournalEntry entry)
    {
        try
        {
            _sink.Append(JournalCodec.SerializeEntry(entry));
            IsFaulted = false;
        }
        catch (Exception ex)
        {
            var first = !IsFaulted;
            IsFaulted = true;
            if (first)
            {
                try
                {
                    Faulted?.Invoke(this, new JournalFaultedEventArgs(ex));
                }
                catch (Exception)
                {
                    // A throwing handler must not break the undo history's event either.
                }
            }
        }
    }
}
