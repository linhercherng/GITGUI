$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/../scripts/Get-VerifiedArchive.ps1"
$root = Join-Path ([IO.Path]::GetTempPath()) ('gitgui-download-test-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$script:calls = [Collections.Generic.List[string]]::new()
$script:mode = 'fallback'
function Start-Sleep { param($Seconds) }
function Invoke-WebRequest {
    param($Uri, $OutFile, $TimeoutSec)
    $script:calls.Add($Uri)
    if ($script:mode -eq 'offline' -or ($script:mode -eq 'fallback' -and $Uri -eq 'https://primary.invalid/git.tar.xz')) { throw 'Simulated DNS failure' }
    [IO.File]::WriteAllText($OutFile, $(if ($script:mode -eq 'corrupt') { 'corrupt' } else { 'verified bytes' }))
}
function Require($Condition, $Message) { if (-not $Condition) { throw $Message } }
try {
    $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('verified bytes'))).ToLowerInvariant()
    $archive = Join-Path $root 'git.tar.xz'
    $urls = @('https://primary.invalid/git.tar.xz', 'https://mirror.invalid/git.tar.xz')
    $result = Get-VerifiedArchive $urls $expected $archive
    Require ($result -ceq $archive -and $script:calls.Count -eq 4) 'Expected three primary attempts then verified mirror'
    $null = Get-VerifiedArchive $urls $expected $archive
    Require ($script:calls.Count -eq 4) 'Verified cache should avoid network'
    Remove-Item -LiteralPath $archive
    $script:mode = 'corrupt'; $script:calls.Clear(); $rejected = $false
    try { $null = Get-VerifiedArchive $urls $expected $archive } catch { $rejected = $_.Exception.Message -like '*SHA-256 mismatch*' }
    Require ($rejected -and $script:calls.Count -eq 1 -and -not (Test-Path -LiteralPath $archive)) 'Hash mismatch must fail closed without fallback'
    $script:mode = 'offline'; $script:calls.Clear(); $rejected = $false
    try { $null = Get-VerifiedArchive $urls $expected $archive } catch { $rejected = $true }
    Require ($rejected -and $script:calls.Count -eq 6) 'Retries must be bounded'
    Require (@(Get-ChildItem -LiteralPath $root).Count -eq 0) 'Partial downloads must be removed'
    Write-Output 'PASS: verified cache, bounded retries, mirror fallback, hash rejection, partial cleanup'
} finally { Remove-Item -LiteralPath $root }
