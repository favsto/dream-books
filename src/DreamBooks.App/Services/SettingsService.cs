using DreamBooks.App.Resources;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Settings;
using DreamBooks.Pdf;

namespace DreamBooks.App.Services;

/// <summary>
/// The app settings at run time (FR-110): loaded once at startup through <see cref="AppSettingsStore"/> (which never
/// throws: anything unreadable reads as the defaults), held in <see cref="Current"/>, pushed to the tools, the toolbar
/// and the theme, and written <see cref="SaveDelay"/> after the last change (debounced, off the UI thread, one write
/// at a time). A write that fails is reported (<see cref="SaveFailed"/>, <see cref="LastSaveFailed"/>,
/// <see cref="Warning"/>), never thrown; the app goes on with the values in memory. Used on the UI thread, except
/// for <see cref="SaveFailed"/>.
/// </summary>
internal sealed class SettingsService
{
    /// <summary>The wait after the last change before the file is written.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly AppSettingsStore _store;
    private readonly IDelayScheduler _scheduler;
    private readonly List<Action<AppSettings, AppSettings>> _listeners = [];
    private AppSettings _current = AppSettings.Default;
    private IDisposable? _pendingSave;
    private Task _writes = Task.CompletedTask;
    private volatile bool _lastSaveFailed; // written by the write chain
    private bool _applying;

    public SettingsService(AppSettingsStore store, IDelayScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scheduler);
        _store = store;
        _scheduler = scheduler;
    }

    /// <summary>Raised on a thread-pool thread after a write failed. Carries nothing about the cause (no path).</summary>
    public event EventHandler? SaveFailed;

    public AppSettings Current => _current;

    /// <summary>True when the last write failed (cleared by the next successful one).</summary>
    public bool LastSaveFailed => _lastSaveFailed;

    /// <summary>A one-line warning for the user while the settings cannot be written; null otherwise.</summary>
    public string? Warning => _lastSaveFailed ? Strings.WarnSettingsNotSaved : null;

    /// <summary>Reads the file (small, read once at startup). Never throws.</summary>
    public void Load() => _current = _store.Load();

    /// <summary>Changes the settings: listeners hear of it at once, the file is written after <see cref="SaveDelay"/>.</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var previous = _current;
        var next = change(previous);
        if (next == previous)
        {
            return;
        }

        _current = next;
        foreach (var listener in _listeners.ToArray())
        {
            listener(previous, next);
        }

        _pendingSave?.Dispose();
        _pendingSave = _scheduler.Schedule(SaveDelay, () =>
        {
            _pendingSave = null;
            QueueWrite(_current);
        });
    }

    /// <summary>The tools take the remembered values now, and every later change of theirs is remembered.</summary>
    public void AttachTools(ToolsViewModel tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        Applying(() => tools.Apply(_current));
        tools.RememberedChanged += (_, _) =>
        {
            if (!_applying)
            {
                Update(tools.CaptureInto);
            }
        };
        _listeners.Add((_, next) => Applying(() => tools.Apply(next)));
    }

    /// <summary>"Toolbar fixed" reaches the toolbar now and on every change.</summary>
    public void AttachToolbar(ToolbarViewModel toolbar)
    {
        ArgumentNullException.ThrowIfNull(toolbar);
        toolbar.IsToolbarFixed = _current.ToolbarFixed;
        _listeners.Add((_, next) => toolbar.IsToolbarFixed = next.ToolbarFixed);
    }

    /// <summary>The theme mode and accent reach <paramref name="apply"/> now and whenever either changes.</summary>
    public void AttachTheme(Action<AppSettings> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        apply(_current);
        _listeners.Add((previous, next) =>
        {
            if (previous.ThemeMode != next.ThemeMode || previous.AccentOverride != next.AccentOverride)
            {
                apply(next);
            }
        });
    }

    /// <summary>Writes a pending change now (closing the app) and waits for the writes. Never throws.</summary>
    public Task FlushAsync()
    {
        if (_pendingSave is { } pending)
        {
            pending.Dispose();
            _pendingSave = null;
            QueueWrite(_current);
        }

        return WhenWrittenAsync();
    }

    /// <summary>
    /// <see cref="FlushAsync"/> for the app's exit, waiting at most <paramref name="timeout"/> (the writes never need
    /// the UI thread, so blocking it cannot deadlock). Never throws.
    /// </summary>
    public void Flush(TimeSpan timeout) => FlushAsync().Wait(timeout);

    /// <summary>Completes when the writes queued so far are done (never faults).</summary>
    public Task WhenWrittenAsync() => _writes;

    private void Applying(Action action)
    {
        var outer = _applying;
        _applying = true;
        try
        {
            action();
        }
        finally
        {
            _applying = outer;
        }
    }

    private void QueueWrite(AppSettings settings) => _writes = WriteAfterAsync(_writes, settings);

    /// <summary>One write after the previous one, on the thread pool; the outcome is reported back on the caller's context.</summary>
    private async Task WriteAfterAsync(Task previous, AppSettings settings)
    {
        await previous.ConfigureAwait(false); // never faults
        var failed = await Task.Run(() => Write(settings)).ConfigureAwait(false);
        Report(failed);
    }

    private bool Write(AppSettings settings)
    {
        var failed = false;
        void OnFailed(object? sender, Exception e) => failed = true;
        _store.Failed += OnFailed;
        try
        {
            _store.Save(settings); // never throws: failures come through Failed
        }
#pragma warning disable CA1031 // A last line of defence: remembering the settings must never fault the write chain.
        catch (Exception)
#pragma warning restore CA1031
        {
            failed = true;
        }
        finally
        {
            _store.Failed -= OnFailed;
        }

        return failed;
    }

    private void Report(bool failed)
    {
        _lastSaveFailed = failed;
        if (!failed)
        {
            return;
        }

        try
        {
            SaveFailed?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // A failing listener must not fault the write chain; the warning stays in LastSaveFailed.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
