param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [string]$Repository,
    [string]$GitPath,
    [ValidateRange(1, 128)][int]$Processors = 8,
    [string]$DotNet = 'dotnet',
    [switch]$LayoutActivities,
    [switch]$ProcessLifecycle,
    [switch]$NoCanary,
    [switch]$NoBuild
)
$ErrorActionPreference='Stop'
if (![OperatingSystem]::IsWindows()) { throw 'The dispatcher profiler canary requires Windows native desktop' }
$project=Join-Path $PSScriptRoot 'GitGui.DispatcherProfiler/GitGui.DispatcherProfiler.csproj'
$app=Join-Path $AppDirectory 'GitGui.App.exe'
$hook=Join-Path $PSScriptRoot 'GitGui.DispatcherProfiler/bin/Release/net10.0/GitGui.DispatcherDiagnosticHook.dll'
if (!$NoBuild) {
    & $DotNet build $project -c Release --configfile (Join-Path $PSScriptRoot 'GitGui.DispatcherProfiler/NuGet.Config') -p:RestoreLockedMode=true --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'External hook build failed' }
}
$root=Join-Path $env:TEMP ('gitgui-dispatcher-canary-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$profile=Join-Path $root 'dispatcher-profile.json'
$probe=Join-Path $root 'desktop-probe.json'
$start=[Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($app))
$start.UseShellExecute=$false
$start.Environment['DOTNET_PROCESSOR_COUNT']=$Processors.ToString([Globalization.CultureInfo]::InvariantCulture)
$start.RedirectStandardError=$true
$start.RedirectStandardOutput=$true
$start.Environment['DOTNET_STARTUP_HOOKS']=[IO.Path]::GetFullPath($hook)
$start.Environment['GITGUI_DISPATCHER_DIAGNOSTIC']=$profile
if (!$NoCanary) { $start.Environment['GITGUI_DISPATCHER_CANARY']='1' }
else { $start.Environment.Remove('GITGUI_DISPATCHER_CANARY') | Out-Null }
if ($GitPath) { $start.Environment['GITGUI_BENCH_GIT']=[IO.Path]::GetFullPath($GitPath) }
if ($LayoutActivities) { $start.Environment['GITGUI_LAYOUT_ACTIVITIES']='1' }
else { $start.Environment.Remove('GITGUI_LAYOUT_ACTIVITIES') | Out-Null }
if ($ProcessLifecycle) { $start.Environment['GITGUI_PROCESS_LIFECYCLE']='1' }
else { $start.Environment.Remove('GITGUI_PROCESS_LIFECYCLE') | Out-Null }
foreach($arg in @('--performance-report',$probe,'--performance-data',(Join-Path $root 'settings'))) { $start.ArgumentList.Add($arg) }
if ($Repository) { $start.ArgumentList.Add('--performance-repository'); $start.ArgumentList.Add([IO.Path]::GetFullPath($Repository)) }
$process=[Diagnostics.Process]::Start($start)
$stderr=$process.StandardError.ReadToEndAsync()
$stdout=$process.StandardOutput.ReadToEndAsync()
try {
    if (!$process.WaitForExit(150000)) { $process.Kill($true); throw 'Native diagnostic exceeded 150 seconds' }
    $errors=$stderr.GetAwaiter().GetResult(); $output=$stdout.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $root 'stderr.txt'),$errors)
    [IO.File]::WriteAllText((Join-Path $root 'stdout.txt'),$output)
    if ($process.ExitCode -ne 0) { throw "Shipping app exited $($process.ExitCode): $errors" }
} finally { $process.Dispose() }
$result=Get-Content $profile -Raw | ConvertFrom-Json
if ($result.overflow -ne 0 -or !$result.gate.allSpansComplete) { throw 'Profiler lost spans or did not close all spans' }
if (!$NoCanary) {
if (!$result.canary.finished -or !$result.canary.queuedExceptionPreserved -or !$result.canary.sendExceptionPreserved) { throw "Canary incomplete: $($result.canary | ConvertTo-Json -Compress)" }
if ($result.gate.accepted) { throw 'Known blocking jobs were not rejected with complete coverage' }
$queued=@($result.spans | Where-Object { $_.Callback -eq 'Profiler.CanaryQueued' })
$send=@($result.spans | Where-Object { $_.Callback -eq 'Profiler.CanarySend' })
$native=@($result.spans | Where-Object { $_.Seam -eq 'WindowImpl.AppWndProc' -and $_.Phase -eq 'canary.wndproc40' })
foreach ($group in @(@{name='queued';rows=$queued},@{name='send';rows=$send},@{name='native';rows=$native})) {
    if ($group.rows.Count -ne 1) { throw "Expected one $($group.name) canary; found $($group.rows.Count)" }
    $duration=($group.rows[0].ExitQpc-$group.rows[0].EnterQpc)*1000.0/$result.timestampFrequency
    if ($duration -lt 35 -or $group.rows[0].NativeTid -ne $result.uiNativeTid) { throw "Incomplete $($group.name) timing/thread identity" }
    Write-Output "$($group.name): $duration ms"
}
}
if (@($result.patches | Select-Object -First 3 | Where-Object { $_.managedNativeHeaderSize -gt 0 }).Count -ne 3) { throw 'Experiment did not use all three shipping ReadyToRun methods' }
$packageRoot=if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$package=Join-Path $packageRoot 'lib.harmony/2.4.2/lib.harmony.2.4.2.nupkg'
$packageHash=[Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($package)))
$restoredHash=[IO.File]::ReadAllText($package+'.sha512').Trim()
if ($packageHash -ne $restoredHash) { throw 'Restored NuGet package content hash mismatch' }
[IO.File]::WriteAllText((Join-Path $root 'package-evidence.json'),(@{package='Lib.Harmony';version='2.4.2';source='https://api.nuget.org/v3/index.json';sha512=$packageHash;hookSha256=(Get-FileHash $hook -Algorithm SHA256).Hash} | ConvertTo-Json))
if (!$NoCanary) { Write-Output "Canary PASS; gate correctly rejected known 40ms blocks. Evidence: $root" }
else { Write-Output "Native profile completed; all spans retained. Gate accepted=$($result.gate.accepted). Evidence: $root" }
