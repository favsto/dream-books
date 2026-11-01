using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DreamBooks.App.Resources;

namespace DreamBooks.App.ViewModels;

/// <summary>
/// One row of the Pages tab (FR-103, UI-04): a page, its displayed size in points (rotation applied, as laid out), whether
/// it is the current page and whether it has marks or handwritten notes, and its Italian automation name
/// ("Pagina 12, pagina corrente, con segni").
/// </summary>
public sealed class PageItem : ObservableObject
{
    private bool _isCurrent;
    private bool _hasMarks;
    private bool _hasNotes;

    public PageItem(int pageIndex, double widthPt, double heightPt)
    {
        PageIndex = pageIndex;
        WidthPt = widthPt;
        HeightPt = heightPt;
    }

    public int PageIndex { get; }

    /// <summary>The 1-based page number shown under the thumbnail.</summary>
    public int Number => PageIndex + 1;

    public double WidthPt { get; }

    public double HeightPt { get; }

    /// <summary>Height ÷ width of the page as displayed (1 for a degenerate size).</summary>
    public double AspectRatio => WidthPt > 0 && HeightPt > 0 ? HeightPt / WidthPt : 1;

    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    public bool HasMarks
    {
        get => _hasMarks;
        internal set
        {
            if (SetProperty(ref _hasMarks, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    public bool HasNotes
    {
        get => _hasNotes;
        internal set
        {
            if (SetProperty(ref _hasNotes, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    public string AutomationName
    {
        get
        {
            var parts = new List<string> { string.Format(CultureInfo.CurrentCulture, Strings.PageRowNameFormat, Number) };
            if (IsCurrent)
            {
                parts.Add(Strings.PageRowCurrent);
            }

            if (HasMarks)
            {
                parts.Add(Strings.PageRowHasMarks);
            }

            if (HasNotes)
            {
                parts.Add(Strings.PageRowHasNotes);
            }

            return string.Join(", ", parts);
        }
    }
}
