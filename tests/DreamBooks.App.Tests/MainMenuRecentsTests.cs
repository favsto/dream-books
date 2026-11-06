using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>The menu's "File recenti" list and "Chiudi documento" (UI-03, FR-03).</summary>
[Collection(nameof(ThemeCollection))]
public sealed class MainMenuRecentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-menurecents-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

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

    private RecentFilesService NewRecents(int count)
    {
        Directory.CreateDirectory(_dir);
        var recents = new RecentFilesService(new RecentFilesStore(Path.Combine(_dir, "state")), _ => true, _scheduler);
        recents.Load();
        for (var i = 0; i < count; i++)
        {
            recents.Opened(Path.Combine(_dir, $"libro {i}.pdf"), 0, 5);
        }

        return recents;
    }

    private static void WithWindow(RecentFilesService recents, Action<MainWindow> test)
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow(null, null, recents);
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1000, 700);
                test(window);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public void File_recenti_is_disabled_without_recents_and_lists_up_to_eight_with_them()
    {
        WithWindow(NewRecents(0), window => Assert.False(window.MainMenu.RecentItem.IsEnabled));

        WithWindow(NewRecents(11), window =>
        {
            var menu = window.MainMenu;
            Assert.True(menu.RecentItem.IsEnabled);
            Assert.Equal(MainMenuButton.MaxRecents, menu.RecentRows.Count);
            Assert.Equal("libro 10.pdf", AutomationProperties.GetName(menu.RecentRows[0])); // newest first
            Assert.False(menu.IsRecentListOpen);
            Assert.Equal(Strings.MenuRecentFiles, AutomationProperties.GetName(menu.RecentItem));
            Assert.All(menu.RecentRows, r => Assert.True(r.MinHeight >= 40));
        });
    }

    [Fact]
    public void Choosing_File_recenti_expands_the_list_and_a_row_opens_the_document_and_closes_the_menu()
    {
        var recents = NewRecents(0);
        var path = Path.Combine(_dir, "a.pdf");
        TestPdfFactory.Write(path, (612, 792));
        recents.Opened(path, 0, 1);
        WithWindow(recents, window =>
        {
            var menu = window.MainMenu;
            menu.Open();
            window.UpdateLayout();

            ToolbarViewTests.Invoke(menu.RecentItem);
            Assert.True(menu.IsOpen);
            Assert.True(menu.IsRecentListOpen);

            var vm = (MainViewModel)window.DataContext;
            ToolbarViewTests.Invoke(menu.RecentRows[0]);
            Assert.False(menu.IsOpen);
            WpfTestThread.PumpUntil(() => vm.Session is not null);
            Assert.Equal(path, vm.Session!.Path);
        });
    }

    [Fact]
    public void Chiudi_documento_is_enabled_with_a_document_and_closes_it()
    {
        var path = Path.Combine(_dir, "a.pdf");
        Directory.CreateDirectory(_dir);
        TestPdfFactory.Write(path, (612, 792));
        WithWindow(NewRecents(0), window =>
        {
            var menu = window.MainMenu;
            var vm = (MainViewModel)window.DataContext;
            Assert.False(menu.CloseItem.IsEnabled);
            Assert.Equal(Strings.MenuCloseDocument, AutomationProperties.GetName(menu.CloseItem));

            var open = vm.OpenRecentAsync(path);
            WpfTestThread.PumpUntil(() => open.IsCompleted);
            Assert.True(menu.CloseItem.IsEnabled);

            var close = vm.CloseDocumentCommand.ExecuteAsync(null);
            WpfTestThread.PumpUntil(() => close.IsCompleted);
            Assert.False(menu.CloseItem.IsEnabled);
            Assert.Null(vm.Session);
        });
    }
}
