param(
    [Parameter(Mandatory = $true)][string]$Archive,
    [switch]$Desktop
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$zip = (Resolve-Path -LiteralPath $Archive).Path
$temporary = Join-Path $env:TEMP ('GITGUI 解壓測試 ' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporary
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $temporary)
$package = @(Get-ChildItem -LiteralPath $temporary -Directory)
if ($package.Count -ne 1) { throw 'ZIP must have exactly one top-level directory' }
$app = $package[0].FullName
$harness = Join-Path $temporary 'test-driver'
Push-Location $repo
try {
    $version = (Get-Content "$app/build-info.json" -Raw | ConvertFrom-Json).version
    & dotnet publish tests/GitGui.PackageSmoke/GitGui.PackageSmoke.csproj -c Release -r win-x64 --self-contained true `
        -p:RuntimeFrameworkVersion=8.0.32 -p:NuGetLockFilePath=packages.win-x64.lock.json -p:RestoreLockedMode=true `
        -p:PublishTrimmed=false -p:PublishSingleFile=false -p:Version=$version -o $harness
    if ($LASTEXITCODE -ne 0) { throw 'Package test driver build failed' }
    # Prove the driver tests the exact released engine, SQLite/libgit2 and runtime binaries.
    foreach ($file in Get-ChildItem -LiteralPath $harness -File) {
        if ($file.Name -like 'GitGui.PackageSmoke.*' -or $file.Extension -eq '.pdb') { continue }
        $packaged = Join-Path $app $file.Name
        if (-not (Test-Path -LiteralPath $packaged) -or
            (Get-FileHash $packaged).Hash -ne (Get-FileHash $file.FullName).Hash) {
            throw "Test driver dependency differs from ZIP: $($file.Name)"
        }
    }
    foreach ($name in @('GitGui.PackageSmoke.exe', 'GitGui.PackageSmoke.dll', 'GitGui.PackageSmoke.deps.json', 'GitGui.PackageSmoke.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $harness $name) -Destination $app
    }
    $names = @('PATH','DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_MULTILEVEL_LOOKUP','COREHOST_TRACE','COREHOST_TRACEFILE',
        'GIT_CONFIG_GLOBAL','GIT_CONFIG_NOSYSTEM','GIT_EXEC_PATH','GIT_TEMPLATE_DIR')
    $saved = @{}
    foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    try {
        $env:PATH = [Environment]::GetFolderPath('System')
        $env:DOTNET_ROOT = Join-Path $temporary 'no-installed-dotnet'
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
        $env:DOTNET_MULTILEVEL_LOOKUP = '0'
        $env:COREHOST_TRACE = '1'
        $env:COREHOST_TRACEFILE = Join-Path $temporary 'host-trace.txt'
        $env:GIT_CONFIG_NOSYSTEM = '1'
        $env:GIT_CONFIG_GLOBAL = Join-Path $temporary 'empty-global-config'
        [Environment]::SetEnvironmentVariable('GIT_EXEC_PATH', $null)
        [Environment]::SetEnvironmentVariable('GIT_TEMPLATE_DIR', $null)
        $process = [Diagnostics.ProcessStartInfo]::new((Join-Path $app 'GitGui.PackageSmoke.exe'))
        $process.UseShellExecute = $false; $process.CreateNoWindow = $true
        $process.RedirectStandardOutput = $true; $process.RedirectStandardError = $true
        $process.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        $process.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
        $process.ArgumentList.Add((Join-Path $temporary 'isolated repositories'))
        $child = [Diagnostics.Process]::Start($process)
        try {
            $stdout = $child.StandardOutput.ReadToEndAsync()
            $stderr = $child.StandardError.ReadToEndAsync()
            if (-not $child.WaitForExit(180000)) { $child.Kill($true); throw 'Package operation tests timed out' }
            $result = $stdout.GetAwaiter().GetResult()
            $errorText = $stderr.GetAwaiter().GetResult()
            if ($child.ExitCode -ne 0) { throw "Package operation tests failed: $errorText" }
            $report = $result.Trim() | ConvertFrom-Json
            if ($report.result -ne 'PASS') { throw 'Package operation report is not PASS' }
            Write-Output $result.Trim()
        } finally { $child.Dispose() }
        & "$PSScriptRoot/WindowsSmoke.ps1" -AppPath (Join-Path $app 'GitGui.App.exe') -Desktop:$Desktop
        $report | Add-Member -NotePropertyName archiveSHA256 -NotePropertyValue (Get-FileHash $zip).Hash.ToLowerInvariant()
        $report | Add-Member -NotePropertyName desktopTested -NotePropertyValue ([bool]$Desktop)
        $report | ConvertTo-Json | Set-Content "$temporary/verification.json" -Encoding utf8
        Write-Output "VERIFICATION: $temporary/verification.json"
        Write-Output "EXTRACTED: $app"
    } finally {
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
} finally { Pop-Location }
