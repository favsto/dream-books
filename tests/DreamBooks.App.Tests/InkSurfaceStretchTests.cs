using System.Windows.Media;
using DreamBooks.App.Ink;

namespace DreamBooks.App.Tests;

/// <summary>How a page bitmap is stretched: sharp at rest, cheap while a zoom is going on (NFR-03).</summary>
public sealed class InkSurfaceStretchTests
{
    [Fact]
    public void The_bitmap_is_stretched_with_a_cheap_filter_only_while_asked_to()
    {
        WpfTestThread.Run(() =>
        {
            var surface = new InkSurface();
            Assert.Equal(BitmapScalingMode.HighQuality, surface.BitmapScaling);

            surface.FastStretch = true;
            Assert.Equal(BitmapScalingMode.Linear, surface.BitmapScaling);

            surface.FastStretch = false;
            Assert.Equal(BitmapScalingMode.HighQuality, surface.BitmapScaling);
        });
    }
}
