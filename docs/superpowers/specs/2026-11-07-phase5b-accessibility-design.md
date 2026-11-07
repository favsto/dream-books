# Phase 5b: accessibility (NFR-30, NFR-31, NFR-32)

Written after an audit, not before: the app was already built with names, keyboard stops and the reduced-effects
setting in mind (phases 3 and 4), so the first job was to measure what was missing. Every decision is Claude's call
unless marked otherwise.

## Approach

Three automatic audits that stay in the test suite as guards, plus a manual checklist for what only a person with a
screen reader or a different Windows setting can judge.

| Requirement | Guard | What it checks |
|---|---|---|
| NFR-31 names and keyboard | `AccessibilityAuditTests` | Sweeps the whole window (all logical and visual children) in 10 states: document, the three tool flyouts, main menu, marks panel, pages panel, search, selection bar, settings. Every visible, enabled button, slider, combo box, text box and list item must have an automation name (or text content) and be a keyboard stop. It must see at least 60 controls, or it fails: a sweep that sees nothing must not pass. A negative test proves it catches an unnamed button and a button that is not a tab stop. |
| NFR-30 contrast | `ContrastAuditTests` | Reads the colours from `Theme.Light.xaml`, `Theme.Dark.xaml` and the splash brushes in `Theme.xaml`, blends opacities, and checks the WCAG ratios: 4.5:1 for text (icon glyphs, flyout headers, splash text and subtext), 3:1 for graphics (active-tool accent, rail mark ticks). A changed colour is checked without touching the test. |
| NFR-32 reduced effects | existing per-view tests + `ShellIntegrationTests.Transparency_off_makes_every_floating_surface_in_the_window_opaque` | Every animating file takes the setting (`FloatingFade.Run` needs the flag); every floating surface turns opaque with transparency effects off. |

## Results of the audit

- Names and keyboard: no control was missing a name or a tab stop. Two classes are excluded on purpose, each with a
  comment in the test: the system caption buttons (minimise, maximise, close: Alt+Space gives the same commands) and
  the track parts inside sliders and scroll bars (the slider or bar itself is the control).
- Contrast: every pair passes AA in both themes with Windows' default accent (#0078D4).
- NFR-32: one real gap, fixed with a test that failed first. The lasso's action bar read the transparency setting only
  when it appeared; it now follows the window, live, like the other floating surfaces
  (`MainWindow.ApplyBackdrop`).

## Known limits (not fixed here)

- **The accent colour is the user's.** The active tool's icon and a few indicators are drawn in the system accent, or
  in the accent chosen in the settings (FR-110). A pale accent (yellow, light green) on the light panel falls under
  3:1 and the active tool is hard to see. A fix would derive an "accent on panel" colour that is darkened or lightened
  until it reaches 3:1, in `ThemeService`. Left for Fausto to decide.
- **Windows high-contrast themes** are not handled: the app keeps its own colours. Not required by NFR-30..32.
- **Screen-reader wording and reading order** can only be judged by listening: see the manual checklist.
- **The lasso itself needs a pen or a mouse** (the loop and the drag). Its actions (colour, copy, paste, delete) are
  buttons that the keyboard reaches. A keyboard-only way to select strokes is out of scope.
