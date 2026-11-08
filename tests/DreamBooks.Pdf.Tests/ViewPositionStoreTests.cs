using DreamBooks.Core.Layout;

namespace DreamBooks.Pdf.Tests;

public sealed class ViewPositionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "db-positions-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

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

    private ViewPositionStore NewStore() => new(_root, () => _now);

    private string FileFor(string path) => Path.Combine(_root, RecoveryStore.DocumentKey(path) + ".json");

    [Fact]
    public void Save_then_TryLoad_round_trips_every_field()
    {
        var store = NewStore();
        var position = new ViewPosition(12, -3.14159265358979, 301.5000001, 1.7320508075688772);
        store.Save(@"C:\Books\One.pdf", position);

        var loaded = store.TryLoad(@"C:\Books\One.pdf");

        Assert.NotNull(loaded);
        Assert.Equal(position, loaded.Value.Position);
        Assert.Equal(_now, loaded.Value.SavedUtc);
    }

    [Fact]
    public void Different_paths_do_not_collide_and_case_is_ignored()
    {
        var store = NewStore();
        store.Save(@"C:\Books\One.pdf", new ViewPosition(1, 0, 0, 1));
        store.Save(@"C:\Books\Two.pdf", new ViewPosition(2, 0, 0, 1));

        Assert.Equal(1, store.TryLoad(@"c:\books\ONE.pdf")!.Value.Position.PageIndex);
        Assert.Equal(2, store.TryLoad(@"C:\Books\Two.pdf")!.Value.Position.PageIndex);
    }

    [Fact]
    public void Corrupt_file_returns_null()
    {
        var store = NewStore();
        store.Save(@"C:\Books\One.pdf", new ViewPosition(1, 0, 0, 1));
        File.WriteAllText(FileFor(@"C:\Books\One.pdf"), "{\"v\":1,\"page\":");

        Assert.Null(store.TryLoad(@"C:\Books\One.pdf"));

        File.WriteAllBytes(FileFor(@"C:\Books\One.pdf"), [0, 1, 2, 255]);
        Assert.Null(store.TryLoad(@"C:\Books\One.pdf"));
    }

    [Fact]
    public void Unknown_version_returns_null()
    {
        var store = NewStore();
        store.Save(@"C:\Books\One.pdf", new ViewPosition(1, 0, 0, 1));
        File.WriteAllText(FileFor(@"C:\Books\One.pdf"), "{\"v\":2,\"page\":1,\"y\":0,\"x\":0,\"zoom\":1,\"saved\":\"2026-10-31T12:00:00.0000000Z\"}");

        Assert.Null(store.TryLoad(@"C:\Books\One.pdf"));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("\"NaN\"")]
    [InlineData("1e999")]
    public void NaN_or_negative_zoom_returns_null(string zoom)
    {
        var store = NewStore();
        store.Save(@"C:\Books\One.pdf", new ViewPosition(1, 0, 0, 1));
        File.WriteAllText(FileFor(@"C:\Books\One.pdf"), "{\"v\":1,\"page\":1,\"y\":0,\"x\":0,\"zoom\":" + zoom + ",\"saved\":\"2026-10-31T12:00:00.0000000Z\"}");

        Assert.Null(store.TryLoad(@"C:\Books\One.pdf"));
    }

    [Fact]
    public void Negative_page_returns_null()
    {
        var store = NewStore();
        store.Save(@"C:\Books\One.pdf", new ViewPosition(1, 0, 0, 1));
        File.WriteAllText(FileFor(@"C:\Books\One.pdf"), "{\"v\":1,\"page\":-4,\"y\":0,\"x\":0,\"zoom\":1,\"saved\":\"2026-10-31T12:00:00.0000000Z\"}");

        Assert.Null(store.TryLoad(@"C:\Books\One.pdf"));
    }

    [Fact]
    public void Missing_folder_returns_null_and_Save_creates_it()
    {
        var store = NewStore();
        Assert.False(Directory.Exists(_root));
        Assert.Null(store.TryLoad(@"C:\Books\One.pdf"));

        store.Save(@"C:\Books\One.pdf", new ViewPosition(3, 1, 2, 1.5));

        Assert.True(Directory.Exists(_root));
        Assert.Equal(3, store.TryLoad(@"C:\Books\One.pdf")!.Value.Position.PageIndex);
    }

    [Fact]
    public void Delete_removes_the_entry_and_is_idempotent()
    {
        var store = NewStore();
        store.Save(@"C:\Books\One.pdf", new ViewPosition(1, 0, 0, 1));

        store.Delete(@"C:\Books\One.pdf");
        store.Delete(@"C:\Books\One.pdf");

        Assert.Null(store.TryLoad(@"C:\Books\One.pdf"));
    }

    [Fact]
    public void Prune_removes_old_and_beyond_max_entries_oldest_first()
    {
        var store = NewStore();
        var start = _now;
        for (var i = 0; i < 6; i++)
        {
            _now = start.AddDays(i);
            store.Save($@"C:\Books\B{i}.pdf", new ViewPosition(i, 0, 0, 1));
        }

        // B0 is 6 days old (too old); B1..B5 are 5 entries, so with a cap of 4 the oldest, B1, goes too.
        _now = start.AddDays(6);
        store.Prune(maxEntries: 4, maxAge: TimeSpan.FromDays(5.5));

        Assert.Null(store.TryLoad(@"C:\Books\B0.pdf"));
        Assert.Null(store.TryLoad(@"C:\Books\B1.pdf"));
        for (var i = 2; i < 6; i++)
        {
            Assert.NotNull(store.TryLoad($@"C:\Books\B{i}.pdf"));
        }
    }

    [Fact]
    public void Prune_with_defaults_keeps_recent_entries_and_removes_unreadable_files()
    {
        var store = NewStore();
        store.Save(@"C:\Books\One.pdf", new ViewPosition(1, 0, 0, 1));
        File.WriteAllText(Path.Combine(_root, "garbage.json"), "not json");

        store.Prune();

        Assert.NotNull(store.TryLoad(@"C:\Books\One.pdf"));
        Assert.False(File.Exists(Path.Combine(_root, "garbage.json")));
    }

    [Fact]
    public void Prune_keeps_a_file_it_cannot_read_because_someone_holds_it()
    {
        var store = NewStore();
        store.Save(@"C:\Books\One.pdf", new ViewPosition(1, 0, 0, 1));
        var file = FileFor(@"C:\Books\One.pdf");

        // A writer (or a virus scanner) holds the file without read sharing, but allows deletion.
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
        {
            store.Prune();
        }

        Assert.True(File.Exists(file));
        Assert.NotNull(store.TryLoad(@"C:\Books\One.pdf"));
    }

    [Fact]
    public void Prune_sweeps_temp_files_older_than_a_day_left_by_a_crash()
    {
        var store = NewStore();
        store.Save(@"C:\Books\One.pdf", new ViewPosition(1, 0, 0, 1));
        var old = Path.Combine(_root, "orphan-old.tmp");
        var recent = Path.Combine(_root, "orphan-recent.tmp");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.SetLastWriteTimeUtc(old, _now.AddDays(-2));
        File.SetLastWriteTimeUtc(recent, _now.AddHours(-1)); // may be a save in progress

        store.Prune();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.NotNull(store.TryLoad(@"C:\Books\One.pdf"));
    }

    [Fact]
    public async Task Save_is_atomic_a_reader_never_sees_a_torn_file()
    {
        var store = NewStore();
        const string Doc = @"C:\Books\One.pdf";
        store.Save(Doc, new ViewPosition(0, 0, 0, 1));
        var file = FileFor(Doc);
        var done = false;
        var torn = 0;
        var missing = 0; // a momentary absence during the swap is tolerated: it is not a torn file
        var failures = new List<Exception>();
        store.Failed += (_, ex) => failures.Add(ex);

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!Volatile.Read(ref done))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();
                    if (!text.StartsWith('{') || !text.EndsWith('}'))
                    {
                        Interlocked.Increment(ref torn);
                    }
                }
                catch (FileNotFoundException)
                {
                    Interlocked.Increment(ref missing);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Windows answers "access denied" (not an IO error) to an open that meets the file while it is being
                    // replaced (delete pending): a moment of the swap, not a torn file.
                }
            }
        })).ToArray();

        for (var i = 1; i <= 500; i++)
        {
            store.Save(Doc, new ViewPosition(i, i * 0.5, i * 1.5, 1 + i / 1000.0));
        }

        Volatile.Write(ref done, true);
        await Task.WhenAll(readers);

        Assert.Equal(0, torn);
        Assert.Empty(failures);
        Assert.Equal(500, store.TryLoad(Doc)!.Value.Position.PageIndex);
    }

    [Fact]
    public void Save_failure_raises_Failed_and_does_not_throw()
    {
        File.WriteAllText(_root, "this is a file where the folder should be");
        var store = NewStore();
        Exception? reported = null;
        store.Failed += (_, ex) => reported = ex;

        store.Save(@"C:\Books\One.pdf", new ViewPosition(1, 0, 0, 1));

        Assert.NotNull(reported);
        Assert.DoesNotContain("One.pdf", reported.Message);
        Assert.Null(store.TryLoad(@"C:\Books\One.pdf"));
    }

    [Fact]
    public void Files_contain_no_document_path_or_text()
    {
        var store = NewStore();
        const string Doc = "C:\\Users\\Zo\u00eb\\Libri\\Segreto Privato.pdf";
        store.Save(Doc, new ViewPosition(5, 10.25, 20.5, 2));

        var file = Assert.Single(Directory.GetFiles(_root));
        var bytes = File.ReadAllBytes(file);
        Assert.All(bytes, b => Assert.True(b < 128));
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("Segreto", text);
        Assert.DoesNotContain("Libri", text);
        Assert.DoesNotContain("C:", text);
        Assert.Equal(Path.Combine(_root, RecoveryStore.DocumentKey(Doc) + ".json"), file);
    }

    [Fact]
    public void Fit_width_flag_round_trips()
    {
        var store = NewStore();
        store.Save(@"C:\Books\Fit.pdf", new ViewPosition(3, 12.5, 306, 1.2), fitWidth: true);
        store.Save(@"C:\Books\Manual.pdf", new ViewPosition(3, 12.5, 306, 1.2), fitWidth: false);

        Assert.True(store.TryLoad(@"C:\Books\Fit.pdf")!.Value.FitWidth);
        Assert.False(store.TryLoad(@"C:\Books\Manual.pdf")!.Value.FitWidth);
        Assert.Contains("\"fit\":true", File.ReadAllText(FileFor(@"C:\Books\Fit.pdf")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_without_the_fit_field_loads_as_not_fit()
    {
        var store = NewStore();
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            FileFor(@"C:\Books\Old.pdf"),
            "{\"v\":1,\"page\":4,\"y\":10,\"x\":306,\"zoom\":1.5,\"saved\":\"2026-10-30T12:00:00.0000000Z\"}");

        var loaded = store.TryLoad(@"C:\Books\Old.pdf");

        Assert.NotNull(loaded);
        Assert.Equal(new ViewPosition(4, 10, 306, 1.5), loaded.Value.Position);
        Assert.False(loaded.Value.FitWidth);
    }

    [Fact]
    public void Save_reports_an_unexpected_failure_instead_of_throwing()
    {
        var store = NewStore();
        Exception? reported = null;
        store.Failed += (_, ex) => reported = ex;

        store.Save("bad\0path.pdf", new ViewPosition(1, 0, 0, 1)); // not a usable path at all

        Assert.NotNull(reported);
        Assert.DoesNotContain("bad", reported.Message, StringComparison.Ordinal);
    }
}
