param(
    [Parameter(Mandatory)][ValidateSet('2.40.0', '2.56.0')][string]$Version,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'compatibility-git.json') -Raw | ConvertFrom-Json -AsHashtable
$pin = $manifest.versions[$Version]
$destinationPath = [IO.Path]::GetFullPath($Destination)
if ($destinationPath.IndexOfAny([char[]]"`r`n") -ge 0) { throw 'Destination cannot contain newlines.' }
if (-not $IsWindows -and $destinationPath -match '\s') { throw 'Use a Unix build prefix without whitespace; repository paths may still contain spaces and Unicode.' }
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$downloadRoot = Join-Path $temporaryRoot 'gitgui-compat-downloads'
$null = New-Item -ItemType Directory -Path $downloadRoot -Force
$executable = Join-Path $destinationPath $(if ($IsWindows) { 'cmd/git.exe' } else { 'bin/git' })
$expectedVersion = if ($IsWindows) { $pin.windowsVersion } else { "git version $Version" }

function Get-VerifiedArchive([string]$Url, [string]$ExpectedHash) {
    $archive = Join-Path $downloadRoot ([IO.Path]::GetFileName(([Uri]$Url).AbsolutePath))
    if (-not (Test-Path -LiteralPath $archive)) {
        $partial = "$archive.partial-$([Guid]::NewGuid().ToString('N'))"
        try {
            Invoke-WebRequest -Uri $Url -OutFile $partial
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ExpectedHash) { throw "Archive SHA-256 mismatch: $Url" }
            Move-Item -LiteralPath $partial -Destination $archive
        } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ExpectedHash) { throw "Cached archive SHA-256 mismatch: $archive" }
    return $archive
}

if (-not (Test-Path -LiteralPath $executable)) {
    if ($IsWindows) {
        if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'The pinned compatibility PortableGit archive targets Windows x64.' }
        $archive = Get-VerifiedArchive $pin.windowsUrl $pin.windowsSHA256
        $extract = Start-Process -FilePath $archive -ArgumentList @('-y', ('-o"' + $destinationPath + '"')) -WindowStyle Hidden -PassThru -Wait
        if ($extract.ExitCode -ne 0) { throw "PortableGit extraction failed: $($extract.ExitCode)" }
    } else {
        $null = Get-Command make -ErrorAction Stop
        $archive = Get-VerifiedArchive $pin.sourceUrl $pin.sourceSHA256
        $buildRoot = Join-Path $temporaryRoot "gitgui-compat-build-$Version"
        $null = New-Item -ItemType Directory -Path $buildRoot -Force
        & tar -xJf $archive -C $buildRoot
        if ($LASTEXITCODE -ne 0) { throw 'Git source extraction failed.' }
        Push-Location (Join-Path $buildRoot "git-$Version")
        try {
            $buildLog = Join-Path $buildRoot 'build.log'
            & make "-j$([Math]::Min(8, [Environment]::ProcessorCount))" "prefix=$destinationPath" NO_TCLTK=YesPlease NO_GETTEXT=YesPlease NO_OPENSSL=YesPlease all install *> $buildLog
            if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $buildLog -Tail 80; throw "Git build failed; see $buildLog" }
        } finally { Pop-Location }
    }
}

$actualVersion = (& $executable --version).Trim()
if ($LASTEXITCODE -ne 0 -or $actualVersion -ne $expectedVersion) { throw "Exact Git pin mismatch: expected '$expectedVersion', got '$actualVersion' ($executable)" }
$paths = if ($IsWindows) { @('cmd', 'bin', 'mingw64/bin', 'ucrt64/bin', 'usr/bin') } else { @('bin') }
$selectedPaths = @($paths | ForEach-Object { Join-Path $destinationPath $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
$env:PATH = ($selectedPaths + @($env:PATH)) -join [IO.Path]::PathSeparator
$env:GITGUI_EXPECTED_GIT_VERSION = $expectedVersion
$env:GITGUI_COMPAT_GIT_ROOT = $destinationPath
$pathVersion = (& git --version).Trim()
if ($LASTEXITCODE -ne 0 -or $pathVersion -ne $expectedVersion) { throw "PATH does not select the exact requested Git: $pathVersion" }
if ($env:GITHUB_PATH) { $selectedPaths | Out-File -LiteralPath $env:GITHUB_PATH -Encoding utf8 -Append }
if ($env:GITHUB_ENV) {
    "GITGUI_EXPECTED_GIT_VERSION=$expectedVersion" | Out-File -LiteralPath $env:GITHUB_ENV -Encoding utf8 -Append
    "GITGUI_COMPAT_GIT_ROOT=$destinationPath" | Out-File -LiteralPath $env:GITHUB_ENV -Encoding utf8 -Append
}
Write-Output "Exact Git selected: $actualVersion ($executable)"
