using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>The start screen's state: the cover cards, their texts, and what a tap does (FR-03, UI-04).</summary>
public sealed class StartViewModelTests : IDisposable
{
    private static readonly DateTime _now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-start-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();
    private readonly List<string> _opened = [];
    private int _openPdfCalls;

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

    private (RecentFilesService Service, StartViewModel Start) Create(int fit = 4, Func<string, bool>? exists = null)
    {
        var service = new RecentFilesService(new RecentFilesStore(_dir), exists ?? (_ => true), _scheduler, () => _now);
        service.Load();
        var start = new StartViewModel(
            service,
            () => { _openPdfCalls++; return Task.CompletedTask; },
            path => { _opened.Add(path); return Task.CompletedTask; },
            fit,
            () => _now);
        return (service, start);
    }

    [Fact]
    public void Cards_follow_the_recents_with_title_when_and_progress()
    {
        var (service, start) = Create();
        service.Opened(@"C:\Libri\Mondo senza fine.pdf", 130, 400);

        var card = Assert.Single(start.Cards);
        Assert.Equal("Mondo senza fine", card.Title);
        Assert.Equal(Strings.WhenToday, card.WhenText);
        Assert.Equal(string.Format(Strings.StartPageFormat, 131), card.PageText);
        Assert.Equal(131.0 / 400, card.Progress, 6);
        Assert.False(card.IsMissing);
        Assert.True(start.HasRecents);
    }

    [Fact]
    public void Cards_are_refreshed_when_the_recents_change_and_the_older_ones_go_to_the_more_card()
    {
        var (service, start) = Create(fit: 2);
        Assert.False(start.HasRecents);
        service.Opened(@"C:\a.pdf", 0, 1);
        service.Opened(@"C:\b.pdf", 0, 1);
        Assert.False(start.ShowMore);
        service.Opened(@"C:\c.pdf", 0, 1);

        Assert.Equal(["c", "b"], start.Cards.Select(c => c.Title));
        Assert.True(start.ShowMore);
    }

    [Fact]
    public async Task Tapping_a_card_opens_its_file_and_the_open_button_asks_for_a_pdf()
    {
        var (service, start) = Create();
        service.Opened(@"C:\a.pdf", 0, 1);

        await start.OpenRecentCommand.ExecuteAsync(start.Cards[0]);
        await start.OpenPdfCommand.ExecuteAsync(null);

        Assert.Equal([@"C:\a.pdf"], _opened);
        Assert.Equal(1, _openPdfCalls);
    }

    [Fact]
    public async Task A_missing_card_says_so_and_a_tap_removes_it_instead_of_opening()
    {
        var (service, start) = Create(exists: _ => false);
        service.Opened(@"C:\gone.pdf", 3, 9);
        await service.RefreshAvailabilityAsync(CancellationToken.None);

        var card = Assert.Single(start.Cards);
        Assert.True(card.IsMissing);
        Assert.Equal(Strings.StartMissing, card.PageText);

        await start.OpenRecentCommand.ExecuteAsync(card);

        Assert.Empty(_opened);
        Assert.Empty(start.Cards);
        Assert.Empty(service.Entries);
    }

    [Fact]
    public void RemoveCommand_takes_a_card_out_of_the_list()
    {
        var (service, start) = Create();
        service.Opened(@"C:\a.pdf", 0, 1);
        service.Opened(@"C:\b.pdf", 0, 1);

        start.RemoveCommand.Execute(start.Cards[0]);

        Assert.Equal(["a"], start.Cards.Select(c => c.Title));
    }

    [Fact]
    public void A_cover_arrives_later_and_raises_the_change_notification()
    {
        var (service, start) = Create();
        service.Opened(@"C:\a.pdf", 0, 1);
        var card = start.Cards[0];
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Null(card.Cover);
        card.Cover = new System.Windows.Media.Imaging.BitmapImage();

        Assert.Contains(nameof(RecentCardViewModel.Cover), raised);
    }

    [Fact]
    public async Task A_new_card_asks_for_its_cover_once_and_keeps_it_across_refreshes()
    {
        var (service, start) = Create();
        var asked = new List<string>();
        var image = new System.Windows.Media.Imaging.BitmapImage();
        start.CoverProvider = (path, _) =>
        {
            asked.Add(path);
            return Task.FromResult<System.Windows.Media.ImageSource?>(image);
        };

        service.Opened(@"C:\a.pdf", 0, 1);
        await WaitUntilAsync(() => start.Cards[0].Cover is not null);
        service.Opened(@"C:\b.pdf", 0, 1); // the list changes: a.pdf keeps its card and its cover
        await WaitUntilAsync(() => start.Cards.All(c => c.Cover is not null));

        Assert.Equal([@"C:\a.pdf", @"C:\b.pdf"], asked);
        Assert.Same(image, start.Cards[1].Cover);
    }

    [Fact]
    public async Task ReloadCover_asks_again_for_one_document()
    {
        var (service, start) = Create();
        var calls = 0;
        start.CoverProvider = (_, _) =>
        {
            calls++;
            return Task.FromResult<System.Windows.Media.ImageSource?>(null);
        };
        service.Opened(@"C:\a.pdf", 0, 1);
        await WaitUntilAsync(() => calls == 1);

        start.ReloadCover(@"C:\A.PDF");
        await WaitUntilAsync(() => calls == 2);
    }

    [Fact]
    public void A_wider_window_shows_more_cards()
    {
        var (service, start) = Create(fit: 2);
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            service.Opened($@"C:\{name}.pdf", 0, 1);
        }

        Assert.Equal(2, start.Cards.Count);
        start.FitCount = 3;

        Assert.Equal(3, start.Cards.Count);
        Assert.True(start.ShowMore);
        start.FitCount = 10;
        Assert.Equal(4, start.Cards.Count);
        Assert.False(start.ShowMore);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "the condition did not become true");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Altri_shows_every_recent_until_everything_fits_again()
    {
        var (service, start) = Create(fit: 2);
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            service.Opened($@"C:\{name}.pdf", 0, 1);
        }

        Assert.True(start.ShowMore);
        start.ShowAllCommand.Execute(null);

        Assert.Equal(4, start.Cards.Count);
        Assert.False(start.ShowMore);
        Assert.Equal(4, start.TotalCount);
        service.Remove(@"C:\a.pdf");
        service.Remove(@"C:\b.pdf");
        Assert.Equal(2, start.Cards.Count);
        service.Opened(@"C:\e.pdf", 0, 1); // 3 recents, 2 fit: "Altri…" is back
        Assert.Equal(2, start.Cards.Count);
        Assert.True(start.ShowMore);
    }

    [Fact]
    public void A_card_has_one_phrase_for_a_screen_reader()
    {
        var (service, start) = Create();
        service.Opened(@"C:\Libri\Mondo.pdf", 4, 40);

        Assert.Equal(
            string.Format(Strings.StartCardNameFormat, "Mondo", Strings.WhenToday, string.Format(Strings.StartPageFormat, 5)),
            start.Cards[0].AutomationName);
    }

    [Fact]
    public void A_zero_page_count_gives_no_progress_instead_of_dividing_by_zero()
    {
        var (service, start) = Create();
        service.Opened(@"C:\a.pdf", 0, 0);

        Assert.Equal(1.0, start.Cards[0].Progress); // the list clamps the page count to 1: page 1 of 1
    }
}
