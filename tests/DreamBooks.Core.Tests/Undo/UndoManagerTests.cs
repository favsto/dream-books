using DreamBooks.Core.Undo;

namespace DreamBooks.Core.Tests.Undo;

public class UndoManagerTests
{
    private readonly List<string> _log = [];

    private RecordingCommand Cmd(string name) => new(name, _log);

    [Fact]
    public void Execute_runs_Do_and_enables_undo()
    {
        var manager = new UndoManager();

        manager.Execute(Cmd("1"));

        Assert.Equal(["do1"], _log);
        Assert.True(manager.CanUndo);
        Assert.False(manager.CanRedo);
    }

    [Fact]
    public void Undo_then_Redo_replays_in_order()
    {
        var manager = new UndoManager();
        manager.Execute(Cmd("1"));
        manager.Execute(Cmd("2"));

        Assert.True(manager.Undo());
        Assert.True(manager.Undo());
        Assert.True(manager.Redo());

        Assert.Equal(["do1", "do2", "undo2", "undo1", "do1"], _log);
    }

    [Fact]
    public void New_command_after_undo_clears_redo()
    {
        var manager = new UndoManager();
        manager.Execute(Cmd("1"));
        manager.Undo();
        Assert.True(manager.CanRedo);

        manager.Execute(Cmd("2"));

        Assert.False(manager.CanRedo);
    }

    [Fact]
    public void Undo_and_Redo_return_false_when_empty()
    {
        var manager = new UndoManager();

        Assert.False(manager.Undo());
        Assert.False(manager.Redo());
        Assert.Empty(_log);
    }

    [Fact]
    public void Keeps_at_least_200_steps_and_drops_oldest_beyond_capacity()
    {
        var manager = new UndoManager(500);
        for (var i = 1; i <= 501; i++)
        {
            manager.Execute(Cmd(i.ToString()));
        }

        for (var i = 0; i < 500; i++)
        {
            Assert.True(manager.Undo());
        }

        Assert.False(manager.Undo());
        Assert.DoesNotContain("undo1", _log);
        Assert.Contains("undo2", _log);
    }

    [Fact]
    public void StateChanged_fires_on_execute_undo_redo_clear()
    {
        var manager = new UndoManager();
        var count = 0;
        manager.StateChanged += (_, _) => count++;

        manager.Execute(Cmd("1"));
        Assert.Equal(1, count);
        manager.Undo();
        Assert.Equal(2, count);
        manager.Redo();
        Assert.Equal(3, count);
        manager.Clear();
        Assert.Equal(4, count);

        manager.Undo();
        manager.Clear();
        Assert.Equal(4, count);
    }

    [Fact]
    public void Redo_with_throwing_Do_keeps_redo_stack_and_raises_no_event()
    {
        var manager = new UndoManager();
        var command = new ThrowingCommand();
        manager.Execute(command);
        manager.Undo();
        command.ThrowOnDo = true;
        var count = 0;
        manager.StateChanged += (_, _) => count++;

        Assert.Throws<InvalidOperationException>(() => manager.Redo());

        Assert.True(manager.CanRedo);
        Assert.False(manager.CanUndo);
        Assert.Equal(0, count);
        command.ThrowOnDo = false;
        Assert.True(manager.Redo());
        Assert.True(manager.CanUndo);
    }

    [Fact]
    public void Undo_with_throwing_Undo_keeps_state_and_raises_no_event()
    {
        var manager = new UndoManager();
        var command = new ThrowingCommand();
        manager.Execute(command);
        command.ThrowOnUndo = true;
        var count = 0;
        manager.StateChanged += (_, _) => count++;

        Assert.Throws<InvalidOperationException>(() => manager.Undo());

        Assert.True(manager.CanUndo);
        Assert.False(manager.CanRedo);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Execute_with_throwing_Do_keeps_both_stacks_and_raises_no_event()
    {
        var manager = new UndoManager();
        manager.Execute(Cmd("1"));
        manager.Execute(Cmd("2"));
        manager.Undo();
        var count = 0;
        manager.StateChanged += (_, _) => count++;

        Assert.Throws<InvalidOperationException>(() => manager.Execute(new ThrowingCommand { ThrowOnDo = true }));

        Assert.True(manager.CanUndo);
        Assert.True(manager.CanRedo);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Exactly_capacity_commands_are_all_undoable()
    {
        var manager = new UndoManager(500);
        for (var i = 1; i <= 500; i++)
        {
            manager.Execute(Cmd(i.ToString()));
        }

        for (var i = 0; i < 500; i++)
        {
            Assert.True(manager.Undo());
        }

        Assert.False(manager.Undo());
        Assert.Contains("undo1", _log);
    }

    [Fact]
    public void Capacity_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UndoManager(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UndoManager(-1));
    }
}
