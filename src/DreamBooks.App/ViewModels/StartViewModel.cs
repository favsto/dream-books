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
    private readonly int _fitCount;
    private readonly Dictionary<string, RecentCardViewModel> _known = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<RecentCardViewModel> _cards = [];
    private bool _showMore;

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

    private void Refresh()
    {
        var now = _utcNow();
        var entries = _recents.Entries;
        var cards = new List<RecentCardViewModel>();
        foreach (var entry in entries.Take(_fitCount))
        {
            if (!_known.TryGetValue(entry.Path, out var card))
            {
                card = new RecentCardViewModel(entry.Path);
                _known[entry.Path] = card;
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
        ShowMore = entries.Count > _fitCount;
        OnPropertyChanged(nameof(HasRecents));
    }
}
