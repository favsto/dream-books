# Phase 4a — WPF UI package notes

Date: 2026-11-02 (Task 0 of the phase 4a plan). Fausto approved adding WPF UI on 2026-11-02.

## Package

| Item | Value |
| --- | --- |
| Package | `WPF-UI` (lepoco/wpfui, <https://github.com/lepoco/wpfui>), pinned in `Directory.Packages.props` |
| Version | **4.3.0** (latest on nuget.org on 2026-11-02; repository commit `153baade`) |
| Dependency | `WPF-UI.Abstractions` 4.3.0 (MIT, 7 KB assembly), nothing else on .NET 10 |
| Target used | `lib/net10.0-windows7.0` (the package also ships net8/net9 and .NET Framework builds); `frameworkReference` Microsoft.WindowsDesktop.App.WPF only |
| License | **MIT** (`<license type="expression">MIT</license>`, `LICENSE.md` in the package: MIT, Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors). `requireLicenseAcceptance` is true (MIT acceptance, no extra terms) |
| Third-party notices | `ThirdPartyNotices.txt`: VirtualizingWrapPanel (MIT), fluentui-system-icons (MIT), dotnet/wpf (MIT), microsoft-ui-xaml (MIT) are compiled in; a notice for the Segoe Fluent Icons font, which is **not** embedded (the only fonts in the assembly are `FluentSystemIcons-Regular/Filled.ttf`, MIT): the app uses the Segoe Fluent Icons font installed with Windows 11, as any Windows app does |
| Size | `Wpf.Ui.dll` for net10.0-windows: 6.6 MB (+ 0.95 MB XML docs, 0.27 MB pdb, not deployed). Restored package folder: 62 MB (the 17 MB `.nupkg` plus six target frameworks), `wpf-ui.abstractions` 0.9 MB. The publish output grows by about 6.6 MB |
| Architecture | Managed only (AnyCPU IL), no native binaries: works for win-x64 and win-arm64 (NFR-21) |
| Network | None: the library makes no network calls (NFR-40) |

## Usage in Dream Books

- `App.AddThemeResources` (code, shared with the tests' application) merges, in this order: `ui:ThemesDictionary` (WPF UI colours, swapped by `ApplicationThemeManager`),
  `ui:ControlsDictionary` (implicit Fluent styles for the standard controls), our `Resources/Theme.xaml`
  (theme-independent tokens) and `Resources/Theme.Light.xaml` or `Theme.Dark.xaml` (our colour tokens per theme,
  swapped by `ThemeService`). Our implicit `Button`/`RadioButton`/`CheckBox` styles are `BasedOn` the WPF UI ones, so
  they keep the 40 px targets on the Fluent look.
- `ThemeService` calls `ApplicationThemeManager.Apply(theme, backdrop, updateAccent: false)` and then
  `ApplicationAccentColorManager.Apply(accent, theme)` (the system accent, or the override), then re-merges a fresh
  copy of our theme dictionary so the accent-derived brushes (`SystemAccentColorPrimary`) are current. WPF UI's
  `SystemThemeWatcher` is **not** used: it would follow Windows even in a manual Light/Dark mode.
- `MainWindow` is a `ui:FluentWindow` with `WindowBackdropType="Mica"`, `ExtendsContentIntoTitleBar="True"` and a
  `ui:TitleBar` laid over the content (the document fills the window, including under the title bar).

## Surprises and things to know

- **ControlsDictionary restyles every standard control implicitly** (Button, TextBox, ScrollViewer, Expander...). The
  document's own `ScrollViewer` opts out (`Style="{x:Null}"`): WPF UI's template adds its own scroll bars and must not
  touch the pen/pan/zoom path. The debug panel's buttons, fields and sliders take the Fluent look as is (the panel
  disappears in Task 5), but its `Expander` keeps the system style (`Style="{x:Null}"`): **WPF UI's Expander lays a
  sideways (`ExpandDirection="Right"`) expander out without its content** (the panel's buttons had no size and were
  missing from the visual tree; three existing tests caught it).
- Our implicit `Button`/`RadioButton`/`CheckBox` styles must be `BasedOn="{StaticResource {x:Type ...}}"`, or they
  replace WPF UI's style with the bare system template (light buttons in the dark theme).
- **The `TitleBar` is 48 px tall by default** (the WinUI tall title bar). Dream Books sets it to 40 px
  (`TitleBarHeight`, the touch target): a smaller strip over the document, still a 40 px target for the window buttons.
- **Mica** is applied by `FluentWindow` when its handle is created (`OnSourceInitialized`) through DWM
  (`DWMWA_SYSTEMBACKDROP_TYPE`, Windows 11 22H2+). On older builds WPF UI falls back to the legacy Mica attribute or
  to the plain window background. It does **not** look at the "Transparency effects" setting: Dream Books does
  (`TransparencySetting`, NFR-32) and switches the window to `WindowBackdropType.None` with an opaque theme brush.
- `ApplicationThemeManager.Apply` also re-applies the backdrop and the dark/light window frame to
  `Application.MainWindow` (`WindowBackgroundManager.UpdateBackground`), so `ThemeService` passes the backdrop
  currently in use. At startup the theme is applied before the window has a handle, so `MainWindow` calls
  `UpdateBackground` itself on `SourceInitialized` (dark Mica tint and frame in the dark theme).
- `Application.ThemeMode` (WPF's own experimental Fluent theme, .NET 9+) clashes by name with a `ThemeMode` enum inside
  `App`: ours is `AppThemeMode`. WPF's own Fluent theme is not used.
- The title bar is a real caption area (WPF UI answers `WM_NCHITTEST`): a press in its height drags the window, also
  with the pen, as with any title bar (it hides in full screen, UI-06, later).
- Tests: the theme lives in the shared test application's resources and `WpfTestThread.Drain`/`PumpUntil` also run
  work that parallel tests queued on the WPF thread, so the tests that switch the theme run alone
  (`ThemeCollection`, parallelization disabled).
- Trimming: WPF does not support trimming, so nothing changes (the app is published self-contained, untrimmed).
