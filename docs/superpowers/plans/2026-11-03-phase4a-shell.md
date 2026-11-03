# Phase 4a Window Shell, Theme, Settings and Floating Toolbar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the pilot window with the final shell: Mica/FluentWindow with a transparent title bar, light/dark theme, a settings file, and the floating toolbar with its option flyouts, retracting while the pen writes; the debug panel disappears and its functions move to their final homes.

**Architecture:** WPF UI supplies the window, theme manager and base controls. Settings are a pure model in Core with a plain-file store following `ViewPositionStore`. Toolbar, flyouts and auto-visibility are view-model state machines (unit-tested with injectable clocks) plus thin XAML. Views never take the pen outside their visible bounds.

**Tech Stack:** unchanged plus **WPF-UI** (lepoco, MIT; Fausto approved on 2026-11-02).

**Spec:** `docs/superpowers/specs/2026-11-03-phase4a-shell-design.md` (binding; read all of it first). Requirements: `docs/requirements.md` sections 3 and 4, UI-02..UI-06.

## Global Constraints

- Only NuGet package allowed: WPF-UI (verify the license is MIT, pin the version, check it targets .NET 10 and win-x64/arm64). No other new package.
- Core has no WPF; the store is plain file I/O; nothing about document content in settings or logs.
- Values (colours, radii 12/999/8 px, shadows, durations) only in the theme dictionaries; UI text only in `Strings.resx` (+ `Strings.Designer.cs`, drift test); NFR-32: animations off when Windows animations are off, opaque panels when transparency effects are off.
- Pen rule: the pen draws; a floating element never takes the pen outside its visible bounds; on the toolbar and on an open flyout the pen uses the controls (back end included), and a pen press outside the flyout closes it and inks (controller ruling after the Task 3 review; the edge panel keeps its own rule: the pen on the sheet closes it); floating surfaces carry an inert stylus plug-in (no wet ink under them); the 3a/3b/3c pen rules (rail knob, edge panel, highlighter) hold.
- 40 px touch targets; `AutomationProperties.Name` on every command; keyboard reachable.
- Nullable on; file-scoped namespaces; one public type per file; `_` prefix for private static readonly/const; warnings are errors; TDD in Core/Pdf; view models unit-tested; never run the WPF app other than the in-process STA tests; do not stage `.claude/settings.json`; commit trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>` (or the model doing the work); `dotnet build` 0 warnings, `dotnet test` green, `dotnet format --verify-no-changes` clean at the end of every task.

## Review Focus

1. **The pen and the page are untouched**: no new layer takes pen input over the document; ink feel, eraser and highlighter behave as before; toolbar retract/return timings. Pinned in Tasks 2, 3, 6.
2. **Nothing lost from the debug panel**: every old function has a final home, reachable by touch, pen-free keyboard and shortcuts, with names. Pinned in Tasks 4, 5.
3. **Theme and materials never flash or break**: switching theme/Mica/transparency at runtime, window resize, restore from minimised. Pinned in Tasks 0, 6.
4. **Settings are safe**: corrupt/unknown/missing file, concurrent writes, read-only folder: defaults, never a crash. Pinned in Task 1.
5. **Eraser mode is one**: the eraser panel's mode drives the tool and the pen's back end; each tool remembers its colour/width. Pinned in Task 2.

---

### Task 0: WPF UI, FluentWindow shell, Mica and theme service

**Files:** Modify `src/DreamBooks.App/DreamBooks.App.csproj` (WPF-UI package), `App.xaml(.cs)` (theme resources: WPF UI theme + our dictionaries), `MainWindow.xaml(.cs)` (becomes a `ui:FluentWindow` with `WindowBackdropType="Mica"`, `ExtendsContentIntoTitleBar`, a transparent title bar with the file name and window buttons); Create `src/DreamBooks.App/Services/ThemeService.cs` (modes System/Light/Dark, accent override, applies through `ApplicationThemeManager`, follows Windows changes in System mode, raises `ThemeChanged`), `src/DreamBooks.App/Resources/Theme.Light.xaml`, `Theme.Dark.xaml` (the existing `Theme.xaml` tokens split into theme-specific brushes: backdrop grey around the pages, page shadow, panel tint and opaque variants, rail/tick brushes, highlighter/search tokens keep their values in both themes unless a dark variant is needed), `docs/superpowers/specs/2026-11-03-phase4a-wpfui-notes.md` (version, license check, size, anything surprising). Test: `tests/DreamBooks.App.Tests/ThemeServiceTests.cs`, smoke test of the window.
- [ ] Add the package, record license/version/size in the notes file, build.
- [ ] Failing tests: `ThemeService_applies_light_dark_and_system`, `ThemeService_follows_a_system_change_only_in_system_mode`, `Accent_override_is_applied_and_cleared`, STA `The_window_is_a_FluentWindow_with_Mica_and_content_into_the_title_bar`, `Switching_the_theme_at_runtime_does_not_throw_and_swaps_the_resources`, `Without_transparency_the_backdrop_falls_back_to_a_solid_brush`.
- [ ] Implement; the debug panel stays for now but must look acceptable on the new shell (do not restyle it); document view fills the whole window including under the title bar.
- [ ] Commit `feat: WPF UI fluent window with Mica, transparent title bar and theme service (UI-06, section 3)`.

### Task 1: Settings model, store and palette rules

**Files:** Create `src/DreamBooks.Core/Settings/AppSettings.cs` (immutable record with defaults: `ThemeMode` System, `AccentOverride` null, `EraserMode` WholeStroke, `PressureCurve` Firm, `Smoothing` Strong, `ToolbarFixed` false, `ToolbarEdge` Bottom, per-tool memory (`PenColor`, `PenWidthMm`, `HighlighterColor`, `HighlighterShape`, `HighlighterAngleDeg`, `HighlighterStraightLine`, `EraserSizeMm`), `CustomPalette` (≤ 12 colours)), `src/DreamBooks.Core/Settings/CustomPalette.cs` (rules: add, dedupe case-insensitively, drop oldest at 12, remove), `src/DreamBooks.Pdf/AppSettingsStore.cs` (`%LOCALAPPDATA%\DreamBooks\settings.json`, `"v":1`, atomic temp+replace, ASCII-safe JSON, corrupt/unknown version/NaN/invalid values → defaults field by field, `Save` never throws and reports through `Failed`, no document content or paths); Test: `tests/DreamBooks.Core.Tests/Settings/*`, `tests/DreamBooks.Pdf.Tests/AppSettingsStoreTests.cs`.
- [ ] Failing tests: defaults equal the factory values; palette add/dedupe/drop-oldest/remove; store round trip of every key; missing file → defaults; corrupt JSON → defaults; unknown version → defaults; an invalid single field falls back for that field only; atomic under concurrent readers; read-only folder raises `Failed` and does not throw; the file contains no path and no text.
- [ ] Implement; commit `feat: versioned app settings file with defaults and a shared custom palette (FR-110)`.

### Task 2: Toolbar, option and visibility view models

**Files:** Create under `src/DreamBooks.App/ViewModels/`: `ToolbarViewModel.cs` (tools: Pen, Highlighter, Eraser, Selection (disabled), Undo, Redo, Bookmark (disabled); `ActiveTool`, `OpenPanel` (None/Pen/Highlighter/Eraser): a tap on an inactive tool activates it and closes any panel; a tap on the active tool toggles its panel), `ToolbarVisibilityState.cs` (pure state machine with an injectable clock: writing for 300 ms retracts over 200 ms, shows 600 ms after pen-up, finger tap on an empty area toggles, `ToolbarFixed` keeps it, panels close on writing, reduced motion = instant), `PenOptionsViewModel.cs` (8 presets + `Palette` + width presets + slider, commands), `EraserOptionsViewModel.cs` (mode whole/partial, size, clear-page command with a confirm flag), extend `HighlighterOptionsViewModel` (custom palette colours, alpha stays 89), `SettingsService.cs` (loads at startup, exposes `AppSettings`, saves debounced 500 ms, applies changes to `ToolsViewModel`); Modify `ToolsViewModel.cs` (one eraser mode drives both the eraser tool and the pen's back end; each tool's colour and width are remembered through the settings; values come from the settings at startup).
- [ ] Failing tests: second-tap panel state machine (all transitions); visibility state machine with a fake clock (writing 299 ms keeps, 300 ms retracts, pen-up 599 ms stays hidden, 600 ms shows, tap toggles, fixed mode, panels closed by writing and reopened never automatically, reduced motion instant); eraser mode change reaches the pen back end (`IsInverted` path); remembered colour/width per tool persisted and restored; palette commands (add from picker, remove, max 12, highlighter uses the same palette with alpha 89); disabled tools cannot become active; clear-page requires the confirmation step and is undoable (Core command); `Strings` drift.
- [ ] Implement; commit `feat: toolbar, option panels and visibility state machines (UI-02, FR-25, FR-31, FR-110)`.

### Task 3: Toolbar view and flyouts

**Files:** Create `src/DreamBooks.App/Views/ToolbarView.xaml(.cs)`, `PenOptionsFlyout.xaml(.cs)`, `HighlighterOptionsFlyout.xaml(.cs)`, `EraserOptionsFlyout.xaml(.cs)`, `ColorSwatches` control (presets + custom + "+" opening a colour picker) under `src/DreamBooks.App/Views/`; Modify `MainWindow.xaml` (host the toolbar over the document, bottom centre), `Theme.*.xaml`, `Strings.resx`/Designer (Italian: tool names, "Colore", "Spessore", "Curva di pressione", "Cancella tutto nella pagina", "Aggiungi colore", …).
- Pill shape 999 px, panel radius 12 px, inner 8 px, two-layer shadow, Segoe Fluent Icons 20 px, 40 px targets, sliding active indicator (150 ms), flyout appear/disappear (180/120 ms fade + scale), retract fade (200 ms), all instant when Windows animations are off; panels opaque when transparency is off. The pen never hits the toolbar outside its visible bounds; a pen press on an open flyout closes it without writing; touch/mouse as normal. Swatch hit targets 40 px; long-press on a custom swatch removes it.
- [ ] Failing STA tests: toolbar shows the 7 buttons with Italian names and the right disabled states; second tap opens the right flyout; flyout closes on Esc, tap outside and pen press (no stroke written); pen press at the toolbar's transparent area (outside its pill) reaches the ink surface; flyout appear/disappear animations use the theme durations and are instant with reduced motion; opaque brushes with transparency off; swatches select presets and custom colours; "+" adds to the palette; window instantiates in both themes; automation names present.
- [ ] Implement; commit `feat: floating toolbar and option flyouts (UI-02, section 3)`.

### Task 4: Indicator chip, navigation buttons and search pill

**Files:** Create `src/DreamBooks.App/Views/PageIndicatorChip.xaml(.cs)` (bottom right: "12 / 48 · 125%", tap = go-to-page field, long tap/hover shows zoom −/+), `NavigationButtons.xaml(.cs)` (Back, previous/next mark, status text, shown with the rail and the indicator, fade 150 ms), `SearchPill.xaml(.cs)` (top centre; field, count text, previous/next, close; Ctrl+F opens and focuses, Esc closes and clears; F3/Shift+F3 as today); Modify `ScrollRail`/`DocumentView` wiring as needed (the old indicator text and go-to-page field move to the chip; keep the rail's behaviour and the pen rules); `MainWindow.xaml(.cs)` key routing (`RouteKey` keeps working; no shortcut changes).
- [ ] Failing tests: the chip shows the page/zoom text and the go-to-page field accepts valid numbers only; zoom buttons call the zoom commands; navigation buttons enable/disable as the commands do and show/hide with the rail; the search pill opens on Ctrl+F, closes on Esc and clears the search, count/next/previous bound to `SearchViewModel`; pen at the pill's transparent surround inks; all names in resx; existing rail/pen/search tests stay green.
- [ ] Implement; commit `feat: page indicator chip, navigation buttons and search pill replace the debug panel's navigation (UI-05, FR-09, FR-13)`.

### Task 5: Main menu, title bar content, save line, and removal of the debug panel

**Files:** Create `src/DreamBooks.App/Views/MainMenuButton.xaml(.cs)` (round button top left, expands a panel: Open, Save, Save as, Recent files (disabled placeholder until 4b), Export (disabled), Marks, Pages, Search, Settings (disabled until 4b)), `SaveProgressLine.xaml(.cs)` (thin line under the title bar while `IsSaving`); Modify `MainWindow.xaml` (delete the `Expander` debug panel and every control it hosted; title bar shows the file name (small) and the dirty mark), Strings.
- [ ] Failing tests: no `Expander` panel in the window; menu entries invoke the existing commands (Open/Save/SaveAs/marks/pages/search), disabled entries disabled; menu closes on Esc, tap outside and pen press (no stroke); the save line shows only while saving; the title shows the file name and the dirty mark; the window instantiates with an open document and without one (the empty canvas, 4b adds the start screen); every old function from the table in the spec has a reachable replacement (a test lists them); `Strings` drift.
- [ ] Implement; commit `feat: main menu, title bar content and save line; the debug panel is removed (UI-03, UI-06)`.

### Task 6: Integration, regression, requirements, publish and checklist (controller)

- [ ] STA/integration sweep: theme switch with a document open; toolbar retracts while a stroke is written with the real pen seams and returns; pen/finger/eraser/highlighter gating tests green; save while the toolbar is open; opening documents repeatedly (the crash fix) with the new shell; reduced animations and transparency; window resize/minimise/restore with Mica; settings persisted across a restart (new view model over the same settings file).
- [ ] Requirements (Italian): UI-02 (custom palette shared by pen and highlighter, lasso/bookmark disabled until their phases, toolbar edge dragging in 4b), section 3 materials (translucent tint, no backdrop blur; opaque fallback), new FR-110 (settings file), mark the 4a items done in section 10 once tested. CLAUDE.md project structure (Core/Settings, Pdf AppSettingsStore, App Services/ThemeService, SettingsService, the toolbar/flyout/chip/search/menu views).
- [ ] Publish `publish-4a`; give Fausto the Italian checklist (look in light/dark, Mica, toolbar retract/return with the pen, flyouts, palette, eraser mode with the pen back end, Windows animations/transparency off, resize, everything the panel used to do) and wait for his result before merging.
