#requires -Version 7.6
param([Parameter(Mandatory)][string]$AppDirectory,[Parameter(Mandatory)][string]$OutputDirectory,[switch]$HoldLock)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$app = (Resolve-Path -LiteralPath $AppDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$data = Join-Path $OutputDirectory 'shared-data'
if ($HoldLock) {
    # Load the production implementation in a separate owner; probe output paths must remain NEW.
    $source = "#nullable enable`nusing System; using System.IO; using System.Threading;`n" + (Get-Content -LiteralPath "$PSScriptRoot/../src/GitGui.App/SingleInstance.cs" -Raw)
    $type = Add-Type -TypeDefinition $source -PassThru
    $method = $type.GetMethod('TryAcquire', [Reflection.BindingFlags]'Static,NonPublic')
    $lease = [IDisposable]$method.Invoke($null, [string[]]@($data))
    if ($null -eq $lease) { throw 'Could not acquire test owner lock.' }
    try {
        [IO.File]::WriteAllText((Join-Path $OutputDirectory 'owner-ready'), 'ready')
        while ($true) { Start-Sleep -Seconds 1 }
    } finally { $lease.Dispose() }
    return
}
$null = New-Item -ItemType Directory -Path $OutputDirectory
function Launch([string]$Name, [bool]$Vbs) {
    $exe = if ($Vbs) { Join-Path ([Environment]::SystemDirectory) 'wscript.exe' } else { Join-Path $app 'GitGui.App.exe' }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    if ($Vbs) { $start.ArgumentList.Add('//nologo'); $start.ArgumentList.Add((Join-Path $app 'GITGUI.vbs')) }
    foreach ($arg in @('--performance-report',(Join-Path $OutputDirectory "$Name.json"),'--performance-data',$data)) { $start.ArgumentList.Add($arg) }
    $start.Environment['GITGUI_BENCH_GIT'] = Join-Path $app 'PortableGit/cmd/git.exe'
    $start.Environment['PATH'] = [Environment]::SystemDirectory
    $start.Environment['DOTNET_ROOT'] = Join-Path $OutputDirectory 'no-installed-dotnet'
    $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
    $start.Environment['GITGUI_BENCH_DRIVER'] = ''
    [Diagnostics.Process]::Start($start)
}
$owner = $null; $second = $null; $recovered = $null
try {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    foreach ($arg in @('-NoProfile','-File',$PSCommandPath,'-AppDirectory',$app,'-OutputDirectory',$OutputDirectory,'-HoldLock')) { $start.ArgumentList.Add($arg) }
    $start.RedirectStandardError = $true
    $owner = [Diagnostics.Process]::Start($start)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'owner-ready'))) {
        if ($owner.HasExited) { throw "Test lock owner failed: $($owner.StandardError.ReadToEnd())" }
        if ($watch.Elapsed.TotalSeconds -gt 30) { throw 'Test lock owner did not become ready.' }
        Start-Sleep -Milliseconds 100
    }
    $second = Launch 'second' $true
    if ($second.WaitForExit(8000) -or (Test-Path -LiteralPath (Join-Path $OutputDirectory 'second.json')) -or (Test-Path -LiteralPath (Join-Path $data 'gitgui.db'))) { throw 'Second process entered the normal workspace instead of showing the duplicate-instance notice.' }
    $second.Kill($true); $second.WaitForExit()
    $owner.Kill($true); $owner.WaitForExit()
    $saved = Join-Path $OutputDirectory 'duplicate-data'
    if (-not ([IO.Path]::GetFullPath($data).StartsWith($OutputDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) { throw 'Test path escaped output directory.' }
    Move-Item -LiteralPath $data -Destination $saved
    $recovered = Launch 'recovered' $false
    if (-not $recovered.WaitForExit(120000) -or $recovered.ExitCode -ne 0) { throw 'Application could not restart after owner termination.' }
    $report = Get-Content -LiteralPath (Join-Path $OutputDirectory 'recovered.json') -Raw | ConvertFrom-Json -AsHashtable
    if (-not $report.completed -or $report.ContainsKey('failure') -or $report.ContainsKey('provenanceFailure')) { throw 'Recovered desktop failed.' }
    @{result='PASS';scope='Production SingleInstance in a separate owner blocks WScript workspace creation; apphost recovers after owner termination'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'verification.json') -Encoding utf8
} finally {
    foreach ($process in @($owner,$second,$recovered)) {
        if ($null -ne $process) { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
    }
}
