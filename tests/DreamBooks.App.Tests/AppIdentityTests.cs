using System.Diagnostics;

namespace DreamBooks.App.Tests;

/// <summary>
/// The name Windows shows for the app ("Apri con", Default apps, task manager) comes from the executable's version
/// resource, which is built from these assembly attributes.
/// </summary>
public sealed class AppIdentityTests
{
    [Fact]
    public void Windows_shows_Dream_Books_and_not_the_assembly_name()
    {
        var info = FileVersionInfo.GetVersionInfo(typeof(App).Assembly.Location);

        Assert.Equal("Dream Books", info.FileDescription);
        Assert.Equal("Dream Books", info.ProductName);
    }
}
