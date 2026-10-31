# Testing

- Every bug fix includes a regression test that fails without the fix and
  passes with it, wherever the bug is reachable from Core, Pdf or a view model.
- Do not reduce existing test coverage.
- Before declaring a task done, run `dotnet build` and `dotnet test` (see
  `CLAUDE.md`).
- Save and reload is the riskiest feature: every change to `PdfDocumentSaver`,
  `StrokeAnnotations`, `StrokeJson` or the stroke model needs a round-trip
  test (save, reopen, compare strokes, bookmarks and page content).
- Pen feel, pressure, eraser and visuals cannot be tested automatically:
  finish those tasks with a numbered manual checklist for Fausto, referencing
  the requirement IDs being verified.
