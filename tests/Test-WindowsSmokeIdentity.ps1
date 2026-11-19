param([Parameter(Mandatory)][string]$AppPath)
$ErrorActionPreference='Stop'
$smoke=Join-Path $PSScriptRoot 'WindowsSmoke.ps1'
$lines=Get-Content -LiteralPath $smoke
$startLines=@(for($i=0;$i -lt $lines.Count;$i++){if($lines[$i] -eq '$child = [Diagnostics.Process]::Start($start)'){$i+1}})
if($startLines.Count -ne 1){throw 'Expected exactly one helper launch seam.'}
$global:GitGuiSmokeProbeCount=0
$global:GitGuiSmokeProbeError=$null
# Connect and close from this unrelated PID before the actual child is launched.
$breakpoint=Set-PSBreakpoint -Script $smoke -Line $startLines[0] -Action {
    try {
        if($pipeName -notlike 'gitgui-smoke-*'){throw 'Missing smoke pipe at launch seam.'}
        $probe=[IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
        try {
            if(-not $probe.ConnectAsync(3000).Wait(4000)){throw 'Probe connect timed out.'}
            $global:GitGuiSmokeProbeCount++
        } finally { $probe.Dispose() }
    } catch { $global:GitGuiSmokeProbeError=$_.Exception.Message }
}
try {
    & $smoke -AppPath $AppPath
    if($global:GitGuiSmokeProbeError -or $global:GitGuiSmokeProbeCount -ne 1){throw "Probe did not run: $global:GitGuiSmokeProbeError"}
    'PASS: unrelated pipe client rejected; actual child UTF-8 request/response and exit verified.'
} finally {
    Remove-PSBreakpoint -Breakpoint $breakpoint
    Write-Output "Unrelated probe injections: $global:GitGuiSmokeProbeCount"
    Remove-Variable -Name GitGuiSmokeProbeCount,GitGuiSmokeProbeError -Scope Global
}
