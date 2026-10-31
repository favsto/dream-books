using DreamBooks.Core.Documents;
using DreamBooks.Core.Geometry;
using DreamBooks.Pdf.Interop;
using static DreamBooks.Pdf.Tests.TestPdfFactory;

namespace DreamBooks.Pdf.Tests;

public sealed class PageGeometryTests : IDisposable
{
    private readonly string _dir;

    public PageGeometryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DreamBooks.Pdf.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public enum Boxes
    {
        Plain,
        OffsetMediaBox,
        OffsetCropBox,
        OffsetMediaAndCropBox,
    }

    public static TheoryData<int, Boxes> RotationsAndBoxes()
    {
        var data = new TheoryData<int, Boxes>();
        foreach (int rotate in new[] { 0, 90, 180, 270 })
        {
            foreach (Boxes boxes in Enum.GetValues<Boxes>())
            {
                data.Add(rotate, boxes);
            }
        }

        return data;
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
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task ModelToUser_then_UserToModel_round_trips(int rotate) // FR-26, FR-81
    {
        string path = WritePdf(Spec(rotate, Boxes.OffsetMediaAndCropBox));
        PageGeometry geometry = await LoadGeometryAsync(path);
        var random = new Random(4242 + rotate);

        for (int i = 0; i < 50; i++)
        {
            var model = new PointD(
                random.NextDouble() * geometry.Dimensions.WidthPt,
                random.NextDouble() * geometry.Dimensions.HeightPt);

            PointD back = geometry.UserToModel(geometry.ModelToUser(model));

            Assert.Equal(model.X, back.X, 1e-6);
            Assert.Equal(model.Y, back.Y, 1e-6);
        }
    }

    [Fact]
    public async Task Unrotated_page_maps_model_top_left_to_user_top_left() // FR-26
    {
        string path = WritePdf(new PageSpec(595, 842));
        PageGeometry geometry = await LoadGeometryAsync(path);

        Assert.Equal(new PointD(0, 842), geometry.ModelToUser(new PointD(0, 0)));
        Assert.Equal(new PointD(595, 0), geometry.ModelToUser(new PointD(595, 842)));
        Assert.Equal(new PointD(0, 0), geometry.UserToModel(new PointD(0, 842)));
    }

    [Fact]
    public async Task Offset_MediaBox_origin_is_added() // FR-26, FR-81
    {
        // MediaBox [100 200 695 1042].
        string path = WritePdf(new PageSpec(595, 842, OriginX: 100, OriginY: 200));
        PageGeometry geometry = await LoadGeometryAsync(path);

        Assert.Equal(new PageDimensions(595, 842), geometry.Dimensions);
        Assert.Equal(new PointD(100, 1042), geometry.ModelToUser(new PointD(0, 0)));
        Assert.Equal(new PointD(695, 200), geometry.ModelToUser(new PointD(595, 842)));
    }

    [Fact]
    public async Task Rotate_90_swaps_visible_dimensions() // FR-26
    {
        string path = WritePdf(new PageSpec(595, 842, Rotate: 90));
        PageGeometry geometry = await LoadGeometryAsync(path);

        Assert.Equal(new PageDimensions(842, 595), geometry.Dimensions);
    }

    [Fact]
    public async Task Offset_CropBox_defines_the_visible_page() // FR-26, FR-81
    {
        string path = WritePdf(new PageSpec(700, 900, CropBox: new PageBox(40, 60, 640, 860)));
        PageGeometry geometry = await LoadGeometryAsync(path);

        Assert.Equal(new PageDimensions(600, 800), geometry.Dimensions);
        Assert.Equal(new PointD(40, 860), geometry.ModelToUser(new PointD(0, 0)));
    }

    [Fact]
    public async Task RectToUser_returns_the_user_space_bounds_of_a_model_rectangle() // FR-81
    {
        // Rotate 90: model x runs along user y (up), model y along user x.
        string path = WritePdf(new PageSpec(595, 842, Rotate: 90, OriginX: 100, OriginY: 200));
        PageGeometry geometry = await LoadGeometryAsync(path);

        (double left, double bottom, double right, double top) = geometry.RectToUser(10, 20, 110, 70);

        Assert.Equal((120.0, 210.0, 170.0, 310.0), (left, bottom, right, top));
    }

    [Theory]
    [MemberData(nameof(RotationsAndBoxes))]
    public async Task Mapping_agrees_with_PDFium_device_transform_and_page_size(int rotate, Boxes boxes) // FR-26
    {
        string path = WritePdf(Spec(rotate, boxes));

        var (geometry, deviceToUser, pdfiumSize) = await PdfiumThread.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(path);
                nint page = document.LoadPage(0);
                try
                {
                    var g = PageGeometry.From(page);
                    int w = (int)g.Dimensions.WidthPt;
                    int h = (int)g.Dimensions.HeightPt;
                    var samples = new List<(PointD Device, PointD User)>();
                    foreach ((int x, int y) in new[] { (0, 0), (w, 0), (0, h), (w, h), (37, 113), (w - 91, 7) })
                    {
                        Assert.NotEqual(0, PdfiumNative.FPDF_DeviceToPage(
                            page, 0, 0, w, h, 0, x, y, out double ux, out double uy));
                        samples.Add((new PointD(x, y), new PointD(ux, uy)));
                    }

                    var size = new PageDimensions(
                        PdfiumNative.FPDF_GetPageWidthF(page), PdfiumNative.FPDF_GetPageHeightF(page));
                    return (g, samples, size);
                }
                finally
                {
                    document.ClosePage(page);
                }
            },
            CancellationToken.None);

        Assert.Equal(pdfiumSize, geometry.Dimensions);
        foreach ((PointD device, PointD user) in deviceToUser)
        {
            // At 1 px per point the device frame is the model frame; PDFium's matrix is float.
            PointD model = geometry.UserToModel(user);
            Assert.Equal(device.X, model.X, 1e-3);
            Assert.Equal(device.Y, model.Y, 1e-3);
        }
    }

    [Theory]
    [MemberData(nameof(RotationsAndBoxes))]
    public async Task Rendered_black_square_corner_matches_mapped_corner_for_each_rotation(int rotate, Boxes boxes) // FR-26, FR-81
    {
        PageSpec spec = Spec(rotate, boxes);
        string path = WritePdf(spec);
        PageGeometry geometry = await LoadGeometryAsync(path);
        int width = (int)geometry.Dimensions.WidthPt;
        int height = (int)geometry.Dimensions.HeightPt;
        Assert.Equal(width, geometry.Dimensions.WidthPt);
        Assert.Equal(height, geometry.Dimensions.HeightPt);

        RenderedPage rendered;
        await using (var renderer = await PdfRenderer.OpenAsync(path))
        {
            rendered = await renderer.RenderPageAsync(0, width, height); // 1 px per point
        }

        // Where the factory drew the marks, in user space (y up).
        PageBox visible = spec.CropBox
            ?? new PageBox(spec.OriginX, spec.OriginY, spec.OriginX + spec.Width, spec.OriginY + spec.Height);
        var squareUser = new PageBox(visible.Left, visible.Top - 100, visible.Left + 100, visible.Top);
        var markerUser = new PageBox(visible.Right - 50, visible.Bottom, visible.Right, visible.Bottom + 20);

        PixelBounds square = FindPixels(rendered, (r, g, b) => r < 60 && g < 60 && b < 60);
        PixelBounds marker = FindPixels(rendered, (r, g, b) => r > 200 && g < 60 && b < 60);

        AssertMatches(geometry, squareUser, square, "black square");
        AssertMatches(geometry, markerUser, marker, "red marker");

        // The user-space top-left corner of the square lands on the pixel the mapping predicts.
        // The pixel to check is the one inside the square that touches that corner.
        PointD corner = geometry.UserToModel(new PointD(squareUser.Left, squareUser.Top));
        PointD opposite = geometry.UserToModel(new PointD(squareUser.Right, squareUser.Bottom));
        int px = (int)Math.Round(corner.X) - (corner.X > opposite.X ? 1 : 0);
        int py = (int)Math.Round(corner.Y) - (corner.Y > opposite.Y ? 1 : 0);
        Assert.True(IsBlack(rendered, px, py), $"pixel ({px},{py}) at the mapped corner is not black");
    }

    private static void AssertMatches(PageGeometry geometry, PageBox user, PixelBounds found, string what)
    {
        PointD[] corners =
        [
            geometry.UserToModel(new PointD(user.Left, user.Bottom)),
            geometry.UserToModel(new PointD(user.Left, user.Top)),
            geometry.UserToModel(new PointD(user.Right, user.Bottom)),
            geometry.UserToModel(new PointD(user.Right, user.Top)),
        ];
        double left = corners.Min(p => p.X);
        double top = corners.Min(p => p.Y);
        double right = corners.Max(p => p.X);
        double bottom = corners.Max(p => p.Y);

        // Pixel bounds are inclusive pixel indexes; the mapped rectangle covers [left, right).
        string message = $"{what}: mapped ({left},{top})-({right},{bottom}), rendered ({found.Left},{found.Top})-({found.Right + 1},{found.Bottom + 1})";
        Assert.True(Math.Abs(found.Left - left) <= 1, message);
        Assert.True(Math.Abs(found.Top - top) <= 1, message);
        Assert.True(Math.Abs(found.Right + 1 - right) <= 1, message);
        Assert.True(Math.Abs(found.Bottom + 1 - bottom) <= 1, message);
    }

    private static bool IsBlack(RenderedPage page, int x, int y)
    {
        int i = (y * page.Stride) + (x * 4);
        return page.Bgra[i] < 60 && page.Bgra[i + 1] < 60 && page.Bgra[i + 2] < 60;
    }

    private static PixelBounds FindPixels(RenderedPage page, Func<byte, byte, byte, bool> match)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < page.PixelHeight; y++)
        {
            for (int x = 0; x < page.PixelWidth; x++)
            {
                int i = (y * page.Stride) + (x * 4);
                if (match(page.Bgra[i + 2], page.Bgra[i + 1], page.Bgra[i]))
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        Assert.True(right >= 0, "no matching pixels found");
        return new PixelBounds(left, top, right, bottom);
    }

    private static PageSpec Spec(int rotate, Boxes boxes) => boxes switch
    {
        Boxes.Plain => new PageSpec(595, 842, rotate),
        Boxes.OffsetMediaBox => new PageSpec(595, 842, rotate, OriginX: 100, OriginY: 200),
        Boxes.OffsetCropBox => new PageSpec(700, 900, rotate, CropBox: new PageBox(40, 60, 640, 860)),
        Boxes.OffsetMediaAndCropBox => new PageSpec(
            700, 900, rotate, OriginX: -150, OriginY: 300, CropBox: new PageBox(-120, 330, 480, 1130)),
        _ => throw new ArgumentOutOfRangeException(nameof(boxes)),
    };

    private string WritePdf(PageSpec spec)
    {
        string path = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        Write(path, spec);
        return path;
    }

    private static Task<PageGeometry> LoadGeometryAsync(string path) => PdfiumThread.RunAsync(
        () =>
        {
            using var document = PdfiumDocument.Open(path);
            nint page = document.LoadPage(0);
            try
            {
                return PageGeometry.From(page);
            }
            finally
            {
                document.ClosePage(page);
            }
        },
        CancellationToken.None);

    private readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom);
}
