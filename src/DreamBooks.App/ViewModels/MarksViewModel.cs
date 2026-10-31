using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.Core.Marks;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The marks of the open document for the UI (FR-101..FR-103): the list (rebuilt from the <see cref="MarkIndex"/>
/// at most once per <see cref="RefreshInterval"/>, so a background scan cannot flood the UI), whether the scan is
/// still running, and previous/next mark and go-to-mark, which jump through the viewport and record Back history.
/// </summary>
/// <remarks>
/// <para>Index changes and scan progress may arrive on any thread: they only set a flag, and the rebuild is
/// scheduled on the UI thread (the synchronization context current when this was created, or inline when there is
/// none). While a scan runs the state is re-read every <see cref="RefreshInterval"/>, so the end of the scan is seen
/// without the scanner calling back.</para>
/// <para>Previous/next start from the viewport's position (the page of the top edge and the offset in it, page
/// points). Right after a jump to a mark, while the view has not moved, they start from that mark instead, because
/// centring a mark puts the top edge above it. A mark within 1 pt of the start counts as "here". At either end there
/// is no jump and <see cref="StatusText"/> says so for <see cref="StatusDuration"/>. The commands do not depend on a
/// save running.</para>
/// </remarks>
public sealed class MarksViewModel : ObservableObject
{
    /// <summary>The list is rebuilt at most once per interval.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long "no other mark" stays visible.</summary>
    public static readonly TimeSpan StatusDuration = TimeSpan.FromSeconds(2);

    private const double _hereToleranceInPoints = 1.0;

    private readonly DocumentViewportViewModel _viewport;
    private readonly IDelayScheduler _scheduler;
    private readonly SynchronizationContext? _context;
    private readonly int _uiThreadId;
    private readonly object _gate = new();
    private MarkIndex? _index;
    private MarkScanner? _scanner;
    private IReadOnlyList<MarkItem> _items = [];
    private bool _isScanning;
    private string? _statusText;
    private IDisposable? _pendingRefresh;
    private IDisposable? _pendingStatusClear;
    private int _dirty;
    private (Mark Mark, Core.Layout.ViewPosition Position)? _lastJump;

    public MarksViewModel(DocumentViewportViewModel viewport, IDelayScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(scheduler);
        _viewport = viewport;
        _scheduler = scheduler;
        _context = SynchronizationContext.Current;
        _uiThreadId = Environment.CurrentManagedThreadId;
        NextCommand = new RelayCommand(() => Step(forward: true), () => Count > 0);
        PreviousCommand = new RelayCommand(() => Step(forward: false), () => Count > 0);
        GoToMarkCommand = new RelayCommand<MarkItem>(GoToMark, item => item is not null);
    }

    /// <summary>One row per mark, in document order.</summary>
    public IReadOnlyList<MarkItem> Items
    {
        get => _items;
        private set
        {
            var hadItems = _items.Count > 0;
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(Count));
                OnPropertyChanged(nameof(IsEmpty));
                if (hadItems != value.Count > 0)
                {
                    NextCommand.NotifyCanExecuteChanged();
                    PreviousCommand.NotifyCanExecuteChanged();
                }
            }
        }
    }

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    /// <summary>True while the background scan has pages left (its marks are still arriving).</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    /// <summary>The empty list's text: what counts as a mark.</summary>
    public string EmptyText => Strings.MarksEmpty;

    /// <summary>A short message after previous/next found nothing ("Nessun altro segno"); null otherwise.</summary>
    public string? StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Goes to the first mark after the current position (FR-102).</summary>
    public IRelayCommand NextCommand { get; }

    /// <summary>Goes to the last mark before the current position (FR-102).</summary>
    public IRelayCommand PreviousCommand { get; }

    /// <summary>Goes to a row's mark (FR-103).</summary>
    public IRelayCommand<MarkItem> GoToMarkCommand { get; }

    /// <summary>The index of the document now shown, or none; the list is rebuilt from it at once.</summary>
    internal void Attach(MarkIndex? index)
    {
        if (_index is { } previous)
        {
            previous.Changed -= OnIndexChanged;
        }

        _index = index;
        _lastJump = null;
        if (index is not null)
        {
            index.Changed += OnIndexChanged;
        }

        lock (_gate)
        {
            _pendingRefresh?.Dispose();
            _pendingRefresh = null;
        }

        Interlocked.Exchange(ref _dirty, 0);
        Rebuild();
    }

    /// <summary>The scan filling the index now (it is replaced when the renderer is), or none.</summary>
    internal void SetScanner(MarkScanner? scanner)
    {
        if (_scanner is { } previous)
        {
            previous.Progress -= OnScanProgress;
        }

        _scanner = scanner;
        if (scanner is not null)
        {
            scanner.Progress += OnScanProgress;
        }

        IsScanning = scanner is { IsCompleted: false };
        if (IsScanning)
        {
            ScheduleRefresh();
        }
    }

    private void OnIndexChanged(object? sender, MarkIndexChangedEventArgs e) => Signal(changed: true);

    private void OnScanProgress(object? sender, EventArgs e) => Signal(changed: false);

    // Any thread.
    private void Signal(bool changed)
    {
        if (changed)
        {
            Interlocked.Exchange(ref _dirty, 1);
        }

        if (Environment.CurrentManagedThreadId == _uiThreadId || _context is null)
        {
            ScheduleRefresh();
        }
        else
        {
            _context.Post(_ => ScheduleRefresh(), null);
        }
    }

    private void ScheduleRefresh()
    {
        lock (_gate)
        {
            _pendingRefresh ??= _scheduler.Schedule(RefreshInterval, Refresh);
        }
    }

    private void Refresh()
    {
        lock (_gate)
        {
            _pendingRefresh = null;
        }

        if (Interlocked.Exchange(ref _dirty, 0) == 1)
        {
            Rebuild();
        }

        IsScanning = _scanner is { IsCompleted: false };
        if (IsScanning)
        {
            ScheduleRefresh(); // see the end of the scan, also while it is paused
        }
    }

    private void Rebuild()
    {
        var marks = _index?.AllMarks() ?? [];
        Items = marks
            .Select(m => new MarkItem(m, string.Format(CultureInfo.CurrentCulture, Strings.MarkPageLabelFormat, m.PageIndex + 1)))
            .ToArray();
    }

    private void Step(bool forward)
    {
        if (_index is not { } index || Count == 0 || _viewport.Layout is null)
        {
            return;
        }

        var (page, y) = StartingPoint();
        var found = forward
            ? index.TryNext(page, y + _hereToleranceInPoints, out var mark)
            : index.TryPrevious(page, y, out mark); // TryPrevious applies the same tolerance itself
        if (!found)
        {
            ShowStatus(Strings.MarksNoOther);
            return;
        }

        JumpTo(mark);
    }

    private (int Page, double YPt) StartingPoint()
    {
        var position = _viewport.Position;
        return _lastJump is { } last && last.Position == position
            ? (last.Mark.PageIndex, last.Mark.TopPt)
            : (position.PageIndex, position.YPt);
    }

    private void GoToMark(MarkItem? item)
    {
        if (item is not null && _viewport.Layout is not null)
        {
            JumpTo(item.Mark);
        }
    }

    private void JumpTo(Mark mark)
    {
        _viewport.ScrollToMark(mark, recordHistory: true);
        _lastJump = (mark, _viewport.Position);
    }

    private void ShowStatus(string text)
    {
        _pendingStatusClear?.Dispose();
        StatusText = text;
        _pendingStatusClear = _scheduler.Schedule(StatusDuration, () =>
        {
            _pendingStatusClear = null;
            StatusText = null;
        });
    }
}
