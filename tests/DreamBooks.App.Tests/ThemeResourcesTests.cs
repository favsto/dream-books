using System.Windows;

namespace DreamBooks.App.Tests;

/// <summary>
/// The theme tokens split into Theme.xaml (the same in both themes) and Theme.Light.xaml / Theme.Dark.xaml: every key
/// the views used before the split (the old single Theme.xaml) still resolves in both themes, and the two theme
/// dictionaries define the same keys.
/// </summary>
public sealed class ThemeResourcesTests
{
    private static readonly string[] _keysBeforeTheSplit =
    [
        "TouchTarget", "Gap", "SectionGap", "ChiselAngleSmallChange",
        "ChiselAngleLargeChange", "EraserCursorFill", "EraserCursorOutline", "EraserCursorOutlineThickness",
        "PageBackdropBrush", "PagePlaceholderBrush", "PageFadeInDuration", "PageFadeInFromOpacity", "RailFadeInDuration",
        "RailFadeOutDuration", "RailIdleDelay", "RailTouchWidth", "RailBarWidth", "RailKnobWidth", "RailKnobHeight",
        "RailKnobPenSlop", "RailKnobFingerZoneWidth", "RailKnobFingerZonePadding", "RailKnobFingerZoneMinHeight",
        "RailBarCornerRadius", "RailKnobCornerRadius", "RailTrackMargin", "RailTrackBrush", "RailKnobBrush",
        "RailPreviewWidth", "RailPreviewMargin", "RailPreviewPadding", "RailPreviewCornerRadius",
        "RailPreviewBackgroundBrush", "RailPreviewShadow", "RailPreviewFadeInDuration", "RailPreviewFadeOutDuration",
        "RailPreviewCrossFadeDuration", "RailPreviewEase", "RailPreviewMinStrokeWidth", "RailTrackBrushOpaque",
        "RailPreviewBackgroundBrushOpaque", "RailMarkTickLength", "RailMarkTickThickness", "RailMarkTickBrush",
        "RailMarkTickBrushOpaque", "RailNoteTickLength", "RailNoteTickThickness", "RailNoteTickOffset", "RailNoteTickColor",
        "RailNoteTickBrush", "RailNoteTickBrushOpaque", "RailSearchTickLength", "RailSearchTickThickness",
        "RailSearchTickOffset", "RailSearchTickColor", "RailSearchTickBrush", "RailSearchTickBrushOpaque",
        "IndicatorChipMargin", "FlingDecayTimeConstant", "FlingMinSpeed", "FlingMaxSpeed", "EdgePanelWidth",
        "EdgePanelOpenDuration", "EdgePanelCloseDuration", "EdgeSwipeZoneWidth", "EdgePanelCornerRadius",
        "EdgePanelPadding", "EdgePanelMargin", "EdgePanelBackgroundBrush", "EdgePanelBackgroundBrushOpaque",
        "EdgePanelShadow", "EdgePanelTitleSize", "EdgePanelDetailMargin", "CloseGlyph",
        "GlyphSize", "GlyphStrokeThickness", "GlyphBrush", "MarkThumbnailWidth", "MarkThumbnailHeight",
        "MarkThumbnailMinStrokeWidth", "MarkThumbnailBorderBrush", "MarkRowPadding", "MarkRowLabelMargin",
        "GoToPageFieldWidth", "EdgePanelTabMargin", "EdgePanelTabPadding", "EdgePanelTabIndicatorThickness",
        "EdgePanelTabIndicatorBrush", "PagesThumbnailWidth", "PagesThumbnailMinStrokeWidth",
        "PagesThumbnailFrameThickness", "PagesThumbnailBorderBrush", "PagesRowPadding", "PagesNumberMargin",
        "PagesCurrentRingThickness", "PagesCurrentRingPadding", "PagesCurrentRingCornerRadius", "PagesCurrentRingBrush",
        "PagesTickColumnWidth", "PagesTickMargin", "PagesTickGap", "PagesTickWidth", "PagesTickHeight",
        "SearchHighlightColor", "SearchCurrentHighlightColor", "SearchHighlightBrush", "SearchCurrentHighlightBrush",
        "SearchHighlightPaddingPt", "EdgePanelTabStyle",
    ];

    /// <summary>The new shell's tokens (section 3): window fallback, floating panels.</summary>
    private static readonly string[] _shellKeys =
    [
        "WindowBackgroundOpaqueBrush", "PageBackdropBrushOpaque", "PanelTintBrushOpaque",
        "PanelDiffuseShadow", "PanelContactShadow", "TitleBarHeight", "TitleBarClearance",

        // The indicator chip, the navigation buttons and the search pill (UI-05, FR-09, FR-13, task 4).
        "NavigationFadeDuration", "IndicatorChipPadding", "NavigationButtonsMargin", "NavigationButtonGap",
        "NavigationStatusPadding", "ChipTextButtonStyle", "ChipTextPadding", "ChipFontSize", "SearchPillMargin",
        "SearchPillPadding", "SearchFieldWidth", "SearchGlyphMargin", "SearchCountMargin", "SearchCountMinWidth",
        "SearchMessageMargin", "SearchMessagePadding", "SearchMessageMaxWidth", "GlyphBack", "GlyphPrevious", "GlyphNext",
        "GlyphSearch", "GlyphClose", "GlyphZoomIn", "GlyphZoomOut",

        // The main menu, the save line and the notices (UI-03, FR-89, FR-84, task 5); the debug panel's PanelWidth,
        // ProgressBarHeight, SectionLabel and Previous/NextMarkGlyph went with it.
        "GlyphFitWidth", "GlyphMenu", "MainMenuMargin", "MainMenuPanelMargin", "MainMenuPanelWidth", "MainMenuPanelPadding",
        "MenuItemPadding", "MenuShortcutMargin", "MenuSeparatorMargin", "MenuSeparatorHeight", "MenuItemStyle",
        "MenuShortcutText", "SaveProgressLineHeight", "SaveProgressBarFraction", "SaveProgressCycleDuration",
        "SaveProgressBarBrush", "TopOverlaysMargin", "NoticeBarMargin", "NoticeBarPadding", "NoticeBarMaxWidth",
        "ToolbarMarginTop", "ToolbarMarginLeft", "ToolbarMarginRight", "FlyoutMarginTop", "FlyoutMarginLeft", "FlyoutMarginRight", "ToolbarPaddingVertical", "ToolbarSeparatorMarginVertical", "ToolbarGripLength", "ToolbarGripDotSize", "ToolbarGripDotGap", "ToolbarSnapFromOpacity", "TopOverlaysMarginBelowToolbar",
        "SettingsPanelWidth", "SettingsPanelMargin", "SettingsTitleSize", "SettingsAccentFieldMargin", "SettingsAccentFieldWidth", "MenuSubListMargin", "SplashBackgroundBrush", "SplashTextBrush", "SplashSubtextBrush", "SplashAccentBrush", "SplashIconSize", "SplashTitleSize", "SplashTaglineSize", "SplashIndicatorWidth", "SplashIndicatorHeight", "SplashPadding", "SplashTitleMargin", "SplashTaglineMargin", "SplashIndicatorMargin",
        "StartMargin", "StartMaxWidth", "StartTitleSize", "StartHeaderMargin", "StartCardWidth", "StartCoverHeight", "StartCardGap", "StartCardMargin", "StartCardTextMargin", "StartCardSubtextMargin", "StartCardTextSize", "StartProgressHeight", "StartProgressMargin", "StartCardButtonStyle", "ErrorLineMargin", "DialogButtonStyle",
        "NoticeTextMargin", "FlyoutFirstHeaderMargin", "ColorPickerHexMargin", "ColorPickerButtonMargin",
    ];

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Every_key_used_by_the_views_resolves_in_both_themes(string theme)
    {
        WpfTestThread.Run(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(Load("Theme.xaml"));
            resources.MergedDictionaries.Add(Load($"Theme.{theme}.xaml"));
            foreach (var key in _keysBeforeTheSplit.Concat(_shellKeys))
            {
                Assert.True(resources.Contains(key), $"'{key}' is missing in the {theme} theme.");
                Assert.NotNull(resources[key]);
            }

            // The implicit styles (40 px targets) are still there.
            Assert.True(resources.Contains(typeof(System.Windows.Controls.Button)));

            // No page shadow (an effect on every page would cost pen latency, NFR-04) and no unused panel tint: the
            // panels use PanelLayerBrush twice.
            Assert.False(resources.Contains("PageShadow"), "PageShadow is back");
            Assert.False(resources.Contains("PanelTintBrush"), "PanelTintBrush is back");
        });
    }

    [Fact]
    public void The_views_take_their_margins_from_the_theme()
    {
        // Values live in the theme dictionaries (wpf-ui rules); "0" only resets a style's margin.
        var views = Path.Combine(RepositoryRoot(), "src", "DreamBooks.App", "Views");
        var files = Directory.GetFiles(views, "*.xaml").Append(Path.Combine(views, "..", "MainWindow.xaml"));
        var literal = new System.Text.RegularExpressions.Regex("""(?:Margin|Padding)="(?!0")[-0-9.,\s]+" """.TrimEnd());
        var found = files
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, index)))
            .Where(entry => literal.IsMatch(entry.line))
            .Select(entry => $"{Path.GetFileName(entry.file)}:{entry.index + 1}: {entry.line.Trim()}")
            .ToList();
        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    [Fact]
    public void The_toolbar_fade_in_the_theme_is_the_retract_the_visibility_state_waits_for()
    {
        // The view fades the pill over the theme's duration; the state machine counts the same time before "hidden"
        // (section 3: 200 ms). Two definitions, one value.
        WpfTestThread.Run(() => Assert.Equal(
            ViewModels.ToolbarVisibilityState.RetractDuration,
            (TimeSpan)Load("Theme.xaml")["ToolbarRetractDuration"]));
    }

    [Fact]
    public void The_light_and_dark_dictionaries_define_the_same_keys_and_nothing_of_the_shared_one()
    {
        WpfTestThread.Run(() =>
        {
            var light = Keys(Load("Theme.Light.xaml"));
            var dark = Keys(Load("Theme.Dark.xaml"));
            var shared = Keys(Load("Theme.xaml"));
            Assert.Equal(light.Order(), dark.Order());
            Assert.Empty(light.Intersect(shared));
        });
    }

    private static ResourceDictionary Load(string file) =>
        new() { Source = new Uri($"/DreamBooks.App;component/Resources/{file}", UriKind.Relative) };

    private static List<string> Keys(ResourceDictionary dictionary) =>
        dictionary.Keys.Cast<object>().Select(k => k.ToString()!).ToList();
}
