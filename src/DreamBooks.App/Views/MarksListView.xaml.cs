using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Documents;

namespace DreamBooks.App.Views;

/// <summary>
/// The marks list (FR-103) over a <see cref="MarksViewModel"/>: one row per mark of the stable
/// <see cref="MarksViewModel.Items"/> (updated in place, so the list keeps its scroll position), virtualised and
/// recycled, each with its ink thumbnail (<see cref="MarkInkThumbnail"/>, through one bounded
/// <see cref="MarkThumbnailCache"/>) and its page label. A tap that did not move (a scroll is not a tap) or Enter on
/// the selected row goes to the mark through <see cref="MarksViewModel.GoToMarkCommand"/> (history recorded), then
/// raises <see cref="EdgePanel.ContentActivatedEvent"/> so the hosting panel hides.
/// </summary>
public partial class MarksListView : UserControl
{
    private MarksViewModel? _marks;
    private MarkThumbnailCache? _thumbnails;
    private Point? _pressAt;

    public MarksListView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as MarksViewModel);
    }

    /// <summary>
    /// The thumbnails of the rows; by default a cache over the view model's <see cref="MarksViewModel.InkSource"/>, made
    /// when the data context is set. Tests inject one.
    /// </summary>
    internal MarkThumbnailCache? Thumbnails
    {
        get => _thumbnails;
        set
        {
            _thumbnails = value;
            MarkInkThumbnail.SetCache(this, value); // inherited by every row's thumbnail
        }
    }

    /// <summary>A press on the list (list coordinates): where a tap on a row starts.</summary>
    internal void PressRow(Point position) => _pressAt = position;

    /// <summary>
    /// The press lifts over a row (list coordinates): a tap (it moved at most <see cref="ScrollRail.TapSlopDip"/>) goes
    /// to its mark; a scroll of the list does not. True when it went.
    /// </summary>
    internal bool ReleaseRow(MarkItem item, Point position)
    {
        var start = _pressAt;
        _pressAt = null;
        if (start is not { } from || (position - from).Length > ScrollRail.TapSlopDip)
        {
            return false;
        }

        Activate(item);
        return true;
    }

    /// <summary>Enter: goes to the selected (or focused) row's mark. True when there was one.</summary>
    internal bool ActivateSelected()
    {
        var item = List.SelectedItem as MarkItem
            ?? (Keyboard.FocusedElement as ListBoxItem)?.DataContext as MarkItem; // focused but not selected yet
        if (item is null)
        {
            return false;
        }

        Activate(item);
        return true;
    }

    private void Activate(MarkItem item)
    {
        if (_marks?.GoToMarkCommand is not { } command || !command.CanExecute(item))
        {
            return;
        }

        command.Execute(item);
        RaiseEvent(new RoutedEventArgs(EdgePanel.ContentActivatedEvent, this)); // the panel hides after the jump
    }

    private void Attach(MarksViewModel? marks)
    {
        if (_marks is { } old)
        {
            old.InkChanged -= OnInkChanged;
        }

        _marks = marks;
        if (marks is null)
        {
            return;
        }

        marks.InkChanged += OnInkChanged;
        var source = marks.InkSource ?? ((_, _) => Task.FromResult<MarkInk?>(null));
        Thumbnails = new MarkThumbnailCache(source);
    }

    // Any thread: the session raises it where the edit or the load happened.
    private void OnInkChanged(object? sender, PageStrokesChangedEventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Thumbnails?.Invalidate(e.PageIndex));

    private void OnListPress(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Stylus)
        {
            return; // the pen never presses the list (the panel closes)
        }

        PressRow(e.GetPosition(List));
    }

    private void OnRowRelease(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Stylus)
        {
            return;
        }

        if (sender is ListBoxItem { DataContext: MarkItem item } && ReleaseRow(item, e.GetPosition(List)))
        {
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && ActivateSelected())
        {
            e.Handled = true;
        }
    }
}
