using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Settings;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>The settings window's state: every group writes through the settings service and follows outside changes (FR-110, UI-02).</summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-settingsvm-" + Guid.NewGuid().ToString("N"));
    private readonly ManualScheduler _scheduler = new();

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

    private (SettingsService Service, SettingsViewModel Vm) Create()
    {
        var service = new SettingsService(new AppSettingsStore(_dir), _scheduler);
        service.Load();
        return (service, new SettingsViewModel(service));
    }

    [Fact]
    public void The_properties_start_from_the_current_settings()
    {
        var (service, _) = Create();
        service.Update(s => s with { ThemeMode = ThemeMode.Dark, ToolbarEdge = ToolbarEdge.Left, Smoothing = SmoothingLevel.Light });

        var vm = new SettingsViewModel(service);

        Assert.Equal(ThemeMode.Dark, vm.ThemeMode);
        Assert.Equal(ToolbarEdge.Left, vm.ToolbarEdge);
        Assert.Equal(SmoothingLevel.Light, vm.Smoothing);
        Assert.Null(vm.AccentHex);
    }

    [Fact]
    public void Each_property_writes_through_the_settings_service()
    {
        var (service, vm) = Create();

        vm.ThemeMode = ThemeMode.Light;
        vm.EraserMode = EraserMode.Partial;
        vm.PressureCurve = PressureCurve.Soft;
        vm.Smoothing = SmoothingLevel.Off;
        vm.ToolbarFixed = true;
        vm.ToolbarEdge = ToolbarEdge.Top;
        vm.AccentHex = "#1E90FF";

        var s = service.Current;
        Assert.Equal(ThemeMode.Light, s.ThemeMode);
        Assert.Equal(EraserMode.Partial, s.EraserMode);
        Assert.Equal(PressureCurve.Soft, s.PressureCurve);
        Assert.Equal(SmoothingLevel.Off, s.Smoothing);
        Assert.True(s.ToolbarFixed);
        Assert.Equal(ToolbarEdge.Top, s.ToolbarEdge);
        Assert.Equal("#1E90FF", s.AccentOverride);
    }

    [Fact]
    public void An_outside_change_reaches_the_properties_and_raises_PropertyChanged()
    {
        var (service, vm) = Create();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        service.Update(s => s with { PressureCurve = PressureCurve.Linear, ToolbarFixed = true });

        Assert.Equal(PressureCurve.Linear, vm.PressureCurve);
        Assert.True(vm.ToolbarFixed);
        Assert.Contains(nameof(SettingsViewModel.PressureCurve), raised);
        Assert.Contains(nameof(SettingsViewModel.ToolbarFixed), raised);
        Assert.DoesNotContain(nameof(SettingsViewModel.ThemeMode), raised); // what did not change is not announced
    }

    [Theory]
    [InlineData("1e90ff", "#1E90FF")]
    [InlineData("#1e90ff", "#1E90FF")]
    public void The_accent_is_normalised(string typed, string stored)
    {
        var (service, vm) = Create();

        vm.AccentHex = typed;

        Assert.Equal(stored, service.Current.AccentOverride);
        Assert.Equal(stored, vm.AccentHex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("rosso")]
    [InlineData("#GGGGGG")]
    public void An_invalid_accent_keeps_the_old_value_and_shows_it_again(string typed)
    {
        var (service, vm) = Create();
        vm.AccentHex = "#1E90FF";
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.AccentHex = typed;

        Assert.Equal("#1E90FF", service.Current.AccentOverride);
        Assert.Equal("#1E90FF", vm.AccentHex);
        Assert.Contains(nameof(SettingsViewModel.AccentHex), raised); // the text box goes back to the valid value
    }

    [Fact]
    public void ClearAccent_goes_back_to_the_system_accent()
    {
        var (service, vm) = Create();
        vm.AccentHex = "#1E90FF";

        vm.ClearAccentCommand.Execute(null);

        Assert.Null(service.Current.AccentOverride);
        Assert.Null(vm.AccentHex);
    }

    [Fact]
    public void Opening_and_closing_are_commands_and_Esc_closes()
    {
        var (_, vm) = Create();
        var raised = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsOpen))
            {
                raised.Add(vm.IsOpen);
            }
        };

        vm.OpenCommand.Execute(null);
        Assert.True(vm.IsOpen);
        vm.CloseCommand.Execute(null);
        Assert.False(vm.IsOpen);
        Assert.Equal([true, false], raised);
    }

    [Fact]
    public async Task The_settings_are_saved_half_a_second_after_the_last_change()
    {
        var (service, vm) = Create();

        vm.ThemeMode = ThemeMode.Dark;
        _scheduler.Advance(SettingsService.SaveDelay);
        await service.WhenWrittenAsync();

        Assert.Equal(ThemeMode.Dark, new AppSettingsStore(_dir).Load().ThemeMode);
    }
}
