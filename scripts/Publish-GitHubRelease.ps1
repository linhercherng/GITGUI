param(
    [Parameter(Mandatory)][string]$AssetsManifest,
    [Parameter(Mandatory)][long]$CiRunId,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/Assert-ReleaseReady.ps1"
function Require($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Hash($Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function GitHubJson([string[]]$Arguments) {
    $json = & gh @Arguments
    Require ($LASTEXITCODE -eq 0) "GitHub command failed: $($Arguments -join ' ')"
    $json | ConvertFrom-Json -Depth 100
}
$repo = Split-Path $PSScriptRoot -Parent
$repository = 'linhercherng/GITGUI'
$assets = Get-Content -LiteralPath $AssetsManifest -Raw | ConvertFrom-Json
$commit = (& git -C $repo rev-parse HEAD).Trim()
Require ($LASTEXITCODE -eq 0 -and $commit -ceq $assets.sourceCommit) 'Packaged source differs from HEAD.'
& git -C $repo diff --exit-code HEAD
Require ($LASTEXITCODE -eq 0) 'Tracked source changes remain.'
$version = & "$PSScriptRoot/Get-ReleaseVersion.ps1"
Require ($version -ceq $assets.version) 'Release version differs from source.'
Require ($assets.files.Count -eq 7) 'Expected seven release assets.'
foreach ($file in $assets.files) {
    Require ((Get-Item -LiteralPath $file.path).Length -eq $file.size -and (Hash $file.path) -ceq $file.sha256) "Asset changed: $($file.path)"
}
$run = GitHubJson @('api',"repos/$repository/actions/runs/$CiRunId")
Require ($run.path -ceq '.github/workflows/ci.yml') 'CI run uses another workflow.'
$jobs = GitHubJson @('api',"repos/$repository/actions/runs/$CiRunId/jobs?per_page=100")
$evidence = Get-Content -LiteralPath (Join-Path (Split-Path $AssetsManifest -Parent) 'verification.json') -Raw | ConvertFrom-Json
Assert-ReleaseReady $evidence $run $jobs.jobs $commit (Hash $assets.archive)
if ($ValidateOnly) { Write-Output 'PASS: exact source, local evidence, six CI grids and asset digests'; return }
$tag = "v$version"
$existing = & git -C $repo ls-remote origin "refs/tags/$tag"
Require ($LASTEXITCODE -eq 0 -and -not $existing) 'Release tag already exists; inspect before retrying.'
& git -C $repo push origin HEAD:refs/heads/main
Require ($LASTEXITCODE -eq 0) 'Cannot fast-forward main.'
$files = @($assets.files | ForEach-Object path)
& gh release create $tag @files --repo $repository --target $commit --title "GITGUI $tag — 可靠性修正與可重現發布（內附 Portable Git）" --notes-file $assets.notes --draft
Require ($LASTEXITCODE -eq 0) 'Draft creation/upload failed; inspect the draft before retrying.'
$draft = GitHubJson @('release','view',$tag,'--repo',$repository,'--json','isDraft,targetCommitish,assets')
Require ($draft.isDraft -and $draft.targetCommitish -ceq $commit) 'Unexpected draft target.'
foreach ($file in $assets.files) {
    $remote = @($draft.assets | Where-Object name -CEQ ([IO.Path]::GetFileName($file.path)))
    Require ($remote.Count -eq 1 -and $remote[0].size -eq $file.size -and $remote[0].digest -ceq ('sha256:' + $file.sha256)) 'Uploaded asset size/SHA-256 differs.'
}
& gh release edit $tag --repo $repository --draft=false --latest
Require ($LASTEXITCODE -eq 0) 'Publication failed; inspect remote state.'
$published = GitHubJson @('release','view',$tag,'--repo',$repository,'--json','url,isDraft,isPrerelease,assets')
Require (-not $published.isDraft -and -not $published.isPrerelease) 'Release is not stable/published.'
$latest = GitHubJson @('api',"repos/$repository/releases/latest")
Require ($latest.tag_name -ceq $tag) 'Latest release differs.'
& git -C $repo fetch origin "refs/tags/${tag}:refs/tags/$tag"
Require ($LASTEXITCODE -eq 0 -and (& git -C $repo rev-parse "$tag^{commit}").Trim() -ceq $commit) 'Published tag commit differs.'
$published | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path (Split-Path $AssetsManifest -Parent) 'published-verification.json') -Encoding utf8
$published.url
