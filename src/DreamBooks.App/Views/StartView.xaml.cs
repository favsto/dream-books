using System.Windows;
using System.Windows.Controls;
using DreamBooks.App.ViewModels;

namespace DreamBooks.App.Views;

/// <summary>
/// The start screen (FR-03): "Riprendi a leggere", "Apri PDF…" and the recent documents as cover cards. The data context
/// is the <see cref="StartViewModel"/>; this view tells it how many cards fit its width.
/// </summary>
public partial class StartView : UserControl
{
    public StartView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateFit();
        DataContextChanged += (_, _) => UpdateFit();
    }

    /// <summary>Cards per row at the current width (at least 1).</summary>
    internal int CardsPerRow
    {
        get
        {
            var card = (double)FindResource("StartCardWidth") + (double)FindResource("StartCardGap");
            return Math.Max(1, (int)(ActualWidth / card));
        }
    }

    /// <summary>One row of cards: the recents that do not fit are behind "Altri…", which takes the last place.</summary>
    internal void UpdateFit()
    {
        if (DataContext is not StartViewModel start || ActualWidth <= 0)
        {
            return;
        }

        var perRow = CardsPerRow;
        start.FitCount = start.TotalCount <= perRow ? perRow : Math.Max(1, perRow - 1);
    }
}
