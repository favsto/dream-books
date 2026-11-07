using DreamBooks.Core.Documents;

namespace DreamBooks.Pdf.Tests;

/// <summary>The same file under another spelling is still the same file (an export must never overwrite its source).</summary>
public sealed class FileIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DreamBooks.Pdf.Tests", "identity-" + Guid.NewGuid().ToString("N"));

    public FileIdentityTests() => Directory.CreateDirectory(_dir);

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

    // A path spelled with the extended-length prefix (two backslashes, a question mark, a backslash): another string, one file.
    private static string Extended(string path) => new string((char)92, 2) + "?" + (char)92 + Path.GetFullPath(path);

    [Fact]
    public void A_file_is_the_same_as_itself_under_another_spelling_and_not_the_same_as_another_file()
    {
        var a = Path.Combine(_dir, "a.pdf");
        var b = Path.Combine(_dir, "b.pdf");
        TestPdfFactory.Write(a, (612, 792));
        File.Copy(a, b);

        Assert.True(FileIdentity.AreSame(a, Extended(a)));
        Assert.True(FileIdentity.AreSame(a, Path.Combine(_dir, ".", "a.pdf")));
        Assert.False(FileIdentity.AreSame(a, b)); // equal bytes, another file
    }

    [Fact]
    public void A_missing_file_is_never_the_same()
    {
        var a = Path.Combine(_dir, "a.pdf");
        TestPdfFactory.Write(a, (612, 792));

        Assert.False(FileIdentity.AreSame(a, Path.Combine(_dir, "missing.pdf")));
        Assert.False(FileIdentity.AreSame(Path.Combine(_dir, "missing.pdf"), a));
    }

    [Fact]
    public async Task The_flattened_export_refuses_the_source_under_another_spelling_and_writes_nothing()
    {
        var source = Path.Combine(_dir, "source.pdf");
        TestPdfFactory.Write(source, (612, 792));
        var before = File.ReadAllBytes(source);

        await Assert.ThrowsAsync<ExportDestinationIsSourceException>(
            () => new FlattenedPdfExporter().ExportAsync(source, Extended(source), [new PageChange(0, [], [])], CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Single(Directory.GetFiles(_dir)); // no temporary copy either
    }
}
