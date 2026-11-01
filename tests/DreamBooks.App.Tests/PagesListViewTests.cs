using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Layout;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

/// <summary>
/// The Pages tab of the left-edge panel (FR-103, UI-04): the tab strip beside "Segni" (pen-transparent like the sheet),
/// one virtualised row per page sized by its aspect ratio, the current page ringed, mark and note ticks, the tap that
/// jumps with Back history and leaves the panel open, the list brought to the current page when shown, and the
/// thumbnails: a low-resolution bitmap with the page's ink drawn over it as vectors, loaded only for realised rows,
/// cancelled on recycle, never stale after a reopen without saving, a placeholder for a page that cannot be rendered.
/// Look and feel are checked on the device.
/// </summary>
public sealed class PagesListViewTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dreambooks-pages-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

    public PagesListViewTests() => Directory.CreateDirectory(_dir);

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
    public void Tab_strip_switches_between_marks_and_pages_and_keeps_the_panel_pen_transparent()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                var panel = window.MarksPanel;
                panel.AnimationsEnabled = () => false;
                Assert.Same(window.PagesList, panel.SecondBody);
                Assert.Same(((MainViewModel)window.DataContext).Pages, window.PagesList.DataContext);

                // Two 40 px tabs with Italian names; the marks tab first.
                Assert.Equal(Visibility.Visible, panel.TabStrip.Visibility);
                Assert.Equal(Strings.MarksList, panel.FirstTab.Content);
                Assert.Equal(Strings.PagesTab, panel.SecondTab.Content);
                Assert.Equal(Strings.MarksTabName, AutomationProperties.GetName(panel.FirstTab));
                Assert.Equal(Strings.PagesTabName, AutomationProperties.GetName(panel.SecondTab));
                Assert.True(panel.FirstTab.MinHeight >= 40 && panel.SecondTab.MinHeight >= 40);
                Assert.True(panel.FirstTab.MinWidth >= 40 && panel.SecondTab.MinWidth >= 40);
                Assert.Equal(0, panel.SelectedTab);

                panel.Open();
                Assert.Equal(Visibility.Visible, panel.BodyHost.Visibility);
                Assert.Equal(Visibility.Collapsed, panel.SecondBodyHost.Visibility);
                Assert.Equal(Visibility.Visible, panel.DetailText.Visibility); // the marks count

                // The tab switches the content in place; the count belongs to the marks.
                panel.SecondTab.IsChecked = true;
                Assert.Equal(1, panel.SelectedTab);
                Assert.True(panel.IsOpen);
                Assert.Equal(Visibility.Collapsed, panel.BodyHost.Visibility);
                Assert.Equal(Visibility.Visible, panel.SecondBodyHost.Visibility);
                Assert.Equal(Visibility.Collapsed, panel.DetailText.Visibility);
                Assert.Equal(Strings.PagesTab, AutomationProperties.GetName(panel));
                panel.FirstTab.IsChecked = true;
                Assert.Equal(0, panel.SelectedTab);
                Assert.Equal(Strings.MarksList, AutomationProperties.GetName(panel));

                // The pen on a tab never presses it: the panel gives way. A finger is left to the tab.
                panel.UpdateLayout();
                var onTab = panel.SecondTab.TranslatePoint(new Point(10, 10), panel);
                Assert.False(panel.RoutePress(onTab, PointerKind.Finger));
                Assert.True(panel.IsOpen);
                Assert.True(panel.RoutePress(onTab, PointerKind.Pen));
                Assert.False(panel.IsOpen);
                Assert.Equal(0, panel.SelectedTab);

                // Shortcuts: Ctrl+M for the marks, Ctrl+Shift+P for the pages (Ctrl+P is left to printing); each opens
                // its tab, switches to it when the other is shown, and closes the panel when its own is shown.
                var bindings = window.InputBindings.OfType<KeyBinding>().ToList();
                var marksKey = bindings.Single(b => b.Key == Key.M && b.Modifiers == ModifierKeys.Control);
                var pagesKey = bindings.Single(b => b.Key == Key.P && b.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
                Assert.Single(bindings, b => b.Key == Key.P); // no clash
                pagesKey.Command.Execute(pagesKey.CommandParameter);
                Assert.True(panel.IsOpen);
                Assert.Equal(1, panel.SelectedTab);
                marksKey.Command.Execute(marksKey.CommandParameter);
                Assert.True(panel.IsOpen);
                Assert.Equal(0, panel.SelectedTab);
                marksKey.Command.Execute(marksKey.CommandParameter);
                Assert.False(panel.IsOpen);

                // The menu entry opens the Pages tab.
                Assert.Equal(Strings.OpenPagesList, AutomationProperties.GetName(window.OpenPagesListButton));
                window.OpenPagesListButton.Command.Execute(window.OpenPagesListButton.CommandParameter);
                Assert.True(panel.IsOpen);
                Assert.Equal(1, panel.SelectedTab);
                window.OpenMarksListButton.Command.Execute(window.OpenMarksListButton.CommandParameter);
                Assert.Equal(0, panel.SelectedTab);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Pen_press_on_the_closed_edge_still_inks_and_on_the_open_sheet_closes_it()
    {
        WpfTestThread.Run(() =>
        {
            var window = new MainWindow();
            try
            {
                var content = (FrameworkElement)window.Content;
                ScrollRailTests.LayOut(content, 1200, 800);
                var panel = window.MarksPanel;
                panel.AnimationsEnabled = () => false;
                panel.Open(1);
                panel.Close();

                // Closed on the Pages tab: nothing of the panel is at the edge.
                var edge = window.DocumentViewer.TranslatePoint(new Point(4, 400), content);
                Assert.False(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(content, edge), panel));
                Assert.True(ScrollRailTests.IsWithin(ScrollRailTests.HitAt(content, edge), window.DocumentViewer));
                Assert.False(panel.RoutePress(new Point(4, 400), PointerKind.Pen));

                // Open on the Pages tab: the pen on the list closes the panel and is taken; outside it closes it and inks.
                panel.Open(1);
                content.UpdateLayout();
                var onList = window.PagesList.TranslatePoint(new Point(60, 100), panel);
                Assert.True(panel.RoutePress(onList, PointerKind.Pen));
                Assert.False(panel.IsOpen);
                panel.Open(1);
                Assert.False(panel.RoutePress(new Point(900, 400), PointerKind.Pen));
                Assert.False(panel.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Rows_are_sized_by_the_page_aspect_ratio()
    {
        WpfTestThread.Run(() =>
        {
            var (_, _, pages, _) = Standalone(new PageDimensions(612, 792), new PageDimensions(792, 612), new PageDimensions(300, 900));
            var list = NewList(pages, NewCache());
            var thumbs = Rows(list).Select(r => FindDescendant<PageThumbnail>(r)!).ToList();
            Assert.Equal(3, thumbs.Count);
            var width = (double)list.FindResource("PagesThumbnailWidth");
            Assert.Equal(140, width);
            Assert.All(thumbs, t => Assert.Equal(width, t.ActualWidth, 3));
            Assert.Equal(width * 792 / 612, thumbs[0].ActualHeight, 3);
            Assert.Equal(width * 612 / 792, thumbs[1].ActualHeight, 3);
            Assert.Equal(width * 3, thumbs[2].ActualHeight, 3);

            // The page number under each thumbnail; rows named in Italian.
            Assert.Equal(["1", "2", "3"], Rows(list).Select(r => Texts(r).Single()));
            Assert.Equal(pages.Pages.Select(p => p.AutomationName), Rows(list).Select(AutomationProperties.GetName));
            Assert.Equal(Strings.PagesListLabel, AutomationProperties.GetName(list.List));
        });
    }

    [Fact]
    public void Current_page_is_ringed_and_follows_the_viewport()
    {
        WpfTestThread.Run(() =>
        {
            var (viewport, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 6).ToArray());
            var list = NewList(pages, NewCache());
            var ring = (Brush)list.FindResource("PagesCurrentRingBrush");
            Assert.Equal([0], RingedPages(list, ring));

            viewport.ScrollToPage(2, recordHistory: false);
            list.UpdateLayout();
            Assert.Equal([2], RingedPages(list, ring));

            // The ring never changes the row's size (no jump in the list).
            var heights = Rows(list).Select(r => r.ActualHeight).Distinct().ToList();
            Assert.Single(heights);
        });
    }

    [Fact]
    public void Tap_goes_to_the_page_records_history_once_and_keeps_the_panel_open()
    {
        WpfTestThread.Run(() =>
        {
            var (viewport, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 10).ToArray());
            var list = new PagesListView { DataContext = pages, Thumbnails = NewCache() };
            var panel = new EdgePanel { AnimationsEnabled = () => false, Title = "t", Body = new Border(), SecondBody = list };
            ScrollRailTests.LayOut(panel, 1000, 800);
            panel.Open(1);
            panel.UpdateLayout();

            // A press that moved (a scroll of the list) is no tap.
            list.PressRow(new Point(10, 10));
            Assert.False(list.ReleaseRow(pages.Pages[3], new Point(10, 70)));
            Assert.Equal(0, viewport.Position.PageIndex);

            list.PressRow(new Point(10, 10));
            Assert.True(list.ReleaseRow(pages.Pages[3], new Point(12, 12)));
            Assert.Equal(3, viewport.Position.PageIndex);
            Assert.True(panel.IsOpen); // the panel stays open: he may look further

            list.PressRow(new Point(10, 10));
            Assert.True(list.ReleaseRow(pages.Pages[5], new Point(10, 10)));
            Assert.Equal(5, viewport.Position.PageIndex);

            // Enter on the selected row does the same.
            list.List.SelectedItem = pages.Pages[1];
            Assert.True(list.ActivateSelected());
            Assert.Equal(1, viewport.Position.PageIndex);
            Assert.True(panel.IsOpen);

            // One Back entry for the whole visit: back to where he was before opening the list.
            viewport.BackCommand.Execute(null);
            Assert.Equal(0, viewport.Position.PageIndex);
            Assert.False(viewport.BackCommand.CanExecute(null));

            // Closing and opening again starts a new visit.
            panel.Close();
            panel.Open(1);
            panel.UpdateLayout();
            list.PressRow(new Point(10, 10));
            Assert.True(list.ReleaseRow(pages.Pages[8], new Point(10, 10)));
            Assert.True(viewport.BackCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Opening_the_tab_scrolls_to_the_current_page()
    {
        WpfTestThread.Run(() =>
        {
            var (viewport, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 200).ToArray());
            var list = new PagesListView { DataContext = pages, Thumbnails = NewCache() };
            var panel = new EdgePanel { AnimationsEnabled = () => false, Title = "t", Body = new Border(), SecondBody = list };
            ScrollRailTests.LayOut(panel, 1000, 800);
            viewport.ScrollToPage(120, recordHistory: false);

            panel.Open(1);
            WpfTestThread.Drain();
            panel.UpdateLayout();
            Assert.True(IsShown(list, 120), "the current page's row is in view");
            Assert.Same(pages.Pages[120], list.List.SelectedItem); // arrows start from it

            // Switching to the tab while open does the same, for the page current then.
            panel.SelectTab(0);
            viewport.ScrollToPage(30, recordHistory: false);
            panel.SelectTab(1);
            WpfTestThread.Drain();
            panel.UpdateLayout();
            Assert.True(IsShown(list, 30), "the new current page's row is in view");
        });
    }

    [Fact]
    public void Switching_tabs_on_the_open_panel_moves_keyboard_focus_into_the_new_tab()
    {
        WpfTestThread.Run(() =>
        {
            // In the window: the shortcuts switch the tab of the open panel; focus follows into the tab shown, so the
            // arrows and Enter keep working in the panel (an unshown window takes no real focus: the request is checked).
            var window = new MainWindow();
            try
            {
                ScrollRailTests.LayOut((FrameworkElement)window.Content, 1200, 800);
                var panel = window.MarksPanel;
                panel.AnimationsEnabled = () => false;
                var bindings = window.InputBindings.OfType<KeyBinding>().ToList();
                var marksKey = bindings.Single(b => b.Key == Key.M && b.Modifiers == ModifierKeys.Control);
                var pagesKey = bindings.Single(b => b.Key == Key.P);

                marksKey.Command.Execute(marksKey.CommandParameter);
                WpfTestThread.Drain();
                Assert.Same(window.MarksList, panel.LastFocusTarget);
                var requests = panel.FocusRequests;

                pagesKey.Command.Execute(pagesKey.CommandParameter);
                WpfTestThread.Drain();
                Assert.Equal(requests + 1, panel.FocusRequests);
                Assert.Same(window.PagesList, panel.LastFocusTarget);

                marksKey.Command.Execute(marksKey.CommandParameter);
                WpfTestThread.Drain();
                Assert.Same(window.MarksList, panel.LastFocusTarget);
                Assert.True(panel.IsOpen);
                Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(panel.Sheet)); // Tab stays in
            }
            finally
            {
                window.Close();
            }
        });

        WpfTestThread.Run(() =>
        {
            // In Pagine, the focus goes to the current page's row.
            var (viewport, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 40).ToArray());
            var list = new PagesListView { DataContext = pages, Thumbnails = NewCache() };
            var panel = new EdgePanel { AnimationsEnabled = () => false, Title = "t", Body = new Border(), SecondBody = list };
            ScrollRailTests.LayOut(panel, 1000, 800);
            viewport.ScrollToPage(25, recordHistory: false);
            panel.Open(0);
            WpfTestThread.Drain();
            panel.SelectTab(1);
            WpfTestThread.Drain();
            Assert.Same(list, panel.LastFocusTarget);
            Assert.Same(pages.Pages[25], list.LastFocusedItem);
        });
    }

    [Fact]
    public void A_thumbnail_read_that_returned_nothing_is_retried_when_the_file_is_back()
    {
        WpfTestThread.Run(() =>
        {
            // A read that ran just as a plain save released the file returns nothing; the rows still shown read again
            // when the renderer is back (the coordinator's retry signal), not only when they are recycled.
            var (_, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 3).ToArray());
            var released = true;
            var cache = new PageThumbnailCache(
                (_, _) => Task.FromResult<BitmapSource?>(released ? null : Solid(140, 181, 0xC0)),
                (page, _) => Task.FromResult<PageInk?>(released ? null : new PageInk(page, 612, 792, [], Core.Ink.PressureCurve.Firm, Core.Ink.SmoothingLevel.Strong)));
            var list = NewList(pages, cache);
            var thumbs = Rows(list).Select(r => FindDescendant<PageThumbnail>(r)!).ToList();
            WpfTestThread.Drain();
            Assert.All(thumbs, t => Assert.False(t.HasBitmap || t.HasInk));

            released = false;
            cache.RetryMissing();
            WpfTestThread.PumpUntil(() => thumbs.All(t => t.HasBitmap && t.HasInk));
        });

        WpfTestThread.Run(() =>
        {
            // The window passes the coordinator's signal (renderer back after a save) on to the thumbnails.
            var window = new MainWindow();
            try
            {
                var cache = window.PagesList.Thumbnails!;
                var before = cache.RetryRequests;
                window.DocumentViewer.Bitmaps.InvalidateAll();
                Assert.Equal(before + 1, cache.RetryRequests);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Selection_follows_the_current_page_while_the_list_is_shown()
    {
        WpfTestThread.Run(() =>
        {
            var (viewport, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 20).ToArray());
            var list = new PagesListView { DataContext = pages, Thumbnails = NewCache() };
            var panel = new EdgePanel { AnimationsEnabled = () => false, Title = "t", Body = new Border(), SecondBody = list };
            ScrollRailTests.LayOut(panel, 1000, 800);
            panel.Open(1);
            WpfTestThread.Drain();
            Assert.Same(pages.Pages[0], list.List.SelectedItem);

            // The document moves under the open list (wheel, keys, Back): the selection goes with the ring.
            viewport.ScrollToPage(4, recordHistory: false);
            viewport.ScrollToPage(6, recordHistory: false);
            WpfTestThread.Drain();
            Assert.Same(pages.Pages[6], list.List.SelectedItem);
            Assert.True(pages.Pages[6].IsCurrent);

            // Hidden, it does not follow (shown again, it is brought to the current page anyway).
            panel.Close();
            viewport.ScrollToPage(9, recordHistory: false);
            WpfTestThread.Drain();
            Assert.Same(pages.Pages[6], list.List.SelectedItem);
            panel.Open(1);
            WpfTestThread.Drain();
            Assert.Same(pages.Pages[9], list.List.SelectedItem);
        });
    }

    [Fact]
    public void A_dpi_change_drops_the_thumbnail_bitmaps_and_the_shown_rows_render_again()
    {
        WpfTestThread.Run(() =>
        {
            var (_, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 3).ToArray());
            var bitmapReads = 0;
            var inkReads = 0;
            var cache = new PageThumbnailCache(
                (_, _) =>
                {
                    bitmapReads++;
                    return Task.FromResult<BitmapSource?>(Solid(140, 181, 0xC0));
                },
                (page, _) =>
                {
                    inkReads++;
                    return Task.FromResult<PageInk?>(new PageInk(page, 612, 792, [], Core.Ink.PressureCurve.Firm, Core.Ink.SmoothingLevel.Strong));
                });
            var list = NewList(pages, cache);
            var thumbs = Rows(list).Select(r => FindDescendant<PageThumbnail>(r)!).ToList();
            WpfTestThread.PumpUntil(() => thumbs.All(t => t.HasBitmap && t.HasInk));
            var (bitmapsBefore, inkBefore) = (bitmapReads, inkReads);

            cache.ClearBitmaps();
            Assert.Equal(thumbs.Count, cache.Count); // the shown rows rendered again at once
            Assert.Equal(bitmapsBefore + thumbs.Count, bitmapReads);
            Assert.Equal(inkBefore, inkReads); // the ink is vectors: nothing to redo
            Assert.All(thumbs, t => Assert.True(t.HasBitmap));
        });

        WpfTestThread.Run(() =>
        {
            // The window wires the coordinator's screen-scale change to the thumbnails.
            var window = new MainWindow();
            try
            {
                var cache = window.PagesList.Thumbnails!;
                var before = cache.BitmapClears;
                window.DocumentViewer.Bitmaps.SetDpiScale(2.5);
                Assert.Equal(before + 1, cache.BitmapClears);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_rotated_page_thumbnail_has_its_displayed_proportions_and_upright_ink()
    {
        WpfTestThread.Run(() =>
        {
            // A /Rotate 90 portrait page is laid out landscape (792 x 612 displayed); its strokes are stored in displayed
            // points, so a vertical line near the left of the displayed page is drawn vertical near the left.
            var item = new PageItem(0, 792, 612);
            var line = new InkStroke(
                Guid.NewGuid(),
                InkTool.Pen,
                InkColor.FromRgb(0, 0, 0),
                10,
                Enumerable.Range(0, 20).Select(i => new InkPoint(80, 100 + (i * 20), 0.6f, null, null, null, i * 10)).ToArray());
            var cache = new PageThumbnailCache(
                (_, _) => Task.FromResult<BitmapSource?>(Solid(140, 108, 0xC0)),
                (page, _) => Task.FromResult<PageInk?>(new PageInk(page, 792, 612, [line], Core.Ink.PressureCurve.Firm, Core.Ink.SmoothingLevel.Strong)));
            var thumb = new PageThumbnail { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            PageThumbnail.SetCache(thumb, cache);
            thumb.Page = item;
            ScrollRailTests.LayOut(thumb, 300, 300);
            WpfTestThread.PumpUntil(() => thumb.HasBitmap && thumb.StrokeCount == 1);

            Assert.Equal(140, thumb.ActualWidth, 3);
            Assert.Equal(140 * 612 / 792.0, thumb.ActualHeight, 3);
            var pixels = Render(thumb);
            var x = (int)(80 * 140 / 792.0); // about 14
            Assert.True(Dark(pixels, thumb, new Int32Rect(x - 3, 25, 6, 40)) >= 40, "a vertical line on the left");
            Assert.Equal(0, Dark(pixels, thumb, new Int32Rect(40, 10, 90, 90)));
        });
    }

    [Fact]
    public void A_stroke_change_raised_on_a_background_thread_redraws_the_thumbnail_ink_on_the_UI_thread()
    {
        WpfTestThread.Run(() =>
        {
            var (_, marks, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 3).ToArray());
            var inkReads = new List<int>();
            var cache = new PageThumbnailCache(
                (_, _) => Task.FromResult<BitmapSource?>(null),
                (page, _) =>
                {
                    Assert.True(System.Windows.Application.Current.Dispatcher.CheckAccess());
                    inkReads.Add(page);
                    return Task.FromResult<PageInk?>(new PageInk(page, 612, 792, [], Core.Ink.PressureCurve.Firm, Core.Ink.SmoothingLevel.Strong));
                });
            var list = NewList(pages, cache);
            WpfTestThread.PumpUntil(() => inkReads.Count >= 3);
            var invalidated = new List<int>();
            cache.InkInvalidated += (_, page) =>
            {
                Assert.True(System.Windows.Application.Current.Dispatcher.CheckAccess());
                invalidated.Add(page);
            };

            Task.Run(() => marks.NotifyInkChanged(1)).Wait(); // as the session raises it from a load or the PDFium thread
            WpfTestThread.PumpUntil(() => invalidated.Count == 1 && inkReads.Count(p => p == 1) == 2);
            Assert.Equal([1], invalidated);
            _ = list;
        });
    }

    [Fact]
    public void A_hidden_list_reads_nothing_and_catches_up_when_shown_again()
    {
        WpfTestThread.Run(() =>
        {
            var (_, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 50).ToArray());
            var requests = new List<(int Page, CancellationToken Token)>();
            var cache = new PageThumbnailCache(
                (page, ct) =>
                {
                    requests.Add((page, ct));
                    return new TaskCompletionSource<BitmapSource?>().Task;
                },
                (_, _) => Task.FromResult<PageInk?>(null));
            var list = new PagesListView { DataContext = pages, Thumbnails = cache };
            var panel = new EdgePanel { AnimationsEnabled = () => false, Title = "t", Body = new Border(), SecondBody = list };
            ScrollRailTests.LayOut(panel, 1000, 800);
            panel.Open(1);
            panel.UpdateLayout();
            WpfTestThread.Drain();
            var shown = requests.ToList();
            Assert.NotEmpty(shown);

            // Closed (he writes): every read under way is given up, and a document change reads nothing.
            panel.Close();
            Assert.True(cache.IsPaused);
            Assert.All(shown, r => Assert.True(r.Token.IsCancellationRequested));
            cache.Clear();
            WpfTestThread.Drain();
            Assert.Equal(shown.Count, requests.Count);

            // Shown again: the visible rows read what they miss.
            panel.Open(1);
            panel.UpdateLayout();
            WpfTestThread.Drain();
            Assert.False(cache.IsPaused);
            Assert.True(requests.Count > shown.Count);

            // The other tab hides it too.
            panel.SelectTab(0);
            Assert.True(cache.IsPaused);
        });
    }

    [Fact]
    public void List_virtualises_a_thousand_pages_and_realises_only_visible_rows()
    {
        WpfTestThread.Run(() =>
        {
            var (_, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 1000).ToArray());
            var bitmapLoads = 0;
            var inkLoads = 0;
            var cache = new PageThumbnailCache(
                (_, _) =>
                {
                    bitmapLoads++;
                    return new TaskCompletionSource<BitmapSource?>().Task;
                },
                (_, _) =>
                {
                    inkLoads++;
                    return new TaskCompletionSource<PageInk?>().Task;
                });
            var list = NewList(pages, cache);

            Assert.Equal(1000, list.List.Items.Count);
            Assert.True(VirtualizingPanel.GetIsVirtualizing(list.List));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list.List));
            var realised = Rows(list).Count;
            Assert.InRange(realised, 1, 20);
            Assert.InRange(bitmapLoads, 1, realised);
            Assert.InRange(inkLoads, 1, realised);
        });
    }

    [Fact]
    public void Thumbnail_requests_are_low_priority_and_cancelled_on_recycle()
    {
        WpfTestThread.Run(() =>
        {
            // The low priority itself is the coordinator's (PageBitmapCoordinatorTests): the list only asks for the rows
            // it realises, and gives up the requests of rows it recycles or unloads.
            var (_, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 300).ToArray());
            var requests = new List<(int Page, CancellationToken Token)>();
            var cache = new PageThumbnailCache(
                (page, ct) =>
                {
                    requests.Add((page, ct));
                    return new TaskCompletionSource<BitmapSource?>().Task;
                },
                (_, _) => Task.FromResult<PageInk?>(null));
            var list = NewList(pages, cache);
            var first = requests.ToList();
            Assert.NotEmpty(first);
            Assert.All(first, r => Assert.InRange(r.Page, 0, 20));
            Assert.All(first, r => Assert.False(r.Token.IsCancellationRequested));

            ScrollViewerOf(list.List).ScrollToVerticalOffset(250 * 220);
            list.UpdateLayout();
            WpfTestThread.Drain();
            list.UpdateLayout();

            Assert.Contains(requests, r => r.Page > 200);
            var shown = Rows(list).Select(r => ((PageItem)r.DataContext).PageIndex).ToHashSet();
            Assert.All(first.Where(r => !shown.Contains(r.Page)), r => Assert.True(r.Token.IsCancellationRequested, $"page {r.Page}"));
            Assert.InRange(requests.Count, 1, first.Count + 40); // nothing for the rows scrolled past
        });
    }

    [Fact]
    public void Ink_is_drawn_over_the_bitmap_and_follows_draw_erase_undo()
    {
        WpfTestThread.Run(() =>
        {
            var (vm, _, _) = NewVm(4);
            var cache = new PageThumbnailCache(
                (_, _) => Task.FromResult<BitmapSource?>(Solid(140, 181, 0xC0)),
                vm.LoadPageInkAsync);
            var list = NewList(vm.Pages, cache);
            var thumb = ThumbnailOf(list, 0);
            WpfTestThread.PumpUntil(() => thumb.HasBitmap && thumb.HasInk);
            Assert.Equal(0, thumb.StrokeCount);

            // The bitmap fills the page box (grey here), with nothing dark on it yet.
            var pixels = Render(thumb);
            Assert.True(Grey(pixels, thumb, new Int32Rect(60, 60, 20, 20)), "the page bitmap is drawn");
            Assert.Equal(0, Dark(pixels, thumb, new Int32Rect(4, 4, 132, 173)));

            // A stroke across the page: the thumbnail draws it over the bitmap.
            var points = Enumerable.Range(0, 20).Select(i => new InkPoint(100 + (i * 20), 400, 0.6f, null, null, null, i * 10)).ToArray();
            Assert.True(vm.Editor!.CommitStroke(points, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2));
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 1);
            pixels = Render(thumb);
            var y = (int)(400 * 140 / 612.0);
            Assert.True(Dark(pixels, thumb, new Int32Rect(30, y - 3, 70, 6)) >= 20, "the stroke is drawn over the bitmap");

            // Erased, then the erase undone, then undone again (the stroke's draw): the ink follows each time.
            vm.BeginErase(vm.Editor!);
            vm.EraseAt(new PointD(300, 400));
            vm.EndErase();
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 0);
            Wait(vm.UndoCommand.ExecuteAsync(null));
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 1);
            Wait(vm.UndoCommand.ExecuteAsync(null));
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 0);
            Assert.True(thumb.HasBitmap); // the bitmap stays: only the ink is read again
        });
    }

    [Fact]
    public void Caches_are_cleared_on_reopen_without_saving()
    {
        WpfTestThread.Run(() =>
        {
            var (vm, dialogs, path) = NewVm(4, SaveChangesAnswer.DontSave);
            var bitmapLoads = 0;
            var cache = new PageThumbnailCache(
                (_, _) =>
                {
                    bitmapLoads++;
                    return Task.FromResult<BitmapSource?>(Solid(140, 181, 0xC0));
                },
                vm.LoadPageInkAsync);
            var list = NewList(vm.Pages, cache);
            var thumb = ThumbnailOf(list, 0);
            WpfTestThread.PumpUntil(() => thumb.HasBitmap && thumb.HasInk);
            var points = Enumerable.Range(0, 10).Select(i => new InkPoint(100 + (i * 20), 400, 0.6f, null, null, null, i * 10)).ToArray();
            Assert.True(vm.Editor!.CommitStroke(points, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2));
            WpfTestThread.PumpUntil(() => thumb.StrokeCount == 1);
            var loadsBefore = bitmapLoads;

            // Closed with "Non salvare" and reopened: the discarded stroke never shows, the bitmaps are read again.
            Open(vm, dialogs, path);
            Assert.Equal(1, dialogs.SaveChangesAsks);
            list.UpdateLayout();
            thumb = ThumbnailOf(list, 0);
            WpfTestThread.PumpUntil(() => thumb.HasInk && thumb.HasBitmap);
            WpfTestThread.Drain();
            Assert.Equal(0, thumb.StrokeCount);
            Assert.True(bitmapLoads > loadsBefore, "the bitmaps were read again");
            Assert.True(cache.Ink.Count <= PageThumbnailCache.DefaultInkCapacity);
        });
    }

    [Fact]
    public void Pages_with_marks_or_notes_show_ticks()
    {
        WpfTestThread.Run(() =>
        {
            var (_, _, pages, index) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 4).ToArray());
            var list = NewList(pages, NewCache());
            index.SetPage(1, [new Mark(1, MarkSide.Left, 100, 300, [Guid.NewGuid()])]);
            index.SetPage(2, [], [new Note(2, 100, 120, [Guid.NewGuid()])]);
            index.SetPage(3, [new Mark(3, MarkSide.Left, 100, 300, [Guid.NewGuid()])], [new Note(3, 100, 120, [Guid.NewGuid()])]);
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            list.UpdateLayout();

            var rows = Rows(list);
            Assert.Equal(4, rows.Count);
            Assert.Equal([false, true, false, true], rows.Select(r => Tick(r, "MarkTick").Visibility == Visibility.Visible));
            Assert.Equal([false, false, true, true], rows.Select(r => Tick(r, "NoteTick").Visibility == Visibility.Visible));

            // The rail's colours (opaque: small marks must read on any background).
            var markTick = Tick(rows[1], "MarkTick");
            var noteTick = Tick(rows[2], "NoteTick");
            Assert.Equal(((SolidColorBrush)list.FindResource("RailMarkTickBrushOpaque")).Color, ((SolidColorBrush)markTick.Fill).Color);
            Assert.Equal(((SolidColorBrush)list.FindResource("RailNoteTickBrushOpaque")).Color, ((SolidColorBrush)noteTick.Fill).Color);
            Assert.False(markTick.IsHitTestVisible);
        });
    }

    [Fact]
    public void A_page_that_fails_to_render_keeps_its_placeholder_and_raises_nothing()
    {
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            WpfTestThread.Run(() =>
            {
                var (_, _, pages, _) = Standalone(Enumerable.Repeat(new PageDimensions(612, 792), 3).ToArray());
                var cache = new PageThumbnailCache(
                    (page, _) => page switch
                    {
                        0 => Task.FromException<BitmapSource?>(new InvalidOperationException("render failed")),
                        1 => throw new InvalidOperationException("render failed at once"),
                        _ => Task.FromResult<BitmapSource?>(null),
                    },
                    (page, _) => page == 0
                        ? Task.FromException<PageInk?>(new IOException("ink unreadable"))
                        : Task.FromResult<PageInk?>(null));
                var list = NewList(pages, cache);
                var thumbs = Rows(list).Select(r => FindDescendant<PageThumbnail>(r)!).ToList();
                WpfTestThread.Drain();

                Assert.Equal(3, thumbs.Count);
                Assert.All(thumbs, t => Assert.False(t.HasBitmap));
                Assert.Equal(["1", "2", "3"], Rows(list).Select(r => Texts(r).Single())); // the page number stays

                // The placeholder: a white page with its frame.
                var pixels = Render(thumbs[0]);
                Assert.True(White(pixels, thumbs[0], new Int32Rect(60, 60, 20, 20)));
            });
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.Equal(0, unobserved);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private (DocumentViewportViewModel Viewport, MarksViewModel Marks, PagesViewModel Pages, MarkIndex Index) Standalone(
        params PageDimensions[] sizes)
    {
        var viewport = new DocumentViewportViewModel(new ZoomViewModel(), _scheduler);
        viewport.Attach(null, new DocumentLayout(sizes), null);
        viewport.ViewportWidthDip = 816;
        viewport.ViewportHeightDip = 600;
        var marks = new MarksViewModel(viewport, _scheduler);
        var pages = new PagesViewModel(viewport, marks);
        var index = new MarkIndex(sizes.Length);
        marks.Attach(index);
        return (viewport, marks, pages, index);
    }

    private (MainViewModel Vm, FakeFileDialogService Dialogs, string Path) NewVm(int pageCount, SaveChangesAnswer answer = SaveChangesAnswer.Cancel)
    {
        var pages = new FakePageSource(Enumerable.Repeat((612.0, 792.0), pageCount).ToArray());
        var dialogs = new FakeFileDialogService { SaveChangesAnswer = answer };
        var vm = new MainViewModel(dialogs, scheduler: _scheduler)
        {
            MarkSourceFactory = _ => new FakeMarkPageSource(pages),
            MarkScanIdleGap = TimeSpan.Zero,
        };
        vm.OpenDocument = (path, _) => Task.FromResult(new OpenedDocument(pages, pages, path, null));
        var path = System.IO.Path.Combine(_dir, "a.pdf");
        File.WriteAllBytes(path, [1, 2, 3]);
        Open(vm, dialogs, path);
        return (vm, dialogs, path);
    }

    private static void Open(MainViewModel vm, FakeFileDialogService dialogs, string path)
    {
        dialogs.NextPath = path;
        var open = vm.OpenPdfCommand.ExecuteAsync(null);
        WpfTestThread.PumpUntil(() => open.IsCompleted);
        Assert.True(open.IsCompletedSuccessfully);
        Assert.Null(vm.ErrorMessage);
    }

    private static void Wait(Task task)
    {
        WpfTestThread.PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static PageThumbnailCache NewCache() =>
        new((_, _) => Task.FromResult<BitmapSource?>(null), (_, _) => Task.FromResult<PageInk?>(null));

    private static PagesListView NewList(PagesViewModel pages, PageThumbnailCache cache)
    {
        var list = new PagesListView { DataContext = pages, Thumbnails = cache };
        ScrollRailTests.LayOut(list, 320, 800);
        WpfTestThread.Drain();
        list.UpdateLayout();
        return list;
    }

    private static PageThumbnail ThumbnailOf(PagesListView list, int page)
    {
        list.UpdateLayout();
        var row = Rows(list).First(r => ((PageItem)r.DataContext).PageIndex == page);
        return FindDescendant<PageThumbnail>(row)!;
    }

    private static List<int> RingedPages(PagesListView list, Brush ring) =>
        Rows(list)
            .Where(r => Descendants<Border>(r).Any(b => b.Name == "Ring"
                && b.BorderBrush is SolidColorBrush shown
                && shown.Color == ((SolidColorBrush)ring).Color
                && shown.Color.A > 0))
            .Select(r => ((PageItem)r.DataContext).PageIndex)
            .ToList();

    private static Rectangle Tick(DependencyObject row, string name) =>
        Descendants<Rectangle>(row).Single(r => r.Name == name);

    private static bool IsShown(PagesListView list, int page)
    {
        var row = Rows(list).FirstOrDefault(r => ((PageItem)r.DataContext).PageIndex == page);
        if (row is null)
        {
            return false;
        }

        var bounds = row.TransformToAncestor(list.List).TransformBounds(new Rect(row.RenderSize));
        return bounds.Bottom > 0 && bounds.Top < list.List.ActualHeight;
    }

    private static ScrollViewer ScrollViewerOf(DependencyObject root) => Descendants<ScrollViewer>(root).First();

    private static List<ListBoxItem> Rows(PagesListView list)
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

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static BitmapSource Solid(int width, int height, byte grey)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = grey;
            pixels[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Render(FrameworkElement element)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static int Count(byte[] pixels, FrameworkElement element, Int32Rect area, Func<byte, byte, byte, byte, bool> match)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var count = 0;
        for (var y = area.Y; y < area.Y + area.Height; y++)
        {
            for (var x = area.X; x < area.X + area.Width; x++)
            {
                var at = ((y * width) + x) * 4;
                if (match(pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int Dark(byte[] pixels, FrameworkElement element, Int32Rect area) =>
        Count(pixels, element, area, (b, g, r, a) => a > 0 && Math.Max(b, Math.Max(g, r)) < 0x60);

    private static bool Grey(byte[] pixels, FrameworkElement element, Int32Rect area) =>
        Count(pixels, element, area, (b, g, r, a) => a == 255 && Math.Abs(b - 0xC0) < 8 && Math.Abs(r - 0xC0) < 8) == area.Width * area.Height;

    private static bool White(byte[] pixels, FrameworkElement element, Int32Rect area) =>
        Count(pixels, element, area, (b, g, r, a) => a == 255 && b == 255 && g == 255 && r == 255) == area.Width * area.Height;
}
