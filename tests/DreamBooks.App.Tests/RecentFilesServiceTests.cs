using DreamBooks.App.Services;
using DreamBooks.Core.Recent;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>The recent documents the app remembers: order, debounced save, and which files are still there (FR-03).</summary>
public sealed class RecentFilesServiceTests : IDisposable
{
    private static readonly DateTime _now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-recent-" + Guid.NewGuid().ToString("N"));
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

    private RecentFilesService Create(Func<string, bool>? exists = null)
    {
        var service = new RecentFilesService(new RecentFilesStore(_dir), exists ?? (_ => true), _scheduler, () => _now);
        service.Load();
        return service;
    }

    [Fact]
    public async Task Opened_puts_the_file_first_and_saves_after_half_a_second()
    {
        var service = Create();
        service.Opened(@"C:\a.pdf", 3, 40);
        service.Opened(@"C:\b.pdf", 0, 5);

        Assert.Equal([@"C:\b.pdf", @"C:\a.pdf"], service.Entries.Select(e => e.Path));
        Assert.Equal("b.pdf", service.Entries[0].FileName);
        Assert.Empty(new RecentFilesStore(_dir).Load().Items); // not yet

        _scheduler.Advance(RecentFilesService.SaveDelay);
        await service.WhenWrittenAsync();

        Assert.Equal([@"C:\b.pdf", @"C:\a.pdf"], new RecentFilesStore(_dir).Load().Items.Select(f => f.Path));
    }

    [Fact]
    public async Task Remove_and_UpdateProgress_save_and_raise_Changed()
    {
        var service = Create();
        service.Opened(@"C:\a.pdf", 0, 10);
        service.Opened(@"C:\b.pdf", 0, 10);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        service.UpdateProgress(@"C:\a.pdf", 7, 10);
        service.Remove(@"C:\b.pdf");
        _scheduler.Advance(RecentFilesService.SaveDelay);
        await service.WhenWrittenAsync();

        Assert.Equal(2, changes);
        var saved = new RecentFilesStore(_dir).Load().Items;
        Assert.Single(saved);
        Assert.Equal(7, saved[0].Page);
    }

    [Fact]
    public async Task A_missing_file_is_marked_after_the_check_and_the_check_runs_off_the_calling_thread()
    {
        var callingThread = Environment.CurrentManagedThreadId;
        var checkedOn = new List<int>();
        var service = Create(path =>
        {
            lock (checkedOn)
            {
                checkedOn.Add(Environment.CurrentManagedThreadId);
            }

            return !path.Contains("gone", StringComparison.Ordinal);
        });
        service.Opened(@"C:\here.pdf", 0, 1);
        service.Opened(@"C:\gone.pdf", 0, 1);
        Assert.All(service.Entries, e => Assert.Null(e.Exists)); // unknown until checked

        await service.RefreshAvailabilityAsync(CancellationToken.None);

        Assert.Equal(false, service.Entries.Single(e => e.FileName == "gone.pdf").Exists);
        Assert.Equal(true, service.Entries.Single(e => e.FileName == "here.pdf").Exists);
        Assert.Equal(2, checkedOn.Count);
        Assert.DoesNotContain(callingThread, checkedOn);
    }

    [Fact]
    public async Task A_check_that_throws_counts_as_missing()
    {
        var service = Create(_ => throw new IOException("network drive gone"));
        service.Opened(@"\\server\share\a.pdf", 0, 1);

        await service.RefreshAvailabilityAsync(CancellationToken.None);

        Assert.Equal(false, service.Entries[0].Exists);
    }

    [Fact]
    public async Task A_cancelled_check_changes_nothing()
    {
        var service = Create();
        service.Opened(@"C:\a.pdf", 0, 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await service.RefreshAvailabilityAsync(cts.Token);

        Assert.Null(service.Entries[0].Exists);
    }

    [Fact]
    public void Load_reads_the_saved_list_with_the_progress()
    {
        new RecentFilesStore(_dir).Save(new RecentFilesList([new RecentFile(@"C:\a.pdf", _now.AddDays(-1), 130, 400)]));

        var service = Create();

        Assert.Equal(130, service.Entries[0].Page);
        Assert.Equal(400, service.Entries[0].PageCount);
        Assert.Equal(_now.AddDays(-1), service.Entries[0].LastOpenedUtc);
    }
}
