using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Commands;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// Crash recovery end to end: the real PDFium renderer, the real recovery store and a generated PDF
/// (FR-84, FR-86, FR-04).
/// </summary>
public sealed class RecoveryRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-recovery-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();

    public RecoveryRoundTripTests() => Directory.CreateDirectory(_dir);

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
    public async Task Strokes_and_erasures_survive_a_crash_and_undo_walks_back_to_the_file()
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfFactory.WriteWithText(path, "Hello Dream Books", (612, 792), (300, 400));
        var root = Path.Combine(_dir, "recovery");
        InkPoint[] first = [new(50, 100, 0.4f, null, null, null, 0), new(250, 140, 0.8f, null, null, null, 30)];
        InkPoint[] second = [new(60, 300, 0.5f, null, null, null, 0), new(200, 320, 0.6f, null, null, null, 20)];

        // Before the crash: two strokes on page 1, the first one erased, one stroke on page 2.
        _dialogs.NextPath = path;
        var before = new MainViewModel(_dialogs, new RecoveryStore(root));
        Guid[] page1;
        Guid page2;
        try
        {
            await before.OpenPdfCommand.ExecuteAsync(null);
            Assert.True(before.Editor!.CommitStroke(first, InkTool.Pen, Palette.Colors[0], 1));
            Assert.True(before.Editor.CommitStroke(second, InkTool.Highlighter, Palette.Colors[1], 2));
            var page = before.Editor.Page;
            var all = page.Strokes.ToArray();
            before.Undo.Execute(new EraseStrokesCommand(page, all, all.Skip(1).ToArray()));
            page1 = page.Strokes.Select(s => s.Id).ToArray();
            await before.NextPageCommand.ExecuteAsync(null);
            Assert.True(before.Editor!.CommitStroke(first, InkTool.Pen, Palette.Colors[2], 1));
            page2 = before.Editor.Page.Strokes[0].Id;
            before.ReportViewState(1, 1.5, 12, 34);

            // A second process sees the journal while the first still writes it (append stream shares reads).
            var seen = Assert.Single(new RecoveryStore(root).FindAll());
            Assert.Equal(Path.GetFullPath(path), seen.Contents.Header!.Path);
        }
        finally
        {
            await before.SimulateCrashForTestAsync(); // the OS releases the journal and the lock
            if (before.Renderer is { } renderer)
            {
                await renderer.DisposeAsync();
            }
        }

        // After the restart.
        var after = new MainViewModel(_dialogs, new RecoveryStore(root));
        try
        {
            await after.TryRestoreLastSessionAsync();

            Assert.NotNull(after.Session);
            Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(after.Session.Path));
            Assert.True(after.IsDirty);
            Assert.Equal(Strings.NoticeRestored, after.Notice);
            Assert.Equal(2, after.PageNumber);
            Assert.Equal(1.5, after.ZoomVm.Zoom);
            Assert.Equal(new RestoredView(1.5, 12, 34), after.ViewToRestore);
            Assert.Equal([page2], after.Editor!.Page.Strokes.Select(s => s.Id));
            var restored1 = await after.Session.EnsurePageAsync(0);
            Assert.Equal(page1, restored1.Strokes.Select(s => s.Id));
            Assert.Equal(InkTool.Highlighter, restored1.Strokes[0].Tool);
            Assert.Equal(second.Length, restored1.Strokes[0].Points.Count);

            // Each restored effect is an undo step: page 2's stroke, then the erase, then the two strokes.
            await after.UndoCommand.ExecuteAsync(null);
            Assert.Empty(after.Editor!.Page.Strokes);
            await after.UndoCommand.ExecuteAsync(null);
            Assert.Equal(1, after.PageNumber); // undo navigates to the page it changes
            Assert.Equal(2, after.Editor!.Page.Strokes.Count); // the erased stroke is back
            await after.UndoCommand.ExecuteAsync(null);
            await after.UndoCommand.ExecuteAsync(null);
            Assert.Empty(after.Editor!.Page.Strokes);
            Assert.False(after.IsDirty);

            // Nothing unsaved any more: closing removes the journal.
            _dialogs.SaveChangesAnswer = SaveChangesAnswer.DontSave;
            Assert.True(await after.CanCloseAsync());
            Assert.Empty(new RecoveryStore(root).FindAll());
        }
        finally
        {
            if (after.Renderer is { } renderer)
            {
                await renderer.DisposeAsync();
            }
        }
    }
}
