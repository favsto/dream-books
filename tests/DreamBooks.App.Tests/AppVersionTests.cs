using System.Text.RegularExpressions;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>The version shown in the settings comes from the build, so it can never disagree with the installer.</summary>
public sealed class AppVersionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-version-" + Guid.NewGuid().ToString("N"));

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
    [InlineData("0.5.0", "0.5.0")]
    [InlineData("0.5.0+4cf58c6d1e2f", "0.5.0")] // the SDK appends the commit as build metadata
    [InlineData("1.2.3-beta.1+abc", "1.2.3-beta.1")]
    [InlineData(" 0.5.0 ", "0.5.0")]
    public void The_build_metadata_after_the_plus_is_not_shown(string informational, string expected)
        => Assert.Equal(expected, AppVersion.Format(informational));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_version_nothing_wrong_is_shown(string? informational)
        => Assert.Equal(string.Empty, AppVersion.Format(informational));

    [Fact]
    public void The_running_app_has_a_three_part_version()
        => Assert.Matches(new Regex(@"^\d+\.\d+\.\d+"), AppVersion.Current);

    [Fact]
    public void The_settings_show_the_app_name_and_the_version()
    {
        var service = new SettingsService(new AppSettingsStore(_dir), new ManualScheduler());
        service.Load();

        var vm = new SettingsViewModel(service);

        Assert.Contains("Dream Books", vm.VersionText);
        Assert.Contains(AppVersion.Current, vm.VersionText);
    }
}
