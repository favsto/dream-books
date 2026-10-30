using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Input.StylusPlugIns;
using System.Windows.Media;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using CorePage = DreamBooks.Core.Model.Page;

namespace DreamBooks.App.Ink;

/// <summary>
/// The page itself: PDF bitmap, committed ink (mode A or B) and the wet ink of the pen.
/// Its size is the page size × <see cref="Scale"/>; strokes are kept in page points and only scaled to
/// draw (FR-26). Pen ink is drawn live by a <see cref="DynamicRenderer"/> on the real-time stylus thread
/// (FR-24, NFR-04); on pen-up one stroke is committed through <see cref="PageEditor.CommitStroke"/>.
/// Fingers never ink (they reach the viewport for pan and zoom); the mouse inks like the pen (NFR-23).
/// </summary>
public sealed class InkSurface : FrameworkElement
{
    /// <summary>
    /// DIPs per page point at which mode A strokes are built (zoom 400%). Coarser than that, WPF's curve
    /// fitting and tessellation tolerances would show when zoomed in; the transform scales the result.
    /// </summary>
    internal const double StockReferenceScale = 4 * Units.DipPerPt;

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(MainViewModel), typeof(InkSurface),
        new PropertyMetadata(null, (d, e) => ((InkSurface)d).OnViewModelChanged((MainViewModel?)e.OldValue, (MainViewModel?)e.NewValue)));

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

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(RendererMode), typeof(InkSurface),
        new PropertyMetadata(RendererMode.Custom, (d, _) => ((InkSurface)d).OnModeChanged()));

    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve), typeof(PressureCurve), typeof(InkSurface),
        new PropertyMetadata(PressureCurve.Linear, (d, _) => ((InkSurface)d).OnOutlineSettingsChanged()));

    public static readonly DependencyProperty SmoothingProperty = DependencyProperty.Register(
        nameof(Smoothing), typeof(SmoothingLevel), typeof(InkSurface),
        new PropertyMetadata(SmoothingLevel.Light, (d, _) => ((InkSurface)d).OnOutlineSettingsChanged()));

    public static readonly DependencyProperty DisplayStrokesProperty = DependencyProperty.Register(
        nameof(DisplayStrokes), typeof(IReadOnlyList<InkStroke>), typeof(InkSurface),
        new PropertyMetadata(Array.Empty<InkStroke>(), (d, _) => ((InkSurface)d).RefreshInk()));

    public static readonly DependencyProperty PageImageProperty = DependencyProperty.Register(
        nameof(PageImage), typeof(ImageSource), typeof(InkSurface),
        new PropertyMetadata(null, (d, e) => ((InkSurface)d)._image.Source = (ImageSource?)e.NewValue));

    private readonly Image _image;
    private readonly CustomInkLayer _customLayer;
    // Mode A committed strokes live in their own presenter, built once at StockReferenceScale and zoomed by
    // a transform; the wet presenter (_presenter) stays untransformed, in surface DIPs like the stylus input.
    private readonly InkPresenter _stockPresenter;
    private readonly ScaleTransform _stockTransform = new(1, 1);
    private readonly InkPresenter _presenter;
    private readonly Visual[] _children;

    private readonly PenDynamicRenderer _stockRenderer = new();
    private readonly CustomDynamicRenderer _customRenderer = new();
    private PenDynamicRenderer? _activeRenderer;
    private bool _hasPressure = true;
    private (InkColor Color, double WidthPt, double Scale, PressureCurve Curve, bool HasPressure)? _rendererSettings;

    // Mode A committed strokes by model stroke Id; the same instances stay in _stockPresenter.Strokes.
    private readonly Dictionary<Guid, Stroke> _stockCache = [];

    // Pen stroke in progress (UI thread copy of the points; the wet ink comes from the renderer).
    private List<InkPoint>? _penPoints;
    private int _penStylusId;
    private bool _penHasPressure;
    private StrokeStyle _penStyle;

    // Mouse stroke in progress, drawn wet on the UI thread through a temporary stock stroke.
    private List<InkPoint>? _mousePoints;
    private Stroke? _mouseWet;
    private StrokeStyle _mouseStyle;

    public InkSurface()
    {
        _image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _customLayer = new CustomInkLayer();
        _stockPresenter = new InkPresenter { IsHitTestVisible = false, RenderTransform = _stockTransform };
        _presenter = new InkPresenter { IsHitTestVisible = false };
        _children = [_image, _customLayer, _stockPresenter, _presenter];
        foreach (var child in _children)
        {
            AddVisualChild(child);
        }

        ClipToBounds = true;
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        Stylus.SetIsTouchFeedbackEnabled(this, false);

        _customLayer.Scale = Scale;
        UpdateStockTransform();
        _customLayer.Curve = Curve;
        _customLayer.Smoothing = Smoothing;
        OnModeChanged();
        Loaded += (_, _) => RefreshIgnoredDevices(null);
    }

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public CorePage? Page
    {
        get => (CorePage?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    /// <summary>DIPs per page point: zoom × <see cref="Units.DipPerPt"/>.</summary>
    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public RendererMode Mode
    {
        get => (RendererMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
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

    /// <summary>True while a pen or mouse stroke is being drawn; the viewport must not pan or zoom then.</summary>
    public bool IsInking => _penPoints is not null || _mousePoints is not null;

    /// <summary>The wet-ink renderer currently plugged in; exposed for tests.</summary>
    internal PenDynamicRenderer ActiveRenderer => _activeRenderer!;

    /// <summary>Mode A committed strokes; exposed for tests.</summary>
    internal StrokeCollection StockStrokes => _stockPresenter.Strokes;

    internal CustomInkLayer CustomLayer => _customLayer;

    /// <summary>Number of stylus plug-ins installed; exposed for tests (only one wet renderer at a time).</summary>
    internal int PlugInCount => StylusPlugIns.Count;

    /// <summary>Zoom applied to the mode A strokes (built at <see cref="StockReferenceScale"/>); for tests.</summary>
    internal double StockStrokeScale => _stockTransform.ScaleX;

    protected override int VisualChildrenCount => _children.Length;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = PageSizeDip();
        foreach (var child in _children)
        {
            ((UIElement)child).Measure(ReferenceEquals(child, _stockPresenter) ? StockPresenterSize() : size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(PageSizeDip());
        foreach (var child in _children)
        {
            ((UIElement)child).Arrange(ReferenceEquals(child, _stockPresenter) ? new Rect(StockPresenterSize()) : rect);
        }

        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        // Transparent background so the whole page is hit-testable for stylus and mouse input.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
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
        }
    }

    protected override void OnStylusInAirMove(StylusEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusInAirMove(e);
        if (IsPen(e.StylusDevice))
        {
            UpdateRendererEnabled(e.StylusDevice);
        }
    }

    protected override void OnStylusDown(StylusDownEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStylusDown(e);
        var device = e.StylusDevice;
        if (!IsPen(device))
        {
            return; // Fingers never ink: touch goes on to the viewport for pan and pinch.
        }

        e.Handled = true; // The pen never scrolls.
        RefreshIgnoredDevices(device);
        UpdateRendererEnabled(device);
        if (_penPoints is not null || _mousePoints is not null || !CanInk(device))
        {
            return; // Back end or eraser tool: erasing arrives in Task 13; no ink here.
        }

        _penStylusId = device.Id;
        _penHasPressure = ReportsPressure(device.TabletDevice);
        _penStyle = CurrentStyle();
        _penPoints = [];
        LockZoom(true);
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
    }

    protected override void OnLostStylusCapture(StylusEventArgs e)
    {
        base.OnLostStylusCapture(e);
        if (_penPoints is not null)
        {
            FinishPenStroke();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);
        if (e.StylusDevice is not null || _penPoints is not null || _mousePoints is not null || !CanInk(null))
        {
            return; // Promoted from pen or touch: already handled (or deliberately ignored) as stylus input.
        }

        _mouseStyle = CurrentStyle();
        var position = e.GetPosition(this);
        _mousePoints = [StylusPointConverter.FromMouse(position, Scale, e.Timestamp)];
        LockZoom(true);
        _mouseWet = new Stroke(
            new StylusPointCollection { new StylusPoint(position.X, position.Y) },
            StockStrokeConverter.CreateAttributes(_mouseStyle.Color, _mouseStyle.WidthPt * Scale, ignorePressure: true));
        _presenter.Strokes.Add(_mouseWet);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (_mousePoints is null || e.StylusDevice is not null)
        {
            return;
        }

        var position = e.GetPosition(this);
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
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_mousePoints is not null)
        {
            FinishMouseStroke();
        }
    }

    private static bool IsPen(StylusDevice? device) => device?.TabletDevice?.Type == TabletDeviceType.Stylus;

    private static bool ReportsPressure(TabletDevice? tablet) =>
        tablet is not null && tablet.SupportedStylusPointProperties.Any(p => p.Id == StylusPointProperties.NormalPressure.Id);

    private Size PageSizeDip() => Page is { } page ? new Size(page.WidthPt * Scale, page.HeightPt * Scale) : default;

    private Size StockPresenterSize() =>
        Page is { } page ? new Size(page.WidthPt * StockReferenceScale, page.HeightPt * StockReferenceScale) : default;

    private bool CanInk(StylusDevice? device) =>
        ViewModel is { Editor: not null } vm
        && ReferenceEquals(vm.Editor.Page, Page)
        && vm.Tools.EffectiveTool == AppTool.Pen
        && device?.Inverted != true;

    private StrokeStyle CurrentStyle()
    {
        var tools = ViewModel!.Tools;
        return new StrokeStyle(tools.Color, tools.BaseWidthPt);
    }

    private void AppendStylusPoints(StylusEventArgs e)
    {
        var scale = Scale;
        foreach (var sp in e.GetStylusPoints(this))
        {
            _penPoints!.Add(StylusPointConverter.ToInkPoint(sp, scale, e.Timestamp, _penHasPressure));
        }
    }

    private void FinishPenStroke()
    {
        var points = _penPoints!;
        _penPoints = null; // Before releasing capture: LostStylusCapture must not finish twice.
        if (Stylus.Captured == this)
        {
            Stylus.Capture(null);
        }

        Commit(points, _penStyle);
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
    }

    private void Commit(List<InkPoint> points, StrokeStyle style)
    {
        // Committing raises Page.StrokesChanged, which redraws the committed ink synchronously, before the
        // dynamic renderer drops its wet visual: no gap between wet and committed ink.
        try
        {
            if (points.Count > 0 && ViewModel?.Editor is { } editor && ReferenceEquals(editor.Page, Page))
            {
                editor.CommitStroke(points, InkTool.Pen, style.Color, style.WidthPt);
            }
        }
        finally
        {
            LockZoom(false);
        }

        ApplyMode();
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
            if (IsInking)
            {
                oldValue.ZoomVm.IsLocked = false;
            }

            oldValue.Tools.PropertyChanged -= OnToolsChanged;
        }

        if (newValue is not null)
        {
            newValue.Tools.PropertyChanged += OnToolsChanged;
        }

        UpdateRendererSettings();
        UpdateRendererEnabled(null);
    }

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateRendererSettings();
        UpdateRendererEnabled(null);
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

        SetCurrentValue(DisplayStrokesProperty, newValue?.Strokes ?? Array.Empty<InkStroke>());
        UpdateRendererEnabled(null);
    }

    private void OnStrokesChanged(object? sender, EventArgs e)
    {
        if (sender is CorePage page && ReferenceEquals(page, Page))
        {
            SetCurrentValue(DisplayStrokesProperty, page.Strokes);
        }
    }

    private void OnScaleChanged()
    {
        _customLayer.Scale = Scale;
        UpdateStockTransform();
        UpdateRendererSettings();
    }

    /// <summary>Zoom only changes the transform of the mode A strokes: no stroke is rebuilt (FR-26).</summary>
    private void UpdateStockTransform()
    {
        _stockTransform.ScaleX = Scale / StockReferenceScale;
        _stockTransform.ScaleY = Scale / StockReferenceScale;
    }

    private void OnOutlineSettingsChanged()
    {
        _customLayer.Curve = Curve;
        _customLayer.Smoothing = Smoothing;
        UpdateRendererSettings();
    }

    private void OnModeChanged()
    {
        ApplyMode();
        RefreshInk();
    }

    /// <summary>Plugs in the wet renderer of the current mode; deferred while a pen stroke is live.</summary>
    private void ApplyMode()
    {
        if (_penPoints is null)
        {
            var next = Mode == RendererMode.Custom ? _customRenderer : _stockRenderer;
            if (!ReferenceEquals(next, _activeRenderer))
            {
                if (_activeRenderer is not null)
                {
                    StylusPlugIns.Remove(_activeRenderer);
                    _presenter.DetachVisuals(_activeRenderer.RootVisual);
                }

                StylusPlugIns.Add(next);
                _presenter.AttachVisuals(next.RootVisual, next.DrawingAttributes);
                _activeRenderer = next;
                UpdateRendererEnabled(null);
            }
        }
    }

    /// <summary>
    /// Brings committed ink in line with <see cref="DisplayStrokes"/> in the current mode. Mode A applies a
    /// diff to one persistent collection (only added or removed strokes change), so a pen lift or an undo
    /// never re-creates the other stroke visuals.
    /// </summary>
    private void RefreshInk()
    {
        var strokes = DisplayStrokes;
        var custom = Mode == RendererMode.Custom;
        _customLayer.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        _customLayer.Strokes = custom ? strokes : Array.Empty<InkStroke>();
        _stockPresenter.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;

        var collection = _stockPresenter.Strokes;
        if (custom)
        {
            _stockCache.Clear();
            collection.Clear();
            return;
        }

        var wanted = new Dictionary<Stroke, int>(strokes.Count);
        var alive = new HashSet<Guid>(strokes.Count);
        var ordered = new Stroke[strokes.Count];
        for (var i = 0; i < strokes.Count; i++)
        {
            var stroke = strokes[i];
            if (!_stockCache.TryGetValue(stroke.Id, out var wpf))
            {
                wpf = StockStrokeConverter.ToWpfStroke(stroke, StockReferenceScale);
                _stockCache[stroke.Id] = wpf;
            }

            alive.Add(stroke.Id);
            ordered[i] = wpf;
            wanted[wpf] = i;
        }

        foreach (var id in _stockCache.Keys.Where(id => !alive.Contains(id)).ToArray())
        {
            _stockCache.Remove(id);
        }

        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!wanted.ContainsKey(collection[i]))
            {
                collection.RemoveAt(i);
            }
        }

        // Insert new strokes at their model position (z-order follows the page's stroke order).
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i < collection.Count && ReferenceEquals(collection[i], ordered[i]))
            {
                continue;
            }

            collection.Remove(ordered[i]); // no-op unless it moved
            collection.Insert(Math.Min(i, collection.Count), ordered[i]);
        }
    }

    /// <summary>Pushes colour, width, scale and curve to both wet renderers, only when something changed.</summary>
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
        var widthDip = vm.Tools.BaseWidthPt * Scale;
        _stockRenderer.DrawingAttributes = StockStrokeConverter.CreateAttributes(vm.Tools.Color, widthDip, ignorePressure: false);
        _customRenderer.DrawingAttributes = StockStrokeConverter.CreateAttributes(vm.Tools.Color, widthDip, ignorePressure: false);
        _customRenderer.Configure(vm.Tools.Color, vm.Tools.BaseWidthPt, Scale, Curve, _hasPressure);
    }

    /// <summary>
    /// No wet pen ink while the effective tool is the eraser or the back end is in use (Review Focus 3).
    /// Not changed during a live pen stroke, so a stroke never loses its wet ink half-way.
    /// </summary>
    private void UpdateRendererEnabled(StylusDevice? device)
    {
        if (_penPoints is not null || _activeRenderer is null)
        {
            return;
        }

        var enabled = ViewModel is { Editor: not null } vm
            && vm.Tools.EffectiveTool == AppTool.Pen
            && device?.Inverted != true;
        if (_activeRenderer.Enabled != enabled)
        {
            _activeRenderer.Enabled = enabled;
        }
    }

    /// <summary>Tells the renderers which tablets are touch screens and which cursors are pen back ends.</summary>
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

        _stockRenderer.SetIgnoredDevices(touchTablets, invertedStyluses);
        _customRenderer.SetIgnoredDevices(touchTablets, invertedStyluses);
    }

    private readonly record struct StrokeStyle(InkColor Color, double WidthPt);
}
