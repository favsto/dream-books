using System.Text;
using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Commands;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Journal;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>
/// The journal's lifecycle, crash recovery and the single writer through the main view model, on a real
/// <see cref="RecoveryStore"/> in a temp folder and a fake page source (FR-84, FR-86, FR-04).
/// </summary>
public sealed class RecoveryServiceTests : IDisposable
{
    private static readonly InkPoint[] _line =
    [
        new(50, 100, 0.5f, null, null, null, 0),
        new(150, 100, 0.5f, null, null, null, 10),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly FakeFileDialogService _dialogs = new();
    private readonly FakeDocumentWriter _writer = new();
    private readonly FakeViewerHandle _viewer = new([]);
    private readonly string _docPath;

    /// <summary>The strokes "in the file", per page, handed to every fresh page source.</summary>
    private readonly Dictionary<int, IReadOnlyList<InkStroke>> _fileStrokes = [];

    public RecoveryServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _root = Path.Combine(_dir, "recovery");
        _docPath = WriteFile("a.pdf");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task First_command_creates_the_journal_and_each_command_appends_a_line()
    {
        var vm = await OpenAsync(NewVm());
        Assert.False(File.Exists(JournalFile(_docPath)));

        Draw(vm);
        Assert.Equal(2, JournalLines(_docPath).Length); // header + add
        Draw(vm);
        Assert.Equal(3, JournalLines(_docPath).Length);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(4, JournalLines(_docPath).Length); // the undo's effect

        var header = JournalCodec.ParseHeader(JournalLines(_docPath)[0]);
        Assert.NotNull(header);
        Assert.Equal(Path.GetFullPath(_docPath), header.Path);
        Assert.Equal(vm.Session!.OpenFingerprint, header.Fingerprint);
    }

    [Fact]
    public async Task No_journal_exists_for_a_document_opened_and_closed_without_changes()
    {
        var vm = await OpenAsync(NewVm(), pages: 2);
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.True(await vm.CanCloseAsync());

        Assert.False(File.Exists(JournalFile(_docPath)));
        Assert.False(Directory.Exists(Path.Combine(_root, RecoveryStore.DocumentKey(_docPath)))); // the lock went too

        // Changed, saved, closed: the file holds everything, nothing is left behind either.
        var again = await OpenAsync(NewVm());
        Draw(again);
        await again.SaveCommand.ExecuteAsync(null);
        Assert.True(await again.CanCloseAsync());
        Assert.False(File.Exists(JournalFile(_docPath)));
    }

    [Fact]
    public async Task Opening_without_changes_creates_no_journal_file()
    {
        var vm = await OpenAsync(NewVm(), pages: 3);
        await vm.NextPageCommand.ExecuteAsync(null);
        await vm.NextPageCommand.ExecuteAsync(null);

        Assert.False(File.Exists(JournalFile(_docPath)));
        Assert.Empty(new RecoveryStore(_root).FindAll());
    }

    [Fact]
    public async Task View_change_alone_creates_no_journal()
    {
        var vm = await OpenAsync(NewVm(), pages: 2);
        vm.ReportViewState(1, 2.0, 10, 20);

        Assert.False(File.Exists(JournalFile(_docPath)));

        Draw(vm);
        vm.ReportViewState(0, 1.5, 0, 0);
        Assert.Equal(3, JournalLines(_docPath).Length); // dirty now: the view is journalled
    }

    [Fact]
    public async Task Successful_save_truncates_the_journal_to_the_new_header()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        Draw(vm);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirty);
        var lines = JournalLines(_docPath);
        var header = JournalCodec.ParseHeader(Assert.Single(lines));
        Assert.NotNull(header);
        Assert.Equal(vm.Session!.OpenFingerprint, header.Fingerprint); // the saved file's fingerprint
        Assert.Equal(await DocumentFingerprint.ComputeAsync(_docPath), header.Fingerprint);

        Draw(vm);
        Assert.Equal(2, JournalLines(_docPath).Length); // appends continue after the truncation
    }

    [Theory]
    [InlineData(false, "draw")]
    [InlineData(false, "erase")]
    [InlineData(false, "undo")]
    [InlineData(true, "draw")]
    [InlineData(true, "erase")]
    [InlineData(true, "undo")]
    public async Task Changes_made_during_a_save_survive_a_crash_right_after_it(bool saveAs, string edit)
    {
        var baseline = Stroke();
        _fileStrokes[0] = [baseline];
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        var savedStrokes = vm.Editor!.Page.Strokes.ToArray(); // what the save writes
        var target = saveAs ? Path.Combine(_dir, "copy.pdf") : _docPath;
        _dialogs.NextSavePath = target;
        _writer.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var saving = saveAs ? vm.SaveAsCommand.ExecuteAsync(null) : vm.SaveCommand.ExecuteAsync(null);
        await _writer.Started.Task;
        switch (edit)
        {
            case "draw":
                Draw(vm);
                break;
            case "erase":
                Erase(vm, baseline.Id);
                break;
            default:
                await vm.UndoCommand.ExecuteAsync(null); // the stroke drawn before the save
                break;
        }

        var expected = vm.Editor!.Page.Strokes.Select(s => s.Id).ToArray();
        _writer.Gate.SetResult();
        await saving;
        Assert.True(vm.IsDirty); // the edit is not in the file
        Assert.Equal(target, vm.Session!.Path);

        await vm.SimulateCrashForTestAsync();
        _fileStrokes[0] = savedStrokes; // the saved file holds the snapshot
        _writer.Gate = null;
        var next = await OpenAsync(NewVm(), path: target);

        Assert.Equal(expected, next.Editor!.Page.Strokes.Select(s => s.Id));
        Assert.True(next.IsDirty);
        Assert.Equal(Strings.NoticeRestored, next.Notice);
        Assert.Equal(0, _dialogs.MismatchAsks);
        if (saveAs)
        {
            Assert.False(File.Exists(JournalFile(_docPath)));
        }
    }

    [Fact]
    public async Task Save_as_without_changes_during_it_creates_no_journal_at_the_new_path()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        var target = Path.Combine(_dir, "copy.pdf");
        _dialogs.NextSavePath = target;

        await vm.SaveAsCommand.ExecuteAsync(null);

        Assert.False(File.Exists(JournalFile(target)));
        Assert.False(File.Exists(JournalFile(_docPath)));
    }

    [Fact]
    public async Task DontSave_deletes_the_journal()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        Assert.True(File.Exists(JournalFile(_docPath)));
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;

        Assert.True(await vm.CanCloseAsync());

        Assert.False(File.Exists(JournalFile(_docPath)));
    }

    [Fact]
    public async Task DontSave_when_opening_another_document_deletes_the_old_journal_only()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
        var other = WriteFile("b.pdf");
        _dialogs.NextPath = other;

        await vm.OpenPdfCommand.ExecuteAsync(null);

        Assert.Equal(other, vm.Session!.Path);
        Assert.False(File.Exists(JournalFile(_docPath)));
        Draw(vm);
        Assert.True(File.Exists(JournalFile(other)));

        // The old document is free again: another window may open it.
        using var oldLock = DocumentLock.TryAcquire(_root, _docPath);
        Assert.NotNull(oldLock);
        Assert.Null(DocumentLock.TryAcquire(_root, other));
    }

    [Fact]
    public async Task Abnormal_end_leaves_the_journal_and_the_next_open_restores_strokes_erasures_and_view()
    {
        var baseline = Stroke();
        _fileStrokes[0] = [baseline];
        var vm = await OpenAsync(NewVm(), pages: 2);
        Draw(vm);
        var drawn = vm.Editor!.Page.Strokes[^1].Id;
        Erase(vm, baseline.Id);
        await vm.NextPageCommand.ExecuteAsync(null);
        Draw(vm);
        var onSecond = vm.Editor!.Page.Strokes[0].Id;
        vm.ReportViewState(1, 2.0, 30, 40);

        await vm.SimulateCrashForTestAsync();
        Assert.True(File.Exists(JournalFile(_docPath)));

        var next = await OpenAsync(NewVm(), pages: 2);

        Assert.Equal(2, next.PageNumber); // back on the page last shown
        Assert.Equal([onSecond], next.Editor!.Page.Strokes.Select(s => s.Id));
        var first = await next.Session!.EnsurePageAsync(0);
        Assert.Equal([drawn], first.Strokes.Select(s => s.Id)); // the baseline stroke stays erased
        Assert.Equal(2.0, next.ZoomVm.Zoom);
        Assert.Equal(new RestoredView(2.0, 30, 40), next.ViewToRestore);
        Assert.True(next.IsDirty);
    }

    [Fact]
    public async Task Restore_marks_the_document_dirty_and_shows_the_notice()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        await vm.SimulateCrashForTestAsync();

        var next = await OpenAsync(NewVm());

        Assert.True(next.IsDirty);
        Assert.Contains("*", next.WindowTitle, StringComparison.Ordinal);
        Assert.Equal(Strings.NoticeRestored, next.Notice);
        Assert.True(next.UndoCommand.CanExecute(null)); // the restored effect is an ordinary undo step
        Assert.Equal(0, _dialogs.MismatchAsks);
    }

    [Fact]
    public async Task Restore_with_skipped_entries_reports_the_counts()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        await vm.SimulateCrashForTestAsync();
        AppendRaw(_docPath, JournalCodec.SerializeEntry(new EraseEntry(0, [Guid.NewGuid()], [])) + "\n");

        var next = await OpenAsync(NewVm());

        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.NoticeRestoredSkipped, 1, 1),
            next.Notice);
    }

    [Fact]
    public async Task Restore_does_not_create_duplicates_of_baseline_strokes()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        var drawn = vm.Editor!.Page.Strokes[0];
        await vm.SimulateCrashForTestAsync();

        // The file got the stroke (a save that crashed before truncating the journal).
        _fileStrokes[0] = [drawn];
        var next = await OpenAsync(NewVm());

        Assert.Equal([drawn.Id], next.Editor!.Page.Strokes.Select(s => s.Id));
        Assert.False(next.IsDirty);
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.NoticeRestoredSkipped, 0, 1),
            next.Notice); // nothing applied, but the user hears that the journal had something it could not apply
    }

    [Fact]
    public async Task Erase_of_a_baseline_stroke_is_restored_after_a_crash()
    {
        var keep = Stroke();
        var gone = Stroke();
        _fileStrokes[0] = [keep, gone];
        var vm = await OpenAsync(NewVm());
        Erase(vm, gone.Id);
        await vm.SimulateCrashForTestAsync();

        var next = await OpenAsync(NewVm());

        Assert.Equal([keep.Id], next.Editor!.Page.Strokes.Select(s => s.Id));
        Assert.True(next.IsDirty);

        await next.UndoCommand.ExecuteAsync(null); // undoing the restored erase brings the stroke back
        Assert.Equal([keep.Id, gone.Id], next.Editor!.Page.Strokes.Select(s => s.Id));
        Assert.False(next.IsDirty);
    }

    [Fact]
    public async Task Journal_with_a_torn_last_line_restores_everything_before_it()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        Draw(vm);
        var ids = vm.Editor!.Page.Strokes.Select(s => s.Id).ToArray();
        await vm.SimulateCrashForTestAsync();
        AppendRaw(_docPath, "{\"t\":\"add\",\"p\":0,\"s\":[{\"v\":1,\"id\""); // cut by the crash

        var next = await OpenAsync(NewVm());

        Assert.Equal(ids, next.Editor!.Page.Strokes.Select(s => s.Id));
        Assert.Equal(Strings.NoticeRestored, next.Notice);
    }

    [Fact]
    public async Task Mismatched_fingerprint_asks_and_Discard_deletes_ApplyAnyway_applies_and_Cancel_keeps()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        var id = vm.Editor!.Page.Strokes[0].Id;
        await vm.SimulateCrashForTestAsync();
        File.WriteAllBytes(_docPath, [9, 9, 9, 9]); // another program changed the file
        var journalBytes = JournalBytes(_docPath);

        // Cancel: nothing applied, the journal kept as it was.
        _dialogs.MismatchAnswer = JournalMismatchAnswer.Cancel;
        var cancelled = await OpenAsync(NewVm());
        Assert.Equal(1, _dialogs.MismatchAsks);
        Assert.Empty(cancelled.Editor!.Page.Strokes);
        Assert.False(cancelled.IsDirty);
        Assert.Equal(Strings.NoticeJournalDisabled, cancelled.Notice);
        Assert.True(await cancelled.CanCloseAsync());
        Assert.Equal(journalBytes, JournalBytes(_docPath));

        // Apply anyway: the stroke is back.
        _dialogs.MismatchAnswer = JournalMismatchAnswer.ApplyAnyway;
        var applied = await OpenAsync(NewVm());
        Assert.Equal(2, _dialogs.MismatchAsks);
        Assert.Equal([id], applied.Editor!.Page.Strokes.Select(s => s.Id));
        Assert.True(applied.IsDirty);
        await applied.SimulateCrashForTestAsync();

        // Discard: the journal is gone and the document opens as it is.
        _dialogs.MismatchAnswer = JournalMismatchAnswer.Discard;
        var discarded = await OpenAsync(NewVm());
        Assert.Equal(3, _dialogs.MismatchAsks);
        Assert.Empty(discarded.Editor!.Page.Strokes);
        Assert.False(File.Exists(JournalFile(_docPath)));
        Draw(discarded);
        var header = JournalCodec.ParseHeader(JournalLines(_docPath)[0]);
        Assert.Equal(discarded.Session!.OpenFingerprint, header!.Fingerprint); // a fresh journal for the file as it is
    }

    [Fact]
    public async Task Second_crash_after_ApplyAnyway_asks_the_mismatch_again()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        await vm.SimulateCrashForTestAsync();
        File.WriteAllBytes(_docPath, [7, 7, 7, 7, 7]);

        _dialogs.MismatchAnswer = JournalMismatchAnswer.ApplyAnyway;
        var applied = await OpenAsync(NewVm());
        Draw(applied); // appended under the old header
        var ids = applied.Editor!.Page.Strokes.Select(s => s.Id).ToArray();
        await applied.SimulateCrashForTestAsync();

        var again = await OpenAsync(NewVm());

        Assert.Equal(2, _dialogs.MismatchAsks);
        Assert.Equal(ids, again.Editor!.Page.Strokes.Select(s => s.Id));
    }

    [Fact]
    public async Task Journal_disabled_after_unreadable_or_cancelled_mismatch_does_not_touch_the_existing_journal()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        await vm.SimulateCrashForTestAsync();
        var journal = JournalFile(_docPath);
        var bytes = JournalBytes(_docPath);

        // Unreadable (held exclusively by someone else): opened without restore, journaling off.
        DreamBooks.App.ViewModels.MainViewModel unreadable;
        using (new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            unreadable = await OpenAsync(NewVm());
            Assert.Empty(unreadable.Editor!.Page.Strokes);
            Assert.Equal(Strings.ErrorJournalUnreadable, unreadable.ErrorMessage);
            Assert.Equal(Strings.NoticeJournalDisabled, unreadable.Notice);
            Draw(unreadable);
            unreadable.ReportViewState(0, 2, 1, 1);
        }

        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
        Assert.True(await unreadable.CanCloseAsync());
        Assert.Equal(bytes, JournalBytes(_docPath));

        // Cancelled mismatch: same.
        File.WriteAllBytes(_docPath, [5, 5, 5]);
        _dialogs.MismatchAnswer = JournalMismatchAnswer.Cancel;
        var cancelled = await OpenAsync(NewVm());
        Draw(cancelled);
        await cancelled.SaveCommand.ExecuteAsync(null);
        Draw(cancelled);
        Assert.True(await cancelled.CanCloseAsync());
        Assert.Equal(bytes, JournalBytes(_docPath));
    }

    [Fact]
    public async Task Missing_document_keeps_the_journal_and_offers_discard()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        await vm.SimulateCrashForTestAsync();
        File.Delete(_docPath);

        var kept = NewVm();
        await kept.TryRestoreLastSessionAsync();
        Assert.Null(kept.Session);
        Assert.Equal([Path.GetFullPath(_docPath)], _dialogs.OrphanAsks);
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.ErrorJournalFileMissing, Path.GetFullPath(_docPath)),
            kept.ErrorMessage);
        Assert.True(File.Exists(JournalFile(_docPath)));

        _dialogs.DiscardOrphan = true;
        var discarded = NewVm();
        await discarded.TryRestoreLastSessionAsync();
        Assert.Equal(2, _dialogs.OrphanAsks.Count);
        Assert.False(File.Exists(JournalFile(_docPath)));
    }

    [Fact]
    public async Task Startup_recovery_with_missing_file_offers_discard_once()
    {
        var other = WriteFile("b.pdf");
        var vmB = await OpenAsync(NewVm(), path: other);
        Draw(vmB);
        await vmB.SimulateCrashForTestAsync();
        var vmA = await OpenAsync(NewVm());
        Draw(vmA);
        await vmA.SimulateCrashForTestAsync();
        File.SetLastWriteTimeUtc(JournalFile(_docPath), DateTime.UtcNow.AddMinutes(1)); // a.pdf is the most recent
        File.Delete(_docPath);

        var vm = NewVm();
        await vm.TryRestoreLastSessionAsync();
        await vm.TryRestoreLastSessionAsync(); // only once per run

        Assert.Single(_dialogs.OrphanAsks);
        Assert.Null(vm.Session); // no loop onto b.pdf's journal
        Assert.True(File.Exists(JournalFile(other)));
    }

    [Fact]
    public async Task Startup_recovery_opens_the_most_recent_journal_whose_file_exists()
    {
        var other = WriteFile("b.pdf");
        var vmA = await OpenAsync(NewVm());
        Draw(vmA);
        await vmA.SimulateCrashForTestAsync();
        var vmB = await OpenAsync(NewVm(), path: other);
        Draw(vmB);
        var idB = vmB.Editor!.Page.Strokes[0].Id;
        await vmB.SimulateCrashForTestAsync();
        File.SetLastWriteTimeUtc(JournalFile(_docPath), DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(JournalFile(other), DateTime.UtcNow);

        var vm = NewVm();
        await vm.TryRestoreLastSessionAsync();

        Assert.Equal(Path.GetFullPath(other), Path.GetFullPath(vm.Session!.Path));
        Assert.Equal([idB], vm.Editor!.Page.Strokes.Select(s => s.Id));
        Assert.Equal(Strings.NoticeRestored, vm.Notice);
        Assert.Null(DocumentLock.TryAcquire(_root, other)); // opened through the normal path: locked
    }

    [Fact]
    public async Task Save_as_deletes_the_old_journal_and_rekeys_to_the_new_path()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        var target = Path.Combine(_dir, "copy.pdf");
        _dialogs.NextSavePath = target;

        await vm.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(target, vm.Session!.Path);
        Assert.False(File.Exists(JournalFile(_docPath)));
        Assert.False(File.Exists(JournalFile(target))); // lazily: only at the next change
        using (var old = DocumentLock.TryAcquire(_root, _docPath))
        {
            Assert.NotNull(old); // the old file is no longer held
        }

        Assert.Null(DocumentLock.TryAcquire(_root, target));

        Draw(vm);
        var header = JournalCodec.ParseHeader(JournalLines(target)[0]);
        Assert.Equal(Path.GetFullPath(target), header!.Path);
        Assert.Equal(await DocumentFingerprint.ComputeAsync(target), header.Fingerprint);
    }

    [Fact]
    public async Task Failed_save_leaves_the_journal_intact()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        var before = JournalBytes(_docPath);
        _writer.Failure = new IOException("disk full");

        await vm.SaveCommand.ExecuteAsync(null);
        _dialogs.NextSavePath = Path.Combine(_dir, "other.pdf");
        await vm.SaveAsCommand.ExecuteAsync(null);

        Assert.True(vm.IsDirty);
        Assert.Equal(before, JournalBytes(_docPath));
        Assert.False(File.Exists(JournalFile(Path.Combine(_dir, "other.pdf"))));
    }

    [Fact]
    public async Task Adopted_copy_keeps_the_journal_on_DontSave()
    {
        var vm = await OpenAsync(NewVm());
        vm.SaveService = new DocumentSaveService(_writer, _dialogs, (temp, target) =>
        {
            File.Delete(target); // the original vanished meanwhile
            throw new ReplaceFailedException(temp, target, new IOException("gone"));
        });
        Draw(vm);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.SaveService.HasAdoptedCopy(vm.Session!));
        var copyFolder = Path.GetDirectoryName(_writer.LastTempPath)!;
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;

        Assert.True(await vm.CanCloseAsync());

        Assert.True(File.Exists(JournalFile(_docPath)));
        var expected = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.NoticeCopyKept, copyFolder);
        Assert.Equal(expected, vm.Notice);
        Assert.Contains(expected, _dialogs.Messages);
    }

    [Fact]
    public async Task Opening_a_document_locked_by_another_process_is_refused_with_the_italian_message()
    {
        var other = WriteFile("b.pdf");
        var vm = await OpenAsync(NewVm(), path: other);
        using var held = DocumentLock.TryAcquire(_root, _docPath); // "another process"
        Assert.NotNull(held);
        var opens = 0;
        var open = vm.OpenDocument;
        vm.OpenDocument = (p, ct) =>
        {
            opens++;
            return open(p, ct);
        };
        _dialogs.NextPath = _docPath;

        await vm.OpenPdfCommand.ExecuteAsync(null);

        Assert.Equal(Strings.ErrorDocumentInUse, vm.ErrorMessage);
        Assert.Equal(other, vm.Session!.Path); // the previous document stays
        Assert.Equal(0, opens);
        Assert.False(File.Exists(JournalFile(_docPath)));
    }

    [Fact]
    public async Task Last_view_state_is_applied_after_restore()
    {
        var vm = await OpenAsync(NewVm(), pages: 3);
        Draw(vm);
        vm.ReportViewState(2, 1.5, 10, 20);
        vm.ReportViewState(1, 20, double.NaN, -5); // the last one wins; clamped and sanitised
        vm.ReportViewState(0, double.NaN, 1, 1); // no usable zoom: not recorded
        Assert.Null(vm.JournalWarning);
        await vm.SimulateCrashForTestAsync();

        var next = await OpenAsync(NewVm(), pages: 3);

        Assert.Equal(2, next.PageNumber);
        Assert.Equal(8, next.ZoomVm.Zoom);
        Assert.Equal(new RestoredView(8, 0, 0), next.ViewToRestore);
    }

    [Fact]
    public async Task Last_view_page_out_of_range_is_clamped()
    {
        var vm = await OpenAsync(NewVm(), pages: 3);
        Draw(vm);
        vm.ReportViewState(2, 1, 0, 0);
        await vm.SimulateCrashForTestAsync();

        var next = await OpenAsync(NewVm(), pages: 2); // the stroke is on page 1, the view on a missing page 3

        Assert.Equal(2, next.PageNumber);
    }

    [Fact]
    public async Task Saved_listener_failure_shows_a_notice()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        vm.Saved += (_, _) => throw new InvalidOperationException("listener broke");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirty);
        Assert.Equal(Strings.WarnJournalListenerFailed, vm.JournalWarning);
        Draw(vm); // a journal write that works does not hide a listener failure
        Assert.Equal(Strings.WarnJournalListenerFailed, vm.JournalWarning);
    }

    [Fact]
    public async Task DiscardRequested_listener_failure_shows_the_listener_notice()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        vm.DiscardRequested += (_, _) => throw new InvalidOperationException("listener broke");
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;

        Assert.True(await vm.CanCloseAsync());

        Assert.Equal(Strings.WarnJournalListenerFailed, vm.JournalWarning);
        Assert.False(File.Exists(JournalFile(_docPath))); // the journal is still discarded
    }

    [Fact]
    public async Task Journal_truncate_failure_after_save_shows_the_warning()
    {
        var vm = NewVm();
        var store = new RecoveryStore(_root);
        vm.Recovery!.BeginJournal = (path, fingerprint) => new FaultyJournal(store.Begin(path, fingerprint));
        await OpenAsync(vm);
        Draw(vm);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirty);
        Assert.Equal(Strings.WarnJournalFault, vm.JournalWarning);
    }

    [Fact]
    public async Task Journal_fault_warns_once_and_clears_when_writing_works_again()
    {
        var vm = NewVm();
        var store = new RecoveryStore(_root);
        var fail = true;
        vm.Recovery!.BeginJournal = (path, fingerprint) =>
            fail ? throw new IOException("recovery folder unavailable") : store.Begin(path, fingerprint);
        await OpenAsync(vm);
        var changes = 0;
        vm.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(MainViewModel.JournalWarning) ? 1 : 0;

        Draw(vm);
        Draw(vm);
        Assert.Equal(Strings.WarnJournalFault, vm.JournalWarning);
        Assert.Equal(1, changes);

        fail = false;
        Draw(vm);
        Assert.Null(vm.JournalWarning);
        Assert.True(File.Exists(JournalFile(_docPath)));

        // The strokes whose lines could not be written are in the journal too (rewritten from the session).
        var ids = vm.Editor!.Page.Strokes.Select(s => s.Id).ToArray();
        Assert.Equal(3, ids.Length);
        await vm.SimulateCrashForTestAsync();
        var next = await OpenAsync(NewVm());
        Assert.Equal(ids, next.Editor!.Page.Strokes.Select(s => s.Id));
    }

    [Fact]
    public async Task A_failed_append_mid_session_is_recovered_by_the_next_command()
    {
        var vm = NewVm();
        var store = new RecoveryStore(_root);
        var failNextAppend = false;
        vm.Recovery!.BeginJournal = (path, fingerprint) =>
            new FaultyJournal(store.Begin(path, fingerprint), () => false, deleteWorks: true, appendFails: () => failNextAppend && !(failNextAppend = false));
        await OpenAsync(vm);
        Draw(vm);
        failNextAppend = true;
        Draw(vm); // this line is lost (disk full for a moment)
        Assert.Equal(Strings.WarnJournalFault, vm.JournalWarning);

        Draw(vm);
        Assert.Null(vm.JournalWarning); // cleared only by the rewrite that put everything back

        var ids = vm.Editor!.Page.Strokes.Select(s => s.Id).ToArray();
        Assert.Equal(3, ids.Length);
        await vm.SimulateCrashForTestAsync();
        var next = await OpenAsync(NewVm());
        Assert.Equal(ids, next.Editor!.Page.Strokes.Select(s => s.Id));
        Assert.Equal(Strings.NoticeRestored, next.Notice); // nothing skipped
    }

    [Fact]
    public async Task DontSave_with_an_undeletable_journal_tells_the_user()
    {
        var vm = NewVm();
        var store = new RecoveryStore(_root);
        vm.Recovery!.BeginJournal = (path, fingerprint) => new FaultyJournal(store.Begin(path, fingerprint));
        await OpenAsync(vm);
        Draw(vm);
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;

        Assert.True(await vm.CanCloseAsync());

        Assert.Equal(Strings.ErrorJournalDeleteFailed, vm.ErrorMessage);
        Assert.Contains(Strings.ErrorJournalDeleteFailed, _dialogs.Messages);
    }

    [Fact]
    public async Task Without_a_recovery_store_nothing_is_journalled_or_locked()
    {
        var vm = new MainViewModel(_dialogs);
        InstallFake(vm);
        vm.SaveService = new DocumentSaveService(_writer, _dialogs);
        _dialogs.NextPath = _docPath;
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Draw(vm);

        Assert.False(Directory.Exists(_root));
        await vm.TryRestoreLastSessionAsync(); // no-op
        Assert.Null(vm.Recovery);
    }

    [Fact]
    public async Task Restore_does_not_journal_the_replay_again_and_a_second_crash_restores_cleanly()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        Draw(vm);
        await vm.SimulateCrashForTestAsync();
        var linesBefore = JournalLines(_docPath).Length;

        var next = await OpenAsync(NewVm());
        Assert.Equal(linesBefore, JournalLines(_docPath).Length); // the replay was not appended again

        Draw(next);
        var ids = next.Editor!.Page.Strokes.Select(s => s.Id).ToArray();
        await next.SimulateCrashForTestAsync();
        var third = await OpenAsync(NewVm());

        Assert.Equal(ids, third.Editor!.Page.Strokes.Select(s => s.Id));
        Assert.Equal(Strings.NoticeRestored, third.Notice); // no duplicates skipped
    }

    [Fact]
    public async Task Replay_failure_keeps_the_journal_and_opens_a_fresh_session_without_journaling()
    {
        var vm = await OpenAsync(NewVm(), pages: 2);
        await vm.NextPageCommand.ExecuteAsync(null);
        Draw(vm); // on page 2
        await vm.SimulateCrashForTestAsync();
        var bytes = JournalBytes(_docPath);
        _failingPages.Add(1); // page 2 can no longer be loaded

        var next = await OpenAsync(NewVm(), pages: 2);

        Assert.Equal(1, next.PageNumber);
        Assert.False(next.IsDirty);
        Assert.False(next.UndoCommand.CanExecute(null)); // a fresh session, not the partly replayed one
        Assert.Equal(Strings.ErrorJournalRestoreFailed, next.ErrorMessage);
        Assert.Equal(Strings.NoticeJournalDisabled, next.Notice);
        Draw(next);
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
        Assert.True(await next.CanCloseAsync());
        Assert.Equal(bytes, JournalBytes(_docPath));
    }

    [Fact]
    public async Task Reopening_the_open_document_with_DontSave_drops_its_journal_and_keeps_the_lock()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        var first = vm.Session;
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
        var discarded = 0;
        vm.DiscardRequested += (_, _) => discarded++;

        await vm.OpenPdfCommand.ExecuteAsync(null); // the same path again

        Assert.NotSame(first, vm.Session);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(1, discarded);
        Assert.Empty(vm.Editor!.Page.Strokes); // the dropped changes were not restored
        Assert.False(File.Exists(JournalFile(_docPath)));
        Assert.Null(DocumentLock.TryAcquire(_root, _docPath)); // still held by this window
        Draw(vm);
        Assert.Equal(2, JournalLines(_docPath).Length); // journaling goes on for the reopened document
    }

    [Fact]
    public async Task Failed_reopen_of_a_document_with_a_disabled_journal_keeps_it_disabled()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        await vm.SimulateCrashForTestAsync();
        File.WriteAllBytes(_docPath, [4, 4, 4, 4]); // the journal no longer matches
        _dialogs.MismatchAnswer = JournalMismatchAnswer.Cancel;
        var disabled = await OpenAsync(NewVm());
        var bytes = JournalBytes(_docPath);
        Draw(disabled);
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
        disabled.OpenDocument = (_, _) => throw new IOException("cannot read");

        await disabled.OpenPdfCommand.ExecuteAsync(null); // reopening the same document fails

        Assert.Equal(Strings.ErrorUnknown, disabled.ErrorMessage);
        Draw(disabled);
        disabled.ReportViewState(0, 2, 0, 0);
        Assert.Equal(bytes, JournalBytes(_docPath)); // never appended under the stale header
    }

    [Fact]
    public async Task Failed_reopen_of_a_dirty_document_journals_everything_unsaved_at_the_next_change()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
        var open = vm.OpenDocument;
        vm.OpenDocument = (_, _) => throw new IOException("cannot read");
        await vm.OpenPdfCommand.ExecuteAsync(null); // the journal was dropped, the reopen failed: still dirty
        Assert.True(vm.IsDirty);

        Draw(vm);
        var ids = vm.Editor!.Page.Strokes.Select(s => s.Id).ToArray();
        await vm.SimulateCrashForTestAsync();
        vm.OpenDocument = open;
        var next = await OpenAsync(NewVm());

        Assert.Equal(ids, next.Editor!.Page.Strokes.Select(s => s.Id)); // both strokes, not just the last one
    }

    [Fact]
    public async Task A_dirty_close_keeps_the_journal_unless_the_user_answered_DontSave()
    {
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        var bytes = JournalBytes(_docPath);

        // Cancel.
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.Cancel;
        Assert.False(await vm.CanCloseAsync());
        Assert.Equal(bytes, JournalBytes(_docPath));

        // Save, and the write fails.
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.Save;
        _writer.Failure = new IOException("disk full");
        Assert.False(await vm.CanCloseAsync());
        Assert.Equal(bytes, JournalBytes(_docPath));
        _writer.Failure = null;

        // Save, and the replacement fails with the original still there.
        vm.SaveService = new DocumentSaveService(_writer, _dialogs, (temp, target) =>
            throw new ReplaceFailedException(temp, target, new IOException("locked")));
        Assert.False(await vm.CanCloseAsync());
        Assert.Equal(bytes, JournalBytes(_docPath));
        Assert.True(vm.IsDirty);

        // Opening another document and cancelling the question keeps it too.
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.Cancel;
        _dialogs.NextPath = WriteFile("b.pdf");
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.Equal(_docPath, vm.Session!.Path);
        Assert.Equal(bytes, JournalBytes(_docPath));
    }

    [Fact]
    public async Task Save_as_onto_a_file_open_in_another_process_stops_journaling_without_touching_its_journal()
    {
        var target = WriteFile("theirs.pdf");
        using var theirs = DocumentLock.TryAcquire(_root, target); // the other process
        Assert.NotNull(theirs);
        var theirJournal = Path.Combine(_root, RecoveryStore.DocumentKey(target), "journal.jsonl");
        File.WriteAllText(theirJournal, "their journal\n");
        var vm = await OpenAsync(NewVm());
        Draw(vm);
        _dialogs.NextSavePath = target;

        await vm.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(target, vm.Session!.Path);
        Assert.Equal(Strings.NoticeJournalDisabled, vm.Notice);
        Assert.False(File.Exists(JournalFile(_docPath))); // the old file's journal is obsolete
        Draw(vm);
        Assert.Equal("their journal\n", File.ReadAllText(theirJournal));
        using var old = DocumentLock.TryAcquire(_root, _docPath);
        Assert.NotNull(old); // the old file is free
    }

    [Fact]
    public async Task Journal_whose_header_names_another_document_is_neither_applied_nor_touched()
    {
        // In a.pdf's folder, a journal naming b.pdf (a copied recovery folder, or a key collision), even with a.pdf's
        // own fingerprint, so only the path tells them apart.
        var other = Path.Combine(_dir, "b.pdf");
        var foreign = JournalFile(_docPath);
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        var header = new JournalHeader(JournalCodec.CurrentFormatVersion, other, await DocumentFingerprint.ComputeAsync(_docPath));
        File.WriteAllLines(foreign, [JournalCodec.SerializeHeader(header), JournalCodec.SerializeEntry(new AddEntry(0, [Stroke()]))]);
        var bytes = JournalBytes(_docPath);

        var vm = await OpenAsync(NewVm());

        Assert.Equal(0, _dialogs.MismatchAsks);
        Assert.Empty(vm.Editor!.Page.Strokes);
        Assert.False(vm.IsDirty);
        Assert.Equal(Strings.NoticeJournalDisabled, vm.Notice);
        Draw(vm);
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
        Assert.True(await vm.CanCloseAsync());
        Assert.Equal(bytes, JournalBytes(_docPath));
    }

    [Fact]
    public async Task Commands_after_a_failed_rewrite_still_reach_the_journal()
    {
        var vm = NewVm();
        var store = new RecoveryStore(_root);
        vm.Recovery!.BeginJournal = (path, fingerprint) => new FaultyJournal(store.Begin(path, fingerprint)); // Truncate always fails
        await OpenAsync(vm);
        Draw(vm);
        var savedStrokes = vm.Editor!.Page.Strokes.ToArray();
        _writer.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = vm.SaveCommand.ExecuteAsync(null);
        await _writer.Started.Task;
        Draw(vm); // during the save: appended to the old journal
        _writer.Gate.SetResult();
        await saving;
        Assert.Equal(Strings.WarnJournalFault, vm.JournalWarning); // the rewrite failed

        Draw(vm);
        Draw(vm); // each resync fails too: the lines must still be appended to the old content
        Assert.Equal(Strings.WarnJournalFault, vm.JournalWarning);
        var expected = vm.Editor!.Page.Strokes.Select(s => s.Id).ToArray();
        await vm.SimulateCrashForTestAsync();

        _fileStrokes[0] = savedStrokes;
        _writer.Gate = null;
        _dialogs.MismatchAnswer = JournalMismatchAnswer.ApplyAnyway; // the old header names the file before the save
        var next = await OpenAsync(NewVm());

        Assert.Equal(1, _dialogs.MismatchAsks);
        Assert.Equal(expected, next.Editor!.Page.Strokes.Select(s => s.Id));
    }

    [Fact]
    public async Task Retired_old_journal_is_deleted_once_the_new_path_journal_is_written()
    {
        var vm = NewVm();
        var store = new RecoveryStore(_root);
        var target = Path.Combine(_dir, "copy.pdf");
        var newPathFailures = 1;
        vm.Recovery!.BeginJournal = (path, fingerprint) => string.Equals(path, target, StringComparison.OrdinalIgnoreCase)
            ? new FaultyJournal(store.Begin(path, fingerprint), () => newPathFailures-- > 0, deleteWorks: true)
            : store.Begin(path, fingerprint);
        await OpenAsync(vm);
        Draw(vm);
        var savedStrokes = vm.Editor!.Page.Strokes.ToArray();
        _dialogs.NextSavePath = target;
        _writer.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = vm.SaveAsCommand.ExecuteAsync(null);
        await _writer.Started.Task;
        Draw(vm); // a catch-up exists, so the new journal is written at once, and that write fails
        _writer.Gate.SetResult();
        await saving;
        Assert.Equal(Strings.WarnJournalFault, vm.JournalWarning);
        Assert.True(File.Exists(JournalFile(_docPath))); // kept: the only journal holding the changes

        Draw(vm); // the resync at the new path works now

        Assert.Null(vm.JournalWarning);
        Assert.False(File.Exists(JournalFile(_docPath))); // never to be restored into the old file
        var ids = vm.Editor!.Page.Strokes.Select(s => s.Id).ToArray();
        await vm.SimulateCrashForTestAsync();
        _fileStrokes[0] = savedStrokes; // copy.pdf holds the snapshot; the journal holds the rest
        _writer.Gate = null;
        var next = await OpenAsync(NewVm(), path: target);
        Assert.Equal(ids, next.Editor!.Page.Strokes.Select(s => s.Id));
    }

    [Fact]
    public async Task Save_without_a_fingerprint_for_the_saved_file_leaves_the_journal_untouched()
    {
        var (session, journal) = await StandaloneJournalAsync();
        var page = await session.EnsurePageAsync(0);
        session.Undo.Execute(new AddStrokesCommand(page, [Stroke()]));
        var snapshot = session.CaptureSaveSnapshot();
        session.Undo.Execute(new AddStrokesCommand(page, [Stroke()])); // "during the save"
        var bytes = JournalBytes(_docPath);
        session.MarkSaved(snapshot);

        journal.OnSaved(new DocumentSavedEventArgs(session, snapshot, _docPath, _docPath, isSaveAs: false), savedFingerprint: null);

        Assert.Equal(bytes, JournalBytes(_docPath)); // no header with a guessed fingerprint
        Assert.True(journal.IsEnabled);
        await journal.DisposeAsync();
        session.Dispose();
    }

    private async Task<(DocumentSession Session, DocumentJournal Journal)> StandaloneJournalAsync()
    {
        var source = new FakePageSource((612, 792));
        var session = new DocumentSession(source, _docPath, await DocumentFingerprint.ComputeAsync(_docPath));
        var journal = new RecoveryService(new RecoveryStore(_root), _dialogs).Attach(session);
        return (session, journal);
    }

    [Fact]
    public async Task Save_as_without_a_fingerprint_for_the_saved_file_keeps_the_old_journal_and_stops_journaling()
    {
        var (session, journal) = await StandaloneJournalAsync();
        var page = await session.EnsurePageAsync(0);
        session.Undo.Execute(new AddStrokesCommand(page, [Stroke()]));
        var bytes = JournalBytes(_docPath);
        var target = Path.Combine(_dir, "copy.pdf");
        var snapshot = session.CaptureSaveSnapshot();
        session.MarkSaved(snapshot, target);

        journal.OnSaved(new DocumentSavedEventArgs(session, snapshot, _docPath, target, isSaveAs: true), savedFingerprint: null);

        Assert.False(journal.IsEnabled);
        Assert.Equal(bytes, JournalBytes(_docPath));
        Assert.False(File.Exists(JournalFile(target)));
        session.Undo.Execute(new AddStrokesCommand(page, [Stroke()]));
        Assert.Equal(bytes, JournalBytes(_docPath)); // no longer recording
        await journal.DisposeAsync();
        session.Dispose();
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("C:\\bad\0name.pdf")]
    public async Task Journal_with_an_unusable_header_path_is_treated_as_foreign(string headerPath)
    {
        var foreign = JournalFile(_docPath);
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        var header = new JournalHeader(JournalCodec.CurrentFormatVersion, headerPath, await DocumentFingerprint.ComputeAsync(_docPath));
        File.WriteAllLines(foreign, [JournalCodec.SerializeHeader(header), JournalCodec.SerializeEntry(new AddEntry(0, [Stroke()]))]);
        var bytes = JournalBytes(_docPath);

        var vm = await OpenAsync(NewVm());

        Assert.Empty(vm.Editor!.Page.Strokes);
        Assert.Equal(Strings.NoticeJournalDisabled, vm.Notice);
        Assert.True(await vm.CanCloseAsync());
        Assert.Equal(bytes, JournalBytes(_docPath));

        var startup = NewVm();
        await startup.TryRestoreLastSessionAsync(); // never offered or opened from an unusable path
        Assert.Empty(_dialogs.OrphanAsks);
        Assert.Null(startup.Session);
    }

    private MainViewModel NewVm()
    {
        var vm = new MainViewModel(_dialogs, new RecoveryStore(_root));
        InstallFake(vm);
        vm.SaveService = new DocumentSaveService(_writer, _dialogs);
        return vm;
    }

    private async Task<MainViewModel> OpenAsync(MainViewModel vm, int pages = 1, string? path = null)
    {
        _pages = pages;
        _dialogs.NextPath = path ?? _docPath;
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Editor);
        return vm;
    }

    private int _pages = 1;

    /// <summary>Pages whose strokes cannot be loaded, in every fresh page source.</summary>
    private readonly HashSet<int> _failingPages = [];

    private void InstallFake(MainViewModel vm) =>
        vm.OpenDocument = (path, _) =>
        {
            var source = new FakePageSource(Enumerable.Repeat((612.0, 792.0), _pages).ToArray());
            foreach (var (index, strokes) in _fileStrokes)
            {
                source.Strokes[index] = strokes;
            }

            source.FailingPages.UnionWith(_failingPages);

            return Task.FromResult(new OpenedDocument(source, source, path, _viewer));
        };

    private string WriteFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(name)); // distinct content per file
        return path;
    }

    private string JournalFile(string documentPath) =>
        Path.Combine(_root, RecoveryStore.DocumentKey(documentPath), "journal.jsonl");

    private string[] JournalLines(string documentPath)
    {
        using var stream = new FileStream(JournalFile(documentPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private byte[] JournalBytes(string documentPath)
    {
        using var stream = new FileStream(JournalFile(documentPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private void AppendRaw(string documentPath, string text) => File.AppendAllText(JournalFile(documentPath), text);

    private static InkStroke Stroke() => new(Guid.NewGuid(), InkTool.Pen, Palette.Colors[0], 1, _line);

    private static void Draw(MainViewModel vm) =>
        Assert.True(vm.Editor!.CommitStroke(_line, InkTool.Pen, Palette.Colors[0], 1));

    private static void Erase(MainViewModel vm, Guid id)
    {
        var page = vm.Editor!.Page;
        var before = page.Strokes.ToArray();
        vm.Undo.Execute(new EraseStrokesCommand(page, before, before.Where(s => s.Id != id).ToArray()));
    }

    /// <summary>
    /// A real journal whose Truncate (when <paramref name="truncateFails"/> says so; always by default) and Delete
    /// (unless <paramref name="deleteWorks"/>) fail, as when the disk or an antivirus refuses.
    /// </summary>
    private sealed class FaultyJournal(
        IJournalSession inner, Func<bool>? truncateFails = null, bool deleteWorks = false, Func<bool>? appendFails = null) : IJournalSession
    {
        public string FilePath => inner.FilePath;

        public void Append(string line)
        {
            if (appendFails?.Invoke() ?? false)
            {
                throw new IOException("disk full");
            }

            inner.Append(line);
        }

        public void Truncate(JournalHeader newHeader, IReadOnlyList<string> lines)
        {
            if (truncateFails?.Invoke() ?? true)
            {
                throw new IOException("truncate refused");
            }

            inner.Truncate(newHeader, lines);
        }

        public void Delete()
        {
            if (!deleteWorks)
            {
                throw new IOException("delete refused");
            }

            inner.Delete();
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
