using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Marks;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The marks of the open document for the UI (FR-101..FR-103): the list (brought in step with the
/// <see cref="MarkIndex"/> at most once per <see cref="RefreshInterval"/>, so a background scan cannot flood the UI),
/// whether the scan is still running, and previous/next mark and go-to-mark, which jump through the viewport and
/// record Back history.
/// </summary>
/// <remarks>
/// <para><see cref="Items"/> is one collection for the life of the view model, updated in place: a refresh inserts
/// and removes only the rows whose mark changed and keeps the very same <see cref="MarkItem"/> instance for every
/// unchanged mark, so a list bound to it keeps its containers, selection and scroll position.</para>
/// <para>Index changes and scan progress may arrive on any thread: they set a flag and schedule one refresh through
/// the <see cref="IDelayScheduler"/>, which accepts any thread and runs it on the UI thread (no synchronization context
/// is needed, so the list cannot silently stop updating). While a scan runs the state is re-read every
/// <see cref="RefreshInterval"/>, so the end of the scan is seen without the scanner calling back.</para>
/// <para>Previous/next compare where each mark would be shown (<see cref="DocumentViewportViewModel.TargetPositionFor"/>)
/// with where the view is now. A mark whose target lies within <see cref="HereBandFraction"/> of the window height of
/// the current position (at least 1 pt) is "here" and skipped: the mark just shown, after Back, after a resize or a
/// zoom change, two marks clamped to the same position at an end of the document, and the left and right lines of one
/// passage, which one jump shows together. Next goes to the first mark whose target is further down, Previous to the
/// last one further up. With nothing there: while the scan still has unscanned pages in that direction nothing is
/// said (marks may still arrive); otherwise <see cref="StatusText"/> says "Nessun altro segno" for
/// <see cref="StatusDuration"/>. The commands do not depend on a save running.</para>
/// </remarks>
public sealed class MarksViewModel : ObservableObject
{
    /// <summary>The list is brought in step with the index at most once per interval.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long "no other mark" stays visible.</summary>
    public static readonly TimeSpan StatusDuration = TimeSpan.FromSeconds(2);

    /// <summary>A mark whose target is closer than this fraction of the window height to the view counts as "here".</summary>
    public const double HereBandFraction = 0.15;

    private const double _minimumBandInPoints = 1.0;

    private readonly DocumentViewportViewModel _viewport;
    private readonly IDelayScheduler _scheduler;
    private readonly object _gate = new();
    private readonly ObservableCollection<MarkItem> _items = [];
    private MarkIndex? _index;
    private MarkScanner? _scanner;
    private IReadOnlyList<Mark> _marks = [];
    private bool _isScanning;
    private string? _statusText;
    private IDisposable? _pendingRefresh;
    private IDisposable? _pendingStatusClear;
    private int _dirty;

    /// <param name="viewport">The view the commands move.</param>
    /// <param name="scheduler">
    /// The refresh throttle and the status timer: called from any thread (the scan's too), it runs its actions on the UI
    /// thread, as <see cref="IDelayScheduler"/> requires (the app's <see cref="DispatcherDelayScheduler"/> marshals to
    /// the dispatcher it was created on), so the list keeps following the index whatever thread changed it.
    /// </param>
    public MarksViewModel(DocumentViewportViewModel viewport, IDelayScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(scheduler);
        _viewport = viewport;
        _scheduler = scheduler;
        Items = new ReadOnlyObservableCollection<MarkItem>(_items);
        NextCommand = new RelayCommand(() => Step(forward: true), CanStep);
        PreviousCommand = new RelayCommand(() => Step(forward: false), CanStep);
        GoToMarkCommand = new RelayCommand<MarkItem>(GoToMark, item => item is not null);
    }

    /// <summary>One row per mark, in document order; updated in place (see the remarks).</summary>
    public ReadOnlyObservableCollection<MarkItem> Items { get; }

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    /// <summary>True while the background scan has pages left (its marks are still arriving).</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                NotifyCommands();
            }
        }
    }

    /// <summary>The empty list's text: what counts as a mark.</summary>
    public string EmptyText => Strings.MarksEmpty;

    /// <summary>A short message after previous/next found nothing ("Nessun altro segno"); null otherwise.</summary>
    public string? StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Goes to the next mark further down (FR-102); enabled with marks or while the scan runs.</summary>
    public IRelayCommand NextCommand { get; }

    /// <summary>Goes to the previous mark further up (FR-102); enabled with marks or while the scan runs.</summary>
    public IRelayCommand PreviousCommand { get; }

    /// <summary>Goes to a row's mark (FR-103); no history entry when it is already shown.</summary>
    public IRelayCommand<MarkItem> GoToMarkCommand { get; }

    /// <summary>
    /// The marks <see cref="Items"/> shows, as one list replaced (never changed in place) at each refresh: what the
    /// rail's ticks are drawn from (FR-101).
    /// </summary>
    internal IReadOnlyList<Mark> Marks => _marks;

    /// <summary>
    /// Raised on the UI thread after each refresh that re-read the index (at most once per <see cref="RefreshInterval"/>,
    /// and at once when the document changes), once <see cref="Items"/> and <see cref="Marks"/> are updated.
    /// </summary>
    internal event EventHandler? MarksRefreshed;

    /// <summary>
    /// The ink of a mark for its row (the mark's strokes and margin notes), read off the UI thread; null when it cannot
    /// be read now (no document, the file released by a save). Set by <see cref="MainViewModel"/>.
    /// </summary>
    internal Func<Mark, CancellationToken, Task<MarkInk?>>? InkSource { get; set; }

    /// <summary>
    /// The strokes of a loaded page changed (or the page just loaded): rows of that page draw their ink again even
    /// when the mark itself is unchanged (a margin note was added). Raised on any thread.
    /// </summary>
    internal event EventHandler<PageStrokesChangedEventArgs>? InkChanged;

    /// <summary>Raises <see cref="InkChanged"/>; any thread.</summary>
    internal void NotifyInkChanged(int pageIndex) => InkChanged?.Invoke(this, new PageStrokesChangedEventArgs(pageIndex));

    /// <summary>The index of the document now shown, or none; the list follows it at once.</summary>
    internal void Attach(MarkIndex? index)
    {
        if (_index is { } previous)
        {
            previous.Changed -= OnIndexChanged;
        }

        _index = index;
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

    private bool CanStep() => Count > 0 || IsScanning;

    private void NotifyCommands()
    {
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
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

        ScheduleRefresh(); // the scheduler accepts any thread and runs the refresh on the UI thread
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

    /// <summary>
    /// Brings <see cref="Items"/> in step with the index by a merge over both sorted sequences: unchanged marks keep
    /// their row, new ones are inserted where they belong, gone ones removed. Linear in the number of marks.
    /// </summary>
    private void Rebuild()
    {
        var marks = _index?.AllMarks() ?? [];
        _marks = marks;
        var countBefore = _items.Count;
        int i = 0, j = 0;
        while (j < marks.Count)
        {
            var mark = marks[j];
            if (i == _items.Count)
            {
                _items.Add(NewItem(mark));
            }
            else if (_items[i].Mark.Equals(mark))
            {
                // Unchanged: the same row stays.
            }
            else
            {
                var order = Compare(_items[i].Mark, mark);
                if (order < 0)
                {
                    _items.RemoveAt(i); // gone: look at the same new mark again
                    continue;
                }

                if (order > 0)
                {
                    _items.Insert(i, NewItem(mark));
                }
                else
                {
                    _items[i] = NewItem(mark); // same place, different mark (its strokes changed)
                }
            }

            i++;
            j++;
        }

        while (_items.Count > i)
        {
            _items.RemoveAt(_items.Count - 1);
        }

        if (countBefore != _items.Count)
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(IsEmpty));
            if ((countBefore == 0) != (_items.Count == 0))
            {
                NotifyCommands();
            }
        }

        MarksRefreshed?.Invoke(this, EventArgs.Empty);
    }

    // The index's own order: page, then top, then bottom.
    private static int Compare(Mark a, Mark b)
    {
        var page = a.PageIndex.CompareTo(b.PageIndex);
        if (page != 0)
        {
            return page;
        }

        var top = a.TopPt.CompareTo(b.TopPt);
        return top != 0 ? top : a.BottomPt.CompareTo(b.BottomPt);
    }

    private static MarkItem NewItem(Mark mark) =>
        new(
            mark,
            string.Format(CultureInfo.CurrentCulture, Strings.MarkPageLabelFormat, mark.PageIndex + 1),
            string.Format(
                CultureInfo.CurrentCulture,
                Strings.MarkRowNameFormat,
                mark.PageIndex + 1,
                mark.Side == MarkSide.Left ? Strings.MarkSideLeft : Strings.MarkSideRight,
                (int)Math.Floor(Math.Max(0, mark.TopPt) / MarkRules.DefaultLineHeightPt) + 1));

    private void Step(bool forward)
    {
        if (_index is not { } index || _viewport.Layout is not { } layout)
        {
            return;
        }

        var scanning = _scanner is { IsCompleted: false };
        if (index.TotalMarkCount == 0 && !scanning)
        {
            return; // nothing to go to at all: the commands are disabled
        }

        var currentTop = _viewport.AbsoluteTopPt(_viewport.Position);
        var windowPt = Math.Max(0, _viewport.WindowHeightPt);
        var band = Math.Max(_minimumBandInPoints, windowPt * HereBandFraction);
        int startPage;
        Mark? found = null;
        if (forward)
        {
            startPage = _viewport.Position.PageIndex;
            for (var p = Math.Max(startPage, 0); p < layout.PageCount && found is null; p++)
            {
                found = index.MarksOnPage(p).FirstOrDefault(m => Delta(m, currentTop) > band);
            }
        }
        else
        {
            startPage = layout.PageAt(currentTop + windowPt);
            for (var p = Math.Min(startPage, layout.PageCount - 1); p >= 0 && found is null; p--)
            {
                found = index.MarksOnPage(p).LastOrDefault(m => Delta(m, currentTop) < -band);
            }
        }

        if (found is not null)
        {
            _viewport.ScrollToMark(found, recordHistory: true);
            return;
        }

        if (scanning && HasUnscannedPages(index, layout.PageCount, startPage, forward))
        {
            return; // the scan has not been there yet: a mark may still appear, so "no other mark" would be wrong
        }

        ShowStatus(Strings.MarksNoOther);
    }

    /// <summary>How far the view would move for the mark, in points (positive = down); NaN when it cannot go there.</summary>
    private double Delta(Mark mark, double currentTop) =>
        _viewport.TargetPositionFor(mark) is { } target ? _viewport.AbsoluteTopPt(target) - currentTop : double.NaN;

    private static bool HasUnscannedPages(MarkIndex index, int pageCount, int startPage, bool forward)
    {
        if (forward)
        {
            for (var p = Math.Max(startPage, 0); p < pageCount; p++)
            {
                if (!index.IsScanned(p))
                {
                    return true;
                }
            }

            return false;
        }

        for (var p = Math.Min(startPage, pageCount - 1); p >= 0; p--)
        {
            if (!index.IsScanned(p))
            {
                return true;
            }
        }

        return false;
    }

    private void GoToMark(MarkItem? item)
    {
        if (item is not null)
        {
            _viewport.ScrollToMark(item.Mark, recordHistory: true);
        }
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
