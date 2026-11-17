param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$CopyleftSources,
    [Parameter(Mandatory)][string]$NotesFile,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Hash($Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$repo = Split-Path $PSScriptRoot -Parent
$version = & "$PSScriptRoot/Get-ReleaseVersion.ps1"
$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify release commit.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must be new; preserve previously tested packages.' }
$null = New-Item -ItemType Directory -Path $output
$expanded = Join-Path $output 'expanded'
[IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $Archive).Path, $expanded)
$app = Join-Path $expanded "GITGUI-$version-win-x64-portable"
$build = Get-Content -LiteralPath "$app/build-info.json" -Raw | ConvertFrom-Json
if ($build.version -cne $version -or $build.sourceCommit -cne $commit) { throw 'ZIP version/source differs from checkout.' }
if ($build.portableGit -cne '2.56.0.3' -or (Hash $CopyleftSources) -cne '6f13373aa0af8c7bdaefde53252c57474f3c2f8191143af1a2dcc70d58dac961') { throw 'Copyleft source archive does not match the bundled Git pin.' }
$localTests = foreach ($name in @('Core','Engine','UI')) {
    [xml]$trx = Get-Content -LiteralPath (Join-Path $EvidenceDirectory "$name.trx") -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ($trx.TestRun.ResultSummary.outcome -cne 'Completed' -or [int]$counts.total -le 0 -or [int]$counts.passed -ne [int]$counts.total) { throw "Incomplete or failed $name TRX." }
    @{name=$name;total=[int]$counts.total;passed=[int]$counts.passed;failed=[int]$counts.failed;skipped=[int]$counts.notExecuted}
}
$packageSmoke = Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'package-verification.json') -Raw | ConvertFrom-Json
$desktop = Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'desktop/verification.json') -Raw | ConvertFrom-Json
$archiveHash = Hash $Archive
if ($packageSmoke.result -cne 'PASS' -or $packageSmoke.archiveSHA256 -cne $archiveHash -or $desktop.result -cne 'PASS' -or $desktop.archiveSHA256 -cne $archiveHash) { throw 'Exact package and desktop checks must pass.' }
$guide = Get-Content -LiteralPath "$app/USER-GUIDE.zh-TW.md" -Raw
foreach ($match in [regex]::Matches($guide, '\]\((images/[^)]+\.png)\)')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $match.Groups[1].Value))) { throw 'Offline manual image is missing.' }
}
$verification = @{result='PASS';version=$version;sourceCommit=$commit;archiveSHA256=$archiveHash;localTests=@($localTests);packageSmoke=$packageSmoke;desktop=$desktop}
$verification | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$output/verification.json" -Encoding utf8
$manualRoot = Join-Path $output "manual/GITGUI-$version-user-guide.zh-TW"
$null = New-Item -ItemType Directory -Path $manualRoot
foreach ($item in @('USER-GUIDE.zh-TW.md','USER-GUIDE.zh-TW.html','images','docs','LICENSE')) { Copy-Item -LiteralPath (Join-Path $app $item) -Destination $manualRoot -Recurse }
$manual = Join-Path $output "GITGUI-$version-user-guide.zh-TW.zip"
[IO.Compression.ZipFile]::CreateFromDirectory((Split-Path $manualRoot -Parent), $manual)
$evidence = Join-Path $output "v$version-validation-evidence.zip"
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path -LiteralPath $EvidenceDirectory).Path, $evidence)
$package = Join-Path $output ([IO.Path]::GetFileName($Archive))
Copy-Item -LiteralPath $Archive -Destination $package
$sources = Join-Path $output ([IO.Path]::GetFileName($CopyleftSources))
Copy-Item -LiteralPath $CopyleftSources -Destination $sources
$notes = Join-Path $output 'release-notes.zh-TW.md'
Copy-Item -LiteralPath $NotesFile -Destination $notes
$releaseInfo = Join-Path $output 'release-info.json'
@{version=$version;sourceCommit=$commit;binarySourceCommit=$build.sourceCommit;archiveSHA256=$archiveHash} | ConvertTo-Json | Set-Content -LiteralPath $releaseInfo -Encoding utf8
$files = @($package,$manual,$sources,"$output/verification.json",$releaseInfo,$evidence)
$sums = Join-Path $output 'SHA256SUMS.txt'
@($files | ForEach-Object { "$(Hash $_)  $([IO.Path]::GetFileName($_))" }) | Set-Content -LiteralPath $sums -Encoding ascii
$files += $sums
@{version=$version;sourceCommit=$commit;archive=$package;archiveSHA256=$archiveHash;notes=$notes;files=@($files | ForEach-Object { @{path=$_;size=(Get-Item -LiteralPath $_).Length;sha256=(Hash $_)} })} |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$output/release-assets.json" -Encoding utf8
Write-Output "RELEASE-ASSETS: $output/release-assets.json"
