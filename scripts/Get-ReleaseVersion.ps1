$properties = [xml](Get-Content -LiteralPath "$PSScriptRoot/../Directory.Build.props" -Raw)
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Directory.Build.props must contain one release Version.' }
$version
