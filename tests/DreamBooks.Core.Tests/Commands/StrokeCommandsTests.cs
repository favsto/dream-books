using DreamBooks.Core.Commands;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Tests.Commands;

public class StrokeCommandsTests
{
    private static InkStroke S(double x)
        => new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1, [new InkPoint(x, 0, 0.5f, null, null, null, 0)]);

    private static Page NewPage() => new(0, 612, 792);

    [Fact]
    public void Add_appends_and_undo_restores_previous_list()
    {
        var page = NewPage();
        var a = S(1);
        var b = S(2);
        var c = S(3);
        page.SetStrokes([a]);
        var command = new AddStrokesCommand(page, [b, c]);

        command.Do();
        Assert.Equal(new[] { a, b, c }, page.Strokes);

        command.Undo();
        Assert.Equal(new[] { a }, page.Strokes);
    }

    [Fact]
    public void Add_then_undo_then_redo_restores_same_stroke_instances()
    {
        var page = NewPage();
        var a = S(1);
        var b = S(2);
        var command = new AddStrokesCommand(page, [a, b]);

        command.Do();
        command.Undo();
        command.Do();

        Assert.Equal(2, page.Strokes.Count);
        Assert.Same(a, page.Strokes[0]);
        Assert.Same(b, page.Strokes[1]);
    }

    [Fact]
    public void Erase_undo_restores_original_order()
    {
        var page = NewPage();
        var a = S(1);
        var b = S(2);
        var c = S(3);
        page.SetStrokes([a, b, c]);
        var command = new EraseStrokesCommand(page, page.Strokes, [a, c]);

        command.Do();
        Assert.Equal(new[] { a, c }, page.Strokes);

        command.Undo();
        Assert.Equal(3, page.Strokes.Count);
        Assert.Same(a, page.Strokes[0]);
        Assert.Same(b, page.Strokes[1]);
        Assert.Same(c, page.Strokes[2]);
    }

    [Fact]
    public void Mixed_sequence_of_50_operations_round_trips()
    {
        var page = NewPage();
        var manager = new UndoManager();
        var random = new Random(42);
        var states = new List<InkStroke[]> { page.Strokes.ToArray() };

        for (var step = 0; step < 50; step++)
        {
            if (page.Strokes.Count > 0 && random.Next(2) == 0)
            {
                var before = page.Strokes;
                var victim = before[random.Next(before.Count)];
                manager.Execute(new EraseStrokesCommand(page, before, before.Where(s => !ReferenceEquals(s, victim)).ToArray()));
            }
            else
            {
                manager.Execute(new AddStrokesCommand(page, [S(step)]));
            }

            states.Add(page.Strokes.ToArray());
        }

        for (var i = 49; i >= 0; i--)
        {
            Assert.True(manager.Undo());
            AssertSameInstances(states[i], page.Strokes);
        }

        for (var i = 1; i <= 50; i++)
        {
            Assert.True(manager.Redo());
            AssertSameInstances(states[i], page.Strokes);
        }
    }

    [Fact]
    public void Stroke_commands_expose_their_page_index_and_data()
    {
        var page = new Page(7, 612, 792);
        var a = S(1);
        var b = S(2);
        var c = S(3);

        var add = new AddStrokesCommand(page, [a, b]);
        var erase = new EraseStrokesCommand(page, [a, b], [b, c]);

        IPageCommand addCommand = add;
        IPageCommand eraseCommand = erase;
        Assert.Equal(7, addCommand.PageIndex);
        Assert.Equal(7, eraseCommand.PageIndex);
        Assert.Equal([a, b], add.Strokes);
        Assert.Equal([a, b], erase.Before);
        Assert.Equal([b, c], erase.After);
        Assert.IsNotType<InkStroke[]>(add.Strokes);
        Assert.IsNotType<InkStroke[]>(erase.Before);
        Assert.IsNotType<InkStroke[]>(erase.After);
    }

    private static void AssertSameInstances(IReadOnlyList<InkStroke> expected, IReadOnlyList<InkStroke> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Same(expected[i], actual[i]);
        }
    }
}
