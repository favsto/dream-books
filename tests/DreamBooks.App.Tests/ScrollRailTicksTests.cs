using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.App.Views;
using DreamBooks.Core.Marks;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// The marks' ticks on the scroll rail (FR-101, UI-07, NFR-32): where they are drawn (the knob's own mapping, merged
/// within one DIP), that they follow the index through the marks list's throttle, never take the pen, stay out of the
/// automation tree and turn opaque with transparency off. Their look is checked on the device.
/// </summary>
public sealed class ScrollRailTicksTests : IDisposable
{
    private const int _pageCount = 30;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-ticks-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

    public ScrollRailTicksTests() => Directory.CreateDirectory(_dir);

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
    public async Task Rail_draws_one_tick_per_merged_position()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var index = vm.MarkIndexForTest!;
            var near = M(10, 100, 200);
            var last = M(_pageCount - 1, 700, 760); // beyond the last scrollable offset
            index.SetPage(10, [near, M(10, 100.2, 200.2)]); // the second is a fraction of a DIP lower: merged
            index.SetPage(20, [M(20, 300, 400)]);
            index.SetPage(_pageCount - 1, [last]);
            Refresh(view);

            var expected = Expected(vm, rail);
            Assert.Equal(3, expected.Count);
            AssertTicks(rail, expected);
            Assert.Equal(rail.TrackLength + (rail.KnobHeight / 2), rail.Ticks.Centres[^1], 6); // the track's end

            // At a mark the knob sits on its tick, also at the clamped end.
            foreach (var mark in new[] { near, last })
            {
                vm.Viewport.ScrollToMark(mark, recordHistory: false);
                ScrollRailTests.Settle(view);
                var knobCentre = rail.KnobOffset.Y + (rail.KnobHeight / 2);
                Assert.Contains(rail.Ticks.Centres, c => Math.Abs(c - knobCentre) <= 1);
            }

            // A zoom or window change moves them with the knob's mapping.
            vm.ZoomVm.ZoomInCommand.Execute(null);
            ScrollRailTests.Settle(view);
            rail.NotifyActivity();
            AssertTicks(rail, Expected(vm, rail));
            ScrollRailTests.LayOut(view, 1000, 500);
            rail.NotifyActivity();
            AssertTicks(rail, Expected(vm, rail));
        });
    }

    [Fact]
    public async Task Ticks_are_not_hit_testable_and_the_pen_still_reaches_the_ink_surface()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            vm.MarkIndexForTest!.SetPage(5, [M(5, 300, 400)]); // a tick near the top of the track, over the shown page
            Refresh(view);
            Assert.Single(rail.Ticks.Centres);

            Assert.False(rail.Ticks.IsHitTestVisible);
            Assert.False(rail.Ticks.Focusable);
            var point = rail.Ticks.TranslatePoint(new Point(rail.Ticks.ActualWidth / 2, rail.Ticks.Centres[0]), view);
            var hit = ScrollRailTests.HitAt(view, point);
            Assert.False(ScrollRailTests.IsWithin(hit, rail));
            Assert.True(IsWithinInkSurface(hit), "the pen on a tick reaches the page's ink surface");
            Assert.False(view.RoutePointerDown(point, PointerKind.Pen));
            Assert.False(rail.IsPressed);

            // Decorative: no automation peer of its own, and none among the rail's.
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(rail.Ticks));
            var peer = UIElementAutomationPeer.CreatePeerForElement(rail);
            Assert.DoesNotContain(rail.Ticks, Owners(peer));
        });
    }

    [Fact]
    public void Ticks_use_opaque_brushes_when_transparency_is_off()
    {
        WpfTestThread.Run(() =>
        {
            var rail = new ScrollRail { TransparencyEnabled = () => false };
            rail.ApplyTransparency();
            Assert.Same(Application.Current.FindResource("RailMarkTickBrushOpaque"), rail.Ticks.Fill);
            Assert.Equal(1, ((SolidColorBrush)rail.Ticks.Fill!).Opacity);

            rail.TransparencyEnabled = () => true;
            rail.ApplyTransparency();
            Assert.Same(Application.Current.FindResource("RailMarkTickBrush"), rail.Ticks.Fill);
            Assert.True(((SolidColorBrush)rail.Ticks.Fill!).Opacity < 1);

            Assert.IsType<double>(Application.Current.FindResource("RailMarkTickLength"));
            Assert.IsType<double>(Application.Current.FindResource("RailMarkTickThickness"));
        });
    }

    [Fact]
    public async Task Ticks_appear_and_vanish_with_the_index_updates()
    {
        await WithViewAsync((vm, view) =>
        {
            var rail = view.Rail;
            var index = vm.MarkIndexForTest!;
            rail.NotifyActivity();
            Assert.Empty(rail.Ticks.Centres);
            Assert.Null(rail.Ticks.TickGeometry); // nothing drawn without marks

            index.SetPage(15, [M(15, 100, 300)]);
            rail.NotifyActivity();
            Assert.Empty(rail.Ticks.Centres); // throttled with the marks list: not yet
            Refresh(view);
            Assert.Single(rail.Ticks.Centres);

            index.SetPage(25, [M(25, 100, 300)]);
            Refresh(view);
            Assert.Equal(2, rail.Ticks.Centres.Count);

            index.SetPage(15, []); // the line erased or undone
            index.SetPage(25, []);
            Refresh(view);
            Assert.Empty(rail.Ticks.Centres);
            Assert.Null(rail.Ticks.TickGeometry);

            // Hidden, the rail does not draw them; shown again, they are current.
            rail.HideNow();
            index.SetPage(12, [M(12, 100, 300)]);
            _scheduler.Advance(MarksViewModel.RefreshInterval);
            Assert.Empty(rail.Ticks.Centres);
            rail.NotifyActivity();
            Assert.Single(rail.Ticks.Centres);

            // Another document (none): the ticks go with its marks.
            vm.Marks.Attach(null);
            rail.NotifyActivity();
            Assert.Empty(rail.Ticks.Centres);
        });
    }

    private static Mark M(int page, double top, double bottom) => new(page, MarkSide.Left, top, bottom, [Guid.NewGuid()]);

    private static IReadOnlyList<double> Expected(MainViewModel vm, ScrollRail rail) =>
        RailTickLayout.Positions(vm.Marks.Marks, vm.Viewport.Layout!, vm.Viewport.WindowHeightPt, rail.TrackLength)
            .Select(p => p + (rail.KnobHeight / 2))
            .ToArray();

    private static void AssertTicks(ScrollRail rail, IReadOnlyList<double> expected)
    {
        Assert.Equal(expected.Count, rail.Ticks.Centres.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], rail.Ticks.Centres[i], 6);
        }

        // One geometry for all of them: one figure per merged position.
        Assert.NotNull(rail.Ticks.TickGeometry);
        Assert.Equal(expected.Count, rail.Ticks.TickGeometry!.GetFlattenedPathGeometry().Figures.Count);
    }

    private void Refresh(DocumentView view)
    {
        _scheduler.Advance(MarksViewModel.RefreshInterval); // the marks list's throttle
        view.Rail.NotifyActivity(); // shown: ticks are drawn only while the rail is
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
            await vm.Renderer!.DisposeAsync();
        }
    }
}
