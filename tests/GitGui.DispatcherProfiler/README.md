# External Windows dispatcher diagnostic

This project is an external `DOTNET_STARTUP_HOOKS` assembly, never an App dependency or a release-package component. Its independent NuGet configuration and lock file pin Harmony 2.4.2 and the Avalonia 11.3.22 compile-time API. The deployed app resolves its own shipping Avalonia assemblies; the hook does not copy replacement Avalonia DLLs.

Run the native canary against a published self-contained ReadyToRun app:

```powershell
./tests/Test-DispatcherProfiler.ps1 -AppDirectory <published-app-directory> -DotNet <dotnet-10-sdk-executable>
```

The script launches one child with process-local injection variables, isolated probe settings and no repository. The hook clears the startup-hook environment variable before Main so Git/askpass children do not inherit injection. It verifies known 40ms queued, UI Send and native message callbacks are recorded and rejected by the 16ms diagnostic gate. Queued/Send exception object identity is also verified. Missing spans, buffer overflow, an unexpected patch target or non-R2R targets invalidate the canary. Every span is retained, including shorter work.

Profile the generated fixture without synthetic stalls:

```powershell
./tests/Test-DispatcherProfiler.ps1 -AppDirectory <published-app-directory> -Repository <generated-fixture> -GitPath <fixture-git-executable> -NoCanary -DotNet <dotnet-10-sdk-executable>
```

`-LayoutActivities` additionally subscribes to Avalonia's per-control measure/arrange activities. It stores control types and fixed control names, never text or content. This heavier mode allocates Activities and can substantially inflate durations; use it to attribute work, not as a product timing baseline.

Each run preserves a complete dispatcher sidecar, the ordinary desktop probe JSON, NuGet content/hash evidence, and process stdout/stderr in a new TEMP directory. `ProductPhaseAtEnter` and `ProductPhaseAtExit` are correlated offline using the desktop report's QPC origin and full phase timeline; older reports without that origin retain uncorrelated spans. Callback identity is metadata only, not delegate closure values.

The sidecar records `targetProcessId`, executable/App assembly/hook SHA256 hashes, plus `desktopProbeReportPath` and `desktopProbeReportSha256` read from that same child's performance-report argument. A gate must verify the file hash and shipping-binary identities before associating these spans with a desktop run. Missing identity/clock/coverage evidence remains unverified.

`-ProcessLifecycle` optionally duplicates handles for Git children started directly by the app, records kernel creation/exit FILETIME and observer callback QPC separately, and correlates runner Notify using an external AsyncLocal operation ID. Only a fixed Git verb class is retained; no argv, working directory, environment or output. Handles and registered waits are released before report serialization. A process-handle signal is checked before reading exit time, whose value is otherwise undefined. This adds observation overhead and cannot distinguish a Git shim's own work from waiting for an inner child.

Coverage includes every call entering `DispatcherOperation.Execute`, the UI-thread Send fastpath in the final Action `Dispatcher.Invoke` overload, and `Avalonia.Win32.WindowImpl.AppWndProc`. Nested spans are inclusive; subtract the union of immediate child intervals for exclusive time, rather than summing nested durations. A job that crosses into `probe-report` must be inspected as report work before calling it a graph-scroll stall.

This does not cover generic synchronous `Invoke<T>` fastpaths, other native windows, COM callbacks, idle waits or actual display presentation. Harmony generates instrumented runtime replacements for the three methods; on-disk shipping files remain unchanged, but JIT/detour overhead is real. Keep unprofiled startup, working-set, and ETW presentation acceptance separate. Do not subtract an assumed profiler cost to make a product gate pass.
