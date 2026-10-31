using System.Text;
using DreamBooks.App.Ink;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// The "file changed by another program" question before Save (FR-80) on real files: a generated PDF opened
/// through the view model with the real PDFium renderer and the real saver; only the dialogs are fakes.
/// </summary>
public sealed class FileChangedPromptTests : IDisposable
{
    private const string _text = "Hello Dream Books";

    private static readonly InkPoint[] _points =
        [new(50, 100, 0.4f, null, null, null, 0), new(250, 140, 0.8f, null, null, null, 30)];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-filechanged-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly FakeFileDialogService _dialogs = new();

    public FileChangedPromptTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "doc.pdf");
        _dialogs.NextPath = _path;
        _dialogs.FileChangedAnswer = FileChangedAnswer.Cancel;
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

    public enum ExternalChange
    {
        /// <summary>Bytes appended (an incremental update by another PDF tool): the length changes.</summary>
        Append,

        /// <summary>The whole file rewritten with different content of the same length.</summary>
        RewriteSameLength,

        /// <summary>One byte in the middle of a file over 128 KB, same length; the last-write time moves on.</summary>
        MiddleByteNewWriteTime,
    }

    [Theory]
    [InlineData(ExternalChange.Append)]
    [InlineData(ExternalChange.RewriteSameLength)]
    [InlineData(ExternalChange.MiddleByteNewWriteTime)]
    public async Task Save_asks_when_the_file_was_changed_by_another_program_after_it_was_opened(ExternalChange change)
    {
        WritePdf(large: change == ExternalChange.MiddleByteNewWriteTime);
        var vm = new MainViewModel(_dialogs);
        try
        {
            await OpenAsync(vm);
            Assert.True(vm.Editor!.CommitStroke(_points, InkTool.Pen, Palette.Colors[0], 1));

            var changed = ChangeExternally(change);

            await vm.SaveCommand.ExecuteAsync(null);

            AssertAskedAndUntouched(vm, changed);
        }
        finally
        {
            await DisposeRendererAsync(vm);
        }
    }

    [Fact]
    public async Task Save_asks_even_when_the_document_has_no_unsaved_changes()
    {
        WritePdf(large: false);
        var vm = new MainViewModel(_dialogs);
        try
        {
            await OpenAsync(vm);
            Assert.False(vm.IsDirty);
            Assert.True(vm.SaveCommand.CanExecute(null)); // Save is enabled on a clean document too

            var changed = ChangeExternally(ExternalChange.Append);

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(1, _dialogs.FileChangedAsks);
            Assert.Equal(changed, await File.ReadAllBytesAsync(_path));
            Assert.Null(vm.ErrorMessage);
        }
        finally
        {
            await DisposeRendererAsync(vm);
        }
    }

    [Fact]
    public async Task Save_asks_after_a_restore_from_the_journal_if_the_file_changed_since()
    {
        WritePdf(large: false);
        var root = Path.Combine(_dir, "recovery");

        // Before the crash: one unsaved stroke, journalled.
        var before = new MainViewModel(_dialogs, new RecoveryStore(root));
        try
        {
            await OpenAsync(before);
            Assert.True(before.Editor!.CommitStroke(_points, InkTool.Pen, Palette.Colors[0], 1));
        }
        finally
        {
            await before.SimulateCrashForTestAsync();
            await DisposeRendererAsync(before);
        }

        // After the restart: the stroke is restored (the file did not change, so no mismatch question), then
        // another program changes the file and the user saves.
        var after = new MainViewModel(_dialogs, new RecoveryStore(root));
        try
        {
            await after.TryRestoreLastSessionAsync();
            Assert.NotNull(after.Session);
            Assert.True(after.IsDirty);
            Assert.Equal(0, _dialogs.MismatchAsks);

            var changed = ChangeExternally(ExternalChange.Append);

            await after.SaveCommand.ExecuteAsync(null);

            AssertAskedAndUntouched(after, changed);
        }
        finally
        {
            await DisposeRendererAsync(after);
        }
    }

    [Fact]
    public async Task Save_does_not_ask_when_nothing_changed()
    {
        WritePdf(large: true);
        var vm = new MainViewModel(_dialogs);
        try
        {
            await OpenAsync(vm);
            Assert.True(vm.Editor!.CommitStroke(_points, InkTool.Pen, Palette.Colors[0], 1));

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(0, _dialogs.FileChangedAsks);
            Assert.Null(vm.ErrorMessage);
            Assert.False(vm.IsDirty);

            // And again after that save: the saved file is the new baseline.
            Assert.True(vm.Editor!.CommitStroke(_points, InkTool.Pen, Palette.Colors[1], 1));
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(0, _dialogs.FileChangedAsks);
            Assert.False(vm.IsDirty);
        }
        finally
        {
            await DisposeRendererAsync(vm);
        }
    }

    [Fact]
    public async Task A_change_that_keeps_length_and_last_write_time_but_alters_the_middle_is_a_known_limit()
    {
        WritePdf(large: true);
        var vm = new MainViewModel(_dialogs);
        try
        {
            await OpenAsync(vm);
            Assert.True(vm.Editor!.CommitStroke(_points, InkTool.Pen, Palette.Colors[0], 1));

            // A tool that edits one byte in the middle and puts the last-write time back. Before the middle sample
            // this went unseen (the fingerprint hashed only the edges); now it is seen when the byte falls in one of
            // the sampled blocks, as here. A byte between two sampled blocks remains the documented limit.
            var lastWrite = File.GetLastWriteTimeUtc(_path);
            var changed = ChangeMiddleByte();
            File.SetLastWriteTimeUtc(_path, lastWrite);

            await vm.SaveCommand.ExecuteAsync(null);

            AssertAskedAndUntouched(vm, changed);
        }
        finally
        {
            await DisposeRendererAsync(vm);
        }
    }

    private void WritePdf(bool large)
    {
        // About 300 bytes a page: 800 pages make a file well over 128 KB, so the fingerprint hashes only its edges.
        var pages = Enumerable.Repeat((612.0, 792.0), large ? 800 : 2).ToArray();
        TestPdfFactory.WriteWithText(_path, _text, pages);
        if (large)
        {
            Assert.True(new FileInfo(_path).Length > 3 * 64 * 1024);
        }
    }

    private async Task OpenAsync(MainViewModel vm)
    {
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Session);
        Assert.NotNull(vm.Renderer); // the real PDFium viewer holds the file open
        Assert.Null(vm.ErrorMessage);
    }

    /// <summary>Changes the open file in place, as another program would, and returns its new bytes.</summary>
    private byte[] ChangeExternally(ExternalChange change)
    {
        switch (change)
        {
            case ExternalChange.Append:
                using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.Write("\n% appended by another program\n"u8);
                }

                break;
            case ExternalChange.RewriteSameLength:
                var bytes = File.ReadAllBytes(_path);
                var at = IndexOf(bytes, _text, bytes.Length / 2);
                bytes[at] = (byte)'J'; // "Hello" -> "Jello": still a valid PDF
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.Write(bytes);
                }

                break;
            case ExternalChange.MiddleByteNewWriteTime:
                var before = File.GetLastWriteTimeUtc(_path);
                ChangeMiddleByte();
                File.SetLastWriteTimeUtc(_path, before.AddSeconds(2)); // what a normal write does
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }

        return File.ReadAllBytes(_path);
    }

    /// <summary>
    /// Changes one letter of the first page text after the 64 KB head, keeping the length: outside the hashed edges,
    /// inside the first sampled block of the middle (a byte between two sampled blocks is still not seen).
    /// </summary>
    private byte[] ChangeMiddleByte()
    {
        var length = new FileInfo(_path).Length;
        var bytes = File.ReadAllBytes(_path);
        var at = IndexOf(bytes, _text, 64 * 1024);
        Assert.InRange(at, 64 * 1024, (68 * 1024) - 1); // the first sampled 4 KB block of the middle
        Assert.True(at < length - (64 * 1024));
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Seek(at, SeekOrigin.Begin);
            stream.WriteByte((byte)'J');
        }

        Assert.Equal(length, new FileInfo(_path).Length);
        return File.ReadAllBytes(_path);
    }

    private static int IndexOf(byte[] bytes, string text, int from)
    {
        var at = bytes.AsSpan(from).IndexOf(Encoding.ASCII.GetBytes(text));
        Assert.True(at >= 0, "The page text was not found.");
        return from + at;
    }

    private void AssertAskedAndUntouched(MainViewModel vm, byte[] changed)
    {
        Assert.Equal(1, _dialogs.FileChangedAsks);
        Assert.True(vm.IsDirty); // Cancel: nothing saved
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(changed, File.ReadAllBytes(_path)); // the other program's version is untouched
        Assert.Empty(Directory.GetFiles(_dir, "*.drbk-tmp"));
    }

    private static async Task DisposeRendererAsync(MainViewModel vm)
    {
        if (vm.Renderer is { } renderer)
        {
            await renderer.DisposeAsync();
        }
    }
}
