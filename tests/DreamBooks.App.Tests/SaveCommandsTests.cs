using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

/// <summary>Save, Save as, close and the dirty mark on the main view model (FR-80, FR-85, FR-89).</summary>
public sealed class SaveCommandsTests : IDisposable
{
    private static readonly InkPoint[] _line =
    [
        new(50, 100, 0.5f, null, null, null, 0),
        new(150, 100, 0.5f, null, null, null, 10),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-savecmd-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly FakeFileDialogService _dialogs = new();
    private readonly FakeDocumentWriter _writer;
    private readonly FakeViewerHandle _viewer;

    public SaveCommandsTests()
    {
        Directory.CreateDirectory(_dir);
        _writer = new FakeDocumentWriter(_log);
        _viewer = new FakeViewerHandle(_log);
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
    public async Task A_PDF_that_cannot_be_rewritten_is_announced_at_open_and_a_normal_one_is_not()
    {
        var normal = await OpenAsync();
        Assert.Null(normal.Notice);

        _viewer.Info = new(IsEncrypted: false, HasRestrictions: true, SignatureCount: 0);
        var restricted = await OpenAsync();
        Assert.Equal(Strings.NoticeNotSavable, restricted.Notice);

        _viewer.Info = new(IsEncrypted: true, HasRestrictions: false, SignatureCount: 0);
        var encrypted = await OpenAsync();
        Assert.Equal(Strings.NoticeNotSavable, encrypted.Notice);
    }

    [Fact]
    public async Task CanClose_without_changes_returns_true_without_asking()
    {
        var vm = new MainViewModel(_dialogs);
        Assert.True(await vm.CanCloseAsync()); // no document at all

        await OpenAsync(vm);
        Assert.True(await vm.CanCloseAsync());
        Assert.Equal(0, _dialogs.SaveChangesAsks);
        Assert.Empty(_writer.Calls);
    }

    [Fact]
    public async Task CanClose_with_changes_Save_then_true_only_if_saved()
    {
        var vm = await OpenAsync();
        Draw(vm);
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.Save;
        _writer.Failure = new IOException("disk full");

        Assert.False(await vm.CanCloseAsync()); // the save failed: stay open
        Assert.True(vm.IsDirty);
        Assert.Equal(Strings.ErrorSaveFailed, vm.ErrorMessage);

        _writer.Failure = null;
        Assert.True(await vm.CanCloseAsync());
        Assert.False(vm.IsDirty);
        Assert.Equal(2, _dialogs.SaveChangesAsks);
        Assert.Equal(2, _writer.Calls.Count);
    }

    [Fact]
    public async Task CanClose_DontSave_returns_true_and_raises_DiscardRequested()
    {
        var vm = await OpenAsync();
        Draw(vm);
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
        var discarded = 0;
        vm.DiscardRequested += (_, _) => discarded++;

        Assert.True(await vm.CanCloseAsync());

        Assert.Equal(1, discarded);
        Assert.Empty(_writer.Calls);
    }

    [Fact]
    public async Task CanClose_Cancel_returns_false()
    {
        var vm = await OpenAsync();
        Draw(vm);
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.Cancel;
        var discarded = 0;
        vm.DiscardRequested += (_, _) => discarded++;

        Assert.False(await vm.CanCloseAsync());

        Assert.Equal(0, discarded);
        Assert.True(vm.IsDirty);
        Assert.Empty(_writer.Calls);
    }

    [Fact]
    public async Task CanClose_waits_for_the_running_save_then_asks_as_usual()
    {
        var vm = await OpenAsync();
        Draw(vm);
        _writer.Gate = new TaskCompletionSource();
        var saving = vm.SaveCommand.ExecuteAsync(null);
        await _writer.Started.Task;

        var closing = vm.CanCloseAsync();
        Assert.False(closing.IsCompleted);
        _writer.Gate.SetResult();
        await saving;

        Assert.True(await closing); // saved and clean: nothing to ask
        Assert.Equal(0, _dialogs.SaveChangesAsks);
        Assert.Single(_writer.Calls);
    }

    [Fact]
    public async Task Save_buttons_are_disabled_while_saving_and_without_a_document()
    {
        var vm = new MainViewModel(_dialogs);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SaveAsCommand.CanExecute(null));

        await OpenAsync(vm);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.SaveAsCommand.CanExecute(null));

        Draw(vm);
        _writer.Gate = new TaskCompletionSource();
        var saving = vm.SaveCommand.ExecuteAsync(null);
        await _writer.Started.Task;

        Assert.True(vm.IsSaving);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SaveAsCommand.CanExecute(null));
        Assert.False(vm.OpenPdfCommand.CanExecute(null));

        _writer.Gate.SetResult();
        await saving;
        Assert.False(vm.IsSaving);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.SaveAsCommand.CanExecute(null));
        Assert.True(vm.OpenPdfCommand.CanExecute(null));
    }

    [Fact]
    public async Task Navigation_commands_are_disabled_while_saving()
    {
        var vm = await OpenAsync(new MainViewModel(_dialogs), pages: 3);
        await vm.GoToPageCommand.ExecuteAsync(2);
        Draw(vm);
        _writer.Gate = new TaskCompletionSource();
        var saving = vm.SaveCommand.ExecuteAsync(null);
        await _writer.Started.Task;

        Assert.False(vm.PreviousPageCommand.CanExecute(null));
        Assert.False(vm.NextPageCommand.CanExecute(null));
        Assert.False(vm.GoToPageCommand.CanExecute(3));
        await vm.GoToPageCommand.ExecuteAsync(3); // a direct call is ignored too
        Assert.Equal(2, vm.PageNumber);

        Draw(vm); // drawing continues during the write
        _writer.Gate.SetResult();
        await saving;

        Assert.True(vm.PreviousPageCommand.CanExecute(null));
        Assert.True(vm.NextPageCommand.CanExecute(null));
        Assert.True(vm.GoToPageCommand.CanExecute(3));
    }

    [Fact]
    public async Task WindowTitle_shows_the_dirty_mark()
    {
        var vm = new MainViewModel(_dialogs);
        Assert.Equal(Strings.AppTitle, vm.WindowTitle);

        await OpenAsync(vm);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal(string.Format(Strings.WindowTitleFormat, "a.pdf", Strings.AppTitle), vm.WindowTitle);
        Assert.False(vm.IsDirty);

        Draw(vm);
        Assert.True(vm.IsDirty);
        Assert.Equal(string.Format(Strings.WindowTitleDirtyFormat, "a.pdf", Strings.AppTitle), vm.WindowTitle);
        Assert.Contains(nameof(MainViewModel.WindowTitle), changed);
        Assert.Contains(nameof(MainViewModel.IsDirty), changed);

        _dialogs.NextSavePath = Path.Combine(_dir, "b.pdf");
        await vm.SaveAsCommand.ExecuteAsync(null);
        Assert.False(vm.IsDirty);
        Assert.Equal(string.Format(Strings.WindowTitleFormat, "b.pdf", Strings.AppTitle), vm.WindowTitle);
    }

    [Fact]
    public async Task Stroke_drawn_while_saving_stays_dirty_after_the_save()
    {
        var vm = await OpenAsync();
        Draw(vm);
        _writer.Gate = new TaskCompletionSource();
        var saving = vm.SaveCommand.ExecuteAsync(null);
        await _writer.Started.Task;

        Draw(vm);
        var late = vm.Editor!.Page.Strokes[^1];
        _writer.Gate.SetResult();
        await saving;

        Assert.True(vm.IsDirty);
        Assert.Contains(" *", vm.WindowTitle, StringComparison.Ordinal);
        Assert.Equal([late], Assert.Single(vm.Session!.CaptureSaveSnapshot().Changes).Added);
    }

    [Fact]
    public async Task Second_save_request_during_a_save_is_ignored_or_queued_never_concurrent()
    {
        var vm = await OpenAsync();
        Draw(vm);
        _writer.Gate = new TaskCompletionSource();
        var saving = vm.SaveCommand.ExecuteAsync(null);
        await _writer.Started.Task;

        _dialogs.NextSavePath = Path.Combine(_dir, "b.pdf");
        var second = vm.SaveAsCommand.ExecuteAsync(null); // bypasses CanExecute, like a stray key press
        var third = vm.SaveCommand.ExecuteAsync(null);
        Assert.Single(_writer.Calls);

        _writer.Gate.SetResult();
        await Task.WhenAll(saving, second, third);
        Assert.Single(_writer.Calls);
        Assert.Empty(_dialogs.SuggestedNames);
        Assert.Equal(Path.Combine(_dir, "a.pdf"), vm.Session!.Path);
    }

    [Fact]
    public async Task No_exception_text_ever_reaches_the_UI()
    {
        var vm = await OpenAsync();
        Draw(vm);
        const string secret = @"C:\Users\someone\very-private-folder\contract.pdf";
        _writer.Failure = new IOException("Cannot write " + secret, new UnauthorizedAccessException(secret));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(Strings.ErrorSaveFailed, vm.ErrorMessage);
        Assert.Equal([Strings.ErrorSaveFailed], _dialogs.Messages);
        Assert.DoesNotContain("private", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.IsDirty);
        Assert.False(vm.IsSaving);

        // A later successful save clears the message.
        _writer.Failure = null;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Saved_event_carries_the_snapshot_and_the_paths()
    {
        var vm = await OpenAsync();
        Draw(vm);
        var events = new List<DocumentSavedEventArgs>();
        vm.Saved += (_, e) => events.Add(e);

        await vm.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(events);
        Assert.Same(vm.Session, saved.Session);
        Assert.False(saved.IsSaveAs);
        Assert.Single(saved.Snapshot.Changes);
    }

    [Fact]
    public async Task Opening_another_document_while_dirty_asks_and_Cancel_keeps_the_current_one()
    {
        var vm = await OpenAsync();
        Draw(vm);
        var session = vm.Session;
        _dialogs.NextPath = WriteFile("b.pdf");
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.Cancel;

        await vm.OpenPdfCommand.ExecuteAsync(null);

        Assert.Equal(1, _dialogs.SaveChangesAsks);
        Assert.Same(session, vm.Session);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task Opening_another_document_while_dirty_Save_saves_first()
    {
        var vm = await OpenAsync();
        Draw(vm);
        var first = vm.Session!;
        _dialogs.NextPath = WriteFile("b.pdf");
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.Save;

        await vm.OpenPdfCommand.ExecuteAsync(null);

        Assert.Single(_writer.Calls);
        Assert.False(first.IsDirty);
        Assert.NotSame(first, vm.Session);
        Assert.Equal(_dialogs.NextPath, vm.Session!.Path);
    }

    [Fact]
    public async Task Opening_another_document_while_dirty_DontSave_discards_only_once_the_new_one_opened()
    {
        var vm = await OpenAsync();
        Draw(vm);
        var first = vm.Session;
        var discardedWhileCurrent = new List<object?>();
        vm.DiscardRequested += (_, _) => discardedWhileCurrent.Add(vm.Session);
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;

        _dialogs.NextPath = Path.Combine(_dir, "missing.pdf");
        vm.OpenDocument = (_, _) => throw new FileNotFoundException();
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.Empty(discardedWhileCurrent); // the open failed: the current document and its changes stay
        Assert.Same(first, vm.Session);

        _dialogs.NextPath = WriteFile("b.pdf");
        InstallFake(vm);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.Equal([first], discardedWhileCurrent);
        Assert.NotSame(first, vm.Session);
    }

    [Fact]
    public async Task Throwing_Saved_handler_neither_changes_the_outcome_nor_blocks_closing()
    {
        var vm = await OpenAsync();
        Draw(vm);
        var later = 0;
        vm.Saved += (_, _) => throw new IOException("journal broke");
        vm.Saved += (_, _) => later++;
        _dialogs.SaveChangesAnswer = SaveChangesAnswer.Save;

        var closing = vm.CanCloseAsync();

        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(10))); // saved, so the window may close
        Assert.Equal(1, later); // the other subscribers still hear about it
        Assert.False(vm.IsSaving);
        Assert.False(vm.IsDirty);
        Assert.True(await vm.CanCloseAsync());
    }

    [Fact]
    public async Task Throwing_ShowMessage_neither_changes_the_outcome_nor_blocks_closing()
    {
        var vm = await OpenAsync();
        Draw(vm);
        _dialogs.ShowMessageFailure = new InvalidOperationException("no window");
        _writer.Failure = new IOException("disk full");

        await vm.SaveCommand.ExecuteAsync(null); // must not throw
        Assert.False(vm.IsSaving);
        Assert.Equal(Strings.ErrorSaveFailed, vm.ErrorMessage);

        _dialogs.SaveChangesAnswer = SaveChangesAnswer.Save;
        var closing = vm.CanCloseAsync();
        Assert.False(await closing.WaitAsync(TimeSpan.FromSeconds(10))); // the save failed: stay

        _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
        Assert.True(await vm.CanCloseAsync());
        Assert.True(MainWindow.SynchronousCloseDecision(vm.CanCloseAsync()));
    }

    [Fact]
    public async Task Close_requested_while_the_outcome_popup_is_open_waits_for_it()
    {
        var vm = await OpenAsync();
        Draw(vm);
        _writer.Failure = new IOException("disk full");
        Task<bool>? closing = null;
        bool? savingDuringPopup = null;
        bool? closedDuringPopup = null;
        int? asksDuringPopup = null;
        _dialogs.WhileShowingMessage = _ =>
        {
            savingDuringPopup = vm.IsSaving;
            closing = vm.CanCloseAsync(); // e.g. the user clicks the close box behind the popup
            closedDuringPopup = closing.IsCompleted; // asserted outside: the popup call is isolated
            asksDuringPopup = _dialogs.SaveChangesAsks;
        };

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(savingDuringPopup);
        Assert.False(closedDuringPopup); // no second question stacked on the popup
        Assert.Equal(0, asksDuringPopup);
        Assert.NotNull(closing);
        Assert.False(await closing.WaitAsync(TimeSpan.FromSeconds(10))); // then asked as usual: Cancel stays
        Assert.Equal(1, _dialogs.SaveChangesAsks);
        Assert.False(vm.IsSaving);
    }

    [Fact]
    public async Task Close_requested_from_a_Saved_listener_waits_for_the_listeners()
    {
        var vm = await OpenAsync();
        Draw(vm);
        Task<bool>? closing = null;
        bool? savingInListener = null;
        var secondListenerRan = false;
        bool? closedInFirst = null;
        bool? closedInSecond = null;
        vm.Saved += (_, _) =>
        {
            savingInListener = vm.IsSaving;
            closing = vm.CanCloseAsync();
            closedInFirst = closing.IsCompleted; // listeners are isolated: assert outside
        };
        vm.Saved += (_, _) =>
        {
            closedInSecond = closing!.IsCompleted;
            secondListenerRan = true;
        };

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(savingInListener);
        Assert.True(secondListenerRan);
        Assert.False(closedInFirst); // the window cannot close before every listener heard of the save
        Assert.False(closedInSecond);
        Assert.True(await closing!.WaitAsync(TimeSpan.FromSeconds(10))); // saved and clean
        Assert.False(vm.IsSaving);
        Assert.Equal(0, _dialogs.SaveChangesAsks);
    }

    private async Task<MainViewModel> OpenAsync() => await OpenAsync(new MainViewModel(_dialogs));

    private async Task<MainViewModel> OpenAsync(MainViewModel vm, int pages = 1)
    {
        _dialogs.NextPath = WriteFile("a.pdf");
        InstallFake(vm, pages);
        vm.SaveService = new DocumentSaveService(_writer, _dialogs);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Editor);
        return vm;
    }

    private void InstallFake(MainViewModel vm, int pages = 1)
    {
        var sizes = Enumerable.Repeat((612.0, 792.0), pages).ToArray();
        vm.OpenDocument = (path, _) =>
        {
            var source = new FakePageSource(sizes);
            return Task.FromResult(new OpenedDocument(source, source, path, _viewer));
        };
    }

    private string WriteFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1, 2, 3]); // fingerprinted and copied by the fake writer, never parsed
        return path;
    }

    private static void Draw(MainViewModel vm) =>
        Assert.True(vm.Editor!.CommitStroke(_line, InkTool.Pen, Palette.Colors[0], 1));
}
