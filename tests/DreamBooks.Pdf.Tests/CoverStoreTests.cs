namespace DreamBooks.Pdf.Tests;

public sealed class CoverStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "db-covers-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void Save_then_TryLoad_round_trips_and_a_missing_key_gives_null()
    {
        var store = new CoverStore(_root);
        store.Save(@"C:\a.pdf", [1, 2, 3]);

        Assert.Equal([1, 2, 3], store.TryLoad(@"C:\A.PDF")); // the key ignores the case of the path
        Assert.Null(store.TryLoad(@"C:\other.pdf"));
    }

    [Fact]
    public void Saving_beyond_the_byte_budget_deletes_the_oldest_covers()
    {
        var store = new CoverStore(_root, maxBytes: 250);
        for (var i = 0; i < 4; i++)
        {
            store.Save($@"C:\f{i}.pdf", new byte[100]);
            foreach (var file in Directory.GetFiles(Path.Combine(_root, "covers"), "*.png"))
            {
                // Older saves get older stamps: the order does not depend on the clock's resolution.
                File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(-1));
            }
        }

        Assert.Null(store.TryLoad(@"C:\f0.pdf"));
        Assert.Null(store.TryLoad(@"C:\f1.pdf"));
        Assert.NotNull(store.TryLoad(@"C:\f3.pdf"));
        Assert.True(Directory.GetFiles(Path.Combine(_root, "covers")).Sum(f => new FileInfo(f).Length) <= 250);
    }

    [Fact]
    public void Delete_and_Clear_remove_covers_and_never_throw()
    {
        var store = new CoverStore(_root);
        store.Save(@"C:\a.pdf", [1]);
        store.Save(@"C:\b.pdf", [1]);

        store.Delete(@"C:\a.pdf");
        store.Delete(@"C:\never-saved.pdf");
        Assert.Null(store.TryLoad(@"C:\a.pdf"));
        Assert.NotNull(store.TryLoad(@"C:\b.pdf"));
        store.Clear();
        Assert.Null(store.TryLoad(@"C:\b.pdf"));
        store.Clear(); // an empty or missing folder is fine
    }

    [Fact]
    public void An_unusable_root_never_throws()
    {
        File.WriteAllText(_root, "a file where the folder should be");
        var store = new CoverStore(_root);

        store.Save(@"C:\a.pdf", [1]);
        Assert.Null(store.TryLoad(@"C:\a.pdf"));
        store.Delete(@"C:\a.pdf");
        store.Clear();
    }
}
