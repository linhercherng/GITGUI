# Read benchmarks

`RepositoryReadBenchmarks` runs BenchmarkDotNet against **existing, explicitly selected owned fixtures**. It never clones, prepares, resets, stages, changes tracked files/index/refs or Git configuration, or asks for credentials. Trusted status retains the fixture's fsmonitor behavior; Git may maintain its daemon's own runtime files. Set either or both fixture variables; only configured fixtures are enumerated. The acceptance protocol uses the same selected Git 2.56 executable for both.

```powershell
$env:GITGUI_BENCH_GIT = Join-Path $env:TEMP 'GITGUI Portable 中文/cmd/git.exe'
$env:GITGUI_BENCH_SYNTHETIC = Join-Path $env:TEMP 'gitgui-m4-large-fixture'
$env:GITGUI_BENCH_REAL = 'C:/path/to/existing/git.git-checkout'
$env:GITGUI_BENCH_SOURCE = 'exact-source-commit'
& "$env:LOCALAPPDATA/GitGuiBuild/dotnet-10.0.402/dotnet.exe" run `
  --project tests/GitGui.Benchmarks -c Release -- `
  --filter '*RepositoryReadBenchmarks*' --artifacts 'artifacts/bdn-repository-reads'
```

The default job uses one launch, one warmup, five measured iterations, and exactly one invocation per iteration. A job mutator also enforces invocation/unroll count one, retains outliers, and disables power-plan enforcement on command-line jobs. Standard BenchmarkDotNet options, such as `--warmupCount 2 --iterationCount 10`, can select a longer run without changing those invariants. A quick harness check can use `--inProcess --warmupCount 0 --iterationCount 1` with a small, separately owned fixture; that does not satisfy large-repository acceptance.

Use `--describe-read-jobs` instead of BenchmarkDotNet arguments to inspect and validate the resolved default job properties without running setup, Git, benchmarks, or power-plan APIs. At least one fixture variable must be configured for parameter discovery; the diagnostic does not open that path. `PowerPlanMode` must be the empty GUID (do not enforce a scheme), with `DontRemove` outliers and the counts above.

Every iteration creates a new `WorkspaceService`, `LibHistoryReader`, selected Git runtime, and SQLite database under a unique `TEMP/gitgui-bdn-read-<guid>` directory. Only that database receives fixture trust. Setup/cleanup and the following read-only fixture description commands are outside timing: Git version/path, canonical root, origin URL (redacted), HEAD, HEAD-reachable commit count, indexed file count, and historical diff selection. `fixture.json` records those values and the source label; preserve it alongside the raw BenchmarkDotNet JSON/logs. Temporary databases are retained as evidence and may be removed after archiving the report.

| Benchmark | What the measured task includes |
|---|---|
| `CompleteOpenFirst500` | The **entire** `OpenAsync` task on a new service: descriptor/configuration, status, refs, history and operation state. It is not the progressive first-history callback or GUI first frame. |
| `RefreshStatus` | One status refresh after one setup-only `OpenAsync`; the setup starts from a new service each iteration. |
| `ReadSelectedDiff` | One diff after one setup-only `OpenAsync`. Synthetic defaults to the existing modified `big.txt`. Real defaults to the first changed path between HEAD and its first parent (root commit supported), using the recorded full HEAD OID. |

Override the repository-relative path with `GITGUI_BENCH_SYNTHETIC_DIFF_PATH` or `GITGUI_BENCH_REAL_DIFF_PATH`. The synthetic path must have a nonempty working diff; the real path must have a nonempty historical diff at HEAD. Empty output fails the benchmark instead of reporting an artificially cheap result. Fixture content and external Git activity must stay stable throughout a comparison; use a quiet machine and retain both input identities and raw samples. `GIT_NO_LAZY_FETCH=1` is set only inside each benchmark child process, so missing blobs in a partial clone fail instead of fetching or modifying its object store. Materialize required objects separately before measurement.

These are engine benchmarks, not native window scrolling, submitted/displayed frame, dispatcher blocking, startup, or resident-memory acceptance. OS disk/page caches are not flushed; “new service” describes application read caches only. BenchmarkDotNet allocation diagnostics describe the measured engine work, not the GUI's total resident memory.

`StatusParserBenchmark` and the existing `--prepare` / `--measure` harness remain available. The new fixture class is compiled only on .NET 10+. Comparisons built for archived M3/.NET 8 source omit this class and retain the existing harness without the new status interface dependency.
