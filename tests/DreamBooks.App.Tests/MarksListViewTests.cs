using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

/// <summary>
/// The marks list in the left-edge panel (FR-103): one virtualised row per mark with its page label and the vector
/// ink of its margin region (loaded off the UI thread, only for realised rows, through a bounded cache), the empty
/// text, the row tap that jumps and hides the panel, and the list following the index in the real window.
/// </summary>
public sealed class MarksListViewTests : IDisposable
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-list-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

    public MarksListViewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Marks_list_shows_one_row_per_mark_with_page_label()
    {
        WpfTestThread.Run(() =>
        {
            var (_, marks, index) = Standalone();
            index.SetPage(1, [Mark(1, 50, 200), Mark(1, 400, 520, MarkSide.Right)]);
            index.SetPage(5, [Mark(5, 100, 300)]);
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            var list = NewList(marks);

            Assert.Same(marks.Items, list.List.ItemsSource);
            Assert.Equal(3, list.List.Items.Count);
            var rows = Rows(list);
            Assert.Equal(3, rows.Count);
            Assert.Equal(["pag. 2", "pag. 2", "pag. 6"], rows.Select(r => Texts(r).First(t => t.StartsWith("pag.", StringComparison.Ordinal))));

            // Each row has an Italian automation name (page and side) and a thumbnail of its mark.
            var names = rows.Select(AutomationProperties.GetName).ToList();
            Assert.Equal(3, names.Distinct().Count());
            Assert.Equal(["Segno a pagina 2, margine sinistro", "Segno a pagina 2, margine destro", "Segno a pagina 6, margine sinistro"], names); // no line number from the fixed default "line"
            Assert.All(rows, r => Assert.NotNull(FindDescendant<MarkInkThumbnail>(r)));
            Assert.Equal(Strings.MarksListLabel, AutomationProperties.GetName(list.List));

            // Rows are updated in place: an added mark keeps the other rows (and the scroll position).
            var first = list.List.Items[0];
            index.SetPage(7, [Mark(7, 100, 300)]);
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            Assert.Equal(4, list.List.Items.Count);
            Assert.Same(first, list.List.Items[0]);
        });
    }

    [Fact]
    public void Row_tap_invokes_GoToMark_and_closes_the_panel()
    {
        WpfTestThread.Run(() =>
        {
            var (viewport, marks, index) = Standalone();
            index.SetPage(6, [Mark(6, 300, 500)]);
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            var list = new MarksListView { DataContext = marks, Thumbnails = new MarkThumbnailCache((_, _) => Task.FromResult<MarkInk?>(null)) };
            var panel = new EdgePanel { AnimationsEnabled = () => false, Title = "t", Body = list };
            ScrollRailTests.LayOut(panel, 1000, 800);
            panel.Open();

            // A tap that moved (a scroll of the list) does not jump.
            var item = marks.Items[0];
            list.PressRow(new Point(10, 10));
            list.ReleaseRow(item, new Point(10, 60));
            Assert.Equal(0, viewport.Position.PageIndex);
            Assert.True(panel.IsOpen);

            list.PressRow(new Point(10, 10));
            list.ReleaseRow(item, new Point(12, 12));
            Assert.Equal(6, viewport.Position.PageIndex);
            Assert.True(viewport.BackCommand.CanExecute(null)); // history recorded (FR-09)
            Assert.False(panel.IsOpen); // auto-hides after the jump

            // Enter on the selected row does the same.
            viewport.BackCommand.Execute(null);
            panel.Open();
            list.List.SelectedItem = item;
            Assert.True(list.ActivateSelected());
            Assert.Equal(6, viewport.Position.PageIndex);
            Assert.False(panel.IsOpen);
        });
    }

    [Fact]
    public void Empty_state_text_is_shown_when_there_are_no_marks()
    {
        WpfTestThread.Run(() =>
        {
            var (_, marks, index) = Standalone();
            var list = NewList(marks);
            Assert.Equal(Strings.MarksEmpty, list.EmptyText.Text);
            Assert.Equal(Visibility.Visible, list.EmptyText.Visibility);
            Assert.True(list.EmptyText.TextWrapping == TextWrapping.Wrap);

            index.SetPage(2, [Mark(2, 100, 300)]);
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            list.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, list.EmptyText.Visibility);

            index.SetPage(2, []);
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            list.UpdateLayout();
            Assert.Equal(Visibility.Visible, list.EmptyText.Visibility);
        });
    }

    [Fact]
    public void Thumbnail_draws_the_margin_ink_scaled_to_the_row()
    {
        WpfTestThread.Run(() =>
        {
            var mark = Mark(0, 100, 260);
            var line = Line(mark.StrokeIds[0], x: 30, top: 100, bottom: 260);
            var release = new TaskCompletionSource<MarkInk?>();
            var cache = new MarkThumbnailCache((_, _) => release.Task);
            var thumb = new MarkInkThumbnail { Width = 120, Height = 60 };
            MarkInkThumbnail.SetCache(thumb, cache);
            thumb.Mark = mark;
            ScrollRailTests.LayOut(thumb, 120, 60);

            // Placeholder until the ink arrives: plain paper.
            Assert.False(thumb.HasInk);
            Assert.Equal(0, DarkPixels(Render(thumb), new Int32Rect(2, 2, 116, 56))); // inside the 1 DIP frame

            release.SetResult(new MarkInk(mark, 612, [line], PressureCurve.Firm, SmoothingLevel.Strong));
            WpfTestThread.PumpUntil(() => thumb.HasInk);
            var pixels = Render(thumb);

            // The band is the page width around the mark: the left margin line lands in the left fifth of the row.
            Assert.True(DarkPixels(pixels, new Int32Rect(2, 2, 22, 56)) >= 10, "the margin line is drawn on the left");
            Assert.Equal(0, DarkPixels(pixels, new Int32Rect(40, 2, 78, 56)));

            // A second thumbnail of the same mark draws at once from the cache.
            var again = new MarkInkThumbnail { Width = 120, Height = 60 };
            MarkInkThumbnail.SetCache(again, cache);
            again.Mark = mark;
            Assert.True(again.HasInk);
            Assert.Equal(1, cache.LoadCount);
        });
    }

    [Fact]
    public void List_virtualises_a_thousand_marks()
    {
        WpfTestThread.Run(() =>
        {
            var (_, marks, index) = Standalone(pageCount: 500);
            for (var page = 0; page < 500; page++)
            {
                index.SetPage(page, [Mark(page, 50, 200), Mark(page, 400, 600)]);
            }

            _scheduler.Advance(MarksViewModel.RefreshInterval);
            Assert.Equal(1000, marks.Count);

            var loads = 0;
            var pending = new TaskCompletionSource<MarkInk?>();
            var list = new MarksListView
            {
                DataContext = marks,
                Thumbnails = new MarkThumbnailCache((_, _) =>
                {
                    loads++;
                    return pending.Task;
                }),
            };
            ScrollRailTests.LayOut(list, 320, 800);
            WpfTestThread.Drain();

            Assert.True(VirtualizingPanel.GetIsVirtualizing(list.List));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list.List));
            var realised = Rows(list).Count;
            Assert.InRange(realised, 1, 40);
            Assert.InRange(loads, 1, realised); // never more loads than visible rows
            pending.SetResult(null);
        });
    }

    [Fact]
    public void List_follows_the_index_in_the_real_window_when_marks_change_off_the_UI_thread()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                var vm = (MainViewModel)window.DataContext;
                window.MarksPanel.AnimationsEnabled = () => false;
                window.MarksPanel.Open();
                var index = new MarkIndex(10);
                vm.Marks.Attach(index);
                Assert.Empty(window.MarksList.List.Items);

                // The scan's thread: the real dispatcher scheduler brings the list up to date on the UI thread.
                Assert.True(Task.Run(() => index.SetPage(4, [Mark(4, 100, 300)])).Wait(_timeout));
                WpfTestThread.PumpUntil(() => window.MarksList.List.Items.Count == 1);
                ((FrameworkElement)window.Content).UpdateLayout();
                Assert.Equal(Visibility.Collapsed, window.MarksList.EmptyText.Visibility);
                Assert.Single(Rows(window.MarksList));

                Assert.True(Task.Run(() => index.SetPage(4, [])).Wait(_timeout));
                WpfTestThread.PumpUntil(() => window.MarksList.List.Items.Count == 0);
                vm.Marks.Attach(null);
            }
            finally
            {
                window.CloseForTests();
            }
        });
    }

    [Fact]
    public async Task Mark_ink_is_the_margin_region_of_the_loaded_page_or_of_the_stored_strokes()
    {
        var pages = new FakePageSource(Enumerable.Repeat((612.0, 792.0), 6).ToArray());
        var stored = Line(Guid.NewGuid(), x: 30, top: 100, bottom: 260);
        var body = Line(Guid.NewGuid(), x: 300, top: 120, bottom: 140); // in the text, not a margin note
        pages.Strokes[4] = [stored, body];
        var source = new FakeMarkPageSource(pages);
        var dialogs = new FakeFileDialogService();
        var vm = new MainViewModel(dialogs, scheduler: _scheduler) { MarkSourceFactory = _ => source, MarkScanIdleGap = TimeSpan.Zero };
        vm.OpenDocument = (path, _) => Task.FromResult(new OpenedDocument(pages, pages, path, null));
        var path = Path.Combine(_dir, "a.pdf");
        File.WriteAllBytes(path, [1, 2, 3]);
        dialogs.NextPath = path;
        await vm.OpenPdfCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Marks.InkSource);

        // Page 4 is not loaded in the session: its stored strokes, read through the scan's source.
        var mark = new Mark(4, MarkSide.Left, 100, 260, [stored.Id]);
        var ink = await vm.Marks.InkSource!(mark, CancellationToken.None).WaitAsync(_timeout);
        Assert.NotNull(ink);
        Assert.Equal(612, ink.PageWidthPt);
        Assert.Equal([stored.Id], ink.Strokes.Select(s => s.Id));

        // The loaded current page: the session's strokes (the live edit), not the stored ones.
        Assert.True(vm.Editor!.CommitStroke(Line(Guid.NewGuid(), x: 20, top: 300, bottom: 500).Points, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1.5));
        var drawn = vm.Editor.Page.Strokes[^1];
        var live = new Mark(0, MarkSide.Left, 300, 500, [drawn.Id]);
        var liveInk = await vm.Marks.InkSource!(live, CancellationToken.None).WaitAsync(_timeout);
        Assert.Equal([drawn.Id], liveInk!.Strokes.Select(s => s.Id));

        // A stroke change on a loaded page tells the thumbnails to redraw.
        var changed = new List<int>();
        vm.Marks.InkChanged += (_, e) => changed.Add(e.PageIndex);
        Assert.True(vm.Editor.CommitStroke(Line(Guid.NewGuid(), x: 25, top: 600, bottom: 610).Points, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1.5));
        Assert.Contains(0, changed);
    }

    [Fact]
    public void A_note_discarded_by_reopening_without_saving_never_shows_in_the_row()
    {
        WpfTestThread.Run(() =>
        {
            var pages = new FakePageSource(Enumerable.Repeat((612.0, 792.0), 6).ToArray());
            pages.Strokes[0] = [Line(Guid.NewGuid(), x: 30, top: 100, bottom: 260)]; // the saved margin line
            var dialogs = new FakeFileDialogService { SaveChangesAnswer = SaveChangesAnswer.DontSave };
            var vm = new MainViewModel(dialogs, scheduler: _scheduler)
            {
                MarkSourceFactory = _ => new FakeMarkPageSource(pages),
                MarkScanIdleGap = TimeSpan.Zero,
            };
            vm.OpenDocument = (path, _) => Task.FromResult(new OpenedDocument(pages, pages, path, null));
            var path = Path.Combine(_dir, "a.pdf");
            File.WriteAllBytes(path, [1, 2, 3]);
            Open(vm, dialogs, path);

            var list = new MarksListView { DataContext = vm.Marks };
            ScrollRailTests.LayOut(list, 320, 800);
            var thumb = Thumbnail(list);
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 1);

            // A margin note next to the mark: the row draws it (cached with it).
            var note = Enumerable.Range(0, 10).Select(i => new InkPoint(60 + (i * 6), 150, 0.5f, null, null, null, i)).ToArray();
            Assert.True(vm.Editor!.CommitStroke(note, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1.5));
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 2);

            // Reopened without saving: the same mark (same stroke ids), but the note is gone.
            Open(vm, dialogs, path);
            Assert.Equal(1, dialogs.SaveChangesAsks);
            list.UpdateLayout();
            thumb = Thumbnail(list);
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 1);
            WpfTestThread.Drain();
            Assert.Equal(1, thumb.StrokeCount);
        });
    }

    [Fact]
    public void A_row_loaded_again_with_the_same_mark_shows_the_current_ink()
    {
        WpfTestThread.Run(() =>
        {
            var mark = Mark(0, 100, 260);
            var strokes = new List<InkStroke> { Line(mark.StrokeIds[0], x: 30, top: 100, bottom: 260) };
            var cache = new MarkThumbnailCache((m, _) => Task.FromResult<MarkInk?>(
                new MarkInk(m, 612, strokes.ToArray(), PressureCurve.Firm, SmoothingLevel.Strong)));
            var thumb = new MarkInkThumbnail { Width = 120, Height = 60 };
            MarkInkThumbnail.SetCache(thumb, cache);
            thumb.Mark = mark;
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 1);

            // Unloaded (scrolled out of a recycling list): it no longer listens; meanwhile a note is added.
            thumb.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            strokes.Add(Line(Guid.NewGuid(), x: 60, top: 150, bottom: 160));
            cache.Invalidate(0);
            Assert.Equal(1, thumb.StrokeCount);

            // Loaded again with the same mark: it asks again and shows the note.
            thumb.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 2);
        });
    }

    [Fact]
    public void Cache_keeps_the_most_recently_used_thumbnails_up_to_its_capacity()
    {
        WpfTestThread.Run(() =>
        {
            var cache = new MarkThumbnailCache(
                (m, _) => Task.FromResult<MarkInk?>(new MarkInk(m, 612, [], PressureCurve.Firm, SmoothingLevel.Strong)),
                capacity: 2);
            var a = Mark(0, 100, 260);
            var b = Mark(1, 100, 260);
            var c = Mark(2, 100, 260);
            Get(cache, a);
            Get(cache, b);
            Assert.True(cache.TryGet(a, out _)); // a is now the most recent
            Get(cache, c); // evicts b, the least recently used
            Assert.Equal(2, cache.Count);
            Assert.True(cache.TryGet(a, out _));
            Assert.True(cache.TryGet(c, out _));
            Assert.False(cache.TryGet(b, out _));
            Assert.Equal(3, cache.LoadCount);

            // Clear (another document): everything goes and shown rows are told to load again.
            var cleared = 0;
            cache.Cleared += (_, _) => cleared++;
            cache.Clear();
            Assert.Equal(0, cache.Count);
            Assert.Equal(1, cleared);
        });
    }

    [Fact]
    public void Invalidating_a_page_drops_its_thumbnails_and_the_shown_rows_load_again()
    {
        WpfTestThread.Run(() =>
        {
            var version = 1;
            var cache = new MarkThumbnailCache((m, _) => Task.FromResult<MarkInk?>(new MarkInk(
                m,
                612,
                Enumerable.Range(0, version).Select(i => Line(Guid.NewGuid(), x: 30 + i, top: 100, bottom: 260)).ToArray(),
                PressureCurve.Firm,
                SmoothingLevel.Strong)));
            var onPage = Mark(3, 100, 260);
            var other = Mark(4, 100, 260);
            Get(cache, other);
            var thumb = new MarkInkThumbnail { Width = 120, Height = 60 };
            MarkInkThumbnail.SetCache(thumb, cache);
            thumb.Mark = onPage;
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 1);
            var invalidated = new List<int>();
            cache.PageInvalidated += (_, p) => invalidated.Add(p);

            version = 2;
            cache.Invalidate(3);
            Assert.Equal([3], invalidated);
            Assert.True(cache.TryGet(other, out _)); // another page keeps its thumbnail
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 2); // the shown row read it again
        });
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private static void Get(MarkThumbnailCache cache, Mark mark)
    {
        var task = cache.GetAsync(mark, CancellationToken.None);
        WpfTestThread.PumpUntil(() => task.IsCompleted);
        Assert.NotNull(task.Result);
    }

    private static void Open(MainViewModel vm, FakeFileDialogService dialogs, string path)
    {
        dialogs.NextPath = path;
        var open = vm.OpenPdfCommand.ExecuteAsync(null);
        WpfTestThread.PumpUntil(() => open.IsCompleted);
        Assert.True(open.IsCompletedSuccessfully);
        Assert.Null(vm.ErrorMessage);
    }

    private MarkInkThumbnail Thumbnail(MarksListView list)
    {
        WpfTestThread.PumpUntil(() =>
        {
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            list.UpdateLayout();
            return Rows(list).Count == 1;
        });
        return FindDescendant<MarkInkThumbnail>(Rows(list)[0])!;
    }

    private (DocumentViewportViewModel Viewport, MarksViewModel Marks, MarkIndex Index) Standalone(int pageCount = 10)
    {
        var viewport = new DocumentViewportViewModel(new ZoomViewModel(), _scheduler);
        var layout = new DocumentLayout(Enumerable.Repeat(new PageDimensions(612, 792), pageCount).ToArray());
        viewport.Attach(null, layout, null);
        viewport.ViewportWidthDip = 816;
        viewport.ViewportHeightDip = 600;
        var marks = new MarksViewModel(viewport, _scheduler);
        var index = new MarkIndex(pageCount);
        marks.Attach(index);
        return (viewport, marks, index);
    }

    private static MarksListView NewList(MarksViewModel marks)
    {
        var list = new MarksListView { DataContext = marks, Thumbnails = new MarkThumbnailCache((_, _) => Task.FromResult<MarkInk?>(null)) };
        ScrollRailTests.LayOut(list, 320, 800);
        WpfTestThread.Drain();
        return list;
    }

    private static Mark Mark(int page, double top, double bottom, MarkSide side = MarkSide.Left) =>
        new(page, side, top, bottom, [Guid.NewGuid()]);

    private static InkStroke Line(Guid id, double x, double top, double bottom) =>
        new(
            id,
            InkTool.Pen,
            InkColor.FromRgb(0, 0, 0),
            1.5,
            Enumerable.Range(0, 20)
                .Select(i => new InkPoint(x, top + ((bottom - top) * i / 19.0), 0.5f, null, null, null, i))
                .ToArray());

    private static List<ListBoxItem> Rows(MarksListView list)
    {
        list.UpdateLayout();
        return Descendants<ListBoxItem>(list.List).Where(i => i.IsVisible || i.Visibility == Visibility.Visible).ToList();
    }

    private static IEnumerable<string> Texts(DependencyObject root) => Descendants<TextBlock>(root).Select(t => t.Text);

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject => Descendants<T>(root).FirstOrDefault();

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var found in Descendants<T>(child))
            {
                yield return found;
            }
        }
    }

    private static byte[] Render(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap(120, 60, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[120 * 60 * 4];
        bitmap.CopyPixels(pixels, 120 * 4, 0);
        return pixels;
    }

    /// <summary>Pixels clearly darker than the paper (any channel below 200) inside a rectangle.</summary>
    private static int DarkPixels(byte[] pixels, Int32Rect area)
    {
        var count = 0;
        for (var y = area.Y; y < area.Y + area.Height; y++)
        {
            for (var x = area.X; x < area.X + area.Width; x++)
            {
                var at = ((y * 120) + x) * 4;
                if (pixels[at + 3] > 0 && Math.Min(pixels[at], Math.Min(pixels[at + 1], pixels[at + 2])) < 200)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
