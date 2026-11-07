using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>The export commands: when they can run, the progress line, failures on the error line, saving waits (FR-87, FR-88, FR-89).</summary>
public sealed class MainViewModelExportTests : IDisposable
{
    private static readonly InkPoint[] _dot =
    [
        new(100, 100, 0.5f, null, null, null, 0),
        new(300, 120, 0.5f, null, null, null, 10),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-exportvm-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();

    public MainViewModelExportTests() => Directory.CreateDirectory(_dir);

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

    private async Task<MainViewModel> OpenAsync()
    {
        var path = Path.Combine(_dir, "a.pdf");
        TestPdfFactory.Write(path, (612, 792), (612, 792));
        _dialogs.NextPath = path;
        var vm = new MainViewModel(_dialogs);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        return vm;
    }

    private static async Task CleanUpAsync(MainViewModel vm)
    {
        if (vm.Renderer is { } renderer)
        {
            await renderer.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_export_commands_need_a_document()
    {
        var vm = new MainViewModel(_dialogs);
        Assert.False(vm.ExportFlattenedCommand.CanExecute(null));
        Assert.False(vm.ExportPagePngCommand.CanExecute(null));
        Assert.False(vm.HasDocument);

        _dialogs.NextPath = Path.Combine(_dir, "a.pdf");
        TestPdfFactory.Write(_dialogs.NextPath, (612, 792));
        await vm.OpenPdfCommand.ExecuteAsync(null);
        try
        {
            Assert.True(vm.ExportFlattenedCommand.CanExecute(null));
            Assert.True(vm.ExportPagePngCommand.CanExecute(null));
            Assert.True(vm.HasDocument);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task A_page_export_writes_the_current_page_and_the_progress_line_shows_meanwhile_and_saving_waits()
    {
        var vm = await OpenAsync();
        try
        {
            await vm.GoToPageCommand.ExecuteAsync(2);
            var png = Path.Combine(_dir, "page.png");
            _dialogs.NextPngPath = png;
            var progress = new List<bool>();
            var saveEnabled = new List<bool>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.ShowsProgress))
                {
                    progress.Add(vm.ShowsProgress);
                    saveEnabled.Add(vm.SaveCommand.CanExecute(null));
                }
            };

            await vm.ExportPagePngCommand.ExecuteAsync(null);

            Assert.True(File.Exists(png));
            Assert.Equal([true, false], progress); // the line shows while it runs, then goes
            Assert.Equal([false, true], saveEnabled); // no save during an export
            Assert.Equal([$"a - pag. 2.png"], _dialogs.SuggestedPngNames);
            Assert.False(vm.IsExporting);
            Assert.Null(vm.ErrorMessage);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task A_failed_export_shows_its_message_on_the_error_line_and_as_a_popup_and_keeps_the_document_as_it_was()
    {
        var vm = await OpenAsync();
        try
        {
            Assert.True(vm.Editor!.CommitStroke(_dot, InkTool.Pen, Palette.Colors[0], 1));
            _dialogs.NextSavePath = Path.Combine(_dir, "no-such-folder", "flat.pdf");

            await vm.ExportFlattenedCommand.ExecuteAsync(null);

            Assert.Equal(Strings.ErrorExportFailed, vm.ErrorMessage);
            Assert.Contains(Strings.ErrorExportFailed, _dialogs.Messages);
            Assert.True(vm.IsDirty); // nothing was saved, nothing was lost
            Assert.False(vm.IsExporting);
            Assert.True(vm.ExportFlattenedCommand.CanExecute(null));
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }

    [Fact]
    public async Task Cancelling_the_dialog_says_nothing_and_a_successful_flattened_export_leaves_the_file_alone()
    {
        var vm = await OpenAsync();
        try
        {
            _dialogs.NextSavePath = null;
            await vm.ExportFlattenedCommand.ExecuteAsync(null);
            Assert.Null(vm.ErrorMessage);
            Assert.Empty(_dialogs.Messages);

            var flat = Path.Combine(_dir, "flat.pdf");
            _dialogs.NextSavePath = flat;
            await vm.ExportFlattenedCommand.ExecuteAsync(null);
            Assert.True(File.Exists(flat));
            Assert.Null(vm.ErrorMessage);
        }
        finally
        {
            await CleanUpAsync(vm);
        }
    }
}
