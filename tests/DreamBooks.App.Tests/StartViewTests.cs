using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>The start screen in the window: covers of the recent documents, the open button, drop to open (FR-03, UI-04).</summary>
[Collection(nameof(ThemeCollection))]
public sealed class StartViewTests : IDisposable
{
    private static readonly DateTime _now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-startview-" + Guid.NewGuid().ToString("N"));
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

    private RecentFilesService NewRecents(int count = 3, Func<string, bool>? exists = null)
    {
        Directory.CreateDirectory(_dir);
        var recents = new RecentFilesService(new RecentFilesStore(Path.Combine(_dir, "state")), exists ?? (_ => true), _scheduler, () => _now);
        recents.Load();
        for (var i = 0; i < count; i++)
        {
            recents.Opened($@"C:\Libri\libro {i}.pdf", i, 10 + i);
        }

        return recents;
    }

    private static IEnumerable<T> Find<T>(DependencyObject root)
        where T : DependencyObject =>
        ToolbarViewTests.Descendants(root).OfType<T>();

    private static void WithWindow(RecentFilesService recents, Action<MainWindow, FrameworkElement> test, double width = 1000)
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow(null, null, recents);
            try
            {
                var root = (FrameworkElement)window.Content;
                ScrollRailTests.LayOut(root, width, 700);
                test(window, root);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public void Without_a_document_the_start_screen_shows_one_card_per_recent_with_its_texts()
    {
        WithWindow(NewRecents(), (window, _) =>
        {
            Assert.Equal(Visibility.Visible, window.StartScreen.Visibility);
            var cards = Find<Button>(window.StartScreen.CardsList)
                .Where(b => b.Command == window.Start!.OpenRecentCommand)
                .ToList();
            Assert.Equal(3, cards.Count);
            Assert.Equal("libro 2", ((RecentCardViewModel)cards[0].CommandParameter).Title); // newest first
            Assert.All(cards, c => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(c))));
            Assert.Equal(Strings.StartOpenPdf, AutomationProperties.GetName(window.StartScreen.OpenButton));
            Assert.True(window.StartScreen.OpenButton.ActualHeight >= 40);
            Assert.All(cards, c => Assert.True(c.ActualWidth >= 40 && c.ActualHeight >= 40));
        });
    }

    [Fact]
    public void The_start_screen_hides_while_a_document_is_open_and_comes_back_when_it_closes()
    {
        var path = Path.Combine(_dir, "a.pdf");
        Directory.CreateDirectory(_dir);
        TestPdfFactory.Write(path, (612, 792));
        WithWindow(NewRecents(0), (window, _) =>
        {
            var vm = (MainViewModel)window.DataContext;
            Assert.Equal(Visibility.Visible, window.StartScreen.Visibility);

            var open = vm.OpenRecentAsync(path);
            WpfTestThread.PumpUntil(() => open.IsCompleted);
            Assert.Equal(Visibility.Collapsed, window.StartScreen.Visibility);

            var close = vm.CloseDocumentCommand.ExecuteAsync(null);
            WpfTestThread.PumpUntil(() => close.IsCompleted);
            Assert.Equal(Visibility.Visible, window.StartScreen.Visibility);
        });
    }


    [Fact]
    public void A_cover_arriving_later_does_not_change_the_size_of_its_card()
    {
        WithWindow(NewRecents(1), (window, _) =>
        {
            var card = Find<Button>(window.StartScreen.CardsList).First(b => b.Command == window.Start!.OpenRecentCommand);
            var before = new Size(card.ActualWidth, card.ActualHeight);

            window.Start!.Cards[0].Cover = new System.Windows.Media.Imaging.BitmapImage();
            window.StartScreen.UpdateLayout();

            Assert.Equal(before, new Size(card.ActualWidth, card.ActualHeight));
        });
    }

    [Fact]
    public void A_missing_card_shows_the_remove_button_and_says_so()
    {
        var recents = NewRecents(2, exists: _ => false);
        WithWindow(recents, (window, _) =>
        {
            var removes = Find<Button>(window.StartScreen.CardsList).Where(b => b.Name == "RemoveButton").ToList();
            Assert.All(removes, b => Assert.Equal(Visibility.Collapsed, b.Visibility)); // not checked yet

            var check = recents.RefreshAvailabilityAsync(CancellationToken.None);
            WpfTestThread.PumpUntil(() => check.IsCompleted);
            window.StartScreen.UpdateLayout();

            Assert.All(Find<Button>(window.StartScreen.CardsList).Where(b => b.Name == "RemoveButton"), b => Assert.Equal(Visibility.Visible, b.Visibility));
            Assert.All(window.Start!.Cards, c => Assert.Equal(Strings.StartMissing, c.PageText));
        });
    }


    [Fact]
    public void With_all_the_recents_shown_in_a_short_window_the_start_screen_scrolls_by_finger_and_wheel()
    {
        WithWindow(NewRecents(20), (window, _) =>
        {
            window.Start!.ShowAllCommand.Execute(null);
            ScrollRailTests.LayOut((FrameworkElement)window.Content, 1000, 500);
            window.StartScreen.UpdateLayout();

            var scroller = Find<ScrollViewer>(window.StartScreen).First();
            Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);
            Assert.Equal(PanningMode.VerticalOnly, scroller.PanningMode);
            Assert.True(scroller.ScrollableHeight > 0, "the lower rows can be reached");
        });
    }

    [Fact]
    public void A_wider_window_fits_more_cards_and_Altri_shows_when_some_do_not_fit()
    {
        WithWindow(NewRecents(8), (window, _) =>
        {
            var narrow = window.Start!.Cards.Count;
            Assert.True(window.Start.ShowMore);
            Assert.True(narrow < 8);

            ScrollRailTests.LayOut((FrameworkElement)window.Content, 1600, 700);
            window.StartScreen.UpdateLayout();

            Assert.True(window.Start.Cards.Count >= narrow);
            Assert.Equal(window.StartScreen.CardsPerRow, window.Start.ShowMore ? window.Start.Cards.Count + 1 : window.Start.Cards.Count);
        }, width: 700);
    }

    [Fact]
    public void Only_the_button_and_the_cards_take_the_pointer_around_them_the_page_does()
    {
        WithWindow(NewRecents(), (window, root) =>
        {
            var beside = new Point(5, 400);
            Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(window.PageHost, beside), window.DocumentViewer));
        });
    }

    [Fact]
    public void A_dropped_file_that_is_not_a_pdf_says_so_and_a_pdf_opens()
    {
        var path = Path.Combine(_dir, "dropped.pdf");
        Directory.CreateDirectory(_dir);
        TestPdfFactory.Write(path, (612, 792));
        WithWindow(NewRecents(0), (window, _) =>
        {
            var vm = (MainViewModel)window.DataContext;
            var text = window.OpenDroppedFileAsync([@"C:\notes.txt", @"C:\x.docx"]);
            WpfTestThread.PumpUntil(() => text.IsCompleted);
            Assert.Equal(Strings.StartNotAPdf, vm.ErrorMessage);
            Assert.Null(vm.Session);

            var pdf = window.OpenDroppedFileAsync([@"C:\notes.txt", path]);
            WpfTestThread.PumpUntil(() => pdf.IsCompleted);
            Assert.NotNull(vm.Session);
            Assert.Null(vm.ErrorMessage);
        });
    }
}
