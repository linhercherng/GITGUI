param(
    [Parameter(Mandatory)][string]$Summary,
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$ProfileReport,
    [Parameter(Mandatory)][string]$ProfileProbeReport,
    [Parameter(Mandatory)][string]$ProfilerHookPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path $output){throw 'OutputDirectory must be new; baseline evidence is immutable.'}
$null=New-Item -ItemType Directory $output
$assert=Join-Path $PSScriptRoot '../scripts/Assert-DesktopPerformance.ps1'
function Hash($path){(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}
function WriteJson($path,$value){$value|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $path -Encoding utf8}
$arguments=@{Summary=$Summary;AppPath=$AppPath;ProfileReports=@($ProfileReport);ProfileProbeReports=@($ProfileProbeReport);ProfilerHookPath=$ProfilerHookPath}
# Negative tests cannot succeed merely because the real performance gate already fails.
$null=& $assert @arguments -OutputPath "$output/baseline-verdict.json"
$originalSummary=Get-Content $Summary -Raw
$s=$originalSummary|ConvertFrom-Json -AsHashtable -Depth 100
$large=@($s.reports|Where-Object {$_.mode -eq 'large' -and $_.sample -eq 1})
if($large.Count -ne 1){throw 'A real, accepted first large sample is required.'}
$baseDirectory=Split-Path ([IO.Path]::GetFullPath($Summary)) -Parent
$originalLarge=Get-Content (Join-Path $baseDirectory $large[0].file) -Raw
$originalProfile=Get-Content $ProfileReport -Raw
$originalProbe=Get-Content $ProfileProbeReport -Raw
$cases=[ordered]@{
    'missing-native-identity'={param($s,$r,$p) $r.Remove('nativeHostIdentity')}
    'different-native-host-bytes'={param($s,$r,$p) $r.nativeHostIdentity.hostSha256='0'*64}
    'different-bundle-bytes'={param($s,$r,$p) $r.nativeHostIdentity.bundleSha256='0'*64}
    'different-composite-code-bytes'={param($s,$r,$p) $r.compositeImageSha256='0'*64}
    'different-launcher-script'={param($s,$r,$p) $r.nativeHostIdentity.launcherSha256='0'*64}
    'different-os-script-host'={param($s,$r,$p) $r.nativeHostIdentity.scriptHostSha256='0'*64}
    'different-launcher-process'={param($s,$r,$p) $r.launchOrigin.LauncherProcessId+=1}
    'different-launcher-creation-time'={param($s,$r,$p) $r.launchOrigin.StartedUtc=([DateTime]$r.launchOrigin.StartedUtc).AddTicks(1)}
    'omitted-launcher-memory'={param($s,$r,$p) $r.steadyMemory.samplesInOrder[0].LauncherWorkingSetBytes=0}
    'different-memory-launcher-process'={param($s,$r,$p) $r.memoryProcesses.launcherProcessId+=1}
    'missing-foreground-evidence'={param($s,$r,$p) $r.scrollRendering.Remove('foreground')}
    'background-scroll-is-not-interactive-acceptance'={param($s,$r,$p) $r.scrollRendering.foreground.samplesInOrder[0].IsActive=$false}
    'different-product-bundle-offset'={param($s,$r,$p) $r.productAssemblies['GitGui.UI'].offset+=1}
    'sidecar-is-not-loaded-bundle-evidence'={param($s,$r,$p) $r.productAssemblies['GitGui.UI'].storage='file'}
    'foreign-profile-process'={param($s,$r,$p) $p.targetProcessId+=1}
    'different-profile-module-mvid'={param($s,$r,$p) $p.patches[0].mvid=[Guid]::NewGuid().ToString('D')}
    'different-profile-bundle-offset'={param($s,$r,$p) $p.patches[0].offset+=1}
}
$results=@()
foreach($case in $cases.GetEnumerator()) {
    $directory=Join-Path $output $case.Key;$null=New-Item -ItemType Directory $directory
    $s=$originalSummary|ConvertFrom-Json -AsHashtable -Depth 100
    $r=$originalLarge|ConvertFrom-Json -AsHashtable -Depth 100
    $p=$originalProfile|ConvertFrom-Json -AsHashtable -Depth 100
    foreach($row in $s.reports){Copy-Item -LiteralPath (Join-Path $baseDirectory $row.file) -Destination $directory}
    & $case.Value $s $r $p
    $raw=Join-Path $directory $large[0].file;WriteJson $raw $r
    ($s.reports|Where-Object {$_.mode -eq 'large' -and $_.sample -eq 1}).sha256=Hash $raw
    $summaryPath=Join-Path $directory 'summary.json';WriteJson $summaryPath $s
    $probePath=Join-Path $directory 'profile-probe.json';[IO.File]::WriteAllText($probePath,$originalProbe)
    $p.desktopProbeReportPath=$probePath;$p.desktopProbeReportSha256=Hash $probePath
    $profilePath=Join-Path $directory 'profile.json';WriteJson $profilePath $p
    $verdict=Join-Path $directory 'verdict.json';$thrown=$false
    try {
        $null=& $assert -Summary $summaryPath -AppPath $AppPath -OutputPath $verdict `
            -ProfileReports @($profilePath) -ProfileProbeReports @($probePath) -ProfilerHookPath $ProfilerHookPath
    } catch {$thrown=$true}
    $decision=Get-Content $verdict -Raw|ConvertFrom-Json -Depth 100
    if(-not $thrown -or $decision.pass){throw "Invalid native evidence was accepted: $($case.Key)"}
    $results+=@{scenario=$case.Key;rejected=$true;verdict=$verdict}
}
@{baselineAccepted=$true;negativeCases=$results.Count;allRejected=$true;cases=$results}|ConvertTo-Json -Depth 8|Set-Content "$output/verification.json"
"PASS: real baseline plus $($results.Count) native bundle/launcher negative cases; $output/verification.json"
