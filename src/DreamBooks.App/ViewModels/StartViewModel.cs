using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Services;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// The start screen (FR-03): "Apri PDF…" and the recent documents as cover cards, as many as fit (<c>fitCount</c>);
/// the older ones are behind the "Altri…" card. A card keeps its cover while the list changes.
/// </summary>
public sealed class StartViewModel : ObservableObject
{
    private readonly RecentFilesService _recents;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, RecentCardViewModel> _known = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<RecentCardViewModel> _cards = [];
    private bool _showMore;
    private Func<string, CancellationToken, Task<ImageSource?>>? _coverProvider;
    private int _fitCount;
    private bool _showAll;

    internal StartViewModel(
        RecentFilesService recents,
        Func<Task> openPdf,
        Func<string, Task> openRecent,
        int fitCount,
        Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(recents);
        ArgumentNullException.ThrowIfNull(openPdf);
        ArgumentNullException.ThrowIfNull(openRecent);
        ArgumentOutOfRangeException.ThrowIfLessThan(fitCount, 1);
        _recents = recents;
        _fitCount = fitCount;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        OpenPdfCommand = new AsyncRelayCommand(openPdf);
        OpenRecentCommand = new AsyncRelayCommand<RecentCardViewModel>(async card =>
        {
            if (card is null)
            {
                return;
            }

            if (card.IsMissing)
            {
                recents.Remove(card.DocumentPath);
            }
            else
            {
                await openRecent(card.DocumentPath);
            }
        });
        ShowAllCommand = new RelayCommand(() =>
        {
            _showAll = true;
            Refresh();
        });
        RemoveCommand = new RelayCommand<RecentCardViewModel>(card =>
        {
            if (card is not null)
            {
                recents.Remove(card.DocumentPath);
            }
        });
        recents.Changed += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>The cards on the screen, newest first.</summary>
    public IReadOnlyList<RecentCardViewModel> Cards
    {
        get => _cards;
        private set => SetProperty(ref _cards, value);
    }

    /// <summary>True when more recents exist than cards fit: the "Altri…" card shows.</summary>
    public bool ShowMore
    {
        get => _showMore;
        private set => SetProperty(ref _showMore, value);
    }

    public bool HasRecents => _recents.Entries.Count > 0;

    public IAsyncRelayCommand OpenPdfCommand { get; }

    public IAsyncRelayCommand<RecentCardViewModel> OpenRecentCommand { get; }

    public IRelayCommand<RecentCardViewModel> RemoveCommand { get; }

    /// <summary>"Altri…": shows every recent, in as many rows as needed.</summary>
    public IRelayCommand ShowAllCommand { get; }

    /// <summary>How many recent documents there are, shown or not.</summary>
    public int TotalCount => _recents.Entries.Count;

    /// <summary>How many cards fit on the screen now (the view sets it from its width); the older recents are behind "Altri…".</summary>
    public int FitCount
    {
        get => _fitCount;
        set
        {
            var fit = Math.Max(1, value);
            if (fit != _fitCount)
            {
                _fitCount = fit;
                Refresh();
            }
        }
    }

    /// <summary>
    /// Where a card gets its cover (the cover cache): asked once when the card first appears, off the UI thread. A
    /// provider set later is asked for the cards already there.
    /// </summary>
    internal Func<string, CancellationToken, Task<ImageSource?>>? CoverProvider
    {
        get => _coverProvider;
        set
        {
            _coverProvider = value;
            foreach (var card in _known.Values)
            {
                _ = LoadCoverAsync(card);
            }
        }
    }

    /// <summary>A cover was stored for this document: its card loads it.</summary>
    internal void ReloadCover(string documentPath)
    {
        if (_known.TryGetValue(documentPath, out var card))
        {
            _ = LoadCoverAsync(card);
        }
    }

    private async Task LoadCoverAsync(RecentCardViewModel card)
    {
        if (_coverProvider is not { } provider)
        {
            return;
        }

        try
        {
            if (await provider(card.DocumentPath, CancellationToken.None) is { } cover)
            {
                card.Cover = cover;
            }
        }
#pragma warning disable CA1031 // A card whose cover cannot be read keeps its placeholder.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void Refresh()
    {
        var now = _utcNow();
        var entries = _recents.Entries;
        var cards = new List<RecentCardViewModel>();
        if (entries.Count <= _fitCount)
        {
            _showAll = false; // everything fits again: nothing is behind "Altri…"
        }

        foreach (var entry in entries.Take(_showAll ? entries.Count : _fitCount))
        {
            if (!_known.TryGetValue(entry.Path, out var card))
            {
                card = new RecentCardViewModel(entry.Path);
                _known[entry.Path] = card;
                _ = LoadCoverAsync(card);
            }

            card.Update(entry, now);
            cards.Add(card);
        }

        var present = entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _known.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _known.Remove(gone);
        }

        Cards = cards;
        ShowMore = !_showAll && entries.Count > _fitCount;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HasRecents));
    }
}
