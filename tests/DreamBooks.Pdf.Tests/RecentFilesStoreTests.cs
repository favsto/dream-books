using System.Text;
using DreamBooks.Core.Recent;
using DreamBooks.Core.Settings;

namespace DreamBooks.Pdf.Tests;

public sealed class RecentFilesStoreTests : IDisposable
{
    private static readonly DateTime _t0 = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "db-recent-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (File.Exists(_root))
            {
                File.Delete(_root);
            }
            else
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string RecentPath => Path.Combine(_root, "recent.json");

    private void Write(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(RecentPath, json, Encoding.ASCII);
    }

    [Fact]
    public void Save_then_Load_round_trips_the_list_in_order()
    {
        var store = new RecentFilesStore(_root);
        var list = new RecentFilesList();
        list.Touch(new RecentFile(@"C:\Libri\b è.pdf", _t0, 3, 40));
        list.Touch(new RecentFile(@"C:\a.pdf", _t0.AddHours(1), 0, 7));

        store.Save(list);
        var loaded = store.Load();

        Assert.Equal(list.Items, loaded.Items);
        Assert.DoesNotContain(File.ReadAllBytes(RecentPath), b => b > 127); // ASCII: non-ASCII is escaped
    }

    [Fact]
    public void The_file_has_the_documented_format()
    {
        var store = new RecentFilesStore(_root);
        store.Save(new RecentFilesList([new RecentFile(@"C:\a.pdf", _t0, 2, 9)]));

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(RecentPath));
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(@"C:\a.pdf", item.GetProperty("path").GetString());
        Assert.Equal(2, item.GetProperty("page").GetInt32());
        Assert.Equal(9, item.GetProperty("pages").GetInt32());
        Assert.True(DateTime.TryParse(item.GetProperty("opened").GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var opened));
        Assert.Equal(_t0, opened);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"v\":1,\"items\":[{\"path\":\"C:\\\\a.pdf\",\"opened\":\"2026-11-01T10:00")] // truncated
    [InlineData("{\"v\":2,\"items\":[{\"path\":\"C:\\\\a.pdf\",\"opened\":\"2026-11-01T10:00:00Z\",\"page\":0,\"pages\":1}]}")]
    [InlineData("{\"items\":[]}")]
    [InlineData("[1,2,3]")]
    public void A_corrupt_or_unknown_file_gives_an_empty_list(string json)
    {
        Write(json);

        Assert.Empty(new RecentFilesStore(_root).Load().Items);
    }

    [Fact]
    public void A_missing_file_gives_an_empty_list()
    {
        Assert.Empty(new RecentFilesStore(_root).Load().Items);
    }

    [Fact]
    public void A_file_with_1000_entries_loads_20_and_skips_invalid_ones()
    {
        var items = string.Join(",", Enumerable.Range(0, 1000).Select(i =>
            $"{{\"path\":\"C:\\\\f{i}.pdf\",\"opened\":\"2026-11-01T10:00:00Z\",\"page\":{i},\"pages\":5}}"));
        Write($"{{\"v\":1,\"items\":[{{\"path\":1}},{{\"opened\":\"x\"}},{items}]}}");

        var loaded = new RecentFilesStore(_root).Load();

        Assert.Equal(RecentFilesList.MaxCount, loaded.Items.Count);
        Assert.Equal(@"C:\f0.pdf", loaded.Items[0].Path);
        Assert.Equal(4, loaded.Items[19].Page); // clamped into its page count
    }

    [Fact]
    public void The_settings_file_never_contains_a_path_after_recents_are_saved()
    {
        var settings = new AppSettingsStore(_root);
        settings.Save(AppSettings.Default);
        new RecentFilesStore(_root).Save(new RecentFilesList([new RecentFile(@"C:\Secret\libro.pdf", _t0, 0, 1)]));

        var text = File.ReadAllText(Path.Combine(_root, "settings.json"));
        Assert.DoesNotContain("Secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("libro", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_save_raises_Failed_without_a_path_and_does_not_throw()
    {
        File.WriteAllText(_root, "a file where the folder should be");
        var store = new RecentFilesStore(_root);
        Exception? failure = null;
        store.Failed += (_, ex) => failure = ex;

        store.Save(new RecentFilesList([new RecentFile(@"C:\Secret\a.pdf", _t0, 0, 1)]));

        Assert.NotNull(failure);
        Assert.DoesNotContain("Secret", failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_orphan_temporary_file_is_swept_at_load_and_a_young_one_is_kept()
    {
        Directory.CreateDirectory(_root);
        var old = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        var young = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(old, "x");
        File.WriteAllText(young, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));

        new RecentFilesStore(_root).Load();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(young));
    }
}
