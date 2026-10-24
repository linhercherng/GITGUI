param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must be new; prior evidence is preserved.' }
$null = New-Item -ItemType Directory -Path $output
$build = Join-Path $output 'build'
$project = Join-Path $PSScriptRoot 'GitGui.Engine.Tests/GitGui.Engine.Tests.csproj'
$filter = 'FullyQualifiedName~GitGui.Engine.Tests.UiPreferencesTests|FullyQualifiedName~GitGui.Engine.Tests.AppearancePreferenceTests|FullyQualifiedName~GitGui.Engine.Tests.InfrastructureTests.Askpass_uses_same_user_pipe|FullyQualifiedName~GitGui.Engine.Tests.SnapshotTests.Restores_refs_index_raw_worktree_untracked_binary_and_survives_gc'
Push-Location $root
try {
    & dotnet test $project -c Release -p:RestoreLockedMode=true -p:JsonSerializerIsReflectionEnabledByDefault=false `
        --artifacts-path $build --filter $filter --logger 'trx;LogFileName=static-json.trx' --results-directory $output
    $testExit = $LASTEXITCODE
} finally { Pop-Location }
$configuration = Join-Path $build 'bin/GitGui.Engine.Tests/release/GitGui.Engine.Tests.runtimeconfig.json'
if (-not (Test-Path -LiteralPath $configuration)) { throw 'Static JSON build did not produce runtime configuration; inspect the dotnet output.' }
$runtime = Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json -AsHashtable
$settings = $runtime.runtimeOptions.configProperties
$disabled = $settings.ContainsKey('System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault') -and `
    $settings['System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault'] -eq $false
[xml]$results = Get-Content -LiteralPath (Join-Path $output 'static-json.trx') -Raw
$counts = $results.TestRun.ResultSummary.Counters
$complete = [int]$counts.total -eq 6 -and [int]$counts.passed -eq 6 -and [int]$counts.failed -eq 0 -and [int]$counts.notExecuted -eq 0
@{ reflectionDisabled=$disabled; exitCode=$testExit; complete=$complete; total=[int]$counts.total; passed=[int]$counts.passed; filter=$filter } | ConvertTo-Json | Set-Content (Join-Path $output 'verification.json') -Encoding utf8
if (-not $disabled) { throw 'Runtime JSON reflection was not disabled; the contract check is invalid.' }
if ($testExit -ne 0 -or -not $complete) { throw 'Static JSON contract tests failed or did not execute all six cases.' }
