param([Parameter(Mandatory)][string]$AppDirectory,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$app = (Resolve-Path -LiteralPath $AppDirectory).Path
$null = New-Item -ItemType Directory -Path $OutputDirectory
$data = Join-Path $OutputDirectory 'shared-data'
function Launch([string]$Name, [bool]$WaitForDriver, [bool]$Vbs) {
    $exe = if ($Vbs) { Join-Path ([Environment]::SystemDirectory) 'wscript.exe' } else { Join-Path $app 'GitGui.App.exe' }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    if ($Vbs) { $start.ArgumentList.Add('//nologo'); $start.ArgumentList.Add((Join-Path $app 'GITGUI.vbs')) }
    foreach ($arg in @('--performance-report',(Join-Path $OutputDirectory "$Name.json"),'--performance-data',$data)) { $start.ArgumentList.Add($arg) }
    $start.Environment['GITGUI_BENCH_GIT'] = Join-Path $app 'PortableGit/cmd/git.exe'
    $start.Environment['PATH'] = [Environment]::SystemDirectory
    $start.Environment['DOTNET_ROOT'] = Join-Path $OutputDirectory 'no-installed-dotnet'
    $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
    $start.Environment['GITGUI_BENCH_DRIVER'] = if ($WaitForDriver) { 'foreground' } else { '' }
    [Diagnostics.Process]::Start($start)
}
$first = $null; $second = $null; $recovered = $null
try {
    $first = Launch 'first' $true $false
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $data 'driver-ready.json'))) {
        if ($first.HasExited -or $watch.Elapsed.TotalSeconds -gt 30) { throw 'First desktop did not become ready.' }
        Start-Sleep -Milliseconds 100
    }
    $second = Launch 'second' $false $true
    if ($second.WaitForExit(8000) -or (Test-Path -LiteralPath (Join-Path $OutputDirectory 'second.json'))) { throw 'Second process entered the normal workspace instead of showing the duplicate-instance notice.' }
    $second.Kill($true); $second.WaitForExit()
    $first.Kill($true); $first.WaitForExit()
    $recovered = Launch 'recovered' $false $false
    if (-not $recovered.WaitForExit(120000) -or $recovered.ExitCode -ne 0) { throw 'Application could not restart after owner termination.' }
    $report = Get-Content -LiteralPath (Join-Path $OutputDirectory 'recovered.json') -Raw | ConvertFrom-Json
    if (-not $report.completed -or $report.failure -or $report.provenanceFailure) { throw 'Recovered desktop failed.' }
    @{result='PASS';scope='Two real apphost/WScript processes share one instance lock; terminated owner releases it'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'verification.json') -Encoding utf8
} finally {
    foreach ($process in @($first,$second,$recovered)) {
        if ($null -ne $process) { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
    }
}
