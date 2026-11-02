using DreamBooks.Core.Documents;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Documents;

public class DocumentSessionHighlighterTests
{
    private static readonly DocumentFingerprint _fingerprint = new(100, 200, new string('a', 64));

    private static DocumentSession Open(int pages) => new(new FakePageSource(pages), @"C:\docs\book.pdf", _fingerprint);

    private static HighlighterSettings Chisel() =>
        HighlighterSettings.Default with { Color = HighlighterPresets.Pink, Chisel = true, ChiselAngleDeg = 30, StraightLine = true };

    [Fact]
    public void A_new_session_uses_the_default_highlighter_settings()
    {
        using var session = Open(1);

        Assert.Same(HighlighterSettings.Default, session.HighlighterSettings);
    }

    [Fact]
    public async Task Setting_the_highlighter_reaches_every_loaded_page_editor()
    {
        using var session = Open(3);
        var first = await session.EnsureEditorAsync(0);
        await session.GoToPageAsync(2);
        var third = session.CurrentEditor!;
        var settings = Chisel();

        session.HighlighterSettings = settings;

        Assert.Same(settings, first.HighlighterSettings);
        Assert.Same(settings, third.HighlighterSettings);
    }

    [Fact]
    public async Task Editors_created_after_the_change_get_the_current_highlighter()
    {
        using var session = Open(3);
        var settings = Chisel();
        session.HighlighterSettings = settings;

        var later = await session.EnsureEditorAsync(1);
        await session.GoToPageAsync(2);

        Assert.Same(settings, later.HighlighterSettings);
        Assert.Same(settings, session.CurrentEditor!.HighlighterSettings);
    }

    [Fact]
    public void Null_highlighter_settings_are_rejected()
    {
        using var session = Open(1);

        Assert.Throws<ArgumentNullException>(() => session.HighlighterSettings = null!);
    }
}
