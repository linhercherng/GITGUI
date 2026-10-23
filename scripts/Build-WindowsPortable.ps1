param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.5.0',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Build this package on Windows with PowerShell 7 and the .NET 10 SDK.' }
$repo = Split-Path $PSScriptRoot -Parent
$sourceStatus = @(& git -C $repo status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or $sourceStatus.Count -ne 0) { throw 'Commit the complete source and start from a clean checkout before producing a release package.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/windows-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must be new: never overwrite a tested release.' }
$null = New-Item -ItemType Directory -Path $output
$name = "GITGUI-$Version-win-x64-portable"
$app = Join-Path $output $name
$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit' }
Push-Location $repo
try {
    & dotnet publish src/GitGui.App/GitGui.App.csproj -c Release -r win-x64 --self-contained true `
        -p:RuntimeFrameworkVersion=10.0.13 -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:PublishSingleFile=false `
        -p:NuGetLockFilePath=packages.win-x64.lock.json -p:RestoreLockedMode=true -p:Version=$Version -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed' }
    & "$repo/tests/PreparePortableGit.ps1" -Destination (Join-Path $app 'PortableGit')
    foreach ($document in @('START-HERE.txt', 'THIRD-PARTY.txt')) {
        (Get-Content -LiteralPath "$repo/docs/portable/$document" -Raw).Replace('0.1.0', $Version) |
            Set-Content -LiteralPath (Join-Path $app $document) -Encoding utf8
    }
    Copy-Item -LiteralPath "$repo/LICENSE" -Destination (Join-Path $app 'LICENSE')
    Copy-Item -LiteralPath "$repo/docs/USER-GUIDE.zh-TW.md" -Destination $app
    $guideDocs = Join-Path $app 'docs'
    $guideImages = Join-Path $app 'images'
    $null = New-Item -ItemType Directory -Path $guideDocs, $guideImages
    foreach ($document in @('M3-COMPATIBILITY.md', 'M3-ARM-DECISION.md', 'M4-ACCEPTANCE.md', 'M4-PERFORMANCE.md', 'specification.md', 'WINDOWS-RELEASE.md')) {
        Copy-Item -LiteralPath (Join-Path "$repo/docs" $document) -Destination $guideDocs
    }
    foreach ($image in @('m3-main.png', 'm3-tools.png', 'm4-main.png', 'm4-main-history.png')) {
        Copy-Item -LiteralPath (Join-Path "$repo/docs/images" $image) -Destination $guideImages
    }
    # Root guide has the same links in the repository and portable package.
    $guide = Join-Path $app 'USER-GUIDE.zh-TW.md'
    (Get-Content -LiteralPath $guide -Raw) -replace '\]\((M3-COMPATIBILITY|M3-ARM-DECISION|M4-ACCEPTANCE|M4-PERFORMANCE|specification|WINDOWS-RELEASE)\.md\)', '](docs/$1.md)' |
        Set-Content -LiteralPath $guide -Encoding utf8
    & "$repo/scripts/Render-UserGuide.ps1" -OutputPath (Join-Path $app 'USER-GUIDE.zh-TW.html')
    $htmlGuide = Join-Path $app 'USER-GUIDE.zh-TW.html'
    (Get-Content -LiteralPath $htmlGuide -Raw) -replace 'href="(M3-COMPATIBILITY|M3-ARM-DECISION|M4-ACCEPTANCE|M4-PERFORMANCE|specification|WINDOWS-RELEASE)\.md"', 'href="docs/$1.md"' |
        Set-Content -LiteralPath $htmlGuide -Encoding utf8
    $licenses = Join-Path $app 'licenses'
    $null = New-Item -ItemType Directory -Path $licenses
    $assets = Get-Content "$repo/src/GitGui.App/obj/project.assets.json" -Raw | ConvertFrom-Json -AsHashtable
    $cache = @($assets.packageFolders.Keys)[0]
    $inventory = foreach ($entry in $assets.libraries.GetEnumerator() | Sort-Object Key) {
        if ($entry.Value.type -ne 'package') { continue }
        $path = Join-Path $cache $entry.Value.path
        $target = Join-Path $licenses $entry.Key.Replace('/', '-')
        $null = New-Item -ItemType Directory -Path $target
        # Preserve NuGet authors/copyright/SPDX expression/repository commit as well as supplied texts.
        Get-ChildItem -LiteralPath $path -File | Where-Object { $_.Name -match '(?i)license|copying|notice|\.nuspec$' } |
            Copy-Item -Destination $target
        foreach ($notice in Get-ChildItem -LiteralPath $path -Recurse -File | Where-Object { $_.DirectoryName -ne $path -and $_.Name -match '(?i)license|copying|notice' }) {
            $relative = [IO.Path]::GetRelativePath($path, $notice.FullName)
            $destination = Join-Path $target $relative
            $null = New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
            Copy-Item -LiteralPath $notice.FullName -Destination $destination
        }
        $spec = Get-ChildItem -LiteralPath $path -Filter '*.nuspec' | Select-Object -First 1
        [xml]$xml = Get-Content -LiteralPath $spec.FullName -Raw
        $licenseNode = $xml.SelectSingleNode('//*[local-name()="license"]')
        if ($licenseNode -and $licenseNode.type -eq 'file') {
            $licenseFile = Join-Path $path $licenseNode.InnerText
            Copy-Item -LiteralPath $licenseFile -Destination (Join-Path $target 'PACKAGE-LICENSE.txt')
        }
        $licenseText = if ($licenseNode) { $licenseNode.InnerText } else { 'See original nuspec licenseUrl and package notices' }
        [ordered]@{ package = $entry.Key; sha512 = $entry.Value.sha512; license = $licenseText }
    }
    # Runtime pack is not a normal application PackageReference.
    $runtime = Join-Path $cache 'microsoft.netcore.app.runtime.win-x64/10.0.13'
    Copy-Item -LiteralPath "$runtime/LICENSE.TXT" -Destination "$licenses/DOTNET-LICENSE.txt"
    Copy-Item -LiteralPath "$runtime/THIRD-PARTY-NOTICES.TXT" -Destination "$licenses/DOTNET-THIRD-PARTY-NOTICES.txt"
    Copy-Item -LiteralPath "$repo/docs/portable/MIT.txt" -Destination $licenses
    Copy-Item -Path "$repo/docs/portable/licenses/*.txt" -Destination $licenses
    $inventory | ConvertTo-Json -Depth 6 | Set-Content "$licenses/nuget-inventory.json" -Encoding utf8
    [ordered]@{ product = 'GITGUI'; version = $Version; sourceCommit = $commit;
        platform = 'win-x64'; selfContained = $true; dotnetRuntime = '10.0.13';
        portableGit = '2.56.0.3'; portableGitArchiveSHA256 = '4fc27b779829a36df77911e3f4dea09c66c790dd6c5f7dfa3b6a5479f1fe354f';
        source = "https://github.com/linhercherng/GITGUI/tree/$commit" } |
        ConvertTo-Json | Set-Content "$app/build-info.json" -Encoding utf8
    # .NET ZIP includes hidden files (e.g. Git's .gitattributes); Compress-Archive does not.
    [IO.Compression.ZipFile]::CreateFromDirectory($app, "$output/$name.zip", [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath "$output/$name.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip" | Set-Content "$output/SHA256SUMS.txt" -Encoding ascii
    Write-Output "PACKAGE: $output/$name.zip"
} finally { Pop-Location }
