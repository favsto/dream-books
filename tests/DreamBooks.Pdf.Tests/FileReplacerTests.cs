using System.Security.Cryptography;

namespace DreamBooks.Pdf.Tests;

public sealed class FileReplacerTests : IDisposable
{
    private readonly string _dir;

    public FileReplacerTests()
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
    public void Replace_swaps_the_target_and_removes_the_temp() // NFR-10
    {
        string target = WriteFile("book.pdf", "original");
        string temp = WriteFile("book.pdf.0123abcd.drbk-tmp", "saved");

        FileReplacer.Replace(temp, target);

        Assert.Equal("saved", File.ReadAllText(target));
        Assert.False(File.Exists(temp));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Replace_to_a_new_path_moves_the_temp() // FR-80, NFR-10
    {
        string target = Path.Combine(_dir, "new.pdf");
        string temp = WriteFile("new.pdf.0123abcd.drbk-tmp", "saved");

        FileReplacer.Replace(temp, target);

        Assert.Equal("saved", File.ReadAllText(target));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void Replace_retries_when_the_target_is_briefly_locked_and_then_succeeds() // NFR-10
    {
        string target = WriteFile("book.pdf", "original");
        string temp = WriteFile("book.pdf.0123abcd.drbk-tmp", "saved");
        var holder = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);

        int attempts = FileReplacer.ReplaceCore(temp, target, attempts: 5, TimeSpan.Zero, attempt =>
        {
            if (attempt == 3)
            {
                holder.Dispose(); // the antivirus lets go before the third attempt
            }
        });

        holder.Dispose();
        Assert.Equal(3, attempts);
        Assert.Equal("saved", File.ReadAllText(target));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public async Task Replace_retries_with_a_real_delay_while_a_lock_is_released() // NFR-10
    {
        string target = WriteFile("book.pdf", "original");
        string temp = WriteFile("book.pdf.0123abcd.drbk-tmp", "saved");
        var holder = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task release = Task.Run(async () =>
        {
            await Task.Delay(100);
            holder.Dispose();
        });

        FileReplacer.Replace(temp, target, attempts: 10, delay: TimeSpan.FromMilliseconds(50));

        await release;
        Assert.Equal("saved", File.ReadAllText(target));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void Replace_failure_leaves_the_target_byte_identical_and_keeps_the_temp() // NFR-10
    {
        string target = WriteFile("book.pdf", "original");
        string temp = WriteFile("book.pdf.0123abcd.drbk-tmp", "saved");
        byte[] before = SHA256.HashData(File.ReadAllBytes(target));

        ReplaceFailedException ex;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ex = Assert.Throws<ReplaceFailedException>(() =>
                FileReplacer.Replace(temp, target, attempts: 3, delay: TimeSpan.Zero));
        }

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(target)));
        Assert.True(File.Exists(temp));
        Assert.Equal("saved", File.ReadAllText(temp));
        Assert.Equal(temp, ex.TempPath);
        Assert.True(ex.TargetExists);
        Assert.True(ex.TempExists);
        Assert.DoesNotContain(_dir, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Replace_failure_after_the_target_disappeared_reports_it_missing() // NFR-10
    {
        string target = WriteFile("book.pdf", "original");
        string temp = WriteFile("book.pdf.0123abcd.drbk-tmp", "saved");
        var targetHolder = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileStream? tempHolder = null;

        ReplaceFailedException ex;
        try
        {
            ex = Assert.Throws<ReplaceFailedException>(() => FileReplacer.ReplaceCore(temp, target, 3, TimeSpan.Zero, attempt =>
            {
                if (attempt == 2)
                {
                    // The target vanishes, and the copy is now locked so it cannot be moved in.
                    targetHolder.Dispose();
                    File.Delete(target);
                    tempHolder = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            }));
        }
        finally
        {
            targetHolder.Dispose();
            tempHolder?.Dispose();
        }

        Assert.False(ex.TargetExists);
        Assert.True(ex.TempExists);
        Assert.False(File.Exists(target));
        Assert.Equal("saved", File.ReadAllText(ex.TempPath));
    }

    [Fact]
    public void Replace_with_a_missing_temp_throws_and_leaves_the_target() // NFR-10
    {
        string target = WriteFile("book.pdf", "original");

        Assert.Throws<FileNotFoundException>(() =>
            FileReplacer.Replace(Path.Combine(_dir, "gone.drbk-tmp"), target, delay: TimeSpan.Zero));

        Assert.Equal("original", File.ReadAllText(target));
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
