using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Media;
using System.Windows.Shapes;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using CorePage = DreamBooks.Core.Model.Page;

namespace DreamBooks.App.Ink;

/// <summary>
/// One page: white paper (the placeholder, never a hole, NFR-03), PDF bitmap, committed ink (outline layer) and the
/// wet ink of the pen. Its size is the page size × <see cref="Scale"/>; strokes are kept in page points and only
/// scaled to draw (FR-26). Pen ink is drawn live by a <see cref="DynamicRenderer"/> on the real-time stylus thread
/// (FR-24, NFR-04); on pen-up one stroke is committed through this surface's own <see cref="Editor"/>, clipped to
/// its page (FR-27). Without an editor (strokes not loaded yet) the surface shows the page but accepts no ink.
/// Fingers never ink (they reach the viewport for pan and zoom); the mouse inks like the pen (NFR-23).
/// The pen's back end erases whatever tool is active, and the tip or the mouse erase with the eraser tool
/// (FR-30, FR-34): the gesture runs through <see cref="MainViewModel.BeginErase(PageEditor)"/> on this page, and
/// the working copy is drawn as committed ink until it ends. A circle shows the eraser area while hovering (FR-32).
/// </summary>
public sealed class InkSurface : FrameworkElement
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(MainViewModel), typeof(InkSurface),
        new PropertyMetadata(null, (d, e) => ((InkSurface)d).OnViewModelChanged((MainViewModel?)e.OldValue, (MainViewModel?)e.NewValue)));

    /// <summary>The editor of the page this surface shows; null while the page's strokes are not loaded.</summary>
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(PageEditor), typeof(InkSurface),
        new PropertyMetadata(null, (d, e) => ((InkSurface)d).OnEditorChanged((PageEditor?)e.NewValue)));

    /// <summary>Page size in points, used for the placeholder while <see cref="Page"/> is null.</summary>
    public static readonly DependencyProperty PageSizePtProperty = DependencyProperty.Register(
        nameof(PageSizePt), typeof(Size), typeof(InkSurface),
        new FrameworkPropertyMetadata(default(Size), FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The paper under the bitmap (theme resource PagePlaceholderBrush): what a page shows before its bitmap.</summary>
    public static readonly DependencyProperty PlaceholderBrushProperty = DependencyProperty.Register(
        nameof(PlaceholderBrush), typeof(Brush), typeof(InkSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(CorePage), typeof(InkSurface),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, e) => ((InkSurface)d).OnPageChanged((CorePage?)e.OldValue, (CorePage?)e.NewValue)));

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(
        nameof(Scale), typeof(double), typeof(InkSurface),
        new FrameworkPropertyMetadata(
            Units.DipPerPt,
            FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, _) => ((InkSurface)d).OnScaleChanged()),
        v => v is double s && s > 0 && double.IsFinite(s));

    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve), typeof(PressureCurve), typeof(InkSurface),
        new PropertyMetadata(PressureCurve.Firm, (d, _) => ((InkSurface)d).OnOutlineSettingsChanged()));

    public static readonly DependencyProperty SmoothingProperty = DependencyProperty.Register(
        nameof(Smoothing), typeof(SmoothingLevel), typeof(InkSurface),
        new PropertyMetadata(SmoothingLevel.Strong, (d, _) => ((InkSurface)d).OnOutlineSettingsChanged()));

    public static readonly DependencyProperty DisplayStrokesProperty = DependencyProperty.Register(
        nameof(DisplayStrokes), typeof(IReadOnlyList<InkStroke>), typeof(InkSurface),
        new PropertyMetadata(Array.Empty<InkStroke>(), (d, _) => ((InkSurface)d).RefreshInk()));

    public static readonly DependencyProperty PageImageProperty = DependencyProperty.Register(
        nameof(PageImage), typeof(ImageSource), typeof(InkSurface),
        new PropertyMetadata(null, (d, e) => ((InkSurface)d)._image.Source = (ImageSource?)e.NewValue));

    private readonly Image _image;
    private readonly CustomInkLayer _customLayer;
    private readonly InkPresenter _presenter;
    private readonly Canvas _overlay;
    private readonly Ellipse _eraserCursor;
    private readonly Visual[] _children;

    private readonly CustomDynamicRenderer _renderer = new();
    private bool _hasPressure = true;
    private (InkColor Color, double WidthPt, double Scale, PressureCurve Curve, bool HasPressure)? _rendererSettings;

    // Pen stroke in progress (UI thread copy of the points; the wet ink comes from the renderer).
    private List<InkPoint>? _penPoints;
    private int _penStylusId;
    private bool _penHasPressure;
    private StrokeStyle _penStyle;

    // Mouse stroke in progress, drawn wet on the UI thread through a temporary stock stroke.
    private List<InkPoint>? _mousePoints;
    private Stroke? _mouseWet;
    private StrokeStyle _mouseStyle;

    // Eraser gesture in progress (pen tip, back end or mouse); the session itself lives in the view model.
    private EraseGesture? _erase;

    // Eraser cursor centre in page points (null: hidden), and whether the hovering pen shows its back end.
    private PointD? _cursorCenterPt;
    private bool _hoverInverted;

    public InkSurface()
    {
        _image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _customLayer = new CustomInkLayer();
        _presenter = new InkPresenter { IsHitTestVisible = false };
        _eraserCursor = new Ellipse { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _eraserCursor.SetResourceReference(Shape.FillProperty, "EraserCursorFill");
        _eraserCursor.SetResourceReference(Shape.StrokeProperty, "EraserCursorOutline");
        _eraserCursor.SetResourceReference(Shape.StrokeThicknessProperty, "EraserCursorOutlineThickness");
        _overlay = new Canvas { IsHitTestVisible = false };
        _overlay.Children.Add(_eraserCursor);
        _children = [_image, _customLayer, _presenter, _overlay];
        foreach (var child in _children)
        {
            AddVisualChild(child);
        }

        ClipToBounds = true;
        SetResourceReference(PlaceholderBrushProperty, "PagePlaceholderBrush");
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        Stylus.SetIsTouchFeedbackEnabled(this, false);

        _customLayer.Scale = Scale;
        _customLayer.Curve = Curve;
        _customLayer.Smoothing = Smoothing;
        StylusPlugIns.Add(_renderer);
        _presenter.AttachVisuals(_renderer.RootVisual, _renderer.DrawingAttributes);
        UpdateRendererEnabled(null);
        RefreshInk();
        Loaded += (_, _) => RefreshIgnoredDevices(null);
    }

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>
    /// The editor of the shown page: ink, erase and commit go through it, never through the current page's editor.
    /// Setting one also shows its page. Null: placeholder only, no ink.
    /// </summary>
    public PageEditor? Editor
    {
        get => (PageEditor?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public CorePage? Page
    {
        get => (CorePage?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public Size PageSizePt
    {
        get => (Size)GetValue(PageSizePtProperty);
        set => SetValue(PageSizePtProperty, value);
    }

    public Brush? PlaceholderBrush
    {
        get => (Brush?)GetValue(PlaceholderBrushProperty);
        set => SetValue(PlaceholderBrushProperty, value);
    }

    /// <summary>DIPs per page point: zoom × <see cref="Units.DipPerPt"/>.</summary>
    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public PressureCurve Curve
    {
        get => (PressureCurve)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public SmoothingLevel Smoothing
    {
        get => (SmoothingLevel)GetValue(SmoothingProperty);
        set => SetValue(SmoothingProperty, value);
    }

    /// <summary>Strokes drawn as committed ink: the page's strokes (or, in Task 13, an eraser preview).</summary>
    public IReadOnlyList<InkStroke> DisplayStrokes
    {
        get => (IReadOnlyList<InkStroke>)GetValue(DisplayStrokesProperty);
        set => SetValue(DisplayStrokesProperty, value);
    }

    /// <summary>The rendered PDF page; stretched to the surface until a sharper render replaces it.</summary>
    public ImageSource? PageImage
    {
        get => (ImageSource?)GetValue(PageImageProperty);
        set => SetValue(PageImageProperty, value);
    }

    /// <summary>True while a pen or mouse stroke or an eraser gesture is live; the viewport must not pan or zoom then.</summary>
    public bool IsInking => _penPoints is not null || _mousePoints is not null || _erase is not null;

    /// <summary>Raised when a stroke or eraser gesture ends (committed or cancelled): <see cref="IsInking"/> is false again.</summary>
    public event EventHandler? InkingEnded;

    /// <summary>
    /// Raised when the pen comes near (in range, entering, or touching down) while this surface has no editor yet: the
    /// view loads the page's strokes at once instead of after its debounce, so the stroke that follows can ink (I1).
    /// </summary>
    public event EventHandler? EditorWanted;

    /// <summary>The pen is over this page: ask for its strokes if they are not here (separate so tests can drive it).</summary>
    internal void NotifyPenNearby()
    {
        if (Editor is null && PageSizePt != default)
        {
            EditorWanted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The eraser area circle (FR-32); exposed for tests.</summary>
    internal FrameworkElement EraserCursor => _eraserCursor;

    /// <summary>The wet-ink renderer; exposed for tests.</summary>
    internal CustomDynamicRenderer ActiveRenderer => _renderer;

    internal CustomInkLayer CustomLayer => _customLayer;

    /// <summary>The PDF bitmap layer; exposed for tests.</summary>
    internal FrameworkElement BitmapLayer => _image;

    /// <summary>Number of stylus plug-ins installed; exposed for tests (only one wet renderer at a time).</summary>
    internal int PlugInCount => StylusPlugIns.Count;
    protected override int VisualChildrenCount => _children.Length;

    /// <summary>A peer, so the page's name ("Pagina 12") reaches UI Automation (NFR-31).</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        // Never more than offered: the document panel offers the page size rounded to whole device pixels, which may be
        // a fraction smaller, and a larger desired size would make WPF arrange (and stretch the bitmap) at it.
        var size = PageSizeDip();
        var offered = new Size(Math.Min(size.Width, availableSize.Width), Math.Min(size.Height, availableSize.Height));
        foreach (var child in _children)
        {
            ((UIElement)child).Measure(ReferenceEquals(child, _image) ? offered : size);
        }

        return offered;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The bitmap fills the surface as placed (whole device pixels, see VirtualPagePanel.ArrangedRect), so it maps
        // 1:1 onto screen pixels; the ink layers keep the exact page size x Scale, within half a pixel of it (FR-26).
        _image.Arrange(new Rect(finalSize));
        var exact = new Rect(PageSizeDip());
        foreach (var child in _children)
        {
            if (!ReferenceEquals(child, _image))
            {
                ((UIElement)child).Arrange(exact);
            }
        }

        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        // The paper under the bitmap (and the placeholder until it arrives); also makes the whole page hit-testable
        // for stylus and mouse input.
        drawingContext.DrawRectangle(PlaceholderBrush ?? Brushes.Transparent, null, new Rect(RenderSize));
    }

    protected override void OnStylusInRange(StylusEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusInRange(e);
        if (IsPen(e.StylusDevice))
        {
            RefreshIgnoredDevices(e.StylusDevice);
            _hasPressure = ReportsPressure(e.StylusDevice.TabletDevice);
            UpdateRendererSettings();
            UpdateRendererEnabled(e.StylusDevice);
            _hoverInverted = e.StylusDevice.Inverted;
            NotifyPenNearby(); // the pen hovers here: the stroke that follows must find the editor
        }
    }

    protected override void OnStylusEnter(StylusEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusEnter(e);
        if (IsPen(e.StylusDevice))
        {
            NotifyPenNearby(); // hovering from another page onto this one
        }
    }

    protected override void OnStylusInAirMove(StylusEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusInAirMove(e);
        if (IsPen(e.StylusDevice))
        {
            UpdateRendererEnabled(e.StylusDevice);
            UpdateEraserCursor(e.GetPosition(this), e.StylusDevice.Inverted);
        }
    }

    protected override void OnStylusOutOfRange(StylusEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusOutOfRange(e);
        if (IsPen(e.StylusDevice))
        {
            _hoverInverted = false;
            HideEraserCursor();
        }
    }

    protected override void OnStylusLeave(StylusEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusLeave(e);
        if (IsPen(e.StylusDevice))
        {
            OnPenLeft();
        }
    }

    /// <summary>A hovering pen left the surface: hide the cursor unless an erase gesture is still live.</summary>
    internal void OnPenLeft()
    {
        if (_erase is null)
        {
            HideEraserCursor();
        }
    }

    protected override void OnStylusDown(StylusDownEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusDown(e);
        var device = e.StylusDevice;
        if (!IsPen(device))
        {
            return; // Fingers never ink nor erase: touch goes on to the viewport for pan and pinch.
        }

        e.Handled = true; // The pen never scrolls.
        NotifyPenNearby(); // no editor yet: this stroke does not ink (nothing is shown), the next one will
        RefreshIgnoredDevices(device);
        UpdateRendererEnabled(device);
        if (IsInking)
        {
            return;
        }

        if (BeginEraseGesture(device.Inverted, device.Id))
        {
            EraseStylusPoints(e);
            device.Capture(this);
            return;
        }

        if (!CanInk(device))
        {
            return;
        }

        BeginPenStroke(device.Id, ReportsPressure(device.TabletDevice));
        AppendStylusPoints(e);
        device.Capture(this);
    }

    protected override void OnStylusMove(StylusEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusMove(e);
        if (_penPoints is not null && e.StylusDevice.Id == _penStylusId)
        {
            AppendStylusPoints(e);
            e.Handled = true;
        }
        else if (_erase is { StylusId: { } id } && e.StylusDevice.Id == id)
        {
            EraseStylusPoints(e);
            e.Handled = true;
        }
        else if (!IsInking && IsPen(e.StylusDevice))
        {
            UpdateEraserCursor(e.GetPosition(this), e.StylusDevice.Inverted);
        }
    }

    protected override void OnStylusUp(StylusEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusUp(e);
        if (_penPoints is not null && e.StylusDevice.Id == _penStylusId)
        {
            AppendStylusPoints(e);
            e.Handled = true;
            FinishPenStroke();
        }
        else if (_erase is { StylusId: { } id } && e.StylusDevice.Id == id)
        {
            EraseStylusPoints(e);
            e.Handled = true;
            EndEraseGesture();
            UpdateEraserCursor(e.GetPosition(this), e.StylusDevice.Inverted); // still hovering
        }
    }

    protected override void OnLostStylusCapture(StylusEventArgs e)
    {
        base.OnLostStylusCapture(e);
        if (_penPoints is not null)
        {
            FinishPenStroke();
        }
        else if (_erase is { StylusId: not null })
        {
            EndEraseGesture();
            HideEraserCursor(); // shown again by the next hover move
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);
        if (e.StylusDevice is not null || IsInking)
        {
            return; // Promoted from pen or touch: already handled (or deliberately ignored) as stylus input.
        }

        if (BeginEraseGesture(inverted: false, stylusId: null))
        {
            EraseAtView(e.GetPosition(this));
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (!CanInk(null))
        {
            return;
        }

        _mouseStyle = CurrentStyle();
        var position = e.GetPosition(this);
        _mousePoints = [StylusPointConverter.FromMouse(position, Scale, e.Timestamp)];
        LockZoom(true);
        _mouseWet = new Stroke(
            new StylusPointCollection { new StylusPoint(position.X, position.Y) },
            MouseWetAttributes(_mouseStyle.Color, _mouseStyle.WidthPt * Scale));
        _presenter.Strokes.Add(_mouseWet);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (e.StylusDevice is not null)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (_erase is { StylusId: null })
        {
            EraseAtView(position);
            return;
        }

        if (_mousePoints is null)
        {
            if (!IsInking)
            {
                UpdateEraserCursor(position, inverted: false);
            }

            return;
        }

        _mousePoints.Add(StylusPointConverter.FromMouse(position, Scale, e.Timestamp));
        _mouseWet?.StylusPoints.Add(new StylusPoint(position.X, position.Y));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonUp(e);
        if (_mousePoints is not null && e.StylusDevice is null)
        {
            _mousePoints.Add(StylusPointConverter.FromMouse(e.GetPosition(this), Scale, e.Timestamp));
            e.Handled = true;
            FinishMouseStroke();
        }
        else if (_erase is { StylusId: null } && e.StylusDevice is null)
        {
            EraseAtView(e.GetPosition(this));
            e.Handled = true;
            EndEraseGesture();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_mousePoints is not null)
        {
            FinishMouseStroke();
        }
        else if (_erase is { StylusId: null })
        {
            EndEraseGesture();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeave(e);
        if (e.StylusDevice is null && _erase is null)
        {
            HideEraserCursor();
        }
    }

    /// <summary>
    /// Starts an eraser gesture if the back end touched (<paramref name="inverted"/>, FR-30) or the effective
    /// tool is the eraser (FR-34). The back end switches the tool to the eraser until <see cref="EndEraseGesture"/>.
    /// </summary>
    /// <param name="stylusId">The pen's stylus device Id, or null for the mouse.</param>
    internal bool BeginEraseGesture(bool inverted, int? stylusId)
    {
        if (IsInking || Editor is not { } editor || ViewModel is not { } vm || !ReferenceEquals(editor.Page, Page))
        {
            return false;
        }

        if ((!inverted && vm.Tools.EffectiveTool != AppTool.Eraser) || vm.ActiveErase is not null)
        {
            return false; // not erasing, or a gesture is live on another page (one at a time)
        }

        if (inverted)
        {
            vm.Tools.BeginInvertedEraser();
        }

        // One gesture at a time, on the page where it starts: a gesture live on another page refuses this one.
        vm.BeginErase(editor);
        if (vm.ActiveErase is null || !ReferenceEquals(vm.ActiveErasePage, editor))
        {
            if (inverted)
            {
                vm.Tools.EndInvertedEraser();
            }

            return false;
        }

        _erase = new EraseGesture(stylusId, inverted);
        return true;
    }

    /// <summary>Moves the live eraser to a surface position (DIPs) and the cursor with it.</summary>
    internal void EraseAtView(Point position)
    {
        if (_erase is null || ViewModel is not { } vm)
        {
            return;
        }

        vm.EraseAt(StylusPointConverter.ToPage(position, Scale));
        ShowEraserCursorAt(position);
    }

    /// <summary>
    /// Ends the gesture exactly once on every path (up, lost capture): one undo step at most, zoom unlocked by
    /// the view model, the back end's eraser handed back to the previous tool (FR-30).
    /// </summary>
    internal void EndEraseGesture()
    {
        if (_erase is not { } gesture)
        {
            return;
        }

        _erase = null; // Before releasing capture: the lost-capture handler must not finish twice.
        if (gesture.StylusId is not null)
        {
            if (Stylus.Captured == this)
            {
                Stylus.Capture(null);
            }
        }
        else if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        var vm = ViewModel;
        try
        {
            // Only this page's gesture: if it was dropped meanwhile (undo mid-gesture), another page's is not ended.
            if (vm is not null && Editor is { } editor && ReferenceEquals(vm.ActiveErasePage, editor))
            {
                vm.EndErase();
            }
        }
        finally
        {
            if (gesture.Inverted)
            {
                vm?.Tools.EndInvertedEraser();
            }

            UpdateRendererEnabled(null);
            InkingEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Drops whatever gesture is live without committing anything (the surface is recycled or its document goes):
    /// a stroke is discarded, an erase cancelled, capture released, the zoom unlocked, the back end's tool given back.
    /// </summary>
    internal void CancelLiveGesture()
    {
        if (!IsInking)
        {
            return;
        }

        var vm = ViewModel;
        var hadStroke = _penPoints is not null || _mousePoints is not null;
        _penPoints = null;
        _mousePoints = null;
        if (_mouseWet is not null)
        {
            _presenter.Strokes.Remove(_mouseWet);
            _mouseWet = null;
        }

        if (_erase is { } gesture)
        {
            _erase = null;
            if (vm is not null && Editor is { } editor && ReferenceEquals(vm.ActiveErasePage, editor))
            {
                vm.CancelErase();
            }

            if (gesture.Inverted)
            {
                vm?.Tools.EndInvertedEraser();
            }
        }

        if (Stylus.Captured == this)
        {
            Stylus.Capture(null);
        }

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (hadStroke)
        {
            LockZoom(false);
        }

        HideEraserCursor();
        UpdateRendererEnabled(null);
        InkingEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows the eraser circle at a surface position while the effective tool is the eraser or the pen's
    /// back end hovers (FR-32); hides it otherwise.
    /// </summary>
    internal void UpdateEraserCursor(Point position, bool inverted)
    {
        _hoverInverted = inverted;
        if (ShowsEraserCursor())
        {
            ShowEraserCursorAt(position);
        }
        else
        {
            HideEraserCursor();
        }
    }

    internal void HideEraserCursor()
    {
        _cursorCenterPt = null;
        _eraserCursor.Visibility = Visibility.Collapsed;
    }


    /// <summary>Attributes of the temporary stroke that shows a mouse stroke wet; the mouse has no pressure.</summary>
    private static DrawingAttributes MouseWetAttributes(InkColor color, double widthDip) => new()
    {
        Color = CustomInkLayer.ToColor(color),
        Width = widthDip,
        Height = widthDip,
        FitToCurve = true,
        IgnorePressure = true,
        StylusTip = StylusTip.Ellipse,
    };
    private static bool IsPen(StylusDevice? device) => device?.TabletDevice?.Type == TabletDeviceType.Stylus;

    private static bool ReportsPressure(TabletDevice? tablet) =>
        tablet is not null && tablet.SupportedStylusPointProperties.Any(p => p.Id == StylusPointProperties.NormalPressure.Id);

    private Size PageSizeDip() => Page is { } page
        ? new Size(page.WidthPt * Scale, page.HeightPt * Scale)
        : new Size(PageSizePt.Width * Scale, PageSizePt.Height * Scale);

    /// <summary>Only a page whose strokes are loaded (an editor of its own) accepts ink.</summary>
    private bool CanInk(StylusDevice? device) =>
        Editor is { } editor
        && ReferenceEquals(editor.Page, Page)
        && ViewModel is { } vm
        && vm.Tools.EffectiveTool == AppTool.Pen
        && device?.Inverted != true;

    private StrokeStyle CurrentStyle()
    {
        var tools = ViewModel!.Tools;
        return new StrokeStyle(tools.Color, tools.BaseWidthPt);
    }

    private void AppendStylusPoints(StylusEventArgs e) => AppendPenPoints(e.GetStylusPoints(this), e.Timestamp);

    /// <summary>
    /// Adds samples (surface DIPs, relative to this page even beyond its edges: the stroke is clipped on commit, FR-27)
    /// to the live pen stroke. Separate from the event handler so tests can drive it.
    /// </summary>
    internal void AppendPenPoints(IReadOnlyList<StylusPoint> points, int timestamp)
    {
        if (_penPoints is null)
        {
            return;
        }

        var scale = Scale;
        foreach (var sp in points)
        {
            _penPoints.Add(StylusPointConverter.ToInkPoint(sp, scale, timestamp, _penHasPressure));
        }
    }

    /// <summary>Every sample of the batch, not only the last: the swept capsule must see each one (FR-31).</summary>
    private void EraseStylusPoints(StylusEventArgs e)
    {
        var points = e.GetStylusPoints(this);
        for (var i = 0; i < points.Count; i++)
        {
            EraseAtView(new Point(points[i].X, points[i].Y));
        }
    }

    private bool ShowsEraserCursor() =>
        ViewModel is { } vm && (_hoverInverted || vm.Tools.EffectiveTool == AppTool.Eraser);

    private void ShowEraserCursorAt(Point position)
    {
        _cursorCenterPt = StylusPointConverter.ToPage(position, Scale);
        PlaceEraserCursor();
        _eraserCursor.Visibility = Visibility.Visible;
    }

    /// <summary>Sizes and centres the circle: eraser diameter in page points × scale, in surface DIPs.</summary>
    private void PlaceEraserCursor()
    {
        if (_cursorCenterPt is not { } center || ViewModel is not { } vm)
        {
            return;
        }

        var diameter = Units.MmToPt(vm.Tools.EraserSizeMm) * Scale;
        var view = StylusPointConverter.ToView(center, Scale);
        _eraserCursor.Width = diameter;
        _eraserCursor.Height = diameter;
        Canvas.SetLeft(_eraserCursor, view.X - (diameter / 2));
        Canvas.SetTop(_eraserCursor, view.Y - (diameter / 2));
    }

    /// <summary>The working copy while an eraser gesture is live on this page, the page strokes otherwise.</summary>
    private IReadOnlyList<InkStroke> CurrentStrokes() =>
        ViewModel is { ActiveErase: { } erase, ActiveErasePage: { } erasePage }
        && Editor is { } editor
        && ReferenceEquals(erasePage, editor)
        && ReferenceEquals(editor.Page, Page)
            ? erase.Strokes
            : Page?.Strokes ?? Array.Empty<InkStroke>();

    private void OnErasePreviewChanged(object? sender, EventArgs e) =>
        SetCurrentValue(DisplayStrokesProperty, CurrentStrokes());

    /// <summary>Starts a pen stroke: state, zoom lock. Separate from the event handler so tests can drive it.</summary>
    internal void BeginPenStroke(int stylusId, bool hasPressure)
    {
        _penStylusId = stylusId;
        _penHasPressure = hasPressure;
        _penStyle = CurrentStyle();
        _penPoints = [];
        LockZoom(true);
    }

    internal void FinishPenStroke()
    {
        var points = _penPoints!;
        _penPoints = null; // Before releasing capture: LostStylusCapture must not finish twice.
        if (Stylus.Captured == this)
        {
            Stylus.Capture(null);
        }

        Commit(points, _penStyle);
        InkingEnded?.Invoke(this, EventArgs.Empty);
    }

    private void FinishMouseStroke()
    {
        var points = _mousePoints!;
        _mousePoints = null;
        if (_mouseWet is not null)
        {
            _presenter.Strokes.Remove(_mouseWet);
            _mouseWet = null;
        }

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Commit(points, _mouseStyle);
        InkingEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Commit(List<InkPoint> points, StrokeStyle style)
    {
        // Committing raises Page.StrokesChanged, which redraws the committed ink synchronously, before the
        // dynamic renderer drops its wet visual: no gap between wet and committed ink. The editor clips the stroke
        // to this page (FR-27).
        try
        {
            if (points.Count > 0 && Editor is { } editor && ReferenceEquals(editor.Page, Page))
            {
                editor.CommitStroke(points, InkTool.Pen, style.Color, style.WidthPt);
            }
        }
        finally
        {
            LockZoom(false);
        }

        UpdateRendererEnabled(null);
    }

    /// <summary>No zoom while a stroke is live: the wet ink would stay at the old scale.</summary>
    private void LockZoom(bool locked)
    {
        if (ViewModel is { } vm)
        {
            vm.ZoomVm.IsLocked = locked;
        }
    }

    private void OnViewModelChanged(MainViewModel? oldValue, MainViewModel? newValue)
    {
        if (oldValue is not null)
        {
            if (_erase is { } gesture)
            {
                _erase = null;
                oldValue.CancelErase();
                if (gesture.Inverted)
                {
                    oldValue.Tools.EndInvertedEraser();
                }
            }

            if (IsInking)
            {
                oldValue.ZoomVm.IsLocked = false;
            }

            oldValue.Tools.PropertyChanged -= OnToolsChanged;
            oldValue.ErasePreviewChanged -= OnErasePreviewChanged;
        }

        if (newValue is not null)
        {
            newValue.Tools.PropertyChanged += OnToolsChanged;
            newValue.ErasePreviewChanged += OnErasePreviewChanged;
        }

        UpdateRendererSettings();
        UpdateRendererEnabled(null);
        HideEraserCursor();
        SetCurrentValue(DisplayStrokesProperty, CurrentStrokes());
    }

    private void OnEditorChanged(PageEditor? editor)
    {
        if (editor is not null && !ReferenceEquals(editor.Page, Page))
        {
            SetCurrentValue(PageProperty, editor.Page); // refreshes the strokes and the renderer gating
            return;
        }

        SetCurrentValue(DisplayStrokesProperty, CurrentStrokes());
        UpdateRendererEnabled(null);
    }

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateRendererSettings();
        UpdateRendererEnabled(null);
        if (_cursorCenterPt is not null)
        {
            if (ShowsEraserCursor())
            {
                PlaceEraserCursor(); // eraser size changed
            }
            else
            {
                HideEraserCursor();
            }
        }
    }

    private void OnPageChanged(CorePage? oldValue, CorePage? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.StrokesChanged -= OnStrokesChanged;
        }

        if (newValue is not null)
        {
            newValue.StrokesChanged += OnStrokesChanged;
        }

        SetCurrentValue(DisplayStrokesProperty, CurrentStrokes());
        UpdateRendererEnabled(null);
    }

    private void OnStrokesChanged(object? sender, EventArgs e)
    {
        if (sender is CorePage page && ReferenceEquals(page, Page))
        {
            SetCurrentValue(DisplayStrokesProperty, CurrentStrokes());
        }
    }

    private void OnScaleChanged()
    {
        _customLayer.Scale = Scale;
        UpdateRendererSettings();
        PlaceEraserCursor();
    }

    private void OnOutlineSettingsChanged()
    {
        _customLayer.Curve = Curve;
        _customLayer.Smoothing = Smoothing;
        UpdateRendererSettings();
    }

    /// <summary>Hands the committed strokes to the outline layer, which caches one geometry per stroke Id.</summary>
    private void RefreshInk() => _customLayer.Strokes = DisplayStrokes;

    /// <summary>Pushes colour, width, scale and curve to the wet renderer, only when something changed.</summary>
    private void UpdateRendererSettings()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var settings = (vm.Tools.Color, vm.Tools.BaseWidthPt, Scale, Curve, _hasPressure);
        if (_rendererSettings == settings)
        {
            return;
        }

        _rendererSettings = settings;
        _renderer.Configure(vm.Tools.Color, vm.Tools.BaseWidthPt, Scale, Curve, _hasPressure);
    }

    /// <summary>
    /// No wet pen ink while the effective tool is the eraser or the back end is in use (Review Focus 3).
    /// Not changed during a live pen stroke, so a stroke never loses its wet ink half-way.
    /// </summary>
    private void UpdateRendererEnabled(StylusDevice? device)
    {
        if (_penPoints is not null)
        {
            return;
        }

        var enabled = Editor is not null
            && ViewModel is { } vm
            && vm.Tools.EffectiveTool == AppTool.Pen
            && device?.Inverted != true;
        if (_renderer.Enabled != enabled)
        {
            _renderer.Enabled = enabled;
        }
    }

    /// <summary>Tells the renderer which tablets are touch screens and which cursors are pen back ends.</summary>
    private void RefreshIgnoredDevices(StylusDevice? current)
    {
        var touchTablets = new HashSet<int>();
        var invertedStyluses = new HashSet<int>();
        foreach (TabletDevice tablet in Tablet.TabletDevices)
        {
            if (tablet.Type == TabletDeviceType.Touch)
            {
                touchTablets.Add(tablet.Id);
            }

            foreach (StylusDevice stylus in tablet.StylusDevices)
            {
                if (stylus.Inverted)
                {
                    invertedStyluses.Add(stylus.Id);
                }
            }
        }

        if (current?.Inverted == true)
        {
            invertedStyluses.Add(current.Id);
        }

        _renderer.SetIgnoredDevices(touchTablets, invertedStyluses);
    }

    private readonly record struct StrokeStyle(InkColor Color, double WidthPt);

    /// <param name="StylusId">The erasing pen's stylus device Id, or null for the mouse.</param>
    /// <param name="Inverted">True for the pen's back end: the previous tool comes back on lift (FR-30).</param>
    private readonly record struct EraseGesture(int? StylusId, bool Inverted);
}
