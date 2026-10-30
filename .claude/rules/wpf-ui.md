---
paths:
  - "src/DreamBooks.App/**"
---

# WPF UI conventions

- Visual values (corner radii, shadows, animation durations, colors) come from
  sections 3 and 4 of `docs/requirements.md` and are defined once as
  resources in a theme dictionary. Never hard-code colors or durations in
  views.
- Support light and dark themes, following the system theme by default.
- Every animation and transparency effect respects the Windows setting that
  reduces animations and transparency (NFR-32).
- Minimum touch target 40 × 40 px, even when the icon is smaller.
- Fingers never draw: they scroll, zoom and tap controls. The pen draws with
  the active tool; the pen's back end always erases (FR-30). Palm rejection
  relies on Windows Ink.
- Use pressure as a continuous value: never quantize it, never set
  `IgnorePressure = true` on pen strokes (FR-20).
- Every command has an `AutomationProperties.Name` and is reachable by
  keyboard (NFR-31).
- User-visible text comes from `.resx` resources (Italian), never literals.
- View models hold no references to WPF controls, so they can be unit-tested.
