param([Parameter(Mandatory)][string]$AppDirectory,[Parameter(Mandatory)][string]$Archive,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$null=New-Item -ItemType Directory -Path $OutputDirectory
$app=(Resolve-Path -LiteralPath $AppDirectory).Path
$archiveHash=(Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
$zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
try {
    foreach($relative in @('GitGui.App.exe','GitGui.App.dll','GitGui.App.r2r.dll','GitGui.Host.dll','GITGUI.vbs','build-info.json','PortableGit/cmd/git.exe')) {
        $entry=@($zip.Entries|Where-Object FullName -CEQ ((Split-Path $app -Leaf)+'/'+$relative))
        if($entry.Count -ne 1){throw "ZIP lacks $relative"}
        $stream=$entry[0].Open()
        try{$expected=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if((Get-FileHash -LiteralPath (Join-Path $app $relative)).Hash -cne $expected){throw "Desktop input differs from ZIP: $relative"}
    }
}finally{$zip.Dispose()}
$results=@()
foreach($mode in @('apphost','vbs')) {
    $report=Join-Path $OutputDirectory "$mode-desktop.json"
    $data=Join-Path $OutputDirectory "$mode-data"
    $exe=if($mode -eq 'apphost'){Join-Path $app 'GitGui.App.exe'}else{Join-Path ([Environment]::SystemDirectory) 'wscript.exe'}
    $start=[Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden'
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    if($mode -eq 'vbs'){$start.ArgumentList.Add('//nologo');$start.ArgumentList.Add((Join-Path $app 'GITGUI.vbs'))}
    foreach($arg in @('--performance-report',$report,'--performance-data',$data)){$start.ArgumentList.Add($arg)}
    $start.Environment['PATH']=[Environment]::SystemDirectory
    $start.Environment['DOTNET_ROOT']=Join-Path $OutputDirectory 'no-installed-dotnet'
    $start.Environment['DOTNET_ROOT_X64']=$start.Environment['DOTNET_ROOT']
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP']='0'
    $start.Environment['GITGUI_BENCH_GIT']=Join-Path $app 'PortableGit/cmd/git.exe'
    $child=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
        if(-not $child.WaitForExit(120000)){$child.Kill($true);throw "$mode desktop probe timed out"}
        $stdout.GetAwaiter().GetResult()|Set-Content (Join-Path $OutputDirectory "$mode-stdout.txt")
        $stderr.GetAwaiter().GetResult()|Set-Content (Join-Path $OutputDirectory "$mode-stderr.txt")
        if($child.ExitCode -ne 0){throw "$mode desktop probe failed with exit $($child.ExitCode)"}
        $probe=Get-Content $report -Raw|ConvertFrom-Json -Depth 100
        if(-not $probe.completed -or $probe.provenanceFailure -or $probe.failure){throw "$mode desktop report failed"}
        if($probe.gitRuntime.executable -ine (Join-Path $app 'PortableGit/cmd/git.exe')){throw 'Desktop Git path escaped package'}
        $results+=@{mode=$mode;result='PASS';report=$report;git=$probe.gitRuntime.executable;gitVersion=$probe.gitRuntime.version}
    } finally {if(-not $child.HasExited){$child.Kill($true)};$child.Dispose()}
}
& "$PSScriptRoot/Test-WindowsSingleInstance.ps1" -AppDirectory $app -OutputDirectory (Join-Path $OutputDirectory 'single-instance')
@{result='PASS';archiveSHA256=$archiveHash;singleInstance='PASS';scope='Isolated desktop launch, native rendering, bundled Git, apphost/WScript exit and cross-process instance recovery; not formal performance acceptance';modes=$results}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $OutputDirectory 'verification.json')
'Apphost and GITGUI.vbs isolated desktop checks: PASS'
