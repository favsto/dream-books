namespace DreamBooks.Pdf.Tests;

public sealed class PdfDocumentInfoTests : IDisposable
{
    private readonly string _dir;

    public PdfDocumentInfoTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DreamBooks.Pdf.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

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
    public async Task Plain_pdf_is_not_encrypted_and_has_no_signatures()
    {
        string path = Path.Combine(_dir, "plain.pdf");
        TestPdfFactory.Write(path, (595, 842));

        await using var renderer = await PdfRenderer.OpenAsync(path);

        Assert.Equal(new PdfDocumentInfo(IsEncrypted: false, HasRestrictions: false, SignatureCount: 0), renderer.Info);
        Assert.True(renderer.Info.CanBeRewritten);
    }

    [Theory]
    [InlineData(false, false, 0, true)]
    [InlineData(true, false, 0, false)]
    [InlineData(false, true, 0, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(false, false, 1, true)]
    public void Decision_function_refuses_encrypted_or_restricted(
        bool encrypted, bool restricted, int signatures, bool expected)
    {
        var info = new PdfDocumentInfo(encrypted, restricted, signatures);

        Assert.Equal(expected, info.CanBeRewritten);
    }

    [Fact]
    public async Task Encrypted_fixture_is_reported_as_encrypted_and_restricted()
    {
        string path = Path.Combine(_dir, "encrypted.pdf");
        TestPdfFactory.WriteEncryptedRestricted(path, (595, 842));

        await using var renderer = await PdfRenderer.OpenAsync(path);

        Assert.True(renderer.Info.IsEncrypted);
        Assert.True(renderer.Info.HasRestrictions);
        Assert.False(renderer.Info.CanBeRewritten);
        Assert.Equal(0, renderer.Info.SignatureCount);
    }
}
