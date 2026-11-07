using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Model;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>The lasso's action bar (FR-51, NFR-31): colours, copy, paste, delete, named and wired to the commands.</summary>
public sealed class SelectionActionBarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-lassobar-" + Guid.NewGuid().ToString("N"));

    public SelectionActionBarTests() => Directory.CreateDirectory(_dir);

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
    public async Task The_bar_is_hidden_until_something_is_selected_and_every_button_has_a_name() // FR-51, NFR-31
    {
        await WithBarAsync((vm, bar) =>
        {
            Assert.Equal(Visibility.Collapsed, bar.Visibility);

            Select(vm);
            bar.UpdateLayout();

            Assert.Equal(Visibility.Visible, bar.Visibility);
            Assert.Equal(vm.Toolbar.Pen.Presets.Count, bar.SwatchButtons.Count);
            Assert.Equal(vm.Toolbar.Pen.Presets.Select(p => p.Name), bar.SwatchButtons.Select(AutomationProperties.GetName));
            Assert.All(bar.SwatchButtons, b => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))));
            Assert.Equal(Resources.Strings.SelectionCopy, AutomationProperties.GetName(bar.CopyButton));
            Assert.Equal(Resources.Strings.SelectionPaste, AutomationProperties.GetName(bar.PasteButton));
            Assert.Equal(Resources.Strings.SelectionDelete, AutomationProperties.GetName(bar.DeleteButton));
        });
    }

    [Fact]
    public async Task A_colour_button_recolours_the_selection_and_delete_removes_it() // FR-51
    {
        await WithBarAsync((vm, bar) =>
        {
            Select(vm);
            var red = vm.Toolbar.Pen.Presets[3];

            bar.SwatchButtons[3].Command.Execute(bar.SwatchButtons[3].CommandParameter);
            Assert.Equal(red.Color, vm.Editor!.Page.Strokes[0].Color);

            Assert.True(bar.DeleteButton.Command.CanExecute(null));
            bar.DeleteButton.Command.Execute(null);
            Assert.Empty(vm.Editor.Page.Strokes);
            Assert.False(bar.DeleteButton.Command.CanExecute(null));
        });
    }

    [Fact]
    public async Task Paste_is_enabled_only_after_a_copy() // FR-51
    {
        await WithBarAsync((vm, bar) =>
        {
            Select(vm);
            Assert.False(bar.PasteButton.Command.CanExecute(null));

            bar.CopyButton.Command.Execute(null);

            Assert.True(bar.PasteButton.Command.CanExecute(null));
        });
    }

    [Fact]
    public async Task With_transparency_off_the_surface_is_opaque() // NFR-32
    {
        await WithBarAsync((_, bar) =>
        {
            bar.TransparencyEnabled = () => false;
            bar.ApplyTransparency();

            Assert.True(bar.OpaqueSurface);
        });
    }

    private static void Select(MainViewModel vm)
    {
        vm.Editor!.CommitStroke(
            [new InkPoint(50, 50, 0.5f, null, null, null, 0), new InkPoint(80, 80, 0.5f, null, null, null, 10)],
            InkTool.Pen,
            InkColor.FromRgb(0, 0, 0),
            2);
        vm.Tools.ActiveTool = AppTool.Selection;
        vm.Selection.SelectLoop([new(0, 0), new(120, 0), new(120, 120), new(0, 120)]);
        Assert.True(vm.Selection.HasSelection);
    }

    private async Task WithBarAsync(Action<MainViewModel, SelectionActionBar> test)
    {
        var path = Path.Combine(_dir, "page.pdf");
        TestPdfFactory.Write(path, (200, 300));
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path });
        await vm.OpenPdfCommand.ExecuteAsync(null);
        try
        {
            WpfTestThread.Run(() =>
            {
                var bar = new SelectionActionBar { DataContext = vm };
                var host = new Window { Content = bar, Width = 600, Height = 200, ShowActivated = false };
                try
                {
                    host.Show();
                    bar.UpdateLayout();
                    test(vm, bar);
                }
                finally
                {
                    host.Close();
                }
            });
        }
        finally
        {
            await vm.Renderer!.DisposeAsync();
        }
    }
}
