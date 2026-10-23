param(
    [Parameter(Mandatory = $true)][string]$PortableGit,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.6.0'
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $output -Force
$sources = Join-Path $output 'PortableGit-2.56.0.3-copyleft-sources'
$null = New-Item -ItemType Directory -Path $sources -Force
$manifest = Join-Path $PSScriptRoot '../docs/portable/portablegit-sources.json'
$records = @(Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json)
# Includes GNU/GPL/LGPL/AGPL components and shared-library variants. GCC runtime
# exception and permissive components retain their original binary notices.
# Archives preserve upstream source, patches and PKGBUILD build recipes.
foreach ($record in $records) {
    $target = Join-Path $sources $record.file
    if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() -ne $record.sha256) {
        $partial = $target + '.partial'
        Invoke-WebRequest -Uri $record.url -OutFile $partial -TimeoutSec 300
        if ((Get-FileHash -LiteralPath $partial).Hash.ToLowerInvariant() -ne $record.sha256) { throw "Source checksum mismatch: $($record.file)" }
        Move-Item -LiteralPath $partial -Destination $target -Force
    }
    # Some Windows tar builds cannot accept Unicode absolute paths. Keep argv
    # ASCII and inherit the Unicode working directory from PowerShell instead.
    Push-Location $sources
    try {
        & tar -tf $record.file 1>$null
        if ($LASTEXITCODE -ne 0) { throw "Source archive cannot be read: $($record.file)" }
    } finally { Pop-Location }
}
Copy-Item -LiteralPath $manifest -Destination "$sources/source-manifest.json" -Force
Copy-Item -LiteralPath (Join-Path $PortableGit 'etc/package-versions.txt') -Destination $sources -Force
(Get-Content -LiteralPath "$PSScriptRoot/../docs/portable/THIRD-PARTY.txt" -Raw).Replace('0.1.0', $Version) |
    Set-Content -LiteralPath (Join-Path $sources 'THIRD-PARTY.txt') -Encoding utf8
Copy-Item -LiteralPath "$PSScriptRoot/../LICENSE" -Destination (Join-Path $sources 'GITGUI-LICENSE.txt') -Force
$zip = Join-Path $output 'PortableGit-2.56.0.3-copyleft-sources.zip'
if (Test-Path -LiteralPath $zip) { throw 'Source ZIP already exists; do not replace released artifacts.' }
[IO.Compression.ZipFile]::CreateFromDirectory($sources, $zip, [IO.Compression.CompressionLevel]::NoCompression, $true)
Write-Output "SOURCES: $zip ($($records.Count) corresponding source packages)"
