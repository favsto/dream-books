using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;
using DreamBooks.Pdf.Interop;
using static DreamBooks.Pdf.Tests.TestPdfFactory;

namespace DreamBooks.Pdf.Tests;

/// <summary>Strokes edited with the lasso operations survive save and reopen unchanged (FR-51, FR-83).</summary>
public sealed class SelectionEditsRoundTripTests : IDisposable
{
    private const double _pageW = 595;
    private const double _pageH = 842;

    private readonly string _dir;

    public SelectionEditsRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DreamBooks.Pdf.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Moved_strokes_survive_save_and_reopen() // FR-51, FR-83
    {
        var (path, editor) = await StartAsync(pages: 1);
        var ids = SeedPen(editor, 100, 200);
        var before = editor.Page.Strokes.ToArray();

        editor.MoveStrokes([ids[0]], 25, 40);

        await SaveAndAssertAsync(path, editor.Page, before);
    }

    [Fact]
    public async Task Scaled_strokes_keep_width_after_reopen() // FR-51, FR-83
    {
        var (path, editor) = await StartAsync(pages: 1);
        var ids = SeedPen(editor, 100, 200);
        var before = editor.Page.Strokes.ToArray();

        var scaled = editor.ScaleStrokes([ids[0]], 1.5, new PointD(100, 100));

        await SaveAndAssertAsync(path, editor.Page, before);
        var back = (await ReadPageAsync(path, 0)).Strokes.Single(s => s.Id == scaled[0]);
        Assert.Equal(3, back.BaseWidthPt, 6);
    }

    [Fact]
    public async Task Recoloured_highlighter_keeps_transparency_after_reopen() // FR-51, FR-83, FR-44
    {
        var (path, editor) = await StartAsync(pages: 1);
        editor.CommitStroke([P(100, 300), P(300, 300)], InkTool.Highlighter, new InkColor(89, 255, 235, 0), 14);
        var id = editor.Page.Strokes[0].Id;
        var before = editor.Page.Strokes.ToArray();

        var recoloured = editor.RecolorStrokes([id], InkColor.FromRgb(0, 200, 0));

        await SaveAndAssertAsync(path, editor.Page, before);
        var back = (await ReadPageAsync(path, 0)).Strokes.Single(s => s.Id == recoloured[0]);
        Assert.Equal(new InkColor(89, 0, 200, 0), back.Color);
    }

    [Fact]
    public async Task Deleted_strokes_are_gone_after_reopen() // FR-51, FR-83
    {
        var (path, editor) = await StartAsync(pages: 1);
        var ids = SeedPen(editor, 100, 200);
        var before = editor.Page.Strokes.ToArray();

        editor.DeleteStrokes([ids[0]]);

        await SaveAndAssertAsync(path, editor.Page, before);
        Assert.DoesNotContain((await ReadPageAsync(path, 0)).Strokes, s => s.Id == ids[0]);
    }

    [Fact]
    public async Task Pasted_strokes_on_other_page_survive() // FR-51, FR-83
    {
        var (path, first) = await StartAsync(pages: 2);
        var ids = SeedPen(first, 100, 200);
        var clipboard = new StrokeClipboard();
        clipboard.Set(first.Strokes([ids[0]]));
        var second = new PageEditor(new Page(1, _pageW, _pageH), new UndoManager());
        var before = second.Page.Strokes.ToArray();

        second.PasteStrokes(clipboard);

        await SaveAndAssertAsync(path, second.Page, before);
    }

    private static InkPoint P(double x, double y) => new(x, y, 0.5f, null, null, null, 0);

    private async Task<(string Path, PageEditor Editor)> StartAsync(int pages)
    {
        string path = Path.Combine(_dir, "source.pdf");
        WriteWithText(path, "Selection round trip", Enumerable.Repeat((_pageW, _pageH), pages).ToArray());
        var editor = new PageEditor(new Page(0, _pageW, _pageH), new UndoManager());
        await Task.CompletedTask;
        return (path, editor);
    }

    /// <summary>Two pen strokes at the given heights; the file gets them as an earlier save would have written.</summary>
    private static IReadOnlyList<Guid> SeedPen(PageEditor editor, double y1, double y2)
    {
        editor.CommitStroke([P(100, y1), P(150, y1 + 5), P(200, y1)], InkTool.Pen, InkColor.FromRgb(0, 0, 0), 2);
        editor.CommitStroke([P(100, y2), P(150, y2 + 5), P(200, y2)], InkTool.Pen, InkColor.FromRgb(0, 0, 255), 2);
        return editor.Page.Strokes.Select(s => s.Id).ToArray();
    }

    /// <summary>
    /// Writes <paramref name="before"/> into the file as the earlier state, saves the diff to
    /// <paramref name="page"/>'s current strokes, reopens and compares every stroke.
    /// </summary>
    private async Task SaveAndAssertAsync(string path, Page page, IReadOnlyList<InkStroke> before)
    {
        // The earlier state: strokes that existed before the edit (the seeds), as saved in the file.
        var earlier = before.Count == 0 ? [] : before;
        await WriteEarlierAsync(path, page.Index, earlier);

        var removed = earlier.Where(b => page.Strokes.All(a => a.Id != b.Id)).Select(b => b.Id).ToArray();
        var added = page.Strokes.Where(a => earlier.All(b => b.Id != a.Id)).ToArray();
        string temp = await new PdfDocumentSaver().WriteVerifiedCopyAsync(path, path, [new PageChange(page.Index, removed, added)]);

        var back = (await ReadPageAsync(temp, page.Index)).Strokes.ToDictionary(s => s.Id);
        Assert.Equal(page.Strokes.Select(s => s.Id).Order(), back.Keys.Order());
        foreach (var stroke in page.Strokes)
        {
            Assert.Equal(StrokeJson.Serialize(stroke), StrokeJson.Serialize(back[stroke.Id]));
        }

        File.Move(temp, path, overwrite: true);
    }

    private static Task WriteEarlierAsync(string path, int pageIndex, IReadOnlyList<InkStroke> strokes) =>
        EditInPlaceAsync(path, pageIndex, page =>
        {
            var geometry = PageGeometry.From(page);
            foreach (var stroke in strokes)
            {
                StrokeAnnotations.Write(page, geometry, stroke);
            }
        });

    private static async Task EditInPlaceAsync(string path, int pageIndex, Action<nint> edit)
    {
        string rewritten = path + ".fixture";
        await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(path);
                nint page = document.LoadPage(pageIndex);
                try
                {
                    edit(page);
                }
                finally
                {
                    document.ClosePage(page);
                }

                document.SaveFullRewrite(rewritten);
            },
            CancellationToken.None);
        File.Move(rewritten, path, overwrite: true);
    }

    private static Task<StrokeReadResult> ReadPageAsync(string path, int pageIndex) => PdfiumThread.RunAsync(
        () =>
        {
            using var document = PdfiumDocument.Open(path);
            nint page = document.LoadPage(pageIndex);
            try
            {
                return StrokeAnnotations.Read(page, PageGeometry.From(page));
            }
            finally
            {
                document.ClosePage(page);
            }
        },
        CancellationToken.None);
}
