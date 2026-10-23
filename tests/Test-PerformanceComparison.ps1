$ErrorActionPreference = 'Stop'
$directory = Join-Path $env:TEMP ('gitgui-perf-comparison-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $directory
$compare = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts/Compare-Performance.ps1'
$baseline = @{ schema=1; fixture=@{ protocol=2; fsmonitorPrimed=$true; windowsFileSystemCache=$true; fingerprint='immutable'; head='abc'; commits=100000; files=50000; diffLines=10000; changedLines=100; git='2.56'; fsmonitor=$true };
    machine=@{ name='host'; os='Windows'; architecture='X64'; processors=8 }; samples=@{layoutIterations=1000}; metrics=@{openFirst500Ms=100;statusMedianMs=100;diff10kLinesMedianMs=100;layout500MedianMs=100} }
$before = Join-Path $directory 'baseline.json'; $after = Join-Path $directory 'candidate.json'
$baseline | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $before
$baseline | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $after
$null = & $compare -Baseline $before -Candidate $after
function Reject($report, [string]$message) {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $after
    $rejected = $false
    try { $null = & $compare -Baseline $before -Candidate $after } catch { $rejected = $true }
    if (-not $rejected) { throw "Comparison incorrectly accepted $message" }
}
$baseline.metrics.statusMedianMs=111; Reject $baseline '11% regression'
$baseline.metrics.statusMedianMs=100; $baseline.machine.name='other'; Reject $baseline 'different host'
$baseline.machine.name='host'; $baseline.fixture.fingerprint='other'; Reject $baseline 'different fixture'
$baseline.fixture.fingerprint='immutable'; $baseline.fixture.fsmonitorPrimed=$false; Reject $baseline 'unprimed fsmonitor'
$baseline.fixture.fsmonitorPrimed=$true; $baseline.fixture.protocol=1; Reject $baseline 'different protocol'
$baseline.fixture.protocol=2; $baseline.fixture.windowsFileSystemCache=$false; Reject $baseline 'different filesystem cache'
$baseline.fixture.windowsFileSystemCache=$true
$baseline.fixture.fingerprint='immutable'; $baseline.metrics.statusMedianMs=0; Reject $baseline 'zero metric'
$baseline.metrics.statusMedianMs='NaN'; Reject $baseline 'NaN metric'
$baseline.metrics.Remove('statusMedianMs'); Reject $baseline 'missing metric'
$baseline.metrics.statusMedianMs=100; $baseline.machine.Remove('os'); Reject $baseline 'missing host field'
'PASS: matching scenario accepted; regression, host/fixture mismatch, zero/NaN and missing metrics rejected.'
