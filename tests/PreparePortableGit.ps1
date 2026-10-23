param([string]$Destination)
$ErrorActionPreference = 'Stop'
$temporaryDirectory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
if (-not $Destination) { $Destination = Join-Path $temporaryDirectory 'GITGUI Portable 中文' }
$Destination = [IO.Path]::GetFullPath($Destination)
$archive = Join-Path $temporaryDirectory 'PortableGit-2.56.0.3-64-bit.7z.exe'
$source = 'https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.3/PortableGit-2.56.0.3-64-bit.7z.exe'
$expected = '4fc27b779829a36df77911e3f4dea09c66c790dd6c5f7dfa3b6a5479f1fe354f'
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $source -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'Official PortableGit archive SHA-256 mismatch'
}
$executable = Join-Path $Destination 'cmd/git.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    $process = Start-Process -FilePath $archive -ArgumentList @('-y', ('-o"' + $Destination + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "PortableGit extraction failed: $($process.ExitCode)" }
}
$version = & $executable --version
if ($LASTEXITCODE -ne 0 -or $version -notlike 'git version 2.56.0.windows.3*') { throw 'PortableGit version mismatch' }
$env:GITGUI_TEST_PORTABLE_ROOT = $Destination
if ($env:GITHUB_ENV) { "GITGUI_TEST_PORTABLE_ROOT=$Destination" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append }
Write-Output "Verified PortableGit: $version ($Destination)"
