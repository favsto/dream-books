namespace DreamBooks.App.Tests;

/// <summary>
/// The scroll rail preview's ink (UI-07) is read as the marks list reads it: the stored strokes of a page nobody opened,
/// without loading the page into the session or the renderer's stroke cache; the live strokes of a loaded page.
/// </summary>
public sealed partial class MarksIntegrationTests
{
    [Fact]
    public void The_rail_preview_reads_a_pages_stored_ink_without_loading_the_page()
    {
        var stored = MarginLine(40, 200, 360);
        var path = Author("rail-ink.pdf", 40, new() { [25] = [stored] });
        WpfTestThread.Run(() =>
        {
            var scheduler = new ManualScheduler();
            var vm = NewVm(path, scheduler, probe: null);
            try
            {
                Wait(vm.OpenPdfCommand.ExecuteAsync(null));
                var cachedBefore = Wait(vm.Renderer!.CachedStrokePageCountAsync());

                var ink = Wait(vm.LoadPageInkAsync(25, CancellationToken.None))!;
                Assert.Equal(25, ink.PageIndex);
                Assert.Equal(_widthPt, ink.WidthPt);
                Assert.Equal(_heightPt, ink.HeightPt);
                Assert.Equal([stored.Id], ink.Strokes.Select(s => s.Id));
                Assert.False(vm.Session!.IsPageLoaded(25));
                Assert.Equal(cachedBefore, Wait(vm.Renderer.CachedStrokePageCountAsync()));

                Assert.Empty(Wait(vm.LoadPageInkAsync(26, CancellationToken.None))!.Strokes); // no ink: an empty page
                Assert.Null(Wait(vm.LoadPageInkAsync(40, CancellationToken.None))); // no such page

                // A loaded page gives its live strokes.
                var editor = Wait(vm.EnsureEditorAsync(25));
                Assert.True(editor.CommitStroke(
                    [new(300, 300, 0.5f, null, null, null, 0), new(400, 300, 0.5f, null, null, null, 10)],
                    DreamBooks.Core.Model.InkTool.Pen,
                    DreamBooks.Core.Model.InkColor.FromRgb(0, 0, 0),
                    1));
                Assert.Equal(2, Wait(vm.LoadPageInkAsync(25, CancellationToken.None))!.Strokes.Count);
            }
            finally
            {
                if (vm.Renderer is { } renderer)
                {
                    Wait(renderer.DisposeAsync().AsTask());
                }
            }
        });
    }
}
