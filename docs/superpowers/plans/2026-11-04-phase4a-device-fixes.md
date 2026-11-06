# Phase 4a Device-Test Fixes Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (or superpowers:executing-plans). Steps use checkbox (`- [ ]`) syntax. Branch: `feature/phase4a-shell` (do **not** merge to `main` until Fausto's device test of the fixed build passes).

**Goal:** Fix the seven defects Fausto found testing `publish-4a-v2` (2026-11-04), each with a regression test where the code can be reached by a test, and publish `publish-4a-v3` with an Italian checklist.

**Spec / context:** `docs/superpowers/specs/2026-11-03-phase4a-shell-design.md`, `docs/requirements.md` sections 3 (shapes: pill 999 px, panels 12 px, inner 8 px) and 4 (UI-02, UI-06), `.claude/rules/wpf-ui.md` (tokens only in theme dictionaries, NFR-32, 40 px targets, names, resx). Reports of 4a: `.superpowers/sdd/2026-11-03-phase4a-shell/` (may have been deleted: rely on `git log`). Pen rule unchanged: floating surfaces never take the pen outside their bounds; the pen operates the controls inside toolbar/flyout/chip/pill/menu surfaces; the side edge panel keeps its own rule.

**Process note (tokens):** Fausto's usage is nearly exhausted. Run this plan in one session after the limit resets. Use one implementer per defect group (order below), `sonnet` for mechanical visual fixes (D3, D4, D5), `opus` for D1, D2, D6 (window/input behaviour). One short review at the end. Commit trailer: `Co-Authored-By: Claude <noreply@anthropic.com>` (the model doing the work). Never run the WPF app except in-process STA tests; `dotnet build` 0 warnings, all tests green, `dotnet format --verify-no-changes` before each commit.

## Defects (Fausto's words, shortened) and root-cause hypotheses

Evidence already gathered: `PillCornerRadius` is `999` (Theme.xaml:168); a WPF `Border` does **not** clamp a huge radius like CSS does, so a wide pill becomes an ellipse. `ChoiceDialog` is a plain `Window` (Services/ChoiceDialog.cs) whose buttons are now restyled by WPF UI. There is no code that sets `WindowState`. Flyouts have a fixed `FlyoutWidth` (PenOptionsFlyout.xaml:16).

### D1 — Title bar is invisible (text white on transparent; window buttons not visible)
- Hypothesis: the transparent title bar draws white foreground (dark Mica palette) over the white page; there is no backing.
- Decision: **no auto-hide**. A compact, always-present top strip made of two tinted pills (the mockup language): a left one for the file name (small), a right one for minimise / maximise / close, each with a translucent tinted background and the theme foreground, 40 px touch targets, `AutomationProperties.Name` in Italian (Riduci a icona / Ingrandisci / Chiudi). The pills sit over the document (the PDF still reaches the top edge); they follow the light/dark theme and fall back to opaque brushes when transparency is off. Title bar drag area keeps the non-pen rule (pen inks, finger/mouse drags).
- [ ] Failing STA tests: the title text and the three window buttons have a foreground that contrasts (≥ 4.5:1) with their backing brush in both themes; the backing exists (not transparent); window buttons are present, 40 px, named, and clickable (invoke Close/Minimize/Maximize through automation peers or the commands); pen on the drag area still inks; finger/mouse still drags (decision-table tests from Task 3 stay green).
- [ ] Implement via WPF UI `TitleBar` template/`ControlTemplate` overrides or a custom title bar content with the pills; tokens in `Theme.*.xaml` (`TitleBarPillBackground`, `TitleBarPillForeground`, radius from D3).

### D2 — Maximise: the window animates to full screen then returns to the small size; restore from maximised works
- Hypothesis (investigate with evidence, do not guess): (a) our `WM_NCHITTEST` handling in `TitleBarPenInput` answering HTCLIENT/HTCAPTION for non-pen input and swallowing the maximise button's non-client hit; (b) WPF UI `FluentWindow` + `ExtendsContentIntoTitleBar` + a hook re-applying `WindowChrome` caption height after the maximise; (c) a settings/placement restore after `StateChanged`; (d) the document view's `SizeChanged` re-layout throwing and being swallowed. Add a reproducible STA test first: raise the maximise command and assert `WindowState == Maximized` after the dispatcher drains and again after 500 ms; check what raises `StateChanged` back to `Normal` (add a temporary trace in the test, remove before committing).
- [ ] Failing test reproducing "maximise then back to Normal"; find the cause (the test must show who changed it); fix; keep the restore (`Maximized → Normal`) working; tests: maximise via button, via double-click on the drag area, via Win+Up (`SC_MAXIMIZE` syscommand), restore; the window keeps `Maximized` for at least 1 s.
- [ ] Device checklist item: maximise/restore by button, double-click, Win+Up, snap layouts.

### D3 — Toolbar and navigation backgrounds are ellipses, not rounded bands
- Cause: `CornerRadius="999"` on a non-square element. Fix: use a value that is half the element height, not 999: a converter/markup that binds `CornerRadius` to `ActualHeight / 2` (`HeightToPillRadiusConverter`), or a fixed token per element (toolbar pill height 52 → radius 26; round 40 px buttons keep radius 20; search pill 44 → 22; chip → height/2). Keep `PanelCornerRadius` 12 for flyouts/menu/notice.
- [ ] Failing STA tests: toolbar pill, chip, search pill, navigation buttons have a rounded-band geometry (corner radius equals half of ActualHeight, not an ellipse: the arc spans only the ends; test by `CornerRadius` values and by a rendered bitmap check of the corner pixel vs the middle of the long edge); the flyout keeps radius 12.
- [ ] Update the mockup parity: compare with `C:\Users\Favsto\AppData\Local\Temp\claude\c--Users-Favsto-workspace-dream-books\13d4bda5-1143-43ff-9541-1993d9cffb84\scratchpad\mockups\phase4-shell.html` (may no longer exist: the rule is "pill = full-height rounded ends, straight long sides").

### D4 — Option flyouts clip their controls (third radio option cut off, sliders/rows truncated)
- Hypothesis: fixed `FlyoutWidth` smaller than the content (three radio buttons per row with labels plus padding), horizontal clipping by the `FloatingSurface` rounded `Clip`, and the Task-3/6 ScrollViewer (vertical only) hides the overflow horizontally.
- [ ] Failing STA tests: for each flyout (Pen, Highlighter, Eraser) at the default and at a narrow window width, measure the content (`DesiredSize`) against the visible area: no child's bounds exceed the surface's content area horizontally; the three-way option rows (curve: Lineare/Morbida/Decisa; smoothing: Nessuna/Leggera/Forte) fit: use a `UniformGrid`/`WrapPanel` with equal columns, or make the flyout width `Auto` bounded by the window (min 320, max window width − 32) instead of a fixed token.
- [ ] Fix layout (no new hard-coded values: tokens), keep vertical scroll for short windows, keep the pen/finger fixes of the review (no panning).

### D5 — Closing with unsaved changes: the dialog's buttons are invisible
- Cause hypothesis: WPF UI restyles `Button` globally (white text on a light/transparent background in the plain `ChoiceDialog` window, which does not use the Fluent window or theme dictionaries).
- Decision: make `ChoiceDialog` a themed, readable dialog: a `FluentWindow`-free simple window with explicit button styles from the theme dictionaries (`DialogButtonStyle`: accent for the default button, neutral for others, 40 px height, 8 px radius, visible foreground in light and dark), the dialog follows the current theme (background `ApplicationBackgroundBrush`, text `TextFillColorPrimaryBrush`), Italian labels: **Salva / Non salvare / Annulla**; Enter = default (Salva), Esc = Annulla. The same dialog is used by the other confirmations (replace file, close with changes).
- [ ] Failing STA tests: both themes: every button has a visible foreground/background contrast ≥ 4.5:1; labels present; default/cancel keys work; the dialog is created with the app resources (theme) even from a test.
- [ ] Add a device item: close with unsaved changes in light and dark.

### D6 — The first click on toolbar / navigation buttons only focuses; the second acts
- Hypothesis (investigate): (a) the window or the document view holds keyboard focus and the first press on a `FloatingSurface` is eaten by a focus change (`Focusable`/`IsTabStop`, `FocusManager`) or by the `RoutePress`/light-dismiss logic in `MainWindow` (`RoutePagePress`, `OnPageHostTouchDown`, `CaptureTouch`) that handles the press first (e.g. closing a panel or calling `e.Handled`); (b) the inert stylus plug-in changes promotion of the first mouse event; (c) the document's `ScrollViewer` capturing the mouse. Reproduce with an STA test that starts with focus on the document view, then raises a real `MouseLeftButtonDown/Up` (and a touch/stylus sequence) on a toolbar button via `RaiseEvent` and asserts the button command runs **once** on the first press.
- [ ] Failing tests: first press runs the command with focus on the document; with focus on the search field; with the edge panel open; for the toolbar, chip, navigation buttons, menu button; for finger, mouse and pen.
- [ ] Fix the root cause (likely: let the surface take keyboard focus on press via `Focusable` + `PreviewMouseDown → Focus()` without handling the event, or stop the window-level routing from consuming the first press); keep every pen/finger rule and tests green.

### D7 — Marks panel shows only the ink of the marks, not the PDF page under it
- Answer for Fausto: no, it is not right. This was a **known 3b-1 limit** ("a crop of the page bitmap behind it arrives with the 3b-2 thumbnail pipeline"); the Pages tab already draws page + ink, the marks list never got the page crop. Fix now.
- [ ] Failing tests: a marks list row's thumbnail shows the page region around the mark (the mark's vertical extent ± a margin, the full page width scaled to the row) from the low-resolution page bitmap, with the ink on top; the bitmap comes from the same thumbnail queue as the Pages tab (low priority, cache budget shared or separate ≤ 40 MB, cleared on document change; never delays visible-page renders); a row whose page bitmap is not ready shows the ink alone and the crop appears when ready (no flicker); marks on rotated pages crop correctly.
- [ ] Reuse `PageThumbnailCache`/`RenderThumbnailAsync`; do not duplicate the page-render path.

## Order and parallelism
1. D3 (shapes) and D5 (dialog) first: mechanical, visible, independent. 2. D1 (title bar pills; uses D3's radius). 3. D4 (flyout layout). 4. D2 and D6 (window/input; investigate with failing tests first). 5. D7. One commit per defect (`fix: ...`), then the sweep below.

## Final sweep and publish
- [ ] Run Core, Pdf, App (App three times), build, format. Update `docs/requirements.md` only where wording changed (UI-06: pills with backing; UI-04: marks thumbnails show the page region; section 3 shapes: rounded band = half height).
- [ ] Publish `publish-4a-v3` (new folder) and write an Italian checklist: title bar pills visible in light/dark and usable by finger/mouse/pen; maximise/restore by button, double-click, Win+Up; close with unsaved changes (buttons visible); toolbar/navigation rounded bands; flyouts fully visible in the three tools; first click works with focus on the PDF; marks panel shows page + ink.
- [ ] Do not merge to `main` until Fausto confirms the v3 device test; then merge locally and ask before pushing.
