param(
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$Candidate,
    [ValidateRange(0, 100)][double]$MaximumRegressionPercent = 10,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$before = Get-Content -LiteralPath $Baseline -Raw | ConvertFrom-Json -AsHashtable
$after = Get-Content -LiteralPath $Candidate -Raw | ConvertFrom-Json -AsHashtable
foreach ($report in @($before, $after)) {
    if ($report.schema -ne 1) { throw 'Unsupported performance report schema' }
    foreach ($field in @('protocol','fsmonitorPrimed','windowsFileSystemCache','fingerprint','head','commits','files','diffLines','changedLines','git','fsmonitor')) {
        if (-not $report.fixture.ContainsKey($field)) { throw "Missing fixture field: $field" }
    }
    foreach ($field in @('name','os','architecture','processors')) {
        if (-not $report.machine.ContainsKey($field) -or [string]::IsNullOrWhiteSpace([string]$report.machine[$field])) { throw "Missing measurement host field: $field" }
    }
    if ([int]$report.machine.processors -lt 1) { throw 'Invalid processor count' }
}
foreach ($field in @('protocol','fsmonitorPrimed','windowsFileSystemCache','fingerprint','head','commits','files','diffLines','changedLines','git','fsmonitor')) {
    if ($before.fixture[$field] -cne $after.fixture[$field]) { throw "Different fixture/scenario: $field" }
}
foreach ($field in @('layoutIterations')) {
    if ($before.samples[$field] -cne $after.samples[$field]) { throw "Different measurement protocol: $field" }
}
foreach ($field in @('name','os','architecture','processors')) {
    if ($before.machine[$field] -cne $after.machine[$field]) { throw "Different measurement host: $field" }
}
$rows = foreach ($metric in @('openFirst500Ms','statusMedianMs','diff10kLinesMedianMs','layout500MedianMs')) {
    if (-not $before.metrics.ContainsKey($metric) -or -not $after.metrics.ContainsKey($metric)) { throw "Missing metric: $metric" }
    $old = [double]$before.metrics[$metric]; $new = [double]$after.metrics[$metric]
    if (-not [double]::IsFinite($old) -or -not [double]::IsFinite($new) -or $old -le 0 -or $new -le 0) { throw "Invalid metric: $metric" }
    $change = ($new / $old - 1) * 100
    [ordered]@{ metric = $metric; baseline = $old; candidate = $new; changePercent = $change; pass = $change -le $MaximumRegressionPercent }
}
$result = [ordered]@{ schema = 1; baselineSHA256 = (Get-FileHash -LiteralPath $Baseline).Hash.ToLowerInvariant();
    candidateSHA256 = (Get-FileHash -LiteralPath $Candidate).Hash.ToLowerInvariant(); maximumRegressionPercent = $MaximumRegressionPercent;
    pass = @($rows | Where-Object { -not $_.pass }).Count -eq 0; comparisons = @($rows) }
$json = $result | ConvertTo-Json -Depth 8
if ($OutputPath) {
    $full = [IO.Path]::GetFullPath($OutputPath)
    $stream = [IO.File]::Open($full, [IO.FileMode]::CreateNew)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json); $stream.Write($bytes) } finally { $stream.Dispose() }
}
$json
if (-not $result.pass) { throw 'Performance regression exceeds the allowed threshold' }
