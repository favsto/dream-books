using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Marks;
using DreamBooks.Core.Model;
using DreamBooks.Core.Search;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// The search results' ticks on the scroll rail (FR-105, UI-07, NFR-32): the third kind beside the marks (centred) and
/// the notes (left), on the right; placed with the knob's own mapping, merged within one DIP, following the search
/// index through the search's refresh throttle, cleared with the search, never taking the pen, out of the automation
/// tree, opaque with transparency off. Their look is checked on the device.
/// </summary>
public sealed class ScrollRailSearchTicksTests : IDisposable
{
    private const int _pageCount = 30;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-searchticks-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

    public ScrollRailSearchTicksTests() => Directory.CreateDirectory(_dir);

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
    public async Task Search_ticks_follow_the_index_with_the_knob_mapping_through_the_throttle()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var index = Attach(vm);
            rail.NotifyActivity();
            Assert.Empty(rail.SearchTicks.Centres);
            Assert.Null(rail.SearchTicks.TickGeometry); // nothing drawn without results

            index.SetPage(10, [H(10, 300, 312), H(10, 300.1, 312.1, left: 300)]); // a fraction of a DIP apart: merged
            index.SetPage(20, [H(20, 100, 112)]);
            index.SetPage(_pageCount - 1, [H(_pageCount - 1, 740, 752)]); // beyond the last scrollable offset
            rail.NotifyActivity();
            Assert.Empty(rail.SearchTicks.Centres); // throttled with the search: not yet
            Refresh(view);

            var expected = Expected(vm, rail);
            Assert.Equal(3, expected.Count);
            AssertSearchTicks(rail, expected);
            Assert.Equal(rail.TrackLength + (rail.KnobHeight / 2), rail.SearchTicks.Centres[^1], 6); // the track's end

            // Hidden, the rail does not draw them; shown again, they are current.
            rail.HideNow();
            index.SetPage(5, [H(5, 100, 112)]);
            _scheduler.Advance(SearchViewModel.RefreshInterval);
            Assert.Equal(3, rail.SearchTicks.Centres.Count);
            rail.NotifyActivity();
            Assert.Equal(4, rail.SearchTicks.Centres.Count);
        });
    }

    [Fact]
    public async Task Clearing_the_search_removes_the_ticks()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var index = Attach(vm);
            index.SetPage(10, [H(10, 300, 312)]);
            Refresh(view);
            Assert.Single(rail.SearchTicks.Centres);

            vm.Search.QueryText = "parola"; // typed (its search not applied yet: the debounce waits)
            vm.Search.ClearCommand.Execute(null); // Esc or the clear button: the field empties, the search stops
            Assert.Null(vm.Search.Index);
            Refresh(view);
            Assert.Empty(rail.SearchTicks.Centres);
            Assert.Null(rail.SearchTicks.TickGeometry);

            // Another document (none): the search goes, and its ticks with it.
            index = Attach(vm);
            index.SetPage(12, [H(12, 300, 312)]);
            Refresh(view);
            Assert.Single(rail.SearchTicks.Centres);
            vm.Search.ResetForDocument();
            Refresh(view);
            Assert.Empty(rail.SearchTicks.Centres);
        });
    }

    [Fact]
    public async Task A_new_query_replaces_the_ticks()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var first = Attach(vm);
            first.SetPage(10, [H(10, 300, 312)]);
            first.SetPage(20, [H(20, 300, 312)]);
            Refresh(view);
            Assert.Equal(2, rail.SearchTicks.Centres.Count);

            var second = Attach(vm); // a new query: a new index, empty at first
            Refresh(view);
            Assert.Empty(rail.SearchTicks.Centres);
            second.SetPage(25, [H(25, 100, 112)]);
            first.SetPage(3, [H(3, 100, 112)]); // the old index is no longer followed
            Refresh(view);
            AssertSearchTicks(rail, Expected(vm, rail));
            Assert.Single(rail.SearchTicks.Centres);
        });
    }

    [Fact]
    public async Task A_truncated_search_draws_what_it_has()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var index = Attach(vm);
            index.SetPage(4, [H(4, 100, 112), H(4, 400, 412)]);
            index.Truncated = true; // the hit cap stopped the scan: no Changed, the scan's last progress refreshes
            Refresh(view);
            Assert.Equal(2, rail.SearchTicks.Centres.Count);
            AssertSearchTicks(rail, Expected(vm, rail));
        });
    }

    [Fact]
    public async Task Three_kinds_remain_visible_when_they_coincide()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            vm.MarkIndexForTest!.SetPage(10, [new Mark(10, MarkSide.Left, 300, 312, [Guid.NewGuid()])], [new Note(10, 300, 312, [Guid.NewGuid()])]);
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            var index = Attach(vm);
            index.SetPage(10, [H(10, 300, 312)]);
            Refresh(view);

            Assert.Single(rail.Ticks.Centres);
            Assert.Single(rail.NoteTicks.Centres);
            Assert.Single(rail.SearchTicks.Centres);
            Assert.Equal(rail.Ticks.Centres[0], rail.SearchTicks.Centres[0], 6); // the same position
            Assert.Equal(rail.Ticks.Centres[0], rail.NoteTicks.Centres[0], 6);

            // Marks centred, notes on the left, search on the right: never overlapping, all on the rail.
            var mark = rail.Ticks.TickGeometry!.Bounds;
            var note = rail.NoteTicks.TickGeometry!.Bounds;
            var search = rail.SearchTicks.TickGeometry!.Bounds;
            Assert.True(note.Right <= mark.Left, $"notes {note} overlap marks {mark}");
            Assert.True(search.Left >= mark.Right, $"search {search} overlaps marks {mark}");
            Assert.True(search.Right <= rail.SearchTicks.ActualWidth, "the search ticks stay on the rail");

            // Drawn under the knob, the marks above the other two.
            var layers = (Panel)VisualTreeHelper.GetParent(rail.SearchTicks);
            Assert.True(layers.Children.IndexOf(rail.SearchTicks) < layers.Children.IndexOf(rail.Ticks));
            Assert.True(layers.Children.IndexOf(rail.Ticks) < layers.Children.IndexOf(rail.Knob));

            // Three different colours.
            var colours = new[] { rail.Ticks, rail.NoteTicks, rail.SearchTicks }
                .Select(layer => ((SolidColorBrush)layer.Fill!).Color)
                .ToArray();
            Assert.Equal(3, colours.Distinct().Count());
        });
    }

    [Fact]
    public async Task Ticks_are_not_hit_testable_and_the_pen_still_inks()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var index = Attach(vm);
            index.SetPage(5, [H(5, 300, 312)]); // a tick near the top of the track, away from the knob
            Refresh(view);
            Assert.Single(rail.SearchTicks.Centres);

            Assert.False(rail.SearchTicks.IsHitTestVisible);
            Assert.False(rail.SearchTicks.Focusable);
            var bounds = rail.SearchTicks.TickGeometry!.Bounds;
            var point = rail.SearchTicks.TranslatePoint(new Point((bounds.Left + bounds.Right) / 2, rail.SearchTicks.Centres[0]), view);
            Assert.Equal(RailZone.None, rail.ZoneAt(view.TranslatePoint(point, rail), PointerKind.Pen));
            var hit = ScrollRailTests.HitAt(view, point);
            Assert.False(ScrollRailTests.IsWithin(hit, rail));
            Assert.True(IsWithinInkSurface(hit), "the pen on a search tick reaches the page's ink surface");
            Assert.False(view.RoutePointerDown(point, PointerKind.Pen));
            Assert.False(rail.IsPressed);

            // Decorative: no automation peer of its own, and none among the rail's.
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(rail.SearchTicks));
            Assert.DoesNotContain(rail.SearchTicks, Owners(UIElementAutomationPeer.CreatePeerForElement(rail)));
        });
    }

    [Fact]
    public void Opaque_brush_when_transparency_is_off()
    {
        WpfTestThread.Run(() =>
        {
            var rail = new ScrollRail { TransparencyEnabled = () => false };
            rail.ApplyTransparency();
            Assert.Same(Application.Current.FindResource("RailSearchTickBrushOpaque"), rail.SearchTicks.Fill);
            Assert.Equal(1, ((SolidColorBrush)rail.SearchTicks.Fill!).Opacity);

            rail.TransparencyEnabled = () => true;
            rail.ApplyTransparency();
            Assert.Same(Application.Current.FindResource("RailSearchTickBrush"), rail.SearchTicks.Fill);
            Assert.True(((SolidColorBrush)rail.SearchTicks.Fill!).Opacity < 1);

            Assert.IsType<double>(Application.Current.FindResource("RailSearchTickLength"));
            Assert.IsType<double>(Application.Current.FindResource("RailSearchTickThickness"));
            Assert.True((double)Application.Current.FindResource("RailSearchTickOffset") > 0); // on the right
        });
    }

    [Fact]
    public async Task Thousands_of_hits_produce_one_geometry()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var index = Attach(vm);
            for (var page = 0; page < _pageCount; page++)
            {
                index.SetPage(page, Enumerable.Range(0, 100).Select(line => H(page, 40 + (line * 7), 46 + (line * 7))).ToArray());
            }

            Refresh(view);
            Assert.Equal(3000, index.TotalHits);
            var expected = Expected(vm, rail);
            Assert.InRange(expected.Count, 2, (int)rail.TrackLength + 1); // at most one tick per DIP
            AssertSearchTicks(rail, expected); // one geometry, one figure per merged position
        });
    }

    [Fact]
    public async Task The_renderer_swap_rebuild_keeps_them()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var index = Attach(vm);
            index.SetPage(10, [H(10, 300, 312)]);
            index.SetPage(20, [H(20, 300, 312)]);
            Refresh(view);
            var before = rail.SearchTicks.Centres.ToArray();
            Assert.Equal(2, before.Length);

            // A plain Save swaps the renderer (and rebuilds the view's bitmaps); the search and its ticks stay.
            view.FlushEditorLoads();
            WpfTestThread.PumpUntil(() => view.SurfaceFor(0)?.Editor is not null);
            InkPoint[] line = [new(10, 20, 0.5f, null, null, null, 0), new(30, 20, 0.5f, null, null, null, 10)];
            Assert.True(view.SurfaceFor(0)!.Editor!.CommitStroke(line, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1));
            var renderer = vm.Renderer;
            var saving = vm.SaveCommand.ExecuteAsync(null);
            WpfTestThread.PumpUntil(() => saving.IsCompleted, 30_000);
            saving.GetAwaiter().GetResult();
            Assert.Null(vm.ErrorMessage);
            Assert.NotSame(renderer, vm.Renderer);
            Refresh(view);

            Assert.Same(index, vm.Search.Index);
            Assert.Equal(before, rail.SearchTicks.Centres);
            Assert.NotNull(rail.SearchTicks.TickGeometry);
        });
    }

    private static SearchHit H(int page, double top, double bottom, double left = 72) =>
        new(page, [new SearchRect(left, top, left + 40, bottom)]);

    /// <summary>A query's index, as the window attaches it when a query is applied.</summary>
    private static SearchIndex Attach(MainViewModel vm)
    {
        var index = new SearchIndex(_pageCount);
        vm.Search.Attach(index);
        return index;
    }

    private static IReadOnlyList<double> Expected(MainViewModel vm, ScrollRail rail) =>
        RailTickLayout.SearchPositions(vm.Search.Index?.AllHits() ?? [], vm.Viewport.Layout!, vm.Viewport.WindowHeightPt, rail.TrackLength)
            .Select(p => p + (rail.KnobHeight / 2))
            .ToArray();

    private static void AssertSearchTicks(ScrollRail rail, IReadOnlyList<double> expected)
    {
        Assert.Equal(expected.Count, rail.SearchTicks.Centres.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], rail.SearchTicks.Centres[i], 6);
        }

        Assert.NotNull(rail.SearchTicks.TickGeometry);
        Assert.Equal(expected.Count, rail.SearchTicks.TickGeometry!.GetFlattenedPathGeometry().Figures.Count);
    }

    /// <summary>The search's refresh throttle runs, and the rail (shown) draws its ticks.</summary>
    private void Refresh(DocumentView view)
    {
        _scheduler.Advance(SearchViewModel.RefreshInterval);
        _scheduler.Advance(MarksViewModel.RefreshInterval);
        view.Rail.NotifyActivity();
        ScrollRailTests.Settle(view);
    }

    private static bool IsWithinInkSurface(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is InkSurface)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<UIElement> Owners(AutomationPeer? peer)
    {
        foreach (var child in peer?.GetChildren() ?? [])
        {
            if (child is UIElementAutomationPeer element)
            {
                yield return element.Owner;
            }

            foreach (var owner in Owners(child))
            {
                yield return owner;
            }
        }
    }

    /// <summary>Opens a 30-page generated PDF, then runs the test on a view laid out at 1000 x 800 DIPs.</summary>
    private async Task WithViewAsync(Action<MainViewModel, DocumentView> test)
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfFactory.Write(path, Enumerable.Repeat((612.0, 792.0), _pageCount).ToArray());
        var vm = new MainViewModel(new FakeFileDialogService { NextPath = path }, scheduler: _scheduler);
        await vm.OpenPdfCommand.ExecuteAsync(null);
        try
        {
            WpfTestThread.Run(() =>
            {
                var view = new DocumentView { BitmapRenderer = new FakeBitmapRenderer() };
                view.DataContext = vm;
                ScrollRailTests.LayOut(view, 1000, 800);
                test(vm, view);
            });
        }
        finally
        {
            if (vm.Renderer is { } renderer)
            {
                await renderer.DisposeAsync();
            }
        }
    }
}
