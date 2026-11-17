$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/../scripts/Assert-ReleaseReady.ps1"
function Require($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Rejected($Evidence, $Run, $Jobs) {
    try { Assert-ReleaseReady $Evidence $Run $Jobs $commit $hash; return $false } catch { return $true }
}
$commit = 'a' * 40; $hash = 'b' * 64
$evidence = @{ result = 'PASS'; sourceCommit = $commit; archiveSHA256 = $hash;
    localTests = @('Core','Engine','UI') | ForEach-Object { @{name=$_;total=1;passed=1;failed=0;skipped=0} };
    packageSmoke = @{result='PASS';archiveSHA256=$hash}; desktop=@{result='PASS';archiveSHA256=$hash} }
$run = @{head_sha=$commit;status='completed';conclusion='success'}
$jobs = @(foreach ($os in @('windows-2022','ubuntu-22.04','macos-15-intel')) {
    foreach ($git in @('2.40.0','2.56.0')) { @{name="Git $git ($os)";status='completed';conclusion='success'} }
})
Assert-ReleaseReady $evidence $run $jobs $commit $hash
$run.head_sha = 'c' * 40
Require (Rejected $evidence $run $jobs) 'Stale CI commit was accepted'
$run.head_sha = $commit
Require (Rejected $evidence $run @($jobs | Select-Object -Skip 1)) 'Missing CI grid was accepted'
$jobs[5].conclusion = 'failure'
Require (Rejected $evidence $run $jobs) 'Failed macOS grid was accepted'
$jobs[5].conclusion = 'success'
$evidence.localTests[1].skipped = 1
Require (Rejected $evidence $run $jobs) 'Skipped local tests were accepted'
$evidence.localTests[1].skipped = 0
$evidence.packageSmoke.archiveSHA256 = 'c' * 64
Require (Rejected $evidence $run $jobs) 'Evidence for another ZIP was accepted'
$version = & "$PSScriptRoot/../scripts/Get-ReleaseVersion.ps1"
Require ($version -match '^\d+\.\d+\.\d+$') 'Invalid release version'
Write-Output 'PASS: release rejects stale CI, missing/failed grid, incomplete tests and wrong package'
