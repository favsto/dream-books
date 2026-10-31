using System.Security.Cryptography;
using DreamBooks.Pdf.Interop;

namespace DreamBooks.Pdf.Tests;

public sealed class PdfFullRewriteTests : IDisposable
{
    private const string _sampleText = "Dream Books (rewrite) test";

    private readonly string _dir;

    public PdfFullRewriteTests()
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

    private string WriteSample()
    {
        string path = Path.Combine(_dir, "source.pdf");
        TestPdfFactory.WriteWithText(path, _sampleText, (595, 842), (842, 595), (300, 400));
        return path;
    }

    [Fact]
    public async Task Rewrite_of_synthetic_pdf_keeps_page_count_sizes_and_text() // FR-82, NFR-10
    {
        string source = WriteSample();
        string target = Path.Combine(_dir, "rewritten.pdf");

        await PdfFullRewrite.RewriteAsync(source, target);

        await using (var renderer = await PdfRenderer.OpenAsync(target))
        {
            Assert.Equal(3, renderer.PageCount);
            Assert.Equal(new PageSize(595, 842), renderer.GetPageSize(0));
            Assert.Equal(new PageSize(842, 595), renderer.GetPageSize(1));
            Assert.Equal(new PageSize(300, 400), renderer.GetPageSize(2));
        }

        string[] texts = await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(target);
                var result = new string[document.PageCount];
                for (int i = 0; i < result.Length; i++)
                {
                    nint page = document.LoadPage(i);
                    try
                    {
                        result[i] = PdfFidelity.ReadPageText(page);
                    }
                    finally
                    {
                        document.ClosePage(page);
                    }
                }

                return result;
            },
            CancellationToken.None);

        Assert.All(texts, text => Assert.Equal(_sampleText, text.Trim()));

        FidelityReport report = await PdfiumThread.RunAsync(
            () => PdfFidelity.Compare(source, target), CancellationToken.None);
        Assert.True(report.IsFaithful, string.Join("; ", report.Differences));
    }

    [Fact]
    public async Task Rewrite_does_not_modify_the_source_file() // NFR-10
    {
        string source = WriteSample();
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(source));

        await PdfFullRewrite.RewriteAsync(source, Path.Combine(_dir, "rewritten.pdf"));

        byte[] after = SHA256.HashData(await File.ReadAllBytesAsync(source));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Rewrite_to_a_path_in_a_missing_folder_throws_and_leaves_no_file()
    {
        string source = WriteSample();
        string target = Path.Combine(_dir, "missing", "rewritten.pdf");

        await Assert.ThrowsAnyAsync<IOException>(() => PdfFullRewrite.RewriteAsync(source, target));

        Assert.False(File.Exists(target));
        Assert.False(Directory.Exists(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public async Task Rewrite_that_fails_mid_write_surfaces_the_write_error_and_leaves_no_file() // NFR-10
    {
        string source = WriteSample();
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(source));
        string target = Path.Combine(_dir, "rewritten.pdf");
        FailingStream? failing = null;

        var ex = await Assert.ThrowsAsync<IOException>(() => PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(source);
                document.SaveFullRewrite(target, inner => failing = new FailingStream(inner, failAfterBytes: 200));
            },
            CancellationToken.None));

        // The write error surfaces, not the one thrown later while disposing the stream.
        Assert.IsType<FailingStream.WriteFailure>(ex.InnerException);
        Assert.NotNull(failing);
        Assert.True(failing.DisposeAttempted);
        Assert.False(File.Exists(target));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }

    [Fact]
    public async Task Rewrite_of_garbage_file_throws_PdfOpenException_InvalidFormat() // NFR-11
    {
        string source = Path.Combine(_dir, "garbage.pdf");
        TestPdfFactory.WriteGarbage(source);
        string target = Path.Combine(_dir, "rewritten.pdf");

        var ex = await Assert.ThrowsAsync<PdfOpenException>(() => PdfFullRewrite.RewriteAsync(source, target));

        Assert.Equal(PdfOpenError.InvalidFormat, ex.Error);
        Assert.False(File.Exists(target));
    }

    /// <summary>
    /// Simulates a disk filling up: writes pass through until the budget is spent, then throw;
    /// once failed, flushing and disposing throw too (as a FileStream flushing its buffer would),
    /// after releasing the inner stream so the file can still be deleted.
    /// </summary>
    private sealed class FailingStream(Stream inner, int failAfterBytes) : Stream
    {
        private int _remaining = failAfterBytes;
        private bool _failed;

        public bool DisposeAttempted { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_failed || buffer.Length > _remaining)
            {
                _failed = true;
                throw new WriteFailure();
            }

            _remaining -= buffer.Length;
            inner.Write(buffer);
        }

        public override void Flush()
        {
            if (_failed)
            {
                throw new IOException("Simulated flush failure.");
            }

            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !DisposeAttempted)
            {
                DisposeAttempted = true;
                inner.Dispose();
                if (_failed)
                {
                    throw new IOException("Simulated flush failure on dispose.");
                }
            }

            base.Dispose(disposing);
        }

        public sealed class WriteFailure() : IOException("Simulated disk full.");
    }
}
