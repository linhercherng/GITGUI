param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$Fixture,
    [Parameter(Mandatory)][string]$GitExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(3, 15)][int]$Samples = 5,
    [ValidateSet('default','software')][string]$Renderer = 'default'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Native Windows desktop measurement requires Windows and an interactive session' }
$app = (Resolve-Path -LiteralPath $AppPath).Path
$fixturePath = (Resolve-Path -LiteralPath $Fixture).Path
$git = (Resolve-Path -LiteralPath $GitExecutable).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must be new; existing measurements are preserved' }
$null = New-Item -ItemType Directory -Path $output
$rows = @()
for ($sample = 1; $sample -le $Samples; $sample++) {
    foreach ($mode in @('cold','large')) {
        $report = Join-Path $output "$mode-$sample.json"
        $dataDirectory = Join-Path $env:TEMP ('gitgui-native-' + [guid]::NewGuid().ToString('N'))
        $start = [Diagnostics.ProcessStartInfo]::new($app)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
        $start.Environment['DOTNET_PROCESSOR_COUNT'] = '8'
        $start.Environment['GITGUI_BENCH_GIT'] = $git
        $start.Environment['GITGUI_BENCH_RENDERER'] = $Renderer
        foreach ($argument in @('--performance-report',$report,'--performance-data',$dataDirectory)) { $start.ArgumentList.Add($argument) }
        if ($mode -eq 'large') { $start.ArgumentList.Add('--performance-repository'); $start.ArgumentList.Add($fixturePath) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw "Native $mode sample $sample timed out" }
            if ($process.ExitCode -ne 0) { throw "Native $mode sample $sample failed; inspect $report" }
        } finally { $process.Dispose() }
        $data = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json -AsHashtable
        $diagnostics = Join-Path $output "logs-$mode-$sample"
        $null = New-Item -ItemType Directory -Path $diagnostics
        Get-ChildItem -LiteralPath $dataDirectory -File -Filter 'gitgui-*.log' | Copy-Item -Destination $diagnostics
        if (-not $data.completed) { throw "Incomplete native sample: $report" }
        $row = [ordered]@{ mode=$mode; sample=$sample; file=[IO.Path]::GetFileName($report); sha256=(Get-FileHash -LiteralPath $report).Hash.ToLowerInvariant();
            startupMs=$data.startupInteractiveAfterInitializationMs; workingSetBytes=$data.workingSetBytes; heartbeatMaxMs=$data.uiDispatcherHeartbeatDelayMs.max }
        if ($data.ContainsKey('startupSurfaceInteractiveMs')) { $row.startupSurfaceMs=$data.startupSurfaceInteractiveMs }
        if ($mode -eq 'large') {
            $row.openFirst500Ms=$data.openFirst500GraphFrameCallbackMs
            $row.statusListMedianMs=$data.statusListFrameCallbackMs.median
            $row.diffMedianMs=$data.diffSelectionFrameCallbackMs.median
            $row.scrollCallbackFps=$data.scrollNativeFrameIntervalsMs.callbackFps
            $row.scrollProviderMaxMs=$data.scrollNativeFrameIntervalsMs.scrollProviderCallbackDurationMs.max
            if ($data.ContainsKey('openFirst500GraphSurfaceSubmissionMs')) {
                $row.openFirst500SurfaceMs=$data.openFirst500GraphSurfaceSubmissionMs
                $row.statusSurfaceMedianMs=$data.statusListSurfaceSubmissionMs.median
                $row.diffSurfaceMedianMs=$data.diffSelectionSurfaceSubmissionMs.median
                $row.scrollSubmittedFps=$data.scrollRendering.evidence.SubmittedFramesPerSecond
                $row.scrollCadencePass=$data.scrollRendering.evidence.Gate.Pass
                $row.steadyResidentMaxBytes=$data.steadyMemory.maximumWorkingSetBytes
            }
        }
        $rows += $row
        $row | ConvertTo-Json -Compress
    }
}
function Median($values) { $sorted=@($values | Sort-Object); $sorted[[int][Math]::Floor($sorted.Count / 2)] }
$large = @($rows | Where-Object mode -eq 'large')
$summary = [ordered]@{ schema=1; appSHA256=(Get-FileHash -LiteralPath $app).Hash.ToLowerInvariant(); renderer=$Renderer; samples=$Samples;
    metrics=[ordered]@{
        coldStartupMedianMs=(Median @($rows | Where-Object mode -eq 'cold' | ForEach-Object startupMs))
        coldStartupMaxMs=(@($rows | Where-Object mode -eq 'cold' | ForEach-Object startupMs) | Measure-Object -Maximum).Maximum
        openFirst500MedianMs=(Median @($large | ForEach-Object openFirst500Ms))
        statusListMedianMs=(Median @($large | ForEach-Object statusListMedianMs))
        diffMedianMs=(Median @($large | ForEach-Object diffMedianMs))
        scrollCallbackMedianFps=(Median @($large | ForEach-Object scrollCallbackFps))
        residentMaxBytes=(@($large | ForEach-Object workingSetBytes) | Measure-Object -Maximum).Maximum
        heartbeatMaxMs=(@($large | ForEach-Object heartbeatMaxMs) | Measure-Object -Maximum).Maximum
    }; reports=$rows; boundaries='RAF callbacks precede compositor presentation. Dispatcher delay is an observed responsiveness proxy, not a proof of every UI callback duration. Raw reports preserve setup, full operation timings and all outliers.' }
if (@($rows | Where-Object { $_.Contains('startupSurfaceMs') }).Count -eq $rows.Count) {
    $summary.surfaceMetrics=[ordered]@{
        coldStartupMedianMs=(Median @($rows | Where-Object mode -eq 'cold' | ForEach-Object startupSurfaceMs))
        coldStartupMaxMs=(@($rows | Where-Object mode -eq 'cold' | ForEach-Object startupSurfaceMs) | Measure-Object -Maximum).Maximum
        openFirst500MedianMs=(Median @($large | ForEach-Object openFirst500SurfaceMs))
        statusMedianMs=(Median @($large | ForEach-Object statusSurfaceMedianMs))
        diffMedianMs=(Median @($large | ForEach-Object diffSurfaceMedianMs))
        steadyResidentMaxBytes=(@($large | ForEach-Object steadyResidentMaxBytes) | Measure-Object -Maximum).Maximum
        scope='Actual render and native surface-update return; physical display completion excluded. Medians are summaries only; Assert-DesktopPerformance checks every raw value.'
    }
}
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
'SUMMARY: ' + (Join-Path $output 'summary.json')
