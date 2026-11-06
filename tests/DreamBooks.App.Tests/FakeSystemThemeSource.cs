using System.Windows.Media;
using DreamBooks.App.Services;

namespace DreamBooks.App.Tests;

/// <summary>A Windows theme the tests set by hand; <see cref="Raise"/> plays a Windows setting change.</summary>
internal sealed class FakeSystemThemeSource : ISystemThemeSource
{
    public bool IsDark { get; set; }

    public Color AccentColor { get; set; } = Color.FromRgb(0x00, 0x78, 0xD4);

    public event EventHandler? Changed;

    public int Subscribers => Changed?.GetInvocationList().Length ?? 0;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
