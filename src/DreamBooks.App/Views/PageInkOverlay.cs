using System.Windows;
using System.Windows.Media;

namespace DreamBooks.App.Views;

/// <summary>
/// A page's ink drawn as vectors over its preview in the scroll rail's bubble (UI-07): the page fills the element's
/// width (as the preview image does), so the strokes are scaled by width ÷ page width and clipped to the element.
/// Strokes are never drawn thinner than <c>RailPreviewMinStrokeWidth</c>. Decorative: never hit-tested.
/// </summary>
public sealed class PageInkOverlay : FrameworkElement
{
    private PageInkPicture? _picture;

    public PageInkOverlay()
    {
        IsHitTestVisible = false;
    }

    /// <summary>The ink shown, or none.</summary>
    internal PageInkPicture? Picture
    {
        get => _picture;
        set
        {
            if (!ReferenceEquals(_picture, value))
            {
                _picture = value;
                InvalidateVisual();
            }
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var size = RenderSize;
        if (_picture is not { } picture || picture.Strokes.Count == 0 || picture.WidthPt <= 0 || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var scale = size.Width / picture.WidthPt;
        var minimumDip = TryFindResource("RailPreviewMinStrokeWidth") as double? ?? 0.75;
        drawingContext.PushClip(new RectangleGeometry(new Rect(size)));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        InkDrawings.Draw(drawingContext, picture.Strokes, scale, minimumDip);
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
