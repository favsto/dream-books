// One-off tool: turns extra/dream-book-icon.png (white background) into src/DreamBooks.App/Assets/app-icon.png
// (transparent, 256 x 256) and app-icon.ico (16..256). Run from the repository root: dotnet run tools/MakeIcon.cs
// Re-run it when a better source image replaces the file in extra/. Uses WPF only: no packages.
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property Nullable=enable
#:property PublishTrimmed=false
#:property PublishAot=false

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var root = Directory.GetCurrentDirectory();
var source = Path.Combine(root, "extra", "dream-book-icon.png");
var outDir = Path.Combine(root, "src", "DreamBooks.App", "Assets");
Directory.CreateDirectory(outDir);

var image = new FormatConvertedBitmap(new BitmapImage(new Uri(source)), PixelFormats.Bgra32, null, 0);
int w = image.PixelWidth, h = image.PixelHeight;
var px = new byte[w * h * 4];
image.CopyPixels(px, w * 4, 0);

bool White(int i) => Math.Min(px[i], Math.Min(px[i + 1], px[i + 2])) > 200 && Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])) - Math.Min(px[i], Math.Min(px[i + 1], px[i + 2])) < 28; // light and neutral
var removed = new bool[w * h];
var queue = new Queue<(int X, int Y)>();
for (var x = 0; x < w; x++) { queue.Enqueue((x, 0)); queue.Enqueue((x, h - 1)); }
for (var y = 0; y < h; y++) { queue.Enqueue((0, y)); queue.Enqueue((w - 1, y)); }
while (queue.Count > 0)
{
    var (x, y) = queue.Dequeue();
    if (x < 0 || y < 0 || x >= w || y >= h || removed[(y * w) + x] || !White(((y * w) + x) * 4)) { continue; }
    removed[(y * w) + x] = true;
    var i = ((y * w) + x) * 4;
    px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0;
    queue.Enqueue((x + 1, y)); queue.Enqueue((x - 1, y)); queue.Enqueue((x, y + 1)); queue.Enqueue((x, y - 1));
}

// Soften the rim: a kept pixel touching the removed background gets partly transparent by how white it is.
var rim = new List<(int I, byte A)>();
for (var y = 0; y < h; y++)
{
    for (var x = 0; x < w; x++)
    {
        var p = (y * w) + x;
        if (removed[p]) { continue; }
        var touches = (x > 0 && removed[p - 1]) || (x < w - 1 && removed[p + 1]) || (y > 0 && removed[p - w]) || (y < h - 1 && removed[p + w]);
        if (!touches) { continue; }
        var i = p * 4;
        var whiteness = (px[i] + px[i + 1] + px[i + 2]) / 3.0;
        rim.Add((i, (byte)Math.Clamp(255 - ((whiteness - 150) * 255 / 105), 0, 255)));
    }
}
foreach (var (i, a) in rim)
{
    // Premultiplied storage: scale the colour with the new alpha.
    px[i] = (byte)(px[i] * a / 255); px[i + 1] = (byte)(px[i + 1] * a / 255); px[i + 2] = (byte)(px[i + 2] * a / 255); px[i + 3] = a;
}
var cut = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);

BitmapSource Resize(int size)
{
    var visual = new DrawingVisual();
    RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
    using (var dc = visual.RenderOpen()) { dc.DrawImage(cut, new Rect(0, 0, size, size)); }
    var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    target.Render(visual);
    target.Freeze();
    return target;
}

byte[] Png(BitmapSource bitmap)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

File.WriteAllBytes(Path.Combine(outDir, "app-icon.png"), Png(Resize(256)));

int[] sizes = [16, 24, 32, 48, 64, 128, 256];
var frames = sizes.Select(s => Png(Resize(s))).ToList();
using var ico = new MemoryStream();
using (var bw = new BinaryWriter(ico))
{
    bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
    var offset = 6 + (16 * sizes.Length);
    for (var n = 0; n < sizes.Length; n++)
    {
        var dim = (byte)(sizes[n] == 256 ? 0 : sizes[n]);
        bw.Write(dim); bw.Write(dim); bw.Write((byte)0); bw.Write((byte)0);
        bw.Write((ushort)1); bw.Write((ushort)32); bw.Write((uint)frames[n].Length); bw.Write((uint)offset);
        offset += frames[n].Length;
    }
    foreach (var f in frames) { bw.Write(f); }
}
File.WriteAllBytes(Path.Combine(outDir, "app-icon.ico"), ico.ToArray());
Console.WriteLine("icon written");
