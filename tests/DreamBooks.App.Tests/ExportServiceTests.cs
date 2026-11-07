using System.Windows.Media;
using System.Windows.Media.Imaging;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;
using DreamBooks.Pdf.Tests;

namespace DreamBooks.App.Tests;

/// <summary>The page image (PNG) and the flattened PDF export: dialogs, names, results, failures that change nothing (FR-87, FR-88).</summary>
public sealed class ExportServiceTests : IDisposable
{
    private static readonly InkPoint[] _dot =
    [
        new(100, 100, 0.5f, null, null, null, 0),
        new(300, 120, 0.5f, null, null, null, 10),
        new(500, 100, 0.5f, null, null, null, 20),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-export-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileDialogService _dialogs = new();

    public ExportServiceTests() => Directory.CreateDirectory(_dir);

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

    private string NewPdf(string name = "Mondo senza fine.pdf", int pages = 3)
    {
        var path = Path.Combine(_dir, name);
        TestPdfFactory.Write(path, Enumerable.Range(0, pages).Select(_ => (612.0, 792.0)).ToArray());
        return path;
    }

    private static InkStroke Stroke() => new(Guid.NewGuid(), InkTool.Pen, InkColor.FromRgb(0, 0, 0), 4, _dot);

    private static BitmapSource Decode(string png)
    {
        var decoder = new PngBitmapDecoder(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
    }

    private static int Dark(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2])) < 128)
            {
                count++;
            }
        }

        return count;
    }

    private async Task<(DocumentSession Session, OpenedDocument Opened)> OpenAsync(string path)
    {
        var opened = await OpenedDocument.OpenPdfAsync(path, CancellationToken.None);
        var session = new DocumentSession(opened.Source, opened.Path, await DocumentFingerprint.ComputeAsync(opened.Path, CancellationToken.None));
        await session.GoToPageAsync(0, CancellationToken.None);
        return (session, opened);
    }

    private static async Task CloseAsync(DocumentSession session, OpenedDocument opened)
    {
        session.Dispose();
        await opened.Lifetime.DisposeAsync();
    }

    [Fact]
    public async Task The_page_image_is_200_dpi_with_the_session_ink_and_the_suggested_name_has_the_page_number()
    {
        var path = NewPdf();
        var (session, opened) = await OpenAsync(path);
        try
        {
            var baseline = Dark(Decode(await ExportBlankAsync(path)));
            Assert.True(session.CurrentEditor!.CommitStroke(_dot, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1));
            var png = Path.Combine(_dir, "page.png");
            _dialogs.NextPngPath = png;

            var result = await new ExportService(_dialogs).ExportPageAsync(session, pageIndex: 0, CancellationToken.None);

            Assert.Equal(ExportOutcome.Done, result.Outcome);
            Assert.Equal([string.Format(Strings.ExportPageNameFormat, "Mondo senza fine", 1)], _dialogs.SuggestedPngNames);
            var image = Decode(png);
            Assert.Equal((1700, 2200), (image.PixelWidth, image.PixelHeight)); // 612 x 792 pt at 200/72 px per pt
            Assert.True(Dark(image) > baseline + 100, "the stroke the session has is in the picture");
        }
        finally
        {
            await CloseAsync(session, opened);
        }
    }

    private async Task<string> ExportBlankAsync(string path)
    {
        var blank = Path.Combine(_dir, "blank.png");
        await new PageImageExporter().ExportAsync(path, 0, [], blank, CancellationToken.None);
        return blank;
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_exports_nothing()
    {
        var path = NewPdf();
        var (session, opened) = await OpenAsync(path);
        try
        {
            _dialogs.NextPngPath = null;
            _dialogs.NextSavePath = null;
            var service = new ExportService(_dialogs);

            Assert.Equal(ExportOutcome.Cancelled, (await service.ExportPageAsync(session, 0, CancellationToken.None)).Outcome);
            Assert.Equal(ExportOutcome.Cancelled, (await service.ExportFlattenedAsync(session, CancellationToken.None)).Outcome);
            Assert.Single(Directory.GetFiles(_dir)); // only the source
        }
        finally
        {
            await CloseAsync(session, opened);
        }
    }

    [Fact]
    public async Task The_flattened_export_suggests_a_name_never_the_open_file_and_leaves_the_document_dirty_state_alone()
    {
        var path = NewPdf();
        var (session, opened) = await OpenAsync(path);
        try
        {
            Assert.True(session.CurrentEditor!.CommitStroke(_dot, InkTool.Pen, InkColor.FromRgb(0, 0, 0), 1));
            Assert.True(session.IsDirty);
            var flat = Path.Combine(_dir, "flat.pdf");
            _dialogs.NextSavePath = flat;
            var before = File.ReadAllBytes(path);

            var result = await new ExportService(_dialogs).ExportFlattenedAsync(session, CancellationToken.None);

            Assert.Equal(ExportOutcome.Done, result.Outcome);
            Assert.Equal([string.Format(Strings.ExportFlattenedNameFormat, "Mondo senza fine")], _dialogs.SuggestedNames);
            Assert.True(File.Exists(flat));
            Assert.True(session.IsDirty); // the export is not a save
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            await CloseAsync(session, opened);
        }
    }

    [Fact]
    public async Task Exporting_onto_the_open_file_is_refused_and_changes_nothing()
    {
        var path = NewPdf();
        var (session, opened) = await OpenAsync(path);
        try
        {
            var before = File.ReadAllBytes(path);
            _dialogs.NextSavePath = path;

            var result = await new ExportService(_dialogs).ExportFlattenedAsync(session, CancellationToken.None);

            Assert.Equal(ExportOutcome.Failed, result.Outcome);
            Assert.Equal(Strings.ErrorExportSameFile, result.Message);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            await CloseAsync(session, opened);
        }
    }

    [Fact]
    public async Task The_open_file_under_another_spelling_is_refused_too_for_both_exports()
    {
        var path = NewPdf();
        var (session, opened) = await OpenAsync(path);
        try
        {
            var before = File.ReadAllBytes(path);
            var spelled = new string((char)92, 2) + "?" + (char)92 + Path.GetFullPath(path); // the extended-length spelling
            _dialogs.NextSavePath = spelled;
            _dialogs.NextPngPath = spelled;
            var service = new ExportService(_dialogs);

            var pdf = await service.ExportFlattenedAsync(session, CancellationToken.None);
            var png = await service.ExportPageAsync(session, 0, CancellationToken.None);

            Assert.Equal((ExportOutcome.Failed, Strings.ErrorExportSameFile), (pdf.Outcome, pdf.Message));
            Assert.Equal((ExportOutcome.Failed, Strings.ErrorExportSameFile), (png.Outcome, png.Message));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(_dir));
        }
        finally
        {
            await CloseAsync(session, opened);
        }
    }

    [Fact]
    public async Task A_failure_says_so_in_Italian_without_paths_and_leaves_nothing_behind()
    {
        var path = NewPdf();
        var (session, opened) = await OpenAsync(path);
        try
        {
            _dialogs.NextSavePath = Path.Combine(_dir, "no-such-folder", "flat.pdf");
            _dialogs.NextPngPath = Path.Combine(_dir, "no-such-folder", "page.png");
            var service = new ExportService(_dialogs);

            var pdf = await service.ExportFlattenedAsync(session, CancellationToken.None);
            var png = await service.ExportPageAsync(session, 0, CancellationToken.None);

            Assert.Equal((ExportOutcome.Failed, Strings.ErrorExportFailed), (pdf.Outcome, pdf.Message));
            Assert.Equal((ExportOutcome.Failed, Strings.ErrorExportFailed), (png.Outcome, png.Message));
            Assert.DoesNotContain(_dir, pdf.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(Directory.GetFiles(_dir));
        }
        finally
        {
            await CloseAsync(session, opened);
        }
    }

    [Fact]
    public async Task An_encrypted_pdf_cannot_be_flattened_and_says_so()
    {
        var path = Path.Combine(_dir, "locked.pdf");
        TestPdfFactory.WriteEncryptedRestricted(path, (612, 792));
        var (session, opened) = await OpenAsync(path);
        try
        {
            _dialogs.NextSavePath = Path.Combine(_dir, "flat.pdf");

            var result = await new ExportService(_dialogs).ExportFlattenedAsync(session, CancellationToken.None);

            Assert.Equal((ExportOutcome.Failed, Strings.ErrorExportEncrypted), (result.Outcome, result.Message));
            Assert.False(File.Exists(Path.Combine(_dir, "flat.pdf")));
        }
        finally
        {
            await CloseAsync(session, opened);
        }
    }

    [Fact]
    public async Task A_cancelled_export_reports_cancelled_and_an_existing_file_is_kept()
    {
        var path = NewPdf();
        var (session, opened) = await OpenAsync(path);
        try
        {
            var png = Path.Combine(_dir, "page.png");
            File.WriteAllText(png, "keep me");
            _dialogs.NextPngPath = png;
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var result = await new ExportService(_dialogs).ExportPageAsync(session, 0, cts.Token);

            Assert.Equal(ExportOutcome.Cancelled, result.Outcome);
            Assert.Equal("keep me", File.ReadAllText(png));
        }
        finally
        {
            await CloseAsync(session, opened);
        }
    }

    [Fact]
    public async Task An_existing_png_is_replaced()
    {
        var path = NewPdf();
        var png = Path.Combine(_dir, "page.png");
        File.WriteAllText(png, "an older file");

        await new PageImageExporter().ExportAsync(path, 1, [], png, CancellationToken.None);

        Assert.Equal((1700, 2200), (Decode(png).PixelWidth, Decode(png).PixelHeight));
        Assert.Equal(2, Directory.GetFiles(_dir).Length); // the source and the image: no temporary file
    }
}
