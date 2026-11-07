using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DreamBooks.Core.Documents;
using DreamBooks.Pdf;

namespace DreamBooks.App.Services;

/// <summary>
/// One page as a PNG (FR-88): the page as the reader sees it, with the strokes the session has (saved or not) on top, on
/// white paper, at <see cref="Dpi"/> dpi, without transparency. The picture is rendered in memory by
/// <see cref="PageSnapshotRenderer"/> (the same engine as the screen and the flattened PDF); the image is written to a
/// temporary file next to the destination and only then replaces it, so a failure leaves nothing behind and an existing
/// destination as it was (NFR-10).
/// </summary>
internal sealed class PageImageExporter
{
    /// <summary>The resolution of the exported image.</summary>
    public const double Dpi = 200;

    private const string _tempExtension = ".drbk-tmp";

    private readonly PageSnapshotRenderer _renderer = new();

    /// <summary>Exports page <paramref name="pageIndex"/> of <paramref name="sourcePath"/>; returns the full destination path.</summary>
    /// <exception cref="DirectoryNotFoundException">The destination folder does not exist.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left behind.</exception>
    public async Task<string> ExportAsync(
        string sourcePath, int pageIndex, IReadOnlyList<PageChange> changes, string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var folder = Path.GetDirectoryName(destination);
        if (folder is null || !Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException("The destination folder does not exist.");
        }

        if (File.Exists(destination) && FileIdentity.AreSame(sourcePath, destination))
        {
            throw new ExportDestinationIsSourceException(); // never over the file it reads
        }

        var page = await _renderer.RenderAsync(sourcePath, pageIndex, changes, Dpi, ct).ConfigureAwait(false);
        var png = Encode(page);
        ct.ThrowIfCancellationRequested();

        var temp = Path.Combine(folder, $"{Path.GetFileName(destination)}.{Guid.NewGuid().ToString("N")[..8]}{_tempExtension}");
        try
        {
            await File.WriteAllBytesAsync(temp, png, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            FileReplacer.Replace(temp, destination);
            return destination;
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static byte[] Encode(RenderedPage page)
    {
        var bitmap = BitmapSource.Create(
            page.PixelWidth, page.PixelHeight, Dpi, Dpi, PixelFormats.Bgra32, null, page.Bgra, page.Stride);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
