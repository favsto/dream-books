using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using DreamBooks.Core.Search;

namespace DreamBooks.App.Views;

/// <summary>
/// The search results of one page (FR-13, FR-105), drawn as translucent rectangles between the PDF bitmap and the ink
/// of an <see cref="Ink.InkSurface"/>, so they never cover the ink. The rectangles arrive in displayed page points
/// (top-left origin, y down) and are scaled here, in the view, by <see cref="Scale"/> (FR-26); each is padded
/// vertically by the theme's <c>SearchHighlightPaddingPt</c>, because PDFium's text boxes hug the glyphs. The current
/// result has the stronger brush. View-only: not hit-testable, not focusable, no automation peer, so the pen and
/// fingers reach the page exactly as without it; nothing is drawn without results.
/// </summary>
/// <remarks>
/// The brushes are translucent by nature, also with Windows transparency effects off (NFR-32): an opaque highlight
/// would hide the text it marks. They are a highlight colour, not a transparency effect.
/// </remarks>
public sealed class SearchHighlightLayer : FrameworkElement
{
    public static readonly DependencyProperty HitBrushProperty = DependencyProperty.Register(
        nameof(HitBrush), typeof(Brush), typeof(SearchHighlightLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentBrushProperty = DependencyProperty.Register(
        nameof(CurrentBrush), typeof(Brush), typeof(SearchHighlightLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingPtProperty = DependencyProperty.Register(
        nameof(PaddingPt), typeof(double), typeof(SearchHighlightLayer),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly SearchRect[] _none = [];

    private double _scale = 1;
    private IReadOnlyList<SearchRect> _hits = _none;
    private IReadOnlyList<SearchRect> _current = _none;

    public SearchHighlightLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SetResourceReference(HitBrushProperty, "SearchHighlightBrush");
        SetResourceReference(CurrentBrushProperty, "SearchCurrentHighlightBrush");
        SetResourceReference(PaddingPtProperty, "SearchHighlightPaddingPt");
    }

    /// <summary>The other results of the page (theme <c>SearchHighlightBrush</c>).</summary>
    public Brush? HitBrush
    {
        get => (Brush?)GetValue(HitBrushProperty);
        set => SetValue(HitBrushProperty, value);
    }

    /// <summary>The current result (theme <c>SearchCurrentHighlightBrush</c>, stronger).</summary>
    public Brush? CurrentBrush
    {
        get => (Brush?)GetValue(CurrentBrushProperty);
        set => SetValue(CurrentBrushProperty, value);
    }

    /// <summary>Added above and below each rectangle, in page points.</summary>
    public double PaddingPt
    {
        get => (double)GetValue(PaddingPtProperty);
        set => SetValue(PaddingPtProperty, value);
    }

    /// <summary>DIPs per page point (the surface's scale: zoom × 4/3).</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            if (value > 0 && double.IsFinite(value) && value != _scale)
            {
                _scale = value;
                InvalidateVisual();
            }
        }
    }

    /// <summary>True when there is something to draw.</summary>
    public bool HasHighlights => _hits.Count > 0 || _current.Count > 0;

    /// <summary>Shows the page's results; empty lists draw nothing.</summary>
    public void SetHighlights(IReadOnlyList<SearchRect> hits, IReadOnlyList<SearchRect> current)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(current);
        if (hits.Count == 0 && current.Count == 0 && !HasHighlights)
        {
            return;
        }

        _hits = hits;
        _current = current;
        InvalidateVisual();
    }

    /// <summary>The rectangles as drawn, in this layer's DIPs, and whether each is the current result (tests).</summary>
    internal IReadOnlyList<(Rect Rect, bool IsCurrent)> ViewRects()
    {
        var list = new List<(Rect, bool)>(_hits.Count + _current.Count);
        list.AddRange(_hits.Select(r => (ToView(r), false)));
        list.AddRange(_current.Select(r => (ToView(r), true)));
        return list;
    }

    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (HitBrush is { } hitBrush)
        {
            foreach (var rect in _hits)
            {
                drawingContext.DrawRectangle(hitBrush, null, ToView(rect));
            }
        }

        if (CurrentBrush is { } currentBrush)
        {
            foreach (var rect in _current)
            {
                drawingContext.DrawRectangle(currentBrush, null, ToView(rect));
            }
        }
    }

    private Rect ToView(SearchRect rect)
    {
        var pad = Math.Max(0, PaddingPt);
        var left = Math.Min(rect.LeftPt, rect.RightPt);
        var top = Math.Min(rect.TopPt, rect.BottomPt) - pad;
        var width = Math.Abs(rect.RightPt - rect.LeftPt);
        var height = Math.Abs(rect.BottomPt - rect.TopPt) + (2 * pad);
        return new Rect(left * _scale, top * _scale, width * _scale, height * _scale);
    }
}
