# Phase 4a — Window shell, theme, settings file and the floating toolbar

Date: 2026-11-03. Status: **written by Claude on Fausto's delegation ("procedi con un tuo ordine"); Fausto approved the visual direction on the mockup (toolbar pill at the bottom, translucent panels without borders, round menu button top-left) and approved adding WPF UI.** Choices marked **[my call]** are mine and can be reversed. Requirements: `docs/requirements.md` sections 3 (principles, materials, shapes, animations) and 4 (UI-01..UI-07, toolbar UI-02, automatic visibility). Phases 1–3 are done, device-tested and merged on `main`.

## 1. Intent and the why

Today the window is a plain WPF `Window` with a "spartan debug panel" (an `Expander` full of buttons) on the left: all commands in permanent view, no theme, no Mica, no title bar design, no toolbar. Fausto wants the app to look and behave like the principles in section 3: the document is the protagonist, commands live in floating elements that appear when needed and retire while writing. 4a builds the shell and the commands for reading and annotating; 4b (separate) builds the start screen, recent files, settings window and the main menu content that needs them.

## 2. Goals, non-goals, anti-goals

**Goals**
1. **Window shell:** WPF UI `FluentWindow` with Mica backdrop and a transparent custom title bar (file name small, window buttons); the document fills the whole window (UI-01, UI-06).
2. **Theme:** light and dark following the Windows setting, switchable by hand (system / light / dark), accent colour from the system with an override; the backdrop around the pages and the page shadow as in section 3 (materials table).
3. **Settings file** (`%LOCALAPPDATA%\DreamBooks\settings.json`, versioned `"v":1`, atomic write, corrupt/unknown version → defaults, never throws, no document content): theme, accent override, eraser mode, pressure curve, smoothing, "toolbar fixed", the toolbar edge, each tool's remembered colour and width, and the custom palette. Defaults stay in code as the factory values (curve Firm, smoothing Strong, eraser WholeStroke, toolbar not fixed, theme System). The settings window that edits them arrives in 4b; 4a already reads and saves them.
4. **Floating toolbar (UI-02):** pill, bottom centre; Pen, Highlighter, Eraser, Selection (the lasso button exists but is disabled until phase 5), separator, Undo, Redo, separator, Bookmark (disabled until bookmarks exist). A tap activates a tool; a second tap on the active tool opens its option panel above the toolbar. The active tool has a sliding indicator (150 ms).
5. **Option panels (flyouts):** Pen: 8 preset colours, **a custom palette** (see section 4), free colour picker, 3 preset widths, width slider, pressure preview. Highlighter: 6 presets + custom colours, round/chisel, chisel angle, straight-line. Eraser: whole stroke / partial stroke, size, "Clear all on this page" with a confirmation (undoable). **The eraser mode chosen in the eraser panel applies to the back end of the pen as well** (one mode, as the code already does today).
6. **Auto visibility** (section 4 table): the toolbar and every flyout dissolve 300 ms after the pen starts writing and reappear 600 ms after the pen is lifted (flyouts stay closed); a finger tap on an empty area shows/hides the toolbar; "Toolbar fixed" keeps it visible. Animations per section 3 (flyout appear 180 ms fade + scale 0.95→1, disappear 120 ms; toolbar retract 200 ms; tool switch 150 ms), all off when Windows animations are off.
7. **The debug panel goes away.** Its functions find their final homes in this stage (section 5).
8. Keep the window usable on touch and pen: 40 px targets, `AutomationProperties.Name` everywhere, keyboard reachable (NFR-31); the pen never hits a floating element outside its visible bounds (same rule as the rail and the edge panel), a pen press on a flyout closes it.

**Non-goals (4a):** start screen, recent files, drag-and-drop open, settings window, main menu content beyond what exists (4b); bookmarks, password PDFs, full screen, view rotation (later); lasso (phase 5); dragging the toolbar to other edges (**[my call] deferred to 4b**: the setting exists, the control follows).

**Anti-goals:** anything that makes the pen feel different (no extra layers over the page that take pen input); flicker or white flashes when the theme changes or the window resizes; a toolbar that stays in the way of writing; elements that take the pen; a change in the saved files.

## 3. Constraints

- NuGet: **WPF-UI** (lepoco, MIT) only, plus nothing else. Pin the version, check the license file and size, and that it targets .NET 10 / Windows 11 (Task 0).
- Dark/light via WPF UI's `ApplicationThemeManager`; Mica via `FluentWindow` (`WindowBackdropType.Mica`); fall back to a solid brush when Mica is unavailable (Windows 10 or "transparency effects" off).
- In-window acrylic blur is **not** available in WPF: panels use a translucent tinted brush plus the two-layer shadow (diffuse + contact). With "transparency effects" off in Windows (NFR-32) the panels use opaque theme brushes.
- Values (colours, radii 12/999/8 px, shadows, durations) live in `Theme.xaml` dictionaries (one per theme); no hard-coded values in views; UI text only from `Strings.resx` (Italian) with the Designer in sync.
- Settings and palette: no document content; written atomically; failures reported through an event, never thrown; reading never blocks startup.
- `DreamBooks.Core` gets only the pure settings model; the store follows the `ViewPositionStore` precedent (plain file I/O).

## 4. Colours and the custom palette [my call, from Fausto's request]

- Pen: 8 fixed presets (black, dark blue, blue, red, green, orange, purple, grey) + **a custom palette of up to 12 colours chosen by the user**: a "+" swatch opens the WPF UI colour picker (HSV square + hex), the chosen colour is added to the palette (the oldest custom colour drops when full), long-press on a custom swatch removes it. The palette is **shared by the pen and the highlighter** (one place, one mental model).
- Highlighter: the 6 presets (FR-41) + the same custom palette; whatever colour is chosen, the highlighter alpha stays 89 (0.35): a custom colour is a hue, not an opacity.
- Each tool remembers its last colour and width between sessions (UI section "Toolbar").

## 5. Where the debug panel's functions go

| Function today | New home |
| --- | --- |
| Open, Save, Save as | the main menu panel (UI-03), also Ctrl+O / Ctrl+S / Ctrl+Shift+S as today |
| Tool choice, colours, widths, eraser mode, highlighter options | the toolbar and its flyouts |
| Undo / Redo | the toolbar buttons (+ shortcuts as today) |
| Page indicator, go to page, zoom +/- | the indicator chip at the bottom right (UI-05: tap = go to page field, long tap or hover shows zoom −/+ buttons); Ctrl +/- as today |
| Back, previous/next mark, mark status text | small round buttons in a group above the indicator chip, shown with the rail and with the indicator; Alt+Left / Alt+Up / Alt+Down as today |
| Search box and its result buttons | a search pill at the top centre, opened by Ctrl+F (and a search icon in the menu), closed by Esc; F3 / Shift+F3 as today |
| Marks list / Pages tab | unchanged (edge panel, Ctrl+M / Ctrl+Shift+P), plus entries in the main menu |
| Save indicator | a thin progress line under the title bar while saving, and the dirty mark in the title as today |

## 6. Testing

- Core/Pdf TDD: settings model defaults and merge, `AppSettingsStore` (round trip, corrupt, unknown version, atomic, no throw, no content), custom palette rules (max 12, drop oldest, dedupe, remove).
- App view models: tool selection and the "second tap opens the panel" state machine, auto-visibility state machine with an injectable clock (retract after 300 ms of writing, reappear 600 ms after pen-up, fixed mode, finger tap toggles), eraser mode applies to the pen's back end, each tool remembers colour and width, palette commands, theme choice, indicator chip commands.
- STA smoke tests: the window instantiates with WPF UI, no `Expander` panel left, toolbar buttons present with names, flyouts open and close, the pen is not intercepted outside visible bounds, theme switch re-applies without exception.
- Device checklist (Italian) at the end: look and feel in light and dark, Mica, toolbar behaviour with the pen (retract and return), flyouts, palette, eraser mode with the pen back end, animations off in Windows, transparency off.

## 7. Known limits to tell Fausto

- Panels are translucent tints, not real blur (WPF has no in-window backdrop blur).
- The lasso and bookmark buttons are visible but disabled until their phases.
- The toolbar stays at the bottom edge in 4a.

## 8. Requirement changes (same commit as the build)

- UI-02: the custom palette (shared by pen and highlighter), the lasso/bookmark buttons disabled until their phases, toolbar edge dragging moved to 4b.
- Section 3 materials: panels are tinted translucency (no backdrop blur) in WPF; note the opaque fallback.
- New settings requirement (`FR-110`): a versioned settings file with the keys above; the settings window is 4b.
