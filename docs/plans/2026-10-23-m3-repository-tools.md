# M3 Repository Tools Implementation Plan

> Execution: superpowers:executing-plans, test-driven development and independent domain delegation.

**Goal:** Complete the original P2 tools and compatibility gates, then publish a tested Windows portable release with Portable Git.

**Architecture:** Typed Core contracts feed a dedicated repository tools service and themed Avalonia window. All mutations share WorkspaceService's common-directory lease, trust policy and command log. Parent snapshots preserve gitlinks without touching child worktrees; sparse snapshots preserve configuration, patterns, index flags and materialized files.

**Tech Stack:** .NET 8, Avalonia, CLI Git, existing snapshot object store, GitHub Actions.

## Checklist

- [x] Recovery domain: real failing tests for gitlink parent undo/redo and sparse configuration/files; implement SnapshotManager extensions, submodule and sparse operations without destructive child traversal.
- [x] Tools domain: real failing tests for worktree lifecycle, LFS tracking/pull, bisect reset and signed tags; implement validated CLI commands and common-dir serialization.
- [x] UI domain: five themed tool tabs, loading/operation state, explicit recovery boundaries, status refresh and command output; headless rendering and interaction tests.
- [x] Compatibility domain implementation: exact-version six-cell CI, local Windows 2.40/2.56 format checks, ARM decision based on native assets. Final six-cell evidence belongs to the next gate.
- [ ] Integration: review changes, run all tests, extend actual ZIP smoke harness, Windows desktop launch and native AskPass verification.
- [ ] Publication: update v0.4.0 docs/package version, build from clean committed source, require successful final-source CI, push main/tag, upload ZIP/source bundle/verification/checksums, independently re-download and hash public assets.

The final two gates run after this source is committed; their completed evidence is recorded in the same-version GitHub Release `verification.json`, rather than rewriting the source commit being verified.

## Recovery and validation choices

Worktree deletion is non-force and requires a clean Git worktree plus explicit confirmation; worktree changes do not pretend to have snapshot undo. LFS install is repository-local and never rewrites history. Bisect exposes manual good/bad/skip and reset, never executes a user-supplied test command. Sparse uses Git's own checkout semantics and a parent snapshot. Submodule add/update never uses force or remote-tracking updates; child dirty data remains under Git's protection. Signed tags use configured Git signing and show actual success/failure.

Each domain writes and runs its focused regression before implementation, then runs the target suite. Compatibility and package tests use isolated temporary repositories and configuration. Release evidence records exact source, Git/runtime versions, test counts and remaining platform boundaries.
