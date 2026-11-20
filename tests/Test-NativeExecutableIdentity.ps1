param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(!$IsWindows){throw 'Windows native identity fixture requires Windows'}
$file=Join-Path $PSScriptRoot '../scripts/Assert-DesktopPerformance.ps1'
$tokens=$null;$errors=$null
$tree=[Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Invalid acceptance script'}
foreach($name in @('Require','Number','Integer','Limit','SameNumber','Hash','HashEquals','NativeBundleValid','MemoryValid')){
    $node=$tree.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    if(!$node){throw "Missing production function $name"}
    . ([scriptblock]::Create($node.Extent.Text))
}
$appDirectory=Join-Path $env:TEMP ('gitgui-native-exe-identity-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory $appDirectory
foreach($name in @('GitGui.App.exe','GitGui.Host.dll','GITGUI.vbs','GitGui.App.r2r.dll')){[IO.File]::WriteAllText((Join-Path $appDirectory $name),$name)}
$app=Join-Path $appDirectory 'GitGui.App.exe'
$rundll=Join-Path ([Environment]::SystemDirectory) 'rundll32.exe'
$wscript=Join-Path ([Environment]::SystemDirectory) 'wscript.exe'
$started=[DateTime]::UtcNow
$legacy=@{launchOrigin=@{ProcessId=2;LauncherProcessId=1;LauncherPath=$wscript;NativeHostPath=(Join-Path $appDirectory 'GitGui.Host.dll');StartedUtc=$started;ProcessStartedUtc=$started.AddMilliseconds(10)};
    nativeHostIdentity=@{processPath=$rundll;processSha256=(Hash $rundll);scriptHostSha256=(Hash $wscript);hostSha256=(Hash (Join-Path $appDirectory 'GitGui.Host.dll'));launcherSha256=(Hash (Join-Path $appDirectory 'GITGUI.vbs'));bundleSha256=(Hash $app)};compositeImageSha256=(Hash (Join-Path $appDirectory 'GitGui.App.r2r.dll'))}
if(!(NativeBundleValid $legacy)){throw 'Original script-host identity no longer validates'}
$exe=Join-Path $appDirectory 'GITGUI.exe';[IO.File]::WriteAllText($exe,'native executable fixture')
$self=@{launchOrigin=@{ProcessId=2;LauncherProcessId=2;LauncherPath=$exe;NativeHostPath=$exe;StartedUtc=$started;ProcessStartedUtc=$started};
    nativeHostIdentity=@{processPath=$exe;processSha256=(Hash $exe);hostSha256=(Hash $exe);launcherSha256=(Hash $exe);bundleSha256=(Hash $app)};compositeImageSha256=(Hash (Join-Path $appDirectory 'GitGui.App.r2r.dll'))}
if(!(NativeBundleValid $self)){throw 'Single native executable identity did not validate'}
$cases=@{
    'wrong-process'={param($r) $r.nativeHostIdentity.processPath=$rundll}
    'wrong-launcher'={param($r) $r.launchOrigin.LauncherPath=$wscript}
    'wrong-native-module'={param($r) $r.launchOrigin.NativeHostPath=(Join-Path $appDirectory 'GitGui.Host.dll')}
    'hidden-parent-process'={param($r) $r.launchOrigin.LauncherProcessId=3}
    'shifted-start-clock'={param($r) $r.launchOrigin.ProcessStartedUtc=$started.AddTicks(1)}
    'missing-os-start'={param($r) $r.launchOrigin.Remove('ProcessStartedUtc')}
    'changed-executable'={param($r) $r.nativeHostIdentity.processSha256='0'*64}
    'changed-launcher'={param($r) $r.nativeHostIdentity.launcherSha256='0'*64}
    'changed-native-module'={param($r) $r.nativeHostIdentity.hostSha256='0'*64}
    'changed-bundle'={param($r) $r.nativeHostIdentity.bundleSha256='0'*64}
    'changed-composite'={param($r) $r.compositeImageSha256='0'*64}
    'invented-script-host'={param($r) $r.nativeHostIdentity.scriptHostSha256=(Hash $wscript)}
}
foreach($case in $cases.GetEnumerator()){
    $report=$self|ConvertTo-Json -Depth 12|ConvertFrom-Json -AsHashtable
    & $case.Value $report
    $rejected=$false;try{$null=NativeBundleValid $report}catch{$rejected=$true}
    if(!$rejected){throw "Accepted invalid executable identity: $($case.Key)"}
}
$rejected=$false;try{$null=NativeBundleValid $legacy}catch{$rejected=$true}
if(!$rejected){throw 'Legacy launcher cannot claim results for the new default executable'}
$memory=$self|ConvertTo-Json -Depth 12|ConvertFrom-Json -AsHashtable
$memory.scrollRendering=@{evidence=@{StartQpc=1000L;EndQpc=101000L;TimestampFrequency=1000000L}}
$memory.steadyMemory=@{intervalMs=100;startQpc=1000L;endQpc=101000L;timestampFrequency=1000000L;maximumWorkingSetBytes=200000000;
    samplesInOrder=@(@{RecordedQpc=1000L;WorkingSetBytes=200000000;DesktopWorkingSetBytes=200000000;LauncherWorkingSetBytes=0},@{RecordedQpc=101000L;WorkingSetBytes=200000000;DesktopWorkingSetBytes=200000000;LauncherWorkingSetBytes=0})}
$null=MemoryValid $memory
$memoryCases=@{
    'duplicate-desktop'={param($r) $r.steadyMemory.samplesInOrder[0].LauncherWorkingSetBytes=1;$r.steadyMemory.samplesInOrder[0].WorkingSetBytes+=1}
    'missing-desktop'={param($r) $r.steadyMemory.samplesInOrder[0].DesktopWorkingSetBytes=0}
    'hidden-live-launcher'={param($r) $r.launchOrigin.LauncherProcessId=3}
    'over-300MB'={param($r) $r.steadyMemory.samplesInOrder[0].WorkingSetBytes=300000001}
    'sampling-gap'={param($r) $r.steadyMemory.samplesInOrder[1].RecordedQpc=202000L}
    'wrong-total'={param($r) $r.steadyMemory.samplesInOrder[0].WorkingSetBytes-=1}
}
foreach($case in $memoryCases.GetEnumerator()){
    $report=$memory|ConvertTo-Json -Depth 15|ConvertFrom-Json -AsHashtable
    & $case.Value $report
    $rejected=$false;try{$null=MemoryValid $report}catch{$rejected=$true}
    if(!$rejected){throw "Accepted invalid native memory: $($case.Key)"}
}
"PASS: legacy/native executable identity and $($cases.Count+1) identity/$($memoryCases.Count) memory negative cases; original limits retained"
