param([string]$Scenario = '*')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$directory = Join-Path ([IO.Path]::GetTempPath()) ('gitgui-native-acceptance-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $directory
$assert = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts/Assert-DesktopPerformance.ps1'
function WriteJson($path, $value) { $value | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $path -Encoding utf8 }
function Hash($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
foreach ($name in @('GitGui.App.exe','GitGui.App.dll','Avalonia.Base.dll','Avalonia.Win32.dll','Hook.dll')) {
    [IO.File]::WriteAllText((Join-Path $directory $name), "synthetic acceptance fixture $name")
}
$productNames=@('GitGui.App','GitGui.Core','GitGui.Controls','GitGui.Engine.Cli','GitGui.Engine.Lib','GitGui.Infrastructure','GitGui.UI')
$productAssemblies=@{}
# Reuse a small existing managed PE as synthetic metadata input; never execute it.
$metadataFixture=[Console].Assembly
foreach($name in $productNames) {
    $file="$name.dll";$path=Join-Path $directory $file
    [IO.File]::Copy($metadataFixture.Location,$path,$true)
    $productAssemblies[$name]=@{fileName=$file;sha256=(Hash $path);mvid=$metadataFixture.ManifestModule.ModuleVersionId.ToString('D');version=$metadataFixture.GetName().Version.ToString()}
}
$app = Join-Path $directory 'GitGui.App.exe'
$hook = Join-Path $directory 'Hook.dll'
$frequency = 1000000L; $start = 2000000L; $end = 7000000L
$metrics = @(); $frames = @(); $batches = @(); $intervals = @()
for ($i=0; $i -lt 299; $i++) {
    $qpc = $start + 5000L + [long][Math]::Round($i * $frequency * 1001 / 60000)
    $metrics += @{ Instrument='avalonia.comp.render.time'; Unit='ms'; Value=1.0; RecordedQpc=$qpc; Phase='graph-scroll' }
    $frames += @{RenderPassQpc=$qpc; CompletedQpc=$qpc+100; BatchId=$i+1; RenderDurationMs=1.0}
    $batches += @{BatchId=$i+1; RequestedQpc=$qpc-2000; CompletedQpc=$qpc+100; CompletedSuccessfully=$true; Phase='graph-scroll'; ActiveGpuContextType='WinUiComposited'; InspectionError=$null}
    if ($i -gt 0) { $intervals += ($qpc - $frames[$i-1].RenderPassQpc) * 1000.0 / $frequency }
}
function Stats($values) { @{samples=$values.Count; min=($values | Measure-Object -Minimum).Minimum; max=($values | Measure-Object -Maximum).Maximum; median=100; values=@($values)} }
$fixture = @{schema=1; protocol=2; root='C:\synthetic-owned'; commits=100000; files=50000; diffLines=10000; changedLines=100; git='git version 2.56.0'; fsmonitor=$true; fsmonitorPrimed=$true; windowsFileSystemCache=$true; fingerprint=('a'*32); head=('b'*40)}
$hostInfo = @{os='Microsoft Windows 10.0'; framework='.NET 10.0.13'; architecture='X64'; processors=8; configuration='Release'; debuggerAttached=$false}
$large = @{schema=1; completed=$true; timelineOriginQpc=1000000L; host=$hostInfo; gitRuntime=@{Executable='C:\git.exe';Version=$fixture.git}; fixture=$fixture;
    startupInteractiveAfterInitializationMs=800; workingSetBytes=200000000; openFirst500GraphFrameCallbackMs=500; openTotalReadyMs=600;
    openPhases=@{PublishedMs=400;FrameCallbackMs=500;TotalReadyMs=600}; statusListFrameCallbackMs=(Stats @(100,100,100));
    statusPhases=@(@{PublishedMs=90;FrameCallbackMs=100;TotalReadyMs=110},@{PublishedMs=90;FrameCallbackMs=100;TotalReadyMs=110},@{PublishedMs=90;FrameCallbackMs=100;TotalReadyMs=110});
    diffSelectionFrameCallbackMs=(Stats @(100,100,100)); diffSourceFileLines=10000; diffDisplayedPatchLines=1000; loadedCommitsDuringScroll=1000;
    phaseTimeline=@(@{phase='probe-setup';elapsedMs=0},@{phase='open500';elapsedMs=10},@{phase='status1';elapsedMs=100},@{phase='status2';elapsedMs=200},@{phase='status3';elapsedMs=300},@{phase='diff1';elapsedMs=400},@{phase='diff2';elapsedMs=500},@{phase='diff3';elapsedMs=600},@{phase='load-more';elapsedMs=700},@{phase='graph-scroll';elapsedMs=1000},@{phase='probe-report';elapsedMs=6000});
    avaloniaMetrics=@{timestampFrequency=$frequency;uiNativeThreadId=42;samplesInOrder=$metrics};
    scrollNativeFrameIntervalsMs=@{startQpc=$start;endQpc=$end;timestampFrequency=$frequency};
    scrollRendering=@{platformType='Avalonia.Win32.WindowImpl';selectedSurfaceTypes=@('Avalonia.Win32.WinRT.Composition.WinUiCompositionDrawingSurface');platformGraphicsType='Angle';avaloniaErrors=@();batchCompletionsInOrder=$batches;
        evidence=@{StartQpc=$start;EndQpc=$end;TimestampFrequency=$frequency;ElapsedMs=5000.0;RenderPassCount=299;CompletedBatchCount=299;FramesInOrder=$frames;CompletionIntervalsMs=$intervals;RenderPassIntervalsMs=$intervals;SubmittedFramesPerSecond=59.8;
            StartToFirstCompletionMs=($frames[0].CompletedQpc-$start)*1000.0/$frequency;LastCompletionToEndMs=($end-$frames[-1].CompletedQpc)*1000.0/$frequency;
            Gate=@{Pass=$true;ExpectedMinimumFrames=298;ObservedFrames=299;MissedInternalOpportunities=0;NominalFps=60;FractionalRefreshHz=(60000.0/1001);GapLimitMs=25.025;Errors=@()}}}}
$cold = @{schema=1;completed=$true;host=$hostInfo;startupInteractiveAfterInitializationMs=800;workingSetBytes=150000000}
$large.compositionModeRequested='default';$cold.compositionModeRequested='default'
$large.productAssemblies=$productAssemblies;$cold.productAssemblies=$productAssemblies
$large.startupSurfaceInteractiveMs=850; $cold.startupSurfaceInteractiveMs=850
$large.openFirst500GraphSurfaceSubmissionMs=550; $large.openPhases.SurfaceSubmissionMs=550
$large.statusListSurfaceSubmissionMs=Stats @(110,110,110)
foreach($p in $large.statusPhases){$p.SurfaceSubmissionMs=110;$p.TotalReadyMs=120}
$large.diffSelectionSurfaceSubmissionMs=Stats @(110,110,110)
$large.diffPhases=@(@{FrameCallbackMs=100;SurfaceSubmissionMs=110},@{FrameCallbackMs=100;SurfaceSubmissionMs=110},@{FrameCallbackMs=100;SurfaceSubmissionMs=110})
$large.surfaceSubmissionErrors=@();$cold.surfaceSubmissionErrors=@()
$memorySamples=@();for($q=$start-100000L;$q -le $end+150000L;$q+=150000L){$memorySamples+=@{RecordedQpc=$q;WorkingSetBytes=200000000}}
$large.steadyMemory=@{intervalMs=150;startQpc=$start;endQpc=$end;timestampFrequency=$frequency;samplesInOrder=$memorySamples;maximumWorkingSetBytes=200000000}
$cold.avaloniaMetrics=@{timestampFrequency=$frequency;samplesInOrder=@()}
function Surface($report,[long]$started,[double]$elapsedMs) {
    $completed=$started+[long]($elapsedMs*$frequency/1000);$render=$completed-1000
    $report.avaloniaMetrics.samplesInOrder+=@{Instrument='avalonia.comp.render.time';Unit='ms';Value=1.0;RecordedQpc=$render;Phase='synthetic-operation'}
    return @{StartedQpc=$started;RequestedQpc=$completed-2000;SceneUpdatedQpc=$completed-1500;CompletedQpc=$completed;RenderPassQpc=$render;RenderDurationMs=1.0;CompletedSuccessfully=$true;RepaintTargetType='Avalonia.Rendering.Composition.CompositionCustomVisual';
        ProbeInkMarker=@{X=10.0;Y=10.0;Width=1.0;Height=1.0;devicePixels=1;renderedQpc=$completed-1200}}
}
$large.startupSurfaceSubmission=Surface $large 100000 850
$cold.startupSurfaceSubmission=Surface $cold 100000 850
$large.openPhases.SurfaceSubmission=Surface $large 1010000 550
for($i=0;$i -lt 3;$i++){
    $large.statusPhases[$i].SurfaceSubmission=Surface $large (1100000+$i*130000) 110
    $large.diffPhases[$i].SurfaceSubmission=Surface $large (1500000+$i*130000) 110
}
# Use the shipping probe's actual subphase vocabulary, rather than bare operation names.
$timeline=@(@{phase='probe-setup';elapsedMs=0})
foreach($operation in @(@('open500',10),@('status1',100),@('status2',200),@('status3',300))) {
    $step=0
    foreach($suffix in @('wait-publication','published-await-bindings','layout','await-native-frame','remainder','complete')) {
        $timeline+=@{phase="$($operation[0]).$suffix";elapsedMs=$operation[1]+5*$step++}
    }
}
foreach($operation in @(@('diff1',400),@('diff2',500),@('diff3',600))) {
    $step=0
    foreach($suffix in @('clear-selection','read','data-ready-await-bindings','layout','await-native-frame','complete')) {
        $timeline+=@{phase="$($operation[0]).$suffix";elapsedMs=$operation[1]+5*$step++}
    }
}
$timeline+=@(@{phase='load-more';elapsedMs=700},@{phase='graph-scroll';elapsedMs=1000},@{phase='probe-report';elapsedMs=6000})
$large.phaseTimeline=$timeline
$rows = @()
for ($i=1;$i -le 3;$i++) { foreach($mode in @('cold','large')) {
    $file="$mode-$i.json"; $path=Join-Path $directory $file
    WriteJson $path $(if($mode -eq 'large'){$large}else{$cold})
    $rows += @{mode=$mode;sample=$i;file=$file;sha256=(Hash $path);startupMs=800;workingSetBytes=$(if($mode -eq 'large'){200000000}else{150000000})}
}}
$summary=@{schema=1;appSHA256=(Hash $app);renderer='default';samples=3;reports=$rows}
$summaryPath=Join-Path $directory 'summary.json'; WriteJson $summaryPath $summary
$probePath=Join-Path $directory 'profile-probe.json'; WriteJson $probePath $large
$patches=@(@{method='Avalonia.Threading.DispatcherOperation.Execute';module=(Join-Path $directory 'Avalonia.Base.dll');sha256=(Hash (Join-Path $directory 'Avalonia.Base.dll'));managedNativeHeaderSize=72},
    @{method='Avalonia.Threading.Dispatcher.Invoke';module=(Join-Path $directory 'Avalonia.Base.dll');sha256=(Hash (Join-Path $directory 'Avalonia.Base.dll'));managedNativeHeaderSize=72},
    @{method='Avalonia.Win32.WindowImpl.AppWndProc';module=(Join-Path $directory 'Avalonia.Win32.dll');sha256=(Hash (Join-Path $directory 'Avalonia.Win32.dll'));managedNativeHeaderSize=72})
$spans=@(); $id=0
foreach($phase in $large.phaseTimeline | Where-Object {$_.phase -notin @('probe-setup','probe-report')}) {
    $qpc=1000000L+[long]($phase.elapsedMs*1000)+100
    foreach($seam in @('DispatcherOperation.Execute','Dispatcher.Invoke UI Send','WindowImpl.AppWndProc')) {
        $spans+=@{Id=++$id;Parent=0;Seam=$seam;Callback='Synthetic.Callback';EnterQpc=$qpc;ExitQpc=$qpc+1000;NativeTid=42;Completed=$true;ProductPhaseAtEnter=$phase.phase;ProductPhaseAtExit=$phase.phase}
    }
}
$profile=@{schema=1;runtime=$hostInfo.framework;targetProcessId=1234;targetExecutableSha256=(Hash $app);targetAppAssemblySha256=(Hash (Join-Path $directory 'GitGui.App.dll'));hookAssemblySha256=(Hash $hook);desktopProbeReportPath=$probePath;desktopProbeReportSha256=(Hash $probePath);
    timestampFrequency=$frequency;originQpc=900000L;uiNativeTid=42;totalEntered=$spans.Count;overflow=0;patches=$patches;productPhasesCorrelated=$true;layoutActivitiesEnabled=$false;
    canary=@{started=$false;finished=$false;failure=$null};gate=@{allSpansComplete=$true;targetMs=16};spans=$spans}
$profilePath=Join-Path $directory 'dispatcher-profile.json'; WriteJson $profilePath $profile
$originalSummary=Get-Content $summaryPath -Raw; $originalLarge=Get-Content (Join-Path $directory 'large-1.json') -Raw
$originalProfile=Get-Content $profilePath -Raw; $originalProbe=Get-Content $probePath -Raw
$cases=0
function RunCase([string]$name,[bool]$expected,[scriptblock]$change,[switch]$NoProfiler) {
    if($name -notlike $Scenario){return}
    $script:cases++
    $s=$originalSummary|ConvertFrom-Json -AsHashtable; $r=$originalLarge|ConvertFrom-Json -AsHashtable
    $p=$originalProfile|ConvertFrom-Json -AsHashtable; $pr=$originalProbe|ConvertFrom-Json -AsHashtable
    & $change $s $r $p $pr
    WriteJson (Join-Path $directory 'large-1.json') $r
    if ($name -ne 'hash mismatch' -and $s.Contains('reports')) { $s.reports[1].sha256=Hash (Join-Path $directory 'large-1.json') }
    WriteJson $summaryPath $s; WriteJson $probePath $pr
    if ($name -ne 'profile hash mismatch') { $p.desktopProbeReportSha256=Hash $probePath }
    WriteJson $profilePath $p
    $output=Join-Path $directory ("decision-$cases.json");$thrown=$false
    $arguments=@{Summary=$summaryPath;AppPath=$app;OutputPath=$output}
    if(-not $NoProfiler){$arguments.ProfileReports=@($profilePath);$arguments.ProfileProbeReports=@($probePath);$arguments.ProfilerHookPath=$hook}
    try{$null=& $assert @arguments}catch{$thrown=$true}
    if(-not (Test-Path -LiteralPath $output)){throw "No decision JSON for '$name'"}
    $decision=Get-Content $output -Raw|ConvertFrom-Json -AsHashtable
    if($decision.pass -ne $expected -or $thrown -eq $expected){throw "Incorrect decision for '$name': $($decision|ConvertTo-Json -Depth 6 -Compress)"}
    if($NoProfiler -and $decision.uiBlocking.status -ne 'unverified'){throw 'Missing profiler must remain unverified'}
}
RunCase 'valid complete evidence' $true {}
RunCase 'unexpected native identity on ordinary candidate' $false {param($s,$r) $r.nativeHostIdentity=@{bundleSha256=$s.appSHA256}}
RunCase 'explicit software framebuffer evidence' $true {param($s,$r)
    $r.scrollRendering.platformGraphicsType='Software (no platform graphics)'
    $r.scrollRendering.selectedSurfaceTypes=@('Avalonia.Win32.FramebufferManager')
    foreach($batch in $r.scrollRendering.batchCompletionsInOrder){$batch.ActiveGpuContextType='Software (no GPU context)'}
}
RunCase 'software declaration without framebuffer' $false {param($s,$r)
    $r.scrollRendering.platformGraphicsType='Software (no platform graphics)'
}
RunCase 'software declaration with GPU context' $false {param($s,$r)
    $r.scrollRendering.platformGraphicsType='Software (no platform graphics)'
    $r.scrollRendering.selectedSurfaceTypes=@('Avalonia.Win32.FramebufferManager')
}
RunCase 'software context inspection error' $false {param($s,$r)
    $r.scrollRendering.platformGraphicsType='Software (no platform graphics)'
    $r.scrollRendering.selectedSurfaceTypes=@('Avalonia.Win32.FramebufferManager')
    foreach($batch in $r.scrollRendering.batchCompletionsInOrder){$batch.ActiveGpuContextType='Software (no GPU context)';$batch.InspectionError='missing inspection'}
}
RunCase 'composition experiment cannot replace normal product' $false {param($s,$r) $r.compositionModeRequested='direct'}
RunCase 'missing composition selection' $false {param($s,$r) $r.Remove('compositionModeRequested')}
RunCase 'missing profiler' $false {} -NoProfiler
RunCase 'missing field' $false {param($s,$r) $r.Remove('openFirst500GraphFrameCallbackMs')}
RunCase 'single status outlier' $false {param($s,$r) $r.statusListFrameCallbackMs.values=@(100,100,301)}
RunCase 'single diff outlier' $false {param($s,$r) $r.diffSelectionFrameCallbackMs.values=@(100,100,201)}
RunCase 'startup outlier' $false {param($s,$r) $r.startupInteractiveAfterInitializationMs=1501}
RunCase 'memory outlier' $false {param($s,$r) $r.workingSetBytes=300000001}
RunCase 'NaN' $false {param($s,$r) $r.diffSelectionFrameCallbackMs.values=@(100,100,'NaN')}
RunCase 'hash mismatch' $false {param($s,$r) $r.workingSetBytes=199999999}
RunCase 'exact count' $false {param($s) $s.reports=$s.reports[0..4]}
RunCase 'duplicate sample' $false {param($s) $s.reports[1].sample=2}
RunCase 'fixture mismatch' $false {param($s,$r) $r.fixture.fingerprint='c'*32}
RunCase 'host mismatch' $false {param($s,$r) $r.host.processors=4}
RunCase 'render errors' $false {param($s,$r) $r.scrollRendering.avaloniaErrors=@(@{RecordedQpc=3000000;Message='renderer failed'})}
RunCase 'forged pass despite dropped frame' $false {param($s,$r) $r.scrollRendering.evidence.FramesInOrder[100].CompletedQpc+=20000}
RunCase 'old report' $false {param($s,$r) $r.Remove('scrollRendering')}
RunCase 'profile hash mismatch' $false {param($s,$r,$p) $p.desktopProbeReportSha256='0'*64}
RunCase 'UI job outlier' $false {param($s,$r,$p) $p.spans[0].ExitQpc=$p.spans[0].EnterQpc+17000;$p.spans[0].ProductPhaseAtExit='open500.await-native-frame'}
RunCase 'heavy layout profiler' $false {param($s,$r,$p) $p.layoutActivitiesEnabled=$true}
RunCase 'incomplete UI span' $false {param($s,$r,$p) $p.spans[0].Completed=$false}
RunCase 'uncorrelated UI phase' $false {param($s,$r,$p) $p.spans[0].ProductPhaseAtEnter='graph-scroll'}
RunCase 'missing product assembly hash' $false {param($s,$r) $r.productAssemblies['GitGui.Core'].Remove('sha256')}
RunCase 'tampered product assembly hash' $false {param($s,$r) $r.productAssemblies['GitGui.Engine.Cli'].sha256='0'*64}
RunCase 'surface startup outlier despite fast RAF' $false {param($s,$r) $r.startupSurfaceInteractiveMs=1501}
RunCase 'surface first500 outlier despite fast RAF' $false {param($s,$r) $r.openFirst500GraphSurfaceSubmissionMs=1001;$r.openPhases.SurfaceSubmissionMs=1001;$r.openPhases.TotalReadyMs=1100;$r.openTotalReadyMs=1100}
RunCase 'surface status outlier despite fast RAF' $false {param($s,$r) $r.statusListSurfaceSubmissionMs.values=@(110,110,301)}
RunCase 'surface diff outlier despite fast RAF' $false {param($s,$r) $r.diffSelectionSurfaceSubmissionMs.values=@(110,110,201)}
RunCase 'missing canonical surface evidence' $false {param($s,$r) $r.Remove('openFirst500GraphSurfaceSubmissionMs')}
RunCase 'surface renderer error' $false {param($s,$r) $r.surfaceSubmissionErrors=@(@{RecordedQpc=1100000L;Message='EndDraw failed'})}
RunCase 'surface batch lacks real render' $false {param($s,$r) $r.openPhases.SurfaceSubmission.RenderPassQpc+=100}
RunCase 'surface batch lacks scene barrier' $false {param($s,$r) $r.openPhases.SurfaceSubmission.Remove('SceneUpdatedQpc')}
RunCase 'surface marker missing' $false {param($s,$r) $r.openPhases.SurfaceSubmission.Remove('ProbeInkMarker')}
RunCase 'surface marker outside scene batch' $false {param($s,$r) $r.openPhases.SurfaceSubmission.ProbeInkMarker.renderedQpc=$r.openPhases.SurfaceSubmission.SceneUpdatedQpc-1}
RunCase 'steady memory single outlier' $false {param($s,$r) $r.steadyMemory.samplesInOrder[12].WorkingSetBytes=300000001;$r.steadyMemory.maximumWorkingSetBytes=300000001}
RunCase 'missing steady memory' $false {param($s,$r) $r.Remove('steadyMemory')}
RunCase 'memory does not cover end of scroll' $false {param($s,$r) $r.steadyMemory.samplesInOrder=$r.steadyMemory.samplesInOrder[0..20]}
RunCase 'memory sampling gap exceeds coverage protocol' $false {param($s,$r) $r.steadyMemory.samplesInOrder=@($r.steadyMemory.samplesInOrder[0..10]) + @($r.steadyMemory.samplesInOrder[12..($r.steadyMemory.samplesInOrder.Count-1)])}
RunCase 'missing reports array still writes verdict' $false {param($s) $s.Remove('reports')}
RunCase 'malformed row still writes verdict' $false {param($s) $s.reports[2]='invalid-row'}
RunCase 'long report-only span is diagnostic' $true {param($s,$r,$p)
    $p.spans[0].EnterQpc=7010000L;$p.spans[0].ExitQpc=7110000L
    $p.spans[0].ProductPhaseAtEnter='probe-report';$p.spans[0].ProductPhaseAtExit='probe-report'
}
RunCase 'cross report boundary has ten product ms' $true {param($s,$r,$p)
    $p.spans[0].EnterQpc=6990000L;$p.spans[0].ExitQpc=7050000L
    $p.spans[0].ProductPhaseAtEnter='graph-scroll';$p.spans[0].ProductPhaseAtExit='probe-report'
}
RunCase 'cross report boundary has seventeen product ms' $false {param($s,$r,$p)
    $p.spans[0].EnterQpc=6983000L;$p.spans[0].ExitQpc=7050000L
    $p.spans[0].ProductPhaseAtEnter='graph-scroll';$p.spans[0].ProductPhaseAtExit='probe-report'
}
foreach($offset in @(20000L,4680000L)) {
    RunCase "burst with silent endpoint $offset" $false {param($s,$r)
        for($i=0;$i -lt 299;$i++) {
            $q=$start+$offset+$i*1000L
            $r.avaloniaMetrics.samplesInOrder[$i].RecordedQpc=$q
            $r.scrollRendering.evidence.FramesInOrder[$i].RenderPassQpc=$q
            $r.scrollRendering.evidence.FramesInOrder[$i].CompletedQpc=$q+100
            $r.scrollRendering.batchCompletionsInOrder[$i].RequestedQpc=$q-2000
            $r.scrollRendering.batchCompletionsInOrder[$i].CompletedQpc=$q+100
        }
        $r.scrollRendering.evidence.CompletionIntervalsMs=@(1.0)*298
        $r.scrollRendering.evidence.RenderPassIntervalsMs=@(1.0)*298
        $r.scrollRendering.evidence.StartToFirstCompletionMs=($r.scrollRendering.evidence.FramesInOrder[0].CompletedQpc-$start)*1000.0/$frequency
        $r.scrollRendering.evidence.LastCompletionToEndMs=($end-$r.scrollRendering.evidence.FramesInOrder[-1].CompletedQpc)*1000.0/$frequency
    }
}
"PASS: $cases acceptance scenarios; evidence preserved at $directory"
