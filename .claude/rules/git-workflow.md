# Git workflow

- Branches: `feature/<description>`, `fix/<description>`, `chore/<description>`.
- Commits follow Conventional Commits (`feat:`, `fix:`, `refactor:`, `docs:`,
  `test:`, `chore:`) and cite requirement IDs when relevant,
  e.g. `feat: pressure-sensitive pen strokes (FR-20, FR-22)`.
- Solo project: work on a branch, then merge into `main` once build and tests
  pass and Fausto has approved the on-device test. Never force-push.
- Keep each branch focused on one goal; requirement doc changes go in the
  same commit as the decision they reflect.
