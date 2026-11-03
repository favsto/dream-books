using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Settings;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>The settings service: load at startup, debounced save, values pushed to the tools and the theme (FR-110).</summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-settings-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

    public SettingsServiceTests() => Directory.CreateDirectory(_dir);

    private string SettingsFile => Path.Combine(_dir, "settings.json");

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

    private SettingsService NewService(string? root = null)
    {
        var service = new SettingsService(new AppSettingsStore(root ?? _dir), _scheduler);
        service.Load();
        return service;
    }

    [Fact]
    public void Without_a_file_the_factory_values_reach_the_tools()
    {
        var service = NewService();
        var tools = new ToolsViewModel { Curve = PressureCurve.Linear, EraserMode = EraserMode.Partial };

        service.AttachTools(tools);

        Assert.Equal(AppSettings.Default, service.Current);
        Assert.Equal(PressureCurve.Firm, tools.Curve);
        Assert.Equal(SmoothingLevel.Strong, tools.Smoothing);
        Assert.Equal(EraserMode.WholeStroke, tools.EraserMode);
        Assert.Equal(AppSettings.DefaultPenColor, tools.Color);
        Assert.Equal(AppSettings.DefaultPenWidthMm, tools.BaseWidthMm);
        Assert.Equal(HighlighterPresets.Yellow, tools.Highlighter.Color);
        Assert.Equal(0, _scheduler.Pending); // nothing changed: nothing to write
    }

    [Fact]
    public async Task Each_tools_colour_and_width_are_remembered_and_restored_by_a_new_view_model()
    {
        var first = NewService();
        var tools = new ToolsViewModel();
        first.AttachTools(tools);
        var pen = new PenOptionsViewModel(tools);
        var teal = InkColor.FromRgb(0x00, 0x80, 0x80);

        pen.AddCustomColorCommand.Execute(teal);
        pen.WidthMm = 1.5;
        tools.Highlighter.SelectColorCommand.Execute(HighlighterPresets.Pink);
        tools.Highlighter.IsChisel = true;
        tools.Highlighter.ChiselAngleDeg = 120;
        tools.Highlighter.IsStraightLine = true;
        tools.EraserMode = EraserMode.Partial;
        tools.EraserSizeMm = 11;
        tools.Curve = PressureCurve.Soft;
        tools.Smoothing = SmoothingLevel.Light;
        _scheduler.Advance(SettingsService.SaveDelay);
        await first.WhenWrittenAsync();
        Assert.True(File.Exists(SettingsFile));

        var second = NewService();
        var restored = new ToolsViewModel();
        second.AttachTools(restored);

        Assert.Equal(teal, restored.Color);
        Assert.Equal(1.5, restored.BaseWidthMm);
        Assert.Equal(HighlighterPresets.Pink, restored.Highlighter.Color);
        Assert.True(restored.Highlighter.IsChisel);
        Assert.Equal(120, restored.Highlighter.ChiselAngleDeg);
        Assert.True(restored.Highlighter.IsStraightLine);
        Assert.Equal(EraserMode.Partial, restored.EraserMode);
        Assert.Equal(11, restored.EraserSizeMm);
        Assert.Equal(PressureCurve.Soft, restored.Curve);
        Assert.Equal(SmoothingLevel.Light, restored.Smoothing);
        Assert.Equal([teal], new PenOptionsViewModel(restored).CustomColors);
        Assert.Equal([teal with { A = HighlighterPresets.Alpha }], restored.Highlighter.CustomColors);
    }

    [Fact]
    public async Task Theme_eraser_mode_colours_and_the_palette_survive_a_restart_into_a_new_main_view_model()
    {
        // The first run: the app's view model, as App and MainWindow wire it.
        var first = NewService();
        var themes = new List<ThemeMode>();
        first.AttachTheme(s => themes.Add(s.ThemeMode));
        var before = new MainViewModel(new FakeFileDialogService(), scheduler: _scheduler);
        first.AttachTools(before.Tools);
        first.AttachToolbar(before.Toolbar);
        var violet = InkColor.FromRgb(0x80, 0x40, 0xC0);
        var teal = InkColor.FromRgb(0x00, 0x80, 0x80);
        before.Toolbar.Pen.AddCustomColorCommand.Execute(violet);
        before.Toolbar.Highlighter.AddCustomColorCommand.Execute(teal);
        before.Toolbar.Eraser.IsPartial = true;
        before.Toolbar.Pen.WidthMm = 2.5;
        first.Update(s => s with { ThemeMode = ThemeMode.Dark, ToolbarFixed = true });
        _scheduler.Advance(SettingsService.SaveDelay);
        await first.WhenWrittenAsync();

        // The restart: a new service over the same file, a new view model.
        var second = NewService();
        var restartThemes = new List<ThemeMode>();
        second.AttachTheme(s => restartThemes.Add(s.ThemeMode));
        var after = new MainViewModel(new FakeFileDialogService(), scheduler: _scheduler);
        second.AttachTools(after.Tools);
        second.AttachToolbar(after.Toolbar);

        Assert.Equal([ThemeMode.Dark], restartThemes);
        Assert.True(after.Toolbar.IsToolbarFixed);
        Assert.Equal(EraserMode.Partial, after.Tools.EraserMode); // the eraser tool and the pen's back end
        Assert.True(after.Toolbar.Eraser.IsPartial);
        Assert.Equal(violet, after.Tools.Color);
        Assert.Equal(2.5, after.Tools.BaseWidthMm);
        Assert.Equal(teal with { A = HighlighterPresets.Alpha }, after.Tools.Highlighter.Color);
        Assert.Equal([violet, teal], after.Toolbar.Pen.CustomColors); // one palette, shared, oldest first
        Assert.Equal([violet with { A = HighlighterPresets.Alpha }, teal with { A = HighlighterPresets.Alpha }], after.Toolbar.Highlighter.CustomColors);
    }

    [Fact]
    public async Task Saving_is_debounced_by_500_ms()
    {
        var service = NewService();
        var tools = new ToolsViewModel();
        service.AttachTools(tools);

        tools.BaseWidthMm = 1;
        _scheduler.Advance(499);
        await service.WhenWrittenAsync();
        Assert.False(File.Exists(SettingsFile));

        tools.BaseWidthMm = 2; // a new change restarts the wait
        _scheduler.Advance(499);
        await service.WhenWrittenAsync();
        Assert.False(File.Exists(SettingsFile));

        _scheduler.Advance(1);
        await service.WhenWrittenAsync();
        Assert.True(File.Exists(SettingsFile));
        Assert.Equal(2, new AppSettingsStore(_dir).Load().PenWidthMm);
        Assert.Equal(0, _scheduler.Pending);
    }

    [Fact]
    public async Task Flush_writes_a_pending_change_at_once()
    {
        var service = NewService();
        var tools = new ToolsViewModel();
        service.AttachTools(tools);
        tools.Color = Palette.Colors[2];

        await service.FlushAsync();

        Assert.Equal(Palette.Colors[2], new AppSettingsStore(_dir).Load().PenColor);
        Assert.Equal(0, _scheduler.Pending);
    }

    [Fact]
    public async Task A_store_failure_is_reported_and_never_thrown()
    {
        var blocker = Path.Combine(_dir, "not-a-folder");
        await File.WriteAllTextAsync(blocker, "x");
        var service = NewService(Path.Combine(blocker, "settings")); // a folder under a file cannot be created
        var tools = new ToolsViewModel();
        service.AttachTools(tools);
        var failures = 0;
        service.SaveFailed += (_, _) => failures++;

        tools.BaseWidthMm = 3;
        _scheduler.Advance(SettingsService.SaveDelay);
        await service.WhenWrittenAsync();

        Assert.Equal(1, failures);
        Assert.True(service.LastSaveFailed);
        Assert.Equal(3, service.Current.PenWidthMm); // the app goes on with the values in memory
    }

    [Fact]
    public async Task A_later_successful_save_clears_the_failure()
    {
        var service = NewService();
        var tools = new ToolsViewModel();
        service.AttachTools(tools);
        Directory.Delete(_dir, recursive: true);
        await File.WriteAllTextAsync(_dir, "x"); // the folder is now a file
        tools.BaseWidthMm = 3;
        _scheduler.Advance(SettingsService.SaveDelay);
        await service.WhenWrittenAsync();
        Assert.True(service.LastSaveFailed);

        File.Delete(_dir);
        tools.BaseWidthMm = 4;
        _scheduler.Advance(SettingsService.SaveDelay);
        await service.WhenWrittenAsync();

        Assert.False(service.LastSaveFailed);
    }

    [Fact]
    public async Task The_save_status_is_raised_when_writing_starts_failing_and_when_it_works_again()
    {
        var root = Path.Combine(_dir, "settings");
        await File.WriteAllTextAsync(root, "x"); // the folder is a file: writing fails
        var service = NewService(root);
        var tools = new ToolsViewModel();
        service.AttachTools(tools);
        var changes = new List<string?>();
        service.SaveStatusChanged += (_, _) => changes.Add(service.Warning);

        tools.BaseWidthMm = 3;
        _scheduler.Advance(SettingsService.SaveDelay);
        await service.WhenWrittenAsync();
        tools.BaseWidthMm = 3.5; // still failing: no new status
        _scheduler.Advance(SettingsService.SaveDelay);
        await service.WhenWrittenAsync();
        Assert.Equal([Strings.WarnSettingsNotSaved], changes);

        File.Delete(root);
        tools.BaseWidthMm = 4;
        _scheduler.Advance(SettingsService.SaveDelay);
        await service.WhenWrittenAsync();
        Assert.Equal([Strings.WarnSettingsNotSaved, null], changes);
    }

    [Fact]
    public void The_theme_and_the_accent_reach_the_theme_service()
    {
        File.WriteAllText(SettingsFile, """{"v":1,"theme":"Dark","accent":"#112233"}""");
        var service = NewService();
        var applied = new List<(ThemeMode Mode, string? Accent)>();

        service.AttachTheme(s => applied.Add((s.ThemeMode, s.AccentOverride)));
        Assert.Equal([(ThemeMode.Dark, "#112233")], applied);

        service.Update(s => s with { ThemeMode = ThemeMode.Light });
        service.Update(s => s with { ToolbarFixed = true }); // not a theme change
        Assert.Equal([(ThemeMode.Dark, "#112233"), (ThemeMode.Light, "#112233")], applied);
    }

    [Fact]
    public void Toolbar_fixed_reaches_the_toolbar()
    {
        File.WriteAllText(SettingsFile, """{"v":1,"toolbarFixed":true}""");
        var service = NewService();
        var toolbar = new ToolbarViewModel(
            new ToolsViewModel(),
            new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }),
            new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }),
            () => null,
            _scheduler,
            () => _scheduler.Now);

        service.AttachToolbar(toolbar);
        Assert.True(toolbar.IsToolbarFixed);

        service.Update(s => s with { ToolbarFixed = false });
        Assert.False(toolbar.IsToolbarFixed);
    }
}
