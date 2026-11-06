using DreamBooks.Core.Recent;

namespace DreamBooks.Core.Tests.Recent;

public class RecentFilesListTests
{
    private static readonly DateTime _t0 = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private static RecentFile File(string path, int minutes = 0, int page = 0, int pages = 10) =>
        new(path, _t0.AddMinutes(minutes), page, pages);

    [Fact]
    public void Touch_puts_a_file_first_and_replaces_the_same_path()
    {
        var list = new RecentFilesList();
        list.Touch(File(@"C:\a.pdf", 0, page: 1));
        list.Touch(File(@"C:\b.pdf", 1));
        list.Touch(File(@"C:\a.pdf", 2, page: 5));

        Assert.Equal([@"C:\a.pdf", @"C:\b.pdf"], list.Items.Select(f => f.Path));
        Assert.Equal(5, list.Items[0].Page);
    }

    [Fact]
    public void Touch_ignores_case_of_the_path()
    {
        var list = new RecentFilesList();
        list.Touch(File(@"C:\Books\A.pdf"));
        list.Touch(File(@"c:\books\a.PDF", 1));

        Assert.Single(list.Items);
        Assert.Equal(@"c:\books\a.PDF", list.Items[0].Path);
    }

    [Fact]
    public void The_list_keeps_the_newest_20()
    {
        var list = new RecentFilesList();
        for (var i = 0; i < 25; i++)
        {
            list.Touch(File($@"C:\f{i}.pdf", i));
        }

        Assert.Equal(RecentFilesList.MaxCount, list.Items.Count);
        Assert.Equal(@"C:\f24.pdf", list.Items[0].Path);
        Assert.Equal(@"C:\f5.pdf", list.Items[^1].Path);
    }

    [Fact]
    public void Constructor_trims_to_20_and_drops_blank_paths_and_duplicates()
    {
        var items = Enumerable.Range(0, 30).Select(i => File($@"C:\f{i}.pdf", -i)).ToList();
        items.Insert(3, File("  "));
        items.Insert(4, File(@"C:\F0.pdf", -100));

        var list = new RecentFilesList(items);

        Assert.Equal(20, list.Items.Count);
        Assert.DoesNotContain(list.Items, f => string.IsNullOrWhiteSpace(f.Path));
        Assert.Single(list.Items, f => f.Path.Equals(@"C:\f0.pdf", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\f0.pdf", list.Items[0].Path); // the given order is kept (newest first)
    }

    [Fact]
    public void Remove_returns_false_for_an_unknown_path()
    {
        var list = new RecentFilesList([File(@"C:\a.pdf")]);

        Assert.False(list.Remove(@"C:\zzz.pdf"));
        Assert.True(list.Remove(@"c:\A.PDF"));
        Assert.Empty(list.Items);
    }

    [Fact]
    public void UpdateProgress_changes_page_without_reordering()
    {
        var list = new RecentFilesList([File(@"C:\a.pdf", 2), File(@"C:\b.pdf", 1)]);

        list.UpdateProgress(@"C:\b.pdf", 7, 40);

        Assert.Equal([@"C:\a.pdf", @"C:\b.pdf"], list.Items.Select(f => f.Path));
        Assert.Equal((7, 40), (list.Items[1].Page, list.Items[1].PageCount));
        list.UpdateProgress(@"C:\unknown.pdf", 1, 1); // nothing happens
        Assert.Equal(2, list.Items.Count);
    }

    [Theory]
    [InlineData(-3, 10, 0, 10)]
    [InlineData(50, 10, 9, 10)]
    [InlineData(4, 0, 0, 1)]
    [InlineData(4, -2, 0, 1)]
    public void Negative_or_invalid_page_counts_are_clamped(int page, int pages, int expectedPage, int expectedPages)
    {
        var list = new RecentFilesList();
        list.Touch(File(@"C:\a.pdf", 0, page, pages));

        Assert.Equal((expectedPage, expectedPages), (list.Items[0].Page, list.Items[0].PageCount));
    }
}
