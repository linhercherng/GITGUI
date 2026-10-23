# UI appearance refresh

Keep the Avalonia desktop workspace and existing commands. Use original vector
action icons plus visible localized labels, a custom branch app icon, consistent
34px buttons, grouped toolbar actions and clear primary/destructive styles.

Four selectable palettes: Graphite, Midnight, Forest and Porcelain. Use semantic
resources for every surface/text/status/editor/graph/dialog. Switch Fluent dark/
light together with custom brushes. Persist only the theme ID in an atomic local
appearance.json; load before first window. Invalid files fall back safely; failed
saves show an error and retain the current palette. No repository changes.

- [x] Add failing preference, live palette and settings interaction tests.
- [x] Implement preference storage, palette resources and localized picker.
- [x] Add app/action icons; refine buttons and toolbar grouping.
- [x] Theme editors, graph and all dialogs, including diff semantic colors.
- [x] Verify four rendered palettes, keyboard/actions and narrow-window layout.
- [ ] Build, regression tests, commit and push; preserve published v0.1.0.

No new UI package dependency. Follow the existing authorized portable release
workflow after CI and packaged desktop smoke pass: publish v0.2.0 with the same
bundled Git/runtime, notices, source archive and checksums. Preserve v0.1.0.
