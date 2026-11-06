using System.IO;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;

namespace DreamBooks.App.ViewModels;

/// <summary>One cover card of the start screen: a recent document with its texts, its reading progress and its cover.</summary>
public sealed partial class RecentCardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _whenText = "";

    [ObservableProperty]
    private string _pageText = "";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isMissing;

    /// <summary>The first page, once rendered or read from the cover cache; null shows the placeholder.</summary>
    [ObservableProperty]
    private ImageSource? _cover;

    internal RecentCardViewModel(string path) => DocumentPath = path;

    /// <summary>The document's path: the card's identity, never shown.</summary>
    public string DocumentPath { get; }

    internal void Update(RecentEntry entry, DateTime nowUtc)
    {
        Title = Path.GetFileNameWithoutExtension(entry.FileName);
        WhenText = RelativeTimeFormatter.Format(entry.LastOpenedUtc, nowUtc);
        IsMissing = entry.Exists == false;
        PageText = IsMissing
            ? Strings.StartMissing
            : string.Format(CultureInfo.CurrentCulture, Strings.StartPageFormat, entry.Page + 1);
        Progress = entry.PageCount > 0 ? Math.Clamp((entry.Page + 1.0) / entry.PageCount, 0, 1) : 0;
    }
}
