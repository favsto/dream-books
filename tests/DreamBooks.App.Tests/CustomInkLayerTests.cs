using System.Windows;
using System.Windows.Media;
using DreamBooks.App.Ink;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.App.Tests;

public sealed class CustomInkLayerTests
{
    private static InkStroke NewStroke(double y = 10) => new(
        Guid.NewGuid(),
        InkTool.Pen,
        InkColor.FromRgb(0, 0, 0),
        1,
        [new InkPoint(10, y, 0.5f, null, null, null, 0), new InkPoint(60, y + 30, 0.5f, null, null, null, 10)]);

    private static Visual[] Visuals(CustomInkLayer layer) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(layer))
            .Select(i => (Visual)VisualTreeHelper.GetChild(layer, i))
            .ToArray();

    [Fact]
    public void Adding_a_stroke_adds_exactly_one_visual_and_keeps_the_others_by_reference()
    {
        WpfTestThread.Run(() =>
        {
            var a = NewStroke();
            var b = NewStroke();
            var layer = new CustomInkLayer { Strokes = [a, b] };
            var before = Visuals(layer);

            layer.Strokes = [a, b, NewStroke()];

            var after = Visuals(layer);
            Assert.Equal(3, after.Length);
            Assert.Same(before[0], after[0]);
            Assert.Same(before[1], after[1]);
        });
    }

    [Fact]
    public void Removing_a_stroke_removes_only_its_visual()
    {
        WpfTestThread.Run(() =>
        {
            var strokes = new[] { NewStroke(), NewStroke(), NewStroke() };
            var layer = new CustomInkLayer { Strokes = strokes };
            var before = Visuals(layer);

            layer.Strokes = [strokes[0], strokes[2]];

            var after = Visuals(layer);
            Assert.Equal(2, after.Length);
            Assert.Same(before[0], after[0]);
            Assert.Same(before[2], after[1]);
            Assert.Equal(2, layer.CachedCount);
        });
    }

    [Fact]
    public void Restored_stroke_after_undo_appears_in_its_original_z_position()
    {
        WpfTestThread.Run(() =>
        {
            var strokes = new[] { NewStroke(), NewStroke(), NewStroke() };
            var layer = new CustomInkLayer { Strokes = strokes };
            var before = Visuals(layer);

            layer.Strokes = [strokes[0], strokes[2]];
            layer.Strokes = strokes;

            var after = Visuals(layer);
            Assert.Equal(3, after.Length);
            Assert.Same(before[0], after[0]);
            Assert.Same(before[2], after[2]);
            Assert.Equal(3, layer.CachedCount);
        });
    }

    [Fact]
    public void Zoom_changes_only_the_transform_and_keeps_every_visual_instance()
    {
        WpfTestThread.Run(() =>
        {
            var layer = new CustomInkLayer { Strokes = [NewStroke(), NewStroke()] };
            var before = Visuals(layer);

            layer.Scale = 4;

            Assert.Equal(before, Visuals(layer));
            Assert.Equal(4, layer.Scale);
            var transform = Assert.IsType<ScaleTransform>(layer.RenderTransform);
            Assert.Equal(4, transform.ScaleX);
            Assert.Equal(4, transform.ScaleY);
        });
    }

    [Fact]
    public void Curve_or_smoothing_change_rebuilds_all_visuals()
    {
        WpfTestThread.Run(() =>
        {
            var strokes = new[] { NewStroke(), NewStroke() };
            var layer = new CustomInkLayer { Strokes = strokes };
            var before = Visuals(layer);

            layer.Curve = PressureCurve.Firm;
            layer.Smoothing = SmoothingLevel.Strong;
            WpfTestThread.Drain();

            var afterBoth = Visuals(layer);
            Assert.Equal(2, afterBoth.Length);
            Assert.DoesNotContain(afterBoth, v => before.Contains(v));
            Assert.Equal(2, layer.CachedCount);

            layer.Smoothing = SmoothingLevel.Light;
            WpfTestThread.Drain();
            Assert.DoesNotContain(Visuals(layer), v => afterBoth.Contains(v));
        });
    }

    [Fact]
    public void Visual_order_matches_stroke_order_after_mixed_add_and_remove()
    {
        WpfTestThread.Run(() =>
        {
            var random = new Random(42);
            var layer = new CustomInkLayer();
            var current = new List<InkStroke>();
            var known = new Dictionary<Guid, Visual>();
            var pool = new List<InkStroke>();

            for (var step = 0; step < 50; step++)
            {
                var roll = random.Next(3);
                if (roll == 0 || current.Count == 0)
                {
                    var stroke = NewStroke(step);
                    pool.Add(stroke);
                    current.Insert(random.Next(current.Count + 1), stroke);
                }
                else if (roll == 1)
                {
                    current.RemoveAt(random.Next(current.Count));
                }
                else
                {
                    var restore = pool[random.Next(pool.Count)];
                    if (!current.Contains(restore))
                    {
                        current.Insert(random.Next(current.Count + 1), restore);
                    }
                }

                layer.Strokes = current.ToArray();

                var visuals = Visuals(layer);
                Assert.Equal(current.Count, visuals.Length);
                Assert.Equal(current.Count, layer.CachedCount);
                for (var i = 0; i < current.Count; i++)
                {
                    if (known.TryGetValue(current[i].Id, out var previous) && visuals.Contains(previous))
                    {
                        Assert.Same(previous, visuals[i]);
                    }

                    known[current[i].Id] = visuals[i];
                }
            }
        });
    }

    [Fact]
    public void Erase_preview_swap_of_DisplayStrokes_updates_only_changed_visuals()
    {
        WpfTestThread.Run(() =>
        {
            var strokes = new[] { NewStroke(), NewStroke(), NewStroke() };
            var layer = new CustomInkLayer { Strokes = strokes };
            var before = Visuals(layer);

            var survivor = NewStroke(99); // partial-erase remainder: new Id
            layer.Strokes = [strokes[0], survivor, strokes[2]];
            var preview = Visuals(layer);
            Assert.Same(before[0], preview[0]);
            Assert.NotSame(before[1], preview[1]);
            Assert.Same(before[2], preview[2]);

            layer.Strokes = strokes; // gesture ended or cancelled
            var back = Visuals(layer);
            Assert.Same(before[0], back[0]);
            Assert.Same(before[2], back[2]);
            Assert.Equal(3, back.Length);
        });
    }

    [Fact]
    public void Layer_is_not_hit_testable_and_ignores_duplicate_strokes()
    {
        WpfTestThread.Run(() =>
        {
            var stroke = NewStroke();
            var layer = new CustomInkLayer { Strokes = [stroke, stroke] };
            Assert.False(layer.IsHitTestVisible);
            Assert.Single(Visuals(layer));
        });
    }
}
