# M4 Production Release Implementation Plan

> Execution: superpowers:executing-plans; test-first changes, independent domain implementation and review.

**Goal:** Complete the original specification's M4 non-functional and distribution gates, update the offline guide and publish a verified Windows portable release with Portable Git.

**Architecture:** Preserve CLI-only writes, common-directory serialization and snapshot semantics. Separate fresh mutation preflight from cached presentation reads; page history against stable visible tips. Keep graph rendering virtualized while exposing lazy automation providers. Share localized command descriptors between keyboard bindings, menus and the command palette. Bundle the current supported .NET LTS runtime without requiring installation.

**Tech Stack:** .NET 10 LTS (SDK 10.0.402/runtime 10.0.13, verified official metadata), Avalonia 11, Git 2.40/2.56, SQLite, BenchmarkDotNet and real Git fixtures.

## Scope and decisions

The original milestone diagram was not supplied. M4 follows the existing ROADMAP plus specification sections 11–13: large-repository performance, bilingual resources, keyboard customization, automation peers, supported runtime, packaging/upgrades and licensing. VT/PTY and hosting-provider integrations remain outside these original requirements. Portable distribution is explicitly authorized; an installer is not required to use the release.

Prefer a narrow read-cache/paging change over a full engine rewrite. Do not replace native Git behavior, cache authorization state or weaken path/snapshot protection. Measure the original 100k-commit/50k-file scale; preserve actual timings and unmet gates rather than changing the targets. A preview must not be described as a completed M4 if a required gate remains unmet.

The user explicitly selected the MIT License on2026-10-23. Include the application LICENSE in source and distribution; preserve separate licenses and source offers for third-party components.

## Ordered checklist

- [x] Audit original specification, current implementation and domain boundaries; inspect M3 provenance and clean source state.
- [x] Runtime: migrate global.json/Directory.Build.props and compatible runtime packages; restore both normal and win-x64 lock files; build all projects without warnings. Pin all official input hashes and preserve installed SDKs.
- [x] Read performance: test status-only refresh and stable history-page behavior before implementing caches; start at500 and append1000; verify ref rewrite, detached HEAD, filters, formats, cancellation and fresh write preflight. Files: WorkspaceReader.cs, WorkspaceService.cs read methods, CliRepoReader.cs, LibHistoryReader.cs, read contracts and new engine tests.
- [x] UI responsiveness: test batched status/history updates, stale selection and asynchronous diff projection; coalesce watcher events before UI posting; materialize visible diff modes only. Files: MainWindowViewModel.cs, watcher methods, DiffEditor.cs and new UI tests.
- [x] Accessibility/localization: test keyboard routes, configurable shortcuts, locale persistence, live resource/automation-name updates, virtual graph peer selection/scroll and focus visibility. Files: CommitGraphControl.cs/peers, UI command/shortcut settings, preferences, windows/resources and focused UI tests. Preserve confirmations and text-entry shortcuts.
- [x] Text/path correctness: test raw commits with declared encoding, headerless Big5 and mixed pages; expose decoding metadata; distinguish escaped invalid-byte paths and refuse ambiguous writes; display effective autocrlf/eol and conversion warnings. Files: Core decoding/path helpers, reader integrations, write guards and new encoding/path/line-ending engine tests.
- [x] Release diagnostics: test privacy-preserving redacted command metadata and14-day retention; integrate lifecycle/command diagnostics without enabling telemetry or recording credentials. Files: AppLogging.cs, Program.cs and InfrastructureTests.
- [ ] Performance evidence: extend GitGui.Benchmarks with argument forwarding, fast-import fixture generation, real read/diff/graph measurements and machine/fixture provenance. Measure baseline and candidate on the same host; enforce10% regression comparison only for matching host/scenario fingerprints. Record desktop cold start, memory, frame/UI timings separately from headless tests.
- [ ] Distribution: update portable build/smoke scripts and runtime/license inventory, test existing settings/database migration and side-by-side upgrade, regenerate bilingual/offline operating instructions with local paths and M4 acceptance results.
- [ ] Integration/publication: independent code review; complete six exact-version CI cells, actual final ZIP/AskPass/desktop tests and all required performance gates; commit/push main and immutable tag; stage and verify four release assets; publish; re-download public ZIP/report/checksums and verify hashes.

## Domain workflow

Each behavior change starts with a focused failing test, then minimal implementation and the same test passing. Domain agents use distinct temporary artifacts paths and freeze edits for integration. Root owns configuration/package/benchmark files and final commits. UI owns controls/resources/keyboard preferences; read owner owns paging and VM read/diff methods; text owner owns encoding/path helpers and mutation guards. Shared files use small coordinated patches.

Focused commands use the verified isolated SDK at `%LOCALAPPDATA%/GitGuiBuild/dotnet-10.0.402/dotnet.exe` and an explicit `--artifacts-path` under TEMP. Final checks use `dotnet restore GitGui.sln --locked-mode`, `dotnet build GitGui.sln -c Release --no-restore`, `dotnet test GitGui.sln -c Release --no-build`, six-cell CI, then Build-WindowsPortable/Test-WindowsPortable with a fresh output directory.

The final-source CI, immutable ZIP and public-download results are recorded in the same-version Release verification.json and local publication receipt after source is committed; do not rewrite the verified commit to backfill publication claims.

## Release decision (2026-10-23)

Functional implementation, locked restore and solution build pass; Core33 + Engine228 + UI82 =343 tests pass. Independent review completed. Original desktop performance gates remain unmet despite measured improvements and targeted cancellation repair. Publish v0.5.0 as an explicit M4 prerelease, retain all measurements and leave the formal M4 performance/publication checklist incomplete. No experimental process cap, direct-Git substitution or software renderer change ships. Final-source CI and ZIP/legacy/public download results are external immutable publication receipts; do not amend this source to claim them in advance.
