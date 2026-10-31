using DreamBooks.App.Ink;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>
/// Save through the real pipeline: PdfDocumentSaver, FileReplacer and the swappable PdfRenderer on a generated
/// PDF (FR-80, FR-81, FR-82, FR-83, NFR-10).
/// </summary>
public sealed class SaveRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-roundtrip-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();

    public SaveRoundTripTests() => Directory.CreateDirectory(_dir);

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
    public async Task Save_with_the_real_writer_puts_the_stroke_in_the_file_and_keeps_the_page_content()
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfFactory.WriteWithText(path, "Hello Dream Books", (612, 792), (300, 400));
        var before = await RenderFirstPageAsync(path);
        _dialogs.NextPath = path;
        var vm = new MainViewModel(_dialogs);
        try
        {
            await vm.OpenPdfCommand.ExecuteAsync(null);
            var oldRenderer = vm.Renderer;
            Assert.NotNull(oldRenderer);
            InkPoint[] points = [new(50, 100, 0.4f, null, null, null, 0), new(250, 140, 0.8f, null, null, null, 30)];
            Assert.True(vm.Editor!.CommitStroke(points, InkTool.Pen, Palette.Colors[0], 1));
            var stroke = vm.Editor.Page.Strokes[0];
            Assert.True(vm.IsDirty);

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Null(vm.ErrorMessage);
            Assert.False(vm.IsDirty);
            Assert.Empty(Directory.GetFiles(_dir, "*.drbk-tmp"));

            await using (var fresh = await PdfRenderer.OpenAsync(path))
            {
                var stored = Assert.Single((await fresh.LoadStrokesAsync(0)).Strokes);
                Assert.Equal(stroke.Id, stored.Id);
                Assert.Equal(stroke.Points.Count, stored.Points.Count);
                Assert.Empty((await fresh.LoadStrokesAsync(1)).Strokes);
                Assert.Equal(2, fresh.PageCount);
            }

            // The viewer was swapped onto the new file and still draws (our strokes hidden, content unchanged).
            var renderer = vm.Renderer;
            Assert.NotNull(renderer);
            Assert.NotSame(oldRenderer, renderer);
            var after = await renderer.RenderPageAsync(0, 200, 259);
            Assert.True(MeanDifference(before, after) <= 2.0, "The page content changed after the save.");

            // Page 2 still loads through the swapped renderer, and a second save works too.
            await vm.GoToPageCommand.ExecuteAsync(2);
            Assert.Null(vm.ErrorMessage);
            Assert.True(vm.Editor!.CommitStroke(points, InkTool.Pen, Palette.Colors[1], 1));
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Null(vm.ErrorMessage);
            Assert.False(vm.IsDirty);
            await using (var fresh = await PdfRenderer.OpenAsync(path))
            {
                Assert.Single((await fresh.LoadStrokesAsync(0)).Strokes);
                Assert.Single((await fresh.LoadStrokesAsync(1)).Strokes);
            }
        }
        finally
        {
            if (vm.Renderer is { } renderer)
            {
                await renderer.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Released_viewer_keeps_dimensions_fails_page_loads_cleanly_and_reopens()
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfFactory.Write(path, (612, 792), (300, 400));
        var viewer = new Services.SwappableRenderer(await PdfRenderer.OpenAsync(path));
        await using (viewer)
        {
            var changes = 0;
            viewer.CurrentChanged += (_, _) => changes++;

            await viewer.ReleaseAsync();
            await viewer.ReleaseAsync(); // idempotent

            Assert.Null(viewer.Current);
            Assert.Equal(1, changes);
            Assert.Equal(2, viewer.PageCount);
            Assert.Equal(300, viewer.GetPageDimensions(1).WidthPt);
            await Assert.ThrowsAsync<InvalidOperationException>(() => viewer.LoadStrokesAsync(0));
            File.Move(path, path + ".moved"); // nothing holds the file any more
            File.Move(path + ".moved", path);

            await viewer.ReopenAsync(path, CancellationToken.None);
            Assert.NotNull(viewer.Current);
            Assert.Equal(2, changes);
            Assert.Empty((await viewer.LoadStrokesAsync(0)).Strokes);
        }

        Assert.Null(viewer.Current);
    }

    private static async Task<RenderedPage> RenderFirstPageAsync(string path)
    {
        await using var renderer = await PdfRenderer.OpenAsync(path);
        return await renderer.RenderPageAsync(0, 200, 259);
    }

    private static double MeanDifference(RenderedPage a, RenderedPage b)
    {
        Assert.Equal(a.Bgra.Length, b.Bgra.Length);
        long total = 0;
        for (var i = 0; i < a.Bgra.Length; i++)
        {
            total += Math.Abs(a.Bgra[i] - b.Bgra[i]);
        }

        return (double)total / a.Bgra.Length;
    }
}
