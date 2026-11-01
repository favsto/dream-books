using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.Core.Search;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The text search of the open document for the UI (FR-13, FR-105): the query field (debounced), the result count,
/// next/previous result with wrap-around and Back history, the current result, and the rectangles each page highlights.
/// </summary>
/// <remarks>
/// <para><b>Queries.</b> <see cref="QueryText"/> is applied <see cref="QueryDelay"/> after the last change (empty at
/// once): <see cref="QueryApplied"/> asks the owner (<see cref="MainViewModel"/>) for a new <see cref="SearchIndex"/>
/// and a scan, which it hands back through <see cref="Attach"/> and <see cref="SetScanner"/>. Once a scan completed
/// without any page having text, the document has no text layer: <see cref="NoTextLayerMessage"/> says so and later
/// queries search nothing, until another document is opened. A scan in which every page failed says the search failed
/// instead (<see cref="CountText"/>).</para>
/// <para><b>Refresh.</b> Index changes and scan progress arrive on the scan's thread: they mark the pages that changed
/// and schedule one refresh through the <see cref="IDelayScheduler"/> (any thread; it runs on the UI thread) at most
/// once per <see cref="RefreshInterval"/>; the refresh updates the count and raises
/// <see cref="PageHighlightsChanged"/> for those pages and <see cref="Refreshed"/>. While the scan runs the state is
/// re-read every interval, so the end of the scan is seen without the scanner calling back.</para>
/// <para><b>Next/previous.</b> When the current result is still where its jump put the view (its target position,
/// <see cref="DocumentViewportViewModel.TargetPositionFor(SearchHit)"/>, within <see cref="HereBandFraction"/> of the
/// window height of the view, the band of the marks), the commands step from it in index order, so results sharing a
/// line are visited one by one. Otherwise (no current result yet, or the reader scrolled away) they start from the view
/// with the marks' rule, except that a result "here" is taken rather than skipped (a result in view is one to show,
/// a mark in view is already shown): Next takes the first result whose target is not above the band, Previous the
/// last one not below it. Past the last result they wrap to the first (and back), saying so for
/// <see cref="StatusDuration"/>. While the scan is still running, a step that would cross pages not searched yet
/// waits (does nothing): a result may still appear there. Only a jump that starts from the view records Back history,
/// so Back after stepping through results returns to where the reader was. The commands do not depend on a save
/// running.</para>
/// <para>Nothing here logs or exposes the query or the matched text.</para>
/// </remarks>
public sealed class SearchViewModel : ObservableObject
{
    /// <summary>A query is applied this long after the last keystroke.</summary>
    public static readonly TimeSpan QueryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The count and the highlights follow the index at most once per interval.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long "Ripartito dall'inizio" / "dalla fine" stays visible.</summary>
    public static readonly TimeSpan StatusDuration = TimeSpan.FromSeconds(2);

    /// <summary>A result whose target is closer than this fraction of the window height to the view counts as "here".</summary>
    public const double HereBandFraction = MarksViewModel.HereBandFraction;

    private const double _minimumBandInPoints = 1.0;

    private static readonly SearchRect[] _noRects = [];

    private readonly DocumentViewportViewModel _viewport;
    private readonly IDelayScheduler _scheduler;
    private readonly object _gate = new();
    private readonly HashSet<int> _dirtyPages = [];
    private string _queryText = string.Empty;
    private SearchIndex? _index;
    private SearchScanner? _scanner;
    private bool _scanPending;
    private SearchHit? _current;
    private string _countText = string.Empty;
    private bool _hasResults;
    private bool _isSearching;
    private string? _noTextLayerMessage;
    private string? _statusText;
    private bool _documentHasNoText;
    private IDisposable? _pendingQuery;
    private IDisposable? _pendingRefresh;
    private IDisposable? _pendingStatusClear;

    /// <param name="viewport">The view the commands move.</param>
    /// <param name="scheduler">The debounce, the refresh throttle and the status timer (see <see cref="IDelayScheduler"/>).</param>
    public SearchViewModel(DocumentViewportViewModel viewport, IDelayScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(scheduler);
        _viewport = viewport;
        _scheduler = scheduler;
        NextCommand = new RelayCommand(() => Step(forward: true), CanStep);
        PreviousCommand = new RelayCommand(() => Step(forward: false), CanStep);
        ClearCommand = new RelayCommand(Clear);
    }

    /// <summary>
    /// The highlights of a page changed: its surface asks <see cref="HitsForPage"/> and <see cref="CurrentRectsForPage"/>
    /// again. -1: every page (a new query, the search cleared). Raised on the UI thread.
    /// </summary>
    public event EventHandler<int>? PageHighlightsChanged;

    /// <summary>
    /// Raised on the UI thread after each refresh that re-read the index, and when the index is replaced: what the rail's
    /// search ticks follow (<see cref="Index"/>).
    /// </summary>
    internal event EventHandler? Refreshed;

    /// <summary>A query to apply: a new search for it, or none when it is empty (the owner handles it).</summary>
    internal event EventHandler<string>? QueryApplied;

    /// <summary>The search was cleared from its field (Esc, the clear button): the keyboard goes back to the document.</summary>
    internal event EventHandler? FocusDocumentRequested;

    /// <summary>What to search; applied <see cref="QueryDelay"/> after the last change, at once when emptied.</summary>
    public string QueryText
    {
        get => _queryText;
        set
        {
            var text = value ?? string.Empty;
            if (!SetProperty(ref _queryText, text))
            {
                return;
            }

            _pendingQuery?.Dispose();
            _pendingQuery = null;
            if (TextFolding.FoldQuery(text).Length == 0)
            {
                QueryApplied?.Invoke(this, string.Empty); // nothing to search: the results go at once
                return;
            }

            _pendingQuery = _scheduler.Schedule(QueryDelay, () =>
            {
                _pendingQuery = null;
                if (!_documentHasNoText)
                {
                    QueryApplied?.Invoke(this, _queryText);
                }
            });
        }
    }

    /// <summary>"3 di 17", "17 risultati", "17+" when capped, "Nessun risultato", "Ricerca in corso…"; empty without a search.</summary>
    public string CountText
    {
        get => _countText;
        private set => SetProperty(ref _countText, value);
    }

    /// <summary>True when the search has found at least one result.</summary>
    public bool HasResults
    {
        get => _hasResults;
        private set
        {
            if (SetProperty(ref _hasResults, value))
            {
                NotifyCommands();
            }
        }
    }

    /// <summary>True while the scan has pages left (results are still arriving), also while a save holds it.</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                NotifyCommands();
            }
        }
    }

    /// <summary>"Nessun testo ricercabile in questo PDF" once a scan found no text layer; null otherwise.</summary>
    public string? NoTextLayerMessage
    {
        get => _noTextLayerMessage;
        private set => SetProperty(ref _noTextLayerMessage, value);
    }

    /// <summary>"Ripartito dall'inizio" / "dalla fine" for a moment after a wrap; null otherwise.</summary>
    public string? StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>The result shown as current (the stronger highlight), or null.</summary>
    public SearchHit? Current
    {
        get => _current;
        private set
        {
            var previous = _current;
            if (!SetProperty(ref _current, value))
            {
                return;
            }

            UpdateCount();
            if (previous is not null)
            {
                PageHighlightsChanged?.Invoke(this, previous.PageIndex);
            }

            if (value is not null && value.PageIndex != previous?.PageIndex)
            {
                PageHighlightsChanged?.Invoke(this, value.PageIndex);
            }
        }
    }

    /// <summary>Goes to the next result (F3, Enter in the field), wrapping at the end of the document.</summary>
    public IRelayCommand NextCommand { get; }

    /// <summary>Goes to the previous result (Shift+F3, Shift+Enter in the field), wrapping at the start.</summary>
    public IRelayCommand PreviousCommand { get; }

    /// <summary>Empties the field and removes the results (Esc); the keyboard goes back to the document.</summary>
    public IRelayCommand ClearCommand { get; }

    /// <summary>The hits of the search shown now (rail ticks), or null without a search.</summary>
    internal SearchIndex? Index => _index;

    /// <summary>True once a scan of this document found no page with text: queries search nothing.</summary>
    internal bool DocumentHasNoText => _documentHasNoText;

    /// <summary>
    /// The highlight rectangles of a page's results other than <see cref="Current"/>, in displayed page points (FR-26:
    /// the view scales them); none without a search.
    /// </summary>
    public IReadOnlyList<SearchRect> HitsForPage(int pageIndex)
    {
        if (_index is not { } index)
        {
            return _noRects;
        }

        var hits = index.HitsOnPage(pageIndex);
        if (hits.Count == 0)
        {
            return _noRects;
        }

        var current = _current;
        var rects = new List<SearchRect>(hits.Count);
        foreach (var hit in hits)
        {
            if (current is null || !hit.Equals(current))
            {
                rects.AddRange(hit.Rects);
            }
        }

        return rects;
    }

    /// <summary>The rectangles of <see cref="Current"/> when it is on the page; none otherwise.</summary>
    public IReadOnlyList<SearchRect> CurrentRectsForPage(int pageIndex) =>
        _current is { } current && current.PageIndex == pageIndex ? current.Rects : _noRects;

    /// <summary>The index of the query just applied, or none: the old results and highlights go at once.</summary>
    internal void Attach(SearchIndex? index)
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
            _dirtyPages.Clear();
        }

        _current = null;
        OnPropertyChanged(nameof(Current));
        UpdateState();
        PageHighlightsChanged?.Invoke(this, -1);
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The scan filling the index now (replaced with the renderer), or none. <paramref name="pending"/>: no scan right now,
    /// but one resumes with pages left (a save released the renderer), so the search still counts as running.
    /// </summary>
    internal void SetScanner(SearchScanner? scanner, bool pending = false)
    {
        if (_scanner is { } previous)
        {
            previous.Progress -= OnScanProgress;
        }

        _scanner = scanner;
        _scanPending = scanner is null && pending;
        if (scanner is not null)
        {
            scanner.Progress += OnScanProgress;
        }

        UpdateState();
        if (IsSearching)
        {
            ScheduleRefresh();
        }
    }

    /// <summary>Another document (or none): the field empties and the "no text" finding is forgotten.</summary>
    internal void ResetForDocument()
    {
        _pendingQuery?.Dispose();
        _pendingQuery = null;
        _documentHasNoText = false;
        NoTextLayerMessage = null;
        if (_queryText.Length > 0)
        {
            _queryText = string.Empty;
            OnPropertyChanged(nameof(QueryText));
        }

        Attach(null);
    }

    private bool ScanRunsOrPending => _index is not null && (_scanPending || _scanner is { IsCompleted: false });

    private bool CanStep() => HasResults || IsSearching;

    private void NotifyCommands()
    {
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
    }

    private void Clear()
    {
        QueryText = string.Empty;
        FocusDocumentRequested?.Invoke(this, EventArgs.Empty);
    }

    // The scan's thread.
    private void OnIndexChanged(object? sender, SearchIndexChangedEventArgs e)
    {
        lock (_gate)
        {
            if (ReferenceEquals(sender, _index))
            {
                _dirtyPages.Add(e.PageIndex);
            }
        }

        ScheduleRefresh();
    }

    // The scan's thread.
    private void OnScanProgress(object? sender, EventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        lock (_gate)
        {
            _pendingRefresh ??= _scheduler.Schedule(RefreshInterval, Refresh);
        }
    }

    private void Refresh()
    {
        int[] pages;
        lock (_gate)
        {
            _pendingRefresh = null;
            pages = [.. _dirtyPages];
            _dirtyPages.Clear();
        }

        UpdateState();
        foreach (var page in pages)
        {
            PageHighlightsChanged?.Invoke(this, page);
        }

        if (pages.Length > 0 || !IsSearching)
        {
            Refreshed?.Invoke(this, EventArgs.Empty);
        }

        if (IsSearching)
        {
            ScheduleRefresh(); // see the end of the scan, also while it is paused
        }
    }

    private void UpdateState()
    {
        var index = _index;
        var scanner = _scanner;
        IsSearching = ScanRunsOrPending;
        HasResults = index is { TotalHits: > 0 };
        if (index is not null && scanner is { IsCompleted: true, HasTextLayer: false } && !AllPagesFailed(index, scanner))
        {
            _documentHasNoText = true;
            NoTextLayerMessage = Strings.SearchNoTextLayer;
        }

        UpdateCount();
    }

    // Every page failed (a damaged file): that is not "no text layer".
    private static bool AllPagesFailed(SearchIndex index, SearchScanner scanner) =>
        index.SearchedCount == 0 && scanner.FailedCount > 0;

    private void UpdateCount()
    {
        if (_index is not { } index)
        {
            CountText = string.Empty;
            return;
        }

        var total = index.TotalHits;
        if (total == 0)
        {
            CountText = IsSearching ? Strings.SearchInProgress
                : _scanner is { IsCompleted: true } scanner && AllPagesFailed(index, scanner) ? Strings.SearchFailed
                : Strings.SearchNoResults;
            return;
        }

        var totalText = index.Truncated
            ? string.Format(CultureInfo.CurrentCulture, Strings.SearchTruncatedFormat, total)
            : total.ToString(CultureInfo.CurrentCulture);
        if (_current is { } current && OrdinalOf(index, current) is { } ordinal)
        {
            CountText = string.Format(CultureInfo.CurrentCulture, Strings.SearchPositionFormat, ordinal, totalText);
        }
        else
        {
            CountText = total == 1 && !index.Truncated
                ? Strings.SearchOneResult
                : string.Format(CultureInfo.CurrentCulture, Strings.SearchResultsFormat, totalText);
        }
    }

    /// <summary>The 1-based place of a hit in document order; null when the index does not hold it.</summary>
    private static int? OrdinalOf(SearchIndex index, SearchHit hit)
    {
        var onPage = index.HitsOnPage(hit.PageIndex);
        var at = -1;
        for (var i = 0; i < onPage.Count; i++)
        {
            if (onPage[i].Equals(hit))
            {
                at = i;
                break;
            }
        }

        if (at < 0)
        {
            return null;
        }

        var before = 0;
        for (var p = 0; p < hit.PageIndex; p++)
        {
            before += index.HitsOnPage(p).Count;
        }

        return before + at + 1;
    }

    private void Step(bool forward)
    {
        if (_index is not { } index || _viewport.Layout is not { } layout || layout.PageCount != index.PageCount)
        {
            return;
        }

        var scanning = ScanRunsOrPending;
        var currentTop = _viewport.AbsoluteTopPt(_viewport.Position);
        var windowPt = Math.Max(0, _viewport.WindowHeightPt);
        var band = Math.Max(_minimumBandInPoints, windowPt * HereBandFraction);

        var fromView = _current is not { } current || !(Math.Abs(Delta(current, currentTop)) <= band);
        Found found;
        if (!fromView)
        {
            found = forward ? After(index, _current!, scanning) : Before(index, _current!, scanning);
        }
        else if (forward)
        {
            found = FromViewForward(index, _viewport.Position.PageIndex, currentTop, band, scanning);
        }
        else
        {
            found = FromViewBackward(index, layout.PageAt(currentTop + windowPt), currentTop, band, scanning);
        }

        if (found.Wait)
        {
            return; // the scan has not been there yet: a result may still appear
        }

        if (found.Hit is null)
        {
            found = forward ? First(index, scanning) : Last(index, scanning);
            if (found.Wait || found.Hit is null)
            {
                return;
            }

            found = found with { Wrapped = true };
        }

        Current = found.Hit;
        _viewport.ScrollToHit(found.Hit!, recordHistory: fromView);
        if (found.Wrapped)
        {
            ShowStatus(forward ? Strings.SearchWrappedToStart : Strings.SearchWrappedToEnd);
        }
    }

    /// <summary>How far the view would move for the hit, in points (positive = down); NaN when it cannot go there.</summary>
    private double Delta(SearchHit hit, double currentTop) =>
        _viewport.TargetPositionFor(hit) is { } target ? _viewport.AbsoluteTopPt(target) - currentTop : double.NaN;

    private Found FromViewForward(SearchIndex index, int startPage, double currentTop, double band, bool scanning)
    {
        for (var p = Math.Max(startPage, 0); p < index.PageCount; p++)
        {
            if (scanning && !index.IsSearched(p))
            {
                return Found.Waiting;
            }

            foreach (var hit in index.HitsOnPage(p))
            {
                if (Delta(hit, currentTop) >= -band)
                {
                    return new Found(hit);
                }
            }
        }

        return Found.None;
    }

    private Found FromViewBackward(SearchIndex index, int startPage, double currentTop, double band, bool scanning)
    {
        for (var p = Math.Min(startPage, index.PageCount - 1); p >= 0; p--)
        {
            if (scanning && !index.IsSearched(p))
            {
                return Found.Waiting;
            }

            var hits = index.HitsOnPage(p);
            for (var i = hits.Count - 1; i >= 0; i--)
            {
                if (Delta(hits[i], currentTop) <= band)
                {
                    return new Found(hits[i]);
                }
            }
        }

        return Found.None;
    }

    private static Found After(SearchIndex index, SearchHit current, bool scanning)
    {
        var next = index.TryNext(current, wrap: false, out var hit) ? hit : null;
        var limit = next?.PageIndex ?? index.PageCount;
        return scanning && AnyUnsearched(index, current.PageIndex + 1, limit - 1) ? Found.Waiting : new Found(next);
    }

    private static Found Before(SearchIndex index, SearchHit current, bool scanning)
    {
        var previous = index.TryPrevious(current, wrap: false, out var hit) ? hit : null;
        var limit = previous?.PageIndex ?? -1;
        return scanning && AnyUnsearched(index, limit + 1, current.PageIndex - 1) ? Found.Waiting : new Found(previous);
    }

    /// <summary>The document's first hit, once every page before it has been searched.</summary>
    private static Found First(SearchIndex index, bool scanning)
    {
        for (var p = 0; p < index.PageCount; p++)
        {
            if (scanning && !index.IsSearched(p))
            {
                return Found.Waiting;
            }

            if (index.HitsOnPage(p) is { Count: > 0 } hits)
            {
                return new Found(hits[0]);
            }
        }

        return Found.None;
    }

    /// <summary>The document's last hit, once every page after it has been searched.</summary>
    private static Found Last(SearchIndex index, bool scanning)
    {
        for (var p = index.PageCount - 1; p >= 0; p--)
        {
            if (scanning && !index.IsSearched(p))
            {
                return Found.Waiting;
            }

            if (index.HitsOnPage(p) is { Count: > 0 } hits)
            {
                return new Found(hits[^1]);
            }
        }

        return Found.None;
    }

    private static bool AnyUnsearched(SearchIndex index, int from, int to)
    {
        for (var p = Math.Max(from, 0); p <= Math.Min(to, index.PageCount - 1); p++)
        {
            if (!index.IsSearched(p))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>A step's outcome: a hit (or none), or "wait" (pages on the way not searched yet), and whether it wrapped.</summary>
    private readonly record struct Found(SearchHit? Hit, bool Wait = false, bool Wrapped = false)
    {
        public static Found None => new(null);

        public static Found Waiting => new(null, Wait: true);
    }
}
