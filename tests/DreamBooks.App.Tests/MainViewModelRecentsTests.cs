using DreamBooks.App.Ink;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>Opening and closing documents keeps the recent list and the start screen in step (FR-03, FR-04).</summary>
public sealed class MainViewModelRecentsTests : IDisposable
{
    private static readonly InkPoint[] _dot =
    [
        new(100, 100, 0.5f, null, null, null, 0),
        new(120, 120, 0.5f, null, null, null, 10),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-recents-vm-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();
    private readonly ManualScheduler _scheduler = new();

    public MainViewModelRecentsTests() => Directory.CreateDirectory(_dir);

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

    private string WritePdf(string name, params (double w, double h)[] pages)
    {
        var path = Path.Combine(_dir, name);
        TestPdfFactory.Write(path, pages);
        return path;
    }

    private (MainViewModel Vm, RecentFilesService Recents) Create()
    {
        var recents = new RecentFilesService(new RecentFilesStore(Path.Combine(_dir, "state")), _ => true, _scheduler);
        recents.Load();
        return (new MainViewModel(_dialogs) { Recents = recents }, recents);
    }

    private static async Task CleanUpAsync(MainViewModel vm)
    {
        if (vm.Renderer is { } renderer)
        {
            await renderer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Opening_a_document_adds_it_to_the_recents_with_its_page_count_and_hides_the_start_screen()
    {
        var (vm, recents) = Create();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.True(vm.IsStartScreenVisible);
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792), (300, 400));
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);

            var entry = Assert.Single(recents.Entries);
            Assert.Equal(_dialogs.NextPath, entry.Path);
            Assert.Equal((0, 2), (entry.Page, entry.PageCount));
            Assert.False(vm.IsStartScreenVisible);
            Assert.Contains(nameof(MainViewModel.IsStartScreenVisible), raised);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task OpenRecentAsync_opens_the_file_and_a_failed_OpenStartupFileAsync_keeps_the_start_screen()
    {
        var (vm, recents) = Create();
        var path = WritePdf("a.pdf", (612, 792));
        try
        {
            await vm.OpenStartupFileAsync(Path.Combine(_dir, "missing.pdf"));
            Assert.True(vm.IsStartScreenVisible);
            Assert.NotNull(vm.ErrorMessage);
            Assert.Empty(recents.Entries);

            await vm.OpenRecentAsync(path);
            Assert.False(vm.IsStartScreenVisible);
            Assert.Single(recents.Entries);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Closing_a_clean_document_shows_the_start_screen_and_remembers_the_page()
    {
        var (vm, recents) = Create();
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792), (300, 400), (300, 400));
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            await vm.GoToPageCommand.ExecuteAsync(3);

            await vm.CloseDocumentCommand.ExecuteAsync(null);

            Assert.Null(vm.Session);
            Assert.True(vm.IsStartScreenVisible);
            Assert.Equal(0, vm.PageCount);
            Assert.Equal(2, recents.Entries[0].Page); // page 3, zero-based
            Assert.Equal(3, recents.Entries[0].PageCount);
            Assert.False(vm.CloseDocumentCommand.CanExecute(null));
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Closing_a_dirty_document_asks_and_Cancel_keeps_it_open_and_Dont_save_closes_it()
    {
        var (vm, _) = Create();
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            Assert.True(vm.Editor!.CommitStroke(_dot, InkTool.Pen, Palette.Colors[0], 1));

            _dialogs.SaveChangesAnswer = SaveChangesAnswer.Cancel;
            await vm.CloseDocumentCommand.ExecuteAsync(null);
            Assert.NotNull(vm.Session);
            Assert.False(vm.IsStartScreenVisible);
            Assert.Equal(1, _dialogs.SaveChangesAsks);

            _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
            await vm.CloseDocumentCommand.ExecuteAsync(null);
            Assert.Null(vm.Session);
            Assert.True(vm.IsStartScreenVisible);
            Assert.False(vm.IsDirty);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Closing_a_document_clears_its_warnings_and_old_messages_but_not_a_message_the_close_itself_made()
    {
        var (vm, _) = Create();
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            vm.Saved += (_, _) => throw new InvalidOperationException("a listener of the save fails");
            Assert.True(vm.Editor!.CommitStroke(_dot, InkTool.Pen, Palette.Colors[0], 1));
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.NotNull(vm.JournalWarning); // the listener's failure is shown
            vm.ShowError("an old error line");

            await vm.CloseDocumentCommand.ExecuteAsync(null);

            Assert.Null(vm.Session);
            Assert.Null(vm.JournalWarning); // it belonged to the document that is gone
            Assert.Null(vm.ErrorMessage);
            Assert.Null(vm.Notice);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Cancelling_the_close_keeps_the_messages_of_the_open_document()
    {
        var (vm, _) = Create();
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            Assert.True(vm.Editor!.CommitStroke(_dot, InkTool.Pen, Palette.Colors[0], 1));
            vm.ShowError("an error that stays");
            _dialogs.SaveChangesAnswer = SaveChangesAnswer.Cancel;

            await vm.CloseDocumentCommand.ExecuteAsync(null);

            Assert.NotNull(vm.Session);
            Assert.Equal("an error that stays", vm.ErrorMessage);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task A_document_chosen_while_a_save_runs_is_ignored_and_changes_nothing()
    {
        var (vm, _) = Create();
        var writer = new FakeDocumentWriter();
        vm.SaveService = new DocumentSaveService(writer, _dialogs);
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        var other = WritePdf("b.pdf", (612, 792));
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            Assert.True(vm.Editor!.CommitStroke(_dot, InkTool.Pen, Palette.Colors[0], 1));
            writer.Gate = new TaskCompletionSource();
            var saving = vm.SaveCommand.ExecuteAsync(null);
            WpfTestThread.PumpUntil(() => vm.IsSaving);

            await vm.OpenRecentAsync(other); // a drop or a menu choice during the save
            await vm.OpenStartupFileAsync(other);

            Assert.Equal(_dialogs.NextPath, vm.Session!.Path);
            Assert.Equal(0, _dialogs.SaveChangesAsks);
            writer.Gate.SetResult();
            await saving;
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Two_opens_at_once_open_one_document_and_Close_waits_for_the_open()
    {
        var (vm, recents) = Create();
        var a = WritePdf("a.pdf", (612, 792));
        var b = WritePdf("b.pdf", (612, 792));
        try
        {
            var first = vm.OpenRecentAsync(a);
            Assert.False(vm.CloseDocumentCommand.CanExecute(null));
            var second = vm.OpenRecentAsync(b); // while the first is still running
            await Task.WhenAll(first, second);

            Assert.Equal(a, vm.Session!.Path);
            Assert.Single(recents.Entries);
            Assert.True(vm.CloseDocumentCommand.CanExecute(null));

            var reopen = vm.OpenRecentAsync(a);
            Assert.False(vm.CloseDocumentCommand.CanExecute(null)); // no close in the middle of a reopen
            await reopen;
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task A_close_asked_while_an_export_runs_waits_for_it_and_no_save_starts_meanwhile()
    {
        var (vm, _) = Create();
        var export = new GatedExportService();
        vm.Exports = export;
        var writer = new FakeDocumentWriter();
        vm.SaveService = new DocumentSaveService(writer, _dialogs);
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.Save;
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            Assert.True(vm.Editor!.CommitStroke(_dot, InkTool.Pen, Palette.Colors[0], 1));
            var exporting = vm.ExportFlattenedCommand.ExecuteAsync(null);
            WpfTestThread.PumpUntil(() => vm.IsExporting);
            Assert.False(vm.CloseDocumentCommand.CanExecute(null));

            var closing = vm.CanCloseAsync(); // the window is closed during the export
            await Task.Delay(100);

            Assert.False(closing.IsCompleted);
            Assert.Empty(writer.Calls); // the save the close asks for does not overlap the export
            export.Release();
            await exporting;
            Assert.True(await closing);
            Assert.Single(writer.Calls); // now it saved
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    /// <summary>An export that waits until the test lets it go.</summary>
    private sealed class GatedExportService : IExportService
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.SetResult();

        public async Task<ExportResult> ExportFlattenedAsync(DreamBooks.Core.Documents.DocumentSession session, CancellationToken ct)
        {
            await _gate.Task;
            return new ExportResult(ExportOutcome.Done);
        }

        public async Task<ExportResult> ExportPageAsync(DreamBooks.Core.Documents.DocumentSession session, int pageIndex, CancellationToken ct)
        {
            await _gate.Task;
            return new ExportResult(ExportOutcome.Done);
        }
    }

    [Fact]
    public async Task A_closed_document_can_be_opened_again()
    {
        var (vm, recents) = Create();
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            await vm.CloseDocumentCommand.ExecuteAsync(null);
            await vm.OpenPdfCommand.ExecuteAsync(null);

            Assert.NotNull(vm.Session);
            Assert.Equal(1, vm.PageNumber);
            Assert.Single(recents.Entries);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Save_as_adds_the_new_file_to_the_recents()
    {
        var (vm, recents) = Create();
        _dialogs.NextPath = WritePdf("a.pdf", (612, 792));
        var copy = Path.Combine(_dir, "copy.pdf");
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            Assert.True(vm.Editor!.CommitStroke(_dot, InkTool.Pen, Palette.Colors[0], 1));
            _dialogs.NextSavePath = copy;

            await vm.SaveAsCommand.ExecuteAsync(null);

            Assert.Equal(copy, recents.Entries[0].Path);
            Assert.Equal(2, recents.Entries.Count);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Without_a_journal_the_restore_does_nothing_and_the_start_screen_stays()
    {
        var (vm, _) = Create();

        await vm.TryRestoreLastSessionAsync();

        Assert.True(vm.IsStartScreenVisible);
    }
}
