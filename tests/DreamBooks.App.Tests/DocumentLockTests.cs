using DreamBooks.App.Services;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>One writer per document across processes: a lock file held open without sharing (single writer, FR-84).</summary>
public sealed class DocumentLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dreambooks-lock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void DocumentLock_blocks_a_second_acquire_for_the_same_document_and_not_for_another()
    {
        var a = Path.Combine(_root, "docs", "a.pdf");
        var b = Path.Combine(_root, "docs", "b.pdf");

        using var first = DocumentLock.TryAcquire(_root, a);
        Assert.NotNull(first);
        Assert.Null(DocumentLock.TryAcquire(_root, a));
        Assert.Null(DocumentLock.TryAcquire(_root, a.ToUpperInvariant())); // the key ignores case
        using var other = DocumentLock.TryAcquire(_root, b);
        Assert.NotNull(other);
    }

    [Fact]
    public void Release_allows_reacquiring_and_removes_the_lock_file()
    {
        var a = Path.Combine(_root, "a.pdf");
        var first = DocumentLock.TryAcquire(_root, a);
        Assert.NotNull(first);
        var lockFile = Path.Combine(_root, RecoveryStore.DocumentKey(a), DocumentLock.LockFileName);
        Assert.True(File.Exists(lockFile));

        first.Dispose();
        first.Dispose(); // idempotent

        Assert.False(File.Exists(lockFile));
        using var again = DocumentLock.TryAcquire(_root, a);
        Assert.NotNull(again);
    }

    [Fact]
    public void A_handle_held_by_someone_else_blocks_and_its_release_unblocks()
    {
        // Stands in for another process: the OS releases such a handle when that process dies.
        var a = Path.Combine(_root, "a.pdf");
        var folder = Path.Combine(_root, RecoveryStore.DocumentKey(a));
        Directory.CreateDirectory(folder);
        var held = new FileStream(Path.Combine(folder, DocumentLock.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        Assert.Null(DocumentLock.TryAcquire(_root, a));

        held.Dispose();
        using var mine = DocumentLock.TryAcquire(_root, a);
        Assert.NotNull(mine);
    }

    [Fact]
    public void Lock_does_not_disturb_the_journal_beside_it()
    {
        var a = Path.Combine(_root, "a.pdf");
        var store = new RecoveryStore(_root);
        using var held = DocumentLock.TryAcquire(_root, a);
        Assert.NotNull(held);

        Assert.Empty(store.FindAll()); // a lock alone is not a journal
        store.Delete(a); // deleting a (missing) journal keeps the lock
        Assert.Null(DocumentLock.TryAcquire(_root, a));
    }
}
