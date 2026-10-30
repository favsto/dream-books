namespace DreamBooks.App.Resources;

/// <summary>Strongly typed access to Strings.resx (Italian UI text). Keep in sync with the resx.</summary>
internal static class Strings
{
    private static readonly System.Resources.ResourceManager Manager =
        new("DreamBooks.App.Resources.Strings", typeof(Strings).Assembly);

    public static string OpenPdf => Get(nameof(OpenPdf));

    public static string PdfFileFilter => Get(nameof(PdfFileFilter));

    public static string Page => Get(nameof(Page));

    public static string Renderer => Get(nameof(Renderer));

    public static string RendererStock => Get(nameof(RendererStock));

    public static string RendererCustom => Get(nameof(RendererCustom));

    public static string ToolPen => Get(nameof(ToolPen));

    public static string ToolEraser => Get(nameof(ToolEraser));

    public static string PressureCurve => Get(nameof(PressureCurve));

    public static string CurveLinear => Get(nameof(CurveLinear));

    public static string CurveSoft => Get(nameof(CurveSoft));

    public static string CurveFirm => Get(nameof(CurveFirm));

    public static string Smoothing => Get(nameof(Smoothing));

    public static string SmoothingOff => Get(nameof(SmoothingOff));

    public static string SmoothingLight => Get(nameof(SmoothingLight));

    public static string SmoothingStrong => Get(nameof(SmoothingStrong));

    public static string Width => Get(nameof(Width));

    public static string Color => Get(nameof(Color));

    public static string EraserMode => Get(nameof(EraserMode));

    public static string EraseWhole => Get(nameof(EraseWhole));

    public static string ErasePartial => Get(nameof(ErasePartial));

    public static string EraserSize => Get(nameof(EraserSize));

    public static string Undo => Get(nameof(Undo));

    public static string Redo => Get(nameof(Redo));

    public static string Zoom => Get(nameof(Zoom));

    public static string FitWidth => Get(nameof(FitWidth));

    public static string Panel => Get(nameof(Panel));

    public static string DiscardStrokesConfirm => Get(nameof(DiscardStrokesConfirm));

    public static string ErrorFileNotFound => Get(nameof(ErrorFileNotFound));

    public static string ErrorInvalidFormat => Get(nameof(ErrorInvalidFormat));

    public static string ErrorPassword => Get(nameof(ErrorPassword));

    public static string ErrorUnsupported => Get(nameof(ErrorUnsupported));

    public static string ErrorUnknown => Get(nameof(ErrorUnknown));

    public static string NoDocument => Get(nameof(NoDocument));

    private static string Get(string name) =>
        Manager.GetString(name, System.Globalization.CultureInfo.CurrentUICulture) ?? name;
}
