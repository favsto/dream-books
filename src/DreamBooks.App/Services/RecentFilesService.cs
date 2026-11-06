using System.IO;
using DreamBooks.Core.Recent;
using DreamBooks.Pdf;

namespace DreamBooks.App.Services;

/// <summary>
/// The recently opened documents (FR-03) for the start screen and the menu: the list in memory, saved half a second after
/// the last change (like the settings) and when the app exits, and the check of which files still exist. UI thread only;
/// the file writes and the existence checks run on the thread pool.
/// </summary>
internal sealed class RecentFilesService
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly RecentFilesStore _store;
    private readonly Func<string, bool> _fileExists;
    private readonly IDelayScheduler _scheduler;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, bool> _exists = new(StringComparer.OrdinalIgnoreCase);
    private RecentFilesList _list = new();
    private IReadOnlyList<RecentEntry> _entries = [];
    private IDisposable? _pendingSave;
    private Task _writes = Task.CompletedTask;

    public RecentFilesService(RecentFilesStore store, Func<string, bool> fileExists, IDelayScheduler scheduler, Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(scheduler);
        _store = store;
        _fileExists = fileExists;
        _scheduler = scheduler;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RecentEntry> Entries => _entries;

    /// <summary>The entries, their progress or their availability changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the saved list (one small file; never throws).</summary>
    public void Load()
    {
        _list = _store.Load();
        Rebuild();
    }

    /// <summary>A document was opened: it goes first, with where the reader is now.</summary>
    public void Opened(string path, int page, int pageCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _list.Touch(new RecentFile(path, _utcNow(), page, pageCount));
        Publish();
    }

    /// <summary>Where the reader stopped (closing the document, leaving the app). The order does not change.</summary>
    public void UpdateProgress(string path, int page, int pageCount)
    {
        _list.UpdateProgress(path, page, pageCount);
        Publish();
    }

    public void Remove(string path)
    {
        if (_list.Remove(path))
        {
            _exists.Remove(path);
            Publish();
        }
    }

    /// <summary>
    /// Checks on the thread pool which files are still there, then updates <see cref="Entries"/> (on the caller's
    /// context). A check that throws (a network drive that is gone) counts as missing; a cancelled one changes nothing.
    /// </summary>
    public async Task RefreshAvailabilityAsync(CancellationToken ct)
    {
        var paths = _list.Items.Select(f => f.Path).ToList();
        List<(string Path, bool Exists)> results;
        try
        {
            results = await Task.Run(() => paths.Select(p => (p, Check(p))).ToList(), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        foreach (var (path, exists) in results)
        {
            _exists[path] = exists;
        }

        Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Completes when the writes queued so far are done (never faults).</summary>
    public Task WhenWrittenAsync() => _writes;

    /// <summary>Writes a pending change now (leaving the app) and waits at most <paramref name="timeout"/>. Never throws.</summary>
    public void Flush(TimeSpan timeout)
    {
        if (_pendingSave is { } pending)
        {
            pending.Dispose();
            _pendingSave = null;
            QueueWrite();
        }

        _writes.Wait(timeout);
    }

    private bool Check(string path)
    {
        try
        {
            return _fileExists(path);
        }
#pragma warning disable CA1031 // An unreachable file (a network drive that is gone) is a missing file, whatever the reason.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private void Publish()
    {
        Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
        _pendingSave?.Dispose();
        _pendingSave = _scheduler.Schedule(SaveDelay, () =>
        {
            _pendingSave = null;
            QueueWrite();
        });
    }

    private void QueueWrite()
    {
        var snapshot = new RecentFilesList(_list.Items);
        _writes = _writes.ContinueWith(_ => _store.Save(snapshot), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void Rebuild() =>
        _entries = _list.Items
            .Select(f => new RecentEntry(
                f.Path,
                Path.GetFileName(f.Path),
                f.LastOpenedUtc,
                f.Page,
                f.PageCount,
                _exists.TryGetValue(f.Path, out var exists) ? exists : null))
            .ToList();
}
