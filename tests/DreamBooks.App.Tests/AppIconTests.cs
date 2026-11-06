using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DreamBooks.App.Tests;

/// <summary>The app icon (phase 4b): a transparent round badge and an .ico with every size Windows asks for.</summary>
public sealed class AppIconTests
{
    private static readonly int[] _icoSizes = [16, 24, 32, 48, 64, 128, 256];

    private static string AssetsDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "src", "DreamBooks.App", "Assets"));

    [Fact]
    public void Icon_png_has_transparent_corners_and_an_opaque_centre()
    {
        WpfTestThread.Run(() =>
        {
            var uri = new Uri("pack://application:,,,/DreamBooks.App;component/Assets/app-icon.png");
            var bitmap = new FormatConvertedBitmap(new BitmapImage(uri), PixelFormats.Bgra32, null, 0);
            Assert.Equal(256, bitmap.PixelWidth);
            Assert.Equal(256, bitmap.PixelHeight);
            var pixel = new byte[4];
            bitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert.Equal(0, pixel[3]);
            bitmap.CopyPixels(new System.Windows.Int32Rect(128, 128, 1, 1), pixel, 4, 0);
            Assert.Equal(255, pixel[3]);
        });
    }

    [Fact]
    public void Icon_ico_holds_all_the_sizes()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AssetsDirectory(), "app-icon.ico"));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0)); // reserved
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2)); // icon type
        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = Enumerable.Range(0, count)
            .Select(i => bytes[6 + (i * 16)] is 0 ? 256 : bytes[6 + (i * 16)])
            .Order()
            .ToArray();
        Assert.Equal(_icoSizes, sizes);
    }
}
