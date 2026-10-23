# M4 Performance Acceptance Implementation Plan

> Execution: superpowers:executing-plans; evidence first, test-first behavior changes, independent review.

**Goal:** Meet the original M4 performance targets, complete supported acceptance evidence, update the operating guide and publish a Windows portable release containing Portable Git only after its release gates pass.

**Architecture:** Preserve CLI-only writes, fresh mutation preflight, trust decisions, common-directory serialization and snapshot invariants. Repair measured read-path/dispatcher bottlenecks with library reads where specification5.1 requires them, retaining CLI fallback for unsupported formats. Keep native desktop rendering and all observed outliers; measure submitted/displayed frames and actual dispatcher work separately from RAF and queue-delay proxies.

**Tech Stack:** .NET10.0.13/SDK10.0.402, Avalonia11.3.22, LibGit2Sharp, pinned Git2.40/2.56, Windows native/EventPipe/ETW diagnostics where available.

## Fixed acceptance contract

Original specification11.1 remains unchanged: cold interactive window≤1500ms; first500 commits≤1000ms; continuous graph scroll60fps; status≤300ms with fsmonitor;10k-line Diff≤200ms; large-repository resident set≤300MB; individual UI-thread blocking≤16ms. Use the same8-worker SSD host and100k-commit/50k-file fixture, plus real git.git coverage required by13. Preserve raw samples and enforce10% regression comparison on identical host/scenario. Do not hide failures with medians or claim RAF callbacks are presented frames.

## Ordered tasks

- [x] Inspect v0.5.0 source and original targets; retain its immutable package, reports and tag as the failing baseline. Create branch codex/m4-performance.
- [ ] Reproduce the native tail on the existing shipping app in a new artifact directory, without other builds/tests. Profile boundaries around runtime resolution, process creation/first output/exit, repository reads, UI publication and rendering. Separate product delays from host scheduling; keep traces local and fixture-only.
- [ ] Review the real read graph (WorkspaceReader/WorkspaceService, not only DI registrations). For each proven bottleneck, write a focused regression test first, observe failure, implement the smallest safe routing/batching change, then verify the same test. Files: src/GitGui.Engine.Cli/WorkspaceReader.cs, src/GitGui.Engine.Lib/*, engine integration tests as required. Recheck SHA256/reftable, config/includes, encoding, cancellation and mutation freshness.
- [ ] Profile UI collection/layout/render and Diff work. Reproduce any long synchronous job with a focused test, then move or bound only proven work. Files: MainWindowViewModel.cs, controls and focused UI tests. Keep stale-selection and write cancellation protections.
- [ ] Strengthen opt-in measurement in DesktopPerformanceProbe.cs and scripts/Measure-DesktopPerformance.ps1: actual dispatcher job spans, application-attributed frame presentation and strict per-sample gates; preserve RAF/heartbeat as diagnostics. Test gate rejection of missing/invalid/over-budget evidence. Do not change normal renderer, OS security policy, process limits or configured Git selection to improve scores.
- [ ] Re-measure fixed-host baseline/candidate and repeat native samples when changes justify it. Include real git.git. If a gate fails, investigate and repair it before declaring formal acceptance or publishing a stable release.
- [ ] Independent source/spec review, locked restore/build and complete local regression; six OS×Git CI cells must pass on the final source. Update M4 acceptance/performance docs and19-chapter Markdown/HTML guide with final evidence and any genuine platform boundaries.
- [ ] Build a new immutable Windows x64 self-contained portable ZIP from clean committed source. Verify exact shipping DLL hashes, isolated Git/.NET, UTF8 AskPass, startup/normal close, all operations and actual previous-release snapshot Undo/Redo with full fixture restoration. Re-measure shipping binaries.
- [ ] Stage GitHub Release with ZIP, Portable Git corresponding sources, verification.json and SHA256SUMS. Verify server digests/bytes, publish only when formal gates pass, then fully download the public ZIP/report/checksums and compare hashes. Record a publication receipt; leave user repositories and old releases intact.

## Investigation evidence

v0.5.0 e7ed4df: engine first500905ms/status192ms/Diff138ms; native first500850/5637/1075/893/976ms. A risk-config CLI query takes5159ms in a slow native open while UI synchronous spans stay short. One native Diff sample is5253ms with heartbeat max8ms, so this tail is not explained by UI blocking. Other UI queue-delay samples exceed16ms without GC. RAF callback counts can exceed1700/s and are unsuitable as displayed FPS evidence.

Read-only audits also found WorkspaceService constructs WorkspaceReader directly; its graph enrichment and working/history Diff still use CLI. These are hypotheses to verify against the actual format/routing implementation, not permission to bypass security or cache mutation preflight.

### Implemented, pending final performance acceptance

The read path now batches descriptor/configuration queries and uses a bounded, invalidated configuration snapshot for eligible native reads. Mutation preflight always resolves fresh configuration. Native Diff/preview retains CLI fallback for unsupported formats, attributes and encodings; focused tests cover exact patch parity, staging round trips and unchanged index bytes. Progressive first-history publication no longer waits for unrelated status work, while repository authorization and writes remain unavailable until the complete state is ready. Refreshing an identical selected Diff retains its document instead of rebuilding editor layout.

Local functional coverage has passed Core33, Engine257 (three initial environment-path assertion failures passed after correcting the test environment to canonical Windows paths), and UI122 cases after the graph TextLayout cache and fixed Diff progress slot. A new Windows command-line overflow regression passed after transporting visible revision OIDs through bounded stdin; its26 related read tests also passed. Native refs/configuration passed29 focused cases after independent review required CLI fallback for external ancestry/object overrides and nontrivial divergence. Alternate-index Diff/preview errors were reproduced, then corrected with a shared native-view environment guard (48 related cases passed; three final alternate-index/empty-environment cases passed). These are functional results, not performance acceptance.

The native probe now records genuine application render scopes and completed native surface-update batches, all steady-memory samples and startup checkpoints. An independent external profiler records queued dispatcher jobs, Action Send and the main native window procedure; its real ReadyToRun canary detects known40ms stalls. The offline gate rejects incomplete, mismatched, experimental-composition or over-budget evidence;44 synthetic scenarios pass. Seven product DLL identities are recorded only after measured intervals and verified against the actual candidate files. RAF and heartbeat remain diagnostic only.

All failing runs remain preserved. Candidate4 failed cold startup at1886ms and then exposed an empty-scene measurement error; the scene barrier was corrected and verified with a single native correctness canary. That canary still had a missed scroll opportunity and is not formal performance evidence. The first real git.git BenchmarkDotNet run exposed Windows error206 from many refs; the bounded-stdin retry completed all six cases with five actual samples each and unchanged power plan. Full Open still has retained multi-second tails.

Candidate6's five native pairs have median first500623ms/status195ms/Diff99ms and maximum steady resident264MB, but first cold startup7669ms and strict scroll cadence failures remain. Composite ReadyToRun and IL-only diagnostics did not repair the first-load failure; neither is adopted. Same-binary default/direct composition A/B and six new-path copies did not reproduce a deterministic path penalty or support changing the default backend. No original threshold, renderer default, OS security setting or power plan is changed. No new release has been published and no formal pass is asserted.

### 2026-10-24 verification checkpoint

Source1f95807 passed all six OS/Git CI cells (run39982317725), including Windows portable ZIP functional verification. Linux strict PNG failure was reproduced and corrected using NoWrap; equivalent-root cache misses and a real Activated/save test race have controlled RED-to-GREEN evidence. Diagnostic capture paths passed8 focused UI cases. Probe worker-GC diagnostics and post-scroll analysis offload build with no warnings and preserve all44 acceptance scenarios.

Candidate7 unprofiled5pairs: cold surface928-1061ms after a separate first EventPipe diagnostic; first500maximum907ms, all15statusmaximum209ms, all15Diffmaximum133ms, steady maximum268816384bytes. Strict cadence passes1/5 and an external product-phase dispatcher span is19.0565ms, so full verdict remains false. New partial managed-bundle diagnostic retains an initial3539ms startup and only1/5cadence passes; it is not adopted. Original first-load failures and all raw samples remain. No tag/release or formal M4 pass has been published.
