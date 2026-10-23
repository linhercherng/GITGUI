<#
Offline acceptance of native Windows measurements. Every raw sample is a gate;
summary medians and RAF/heartbeat counters are never substitutes for evidence.
Profiler runs are diagnostic companions with the same binaries/fixture/host,
not startup or resident-memory baselines. All inputs remain immutable.
#>
param(
    [Parameter(Mandatory)][string]$Summary,
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string[]]$ProfileReports = @(),
    [string[]]$ProfileProbeReports = @(),
    [string]$ProfilerHookPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = [Collections.Generic.List[object]]::new()
$inputs = [Collections.Generic.List[object]]::new()
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'OutputPath must be new; existing evidence is preserved' }
function Check([string]$scope, [string]$metric, [scriptblock]$body) {
    try {
        $observed = & $body
        $checks.Add([ordered]@{scope=$scope;metric=$metric;status='pass';observed=$observed})
    } catch {
        $checks.Add([ordered]@{scope=$scope;metric=$metric;status='fail';reason=$_.Exception.Message})
    }
}
function Require([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Number($value) {
    Require ($null -ne $value -and $value -is [ValueType] -and $value -isnot [bool]) 'Missing or nonnumeric value'
    $n = [double]$value
    Require ([double]::IsFinite($n)) 'Nonfinite value'
    return $n
}
function Integer($value, [long]$minimum=0) {
    $n = Number $value
    Require ($n -ge $minimum -and $n -eq [Math]::Floor($n) -and $n -le 9007199254740991) 'Invalid exact integer'
    return [long]$n
}
function Limit($value,[double]$maximum) {
    $n = Number $value; Require ($n -ge 0 -and $n -le $maximum) "Observed $n exceeds 0..$maximum"
    return $n
}
function SameNumber($actual,$expected,[double]$tolerance=0.000001) {
    $a=Number $actual; $e=Number $expected
    Require ([Math]::Abs($a-$e) -le $tolerance) "Number mismatch: $a versus $e"
}
function True($value,[string]$message) { Require ($value -is [bool] -and $value) $message }
function False($value,[string]$message) { Require ($value -is [bool] -and -not $value) $message }
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function HashEquals($declared,[string]$path) {
    Require ($declared -is [string] -and $declared -match '^[a-fA-F0-9]{64}$') 'Missing or invalid SHA256'
    Require ($declared -eq (Hash $path)) "SHA256 mismatch: $path"
}
function ReadReport([string]$path) {
    $full=[IO.Path]::GetFullPath($path)
    $bytes=[IO.File]::ReadAllBytes($full)
    $inputs.Add(@{path=$full;sha256=([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))).ToLowerInvariant()})
    $json=[Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable -Depth 100
    Require ($json -is [Collections.IDictionary]) 'JSON root must be an object'
    return $json
}
function HostValid($report) {
    Require ($report.Contains('compositionModeRequested') -and $report.compositionModeRequested -ceq 'default') 'Formal acceptance requires the normal product composition selection; experiments are diagnostic only'
    $h=$report.host
    Require ($h -is [Collections.IDictionary]) 'Missing host'
    Require ($h.os -is [string] -and $h.os -match 'Windows') 'Requires native Windows host'
    Require ($h.framework -is [string] -and $h.framework -match '^\.NET 10\.') 'Requires shipping .NET10 runtime'
    Require ($h.architecture -ceq 'X64' -and $h.configuration -ceq 'Release') 'Requires x64 Release'
    SameNumber $h.processors 8
    False $h.debuggerAttached 'Debugger must not be attached'
    if ($null -ne $script:referenceHost) {
        foreach($field in @('os','framework','architecture','processors','configuration','debuggerAttached')) {
            Require ($h[$field] -ceq $script:referenceHost[$field]) "Different measurement host: $field"
        }
    } else { $script:referenceHost=$h }
}
function ProductAssembliesValid($report) {
    $names=@('GitGui.App','GitGui.Core','GitGui.Controls','GitGui.Engine.Cli','GitGui.Engine.Lib','GitGui.Infrastructure','GitGui.UI')
    if($null -eq $script:candidateProducts) {
        $actual=@{}
        foreach($name in $names) {
            $file="$name.dll";$path=Join-Path $appDirectory $file
            $stream=[IO.File]::OpenRead($path)
            try {
                $pe=[Reflection.PortableExecutable.PEReader]::new($stream)
                try { $metadata=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe);$mvid=$metadata.GetGuid($metadata.GetModuleDefinition().Mvid).ToString('D') }
                finally { $pe.Dispose() }
            } finally { $stream.Dispose() }
            $actual[$name]=@{fileName=$file;sha256=(Hash $path);mvid=$mvid;version=[Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString()}
        }
        $script:candidateProducts=$actual
    }
    $declared=$report.productAssemblies
    Require ($declared -is [Collections.IDictionary] -and $declared.Count -eq 7) 'Missing exact seven loaded product assembly identities'
    foreach($name in $names) {
        $entry=$declared[$name];Require ($entry -is [Collections.IDictionary]) "Missing assembly identity: $name"
        Require ($entry.sha256 -is [string] -and $entry.sha256 -match '^[a-fA-F0-9]{64}$') "Missing/invalid assembly SHA256: $name"
        foreach($field in @('fileName','sha256','mvid','version')) {
            Require ($entry[$field] -is [string] -and $entry[$field] -ceq $candidateProducts[$name][$field]) "Candidate assembly identity mismatch: $name/$field"
            if($null -ne $script:referenceProducts) { Require ($entry[$field] -ceq $script:referenceProducts[$name][$field]) "Cross-run assembly identity mismatch: $name/$field" }
        }
    }
    if($null -eq $script:referenceProducts){$script:referenceProducts=$declared}
}
function FixtureValid($report) {
    $f=$report.fixture
    Require ($f -is [Collections.IDictionary]) 'Missing fixture'
    foreach($pair in @(@('schema',1),@('protocol',2),@('commits',100000),@('files',50000),@('diffLines',10000),@('changedLines',100))) {
        SameNumber $f[$pair[0]] $pair[1]
    }
    foreach($field in @('fsmonitor','fsmonitorPrimed','windowsFileSystemCache')) { True $f[$field] "Fixture $field must be true" }
    Require ($f.fingerprint -is [string] -and $f.fingerprint -match '^[a-fA-F0-9]{32}$') 'Invalid fixture fingerprint'
    Require ($f.head -is [string] -and $f.head -match '^(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})$') 'Invalid fixture HEAD'
    Require (-not [string]::IsNullOrWhiteSpace($f.root)) 'Missing fixture root'
    Require ($f.git -is [string] -and $f.git -match '^git version ') 'Missing fixture Git version'
    Require ($report.gitRuntime.Version -ceq $f.git) 'Fixture/runtime Git mismatch'
    if($null -ne $script:referenceFixture) {
        foreach($field in @('schema','protocol','root','commits','files','diffLines','changedLines','git','fsmonitor','fsmonitorPrimed','windowsFileSystemCache','fingerprint','head')) {
            Require ($f[$field] -ceq $script:referenceFixture[$field]) "Different fixture: $field"
        }
    } else { $script:referenceFixture=$f }
}
function Samples($stats,[double]$maximum) {
    Require ($stats -is [Collections.IDictionary]) 'Missing sample statistics'
    SameNumber $stats.samples 3
    Require ($stats.values -is [array] -and $stats.values.Count -eq 3) 'Exactly three operation samples required'
    foreach($value in $stats.values) { $null=Limit $value $maximum }
    return ,@($stats.values)
}
function SurfaceValid($e,$report,$elapsedMs) {
    Require ($e -is [Collections.IDictionary]) 'Missing canonical rendered surface submission evidence'
    Require ($report.surfaceSubmissionErrors -is [array] -and $report.surfaceSubmissionErrors.Count -eq 0) 'Renderer/session errors or missing global error capture'
    $frequency=Integer $report.avaloniaMetrics.timestampFrequency 1
    $started=Integer $e.StartedQpc 1;$requested=Integer $e.RequestedQpc 1;$updated=Integer $e.SceneUpdatedQpc 1;$completed=Integer $e.CompletedQpc 1;$render=Integer $e.RenderPassQpc 1
    True $e.CompletedSuccessfully 'Surface batch did not complete'
    Require ($started -le $requested -and $requested -le $updated -and $updated -le $render -and $render -le $completed) 'Surface operation/request/scene/render/completion order invalid'
    Require (-not [string]::IsNullOrWhiteSpace($e.RepaintTargetType)) 'Missing existing visible repaint target identity'
    SameNumber $elapsedMs (($completed-$started)*1000.0/$frequency) 0.001
    $passes=@($report.avaloniaMetrics.samplesInOrder|Where-Object {$_.Instrument -ceq 'avalonia.comp.render.time' -and $_.RecordedQpc -eq $render})
    Require ($passes.Count -eq 1 -and $passes[0].Unit -ceq 'ms') 'Surface batch missing unique genuine render histogram'
    $null=Limit $passes[0].Value ([double]::MaxValue);SameNumber $e.RenderDurationMs $passes[0].Value
}
function MemoryValid($report) {
    $m=$report.steadyMemory;$e=$report.scrollRendering.evidence
    Require ($m -is [Collections.IDictionary]) 'Missing steady-window worker memory samples'
    $interval=Number $m.intervalMs;Require ($interval -ge 100 -and $interval -le 200) 'Memory worker interval must be 100..200ms'
    $frequency=Integer $m.timestampFrequency 1;SameNumber $frequency $e.TimestampFrequency
    $start=Integer $m.startQpc 1;$end=Integer $m.endQpc 1;SameNumber $start $e.StartQpc;SameNumber $end $e.EndQpc
    Require ($m.samplesInOrder -is [array] -and $m.samplesInOrder.Count -ge 2) 'Missing memory samples'
    $previous=$null;$maximum=0.0;$largestGap=0.0
    foreach($sample in $m.samplesInOrder) {
        $qpc=Integer $sample.RecordedQpc 1;$bytes=Limit $sample.WorkingSetBytes 300000000;Require ($bytes -gt 0) 'Invalid zero working set'
        $maximum=[Math]::Max($maximum,$bytes)
        if($null -ne $previous){Require ($qpc -gt $previous) 'Memory samples are not ordered';$gap=($qpc-$previous)*1000.0/$frequency;$largestGap=[Math]::Max($largestGap,$gap);Require ($gap -le 200) "Memory sampling gap $gap ms exceeds 200ms coverage protocol"}
        $previous=$qpc
    }
    Require ($m.samplesInOrder[0].RecordedQpc -le $start -and $m.samplesInOrder[-1].RecordedQpc -ge $end) 'Memory samples do not bracket the entire exact scroll interval'
    SameNumber $m.maximumWorkingSetBytes $maximum
    return @{samples=$m.samplesInOrder.Count;maximumWorkingSetBytes=$maximum;largestSamplingGapMs=$largestGap}
}
function ScrollValid($report) {
    $render=$report.scrollRendering; $e=$render.evidence
    Require ($e -is [Collections.IDictionary]) 'Missing genuine render/surface-submission evidence (old RAF-only reports are insufficient)'
    $frequency=Integer $e.TimestampFrequency 1; $start=Integer $e.StartQpc 1; $end=Integer $e.EndQpc 1
    Require ($end -gt $start) 'Invalid exact scroll range'
    $duration=($end-$start)*1000.0/$frequency; Require ($duration -ge 5000) 'Scroll shorter than five seconds'
    SameNumber $e.ElapsedMs $duration
    SameNumber $report.avaloniaMetrics.timestampFrequency $frequency
    SameNumber $report.scrollNativeFrameIntervalsMs.timestampFrequency $frequency
    SameNumber $report.scrollNativeFrameIntervalsMs.startQpc $start
    SameNumber $report.scrollNativeFrameIntervalsMs.endQpc $end
    Require (-not [string]::IsNullOrWhiteSpace($render.platformType)) 'Missing actual native platform'
    Require ($render.selectedSurfaceTypes -is [array] -and $render.selectedSurfaceTypes.Count -gt 0) 'Missing native surface identity'
    Require (-not [string]::IsNullOrWhiteSpace($render.platformGraphicsType)) 'Missing platform graphics identity'
    Require ($render.avaloniaErrors -is [array] -and $render.avaloniaErrors.Count -eq 0) 'Renderer errors or missing error capture'
    $passes=@($report.avaloniaMetrics.samplesInOrder | Where-Object {$_.Instrument -ceq 'avalonia.comp.render.time' -and $_.RecordedQpc -ge $start -and $_.RecordedQpc -lt $end} | Sort-Object RecordedQpc)
    Require ($passes.Count -gt 1) 'No real render histograms in exact scroll interval'
    $passByTime=@{}
    foreach($p in $passes) {
        $q=Integer $p.RecordedQpc 1; Require ($p.Unit -ceq 'ms') 'Wrong render histogram unit'
        $null=Limit $p.Value ([double]::MaxValue)
        Require (-not $passByTime.ContainsKey($q)) 'Duplicate render timestamp'
        $passByTime[$q]=$p
    }
    $batchById=@{}
    Require ($render.batchCompletionsInOrder -is [array] -and $render.batchCompletionsInOrder.Count -gt 0) 'Missing CompositionBatch completion evidence'
    foreach($b in $render.batchCompletionsInOrder) {
        $id=Integer $b.BatchId 1; $requested=Integer $b.RequestedQpc 1; $completed=Integer $b.CompletedQpc 1
        True $b.CompletedSuccessfully 'A batch did not complete successfully'
        Require ($completed -ge $requested) 'Batch completed before request'
        # Duplicate public notifications do not establish extra frames. Keep the earliest notification, as the probe does.
        if(-not $batchById.ContainsKey($id) -or $completed -lt $batchById[$id].CompletedQpc) { $batchById[$id]=$b }
    }
    Require ($e.FramesInOrder -is [array] -and $e.FramesInOrder.Count -gt 1) 'Missing distinct submitted frames'
    SameNumber $e.RenderPassCount $passes.Count; SameNumber $e.CompletedBatchCount $batchById.Count
    $usedPass=@{}; $usedBatch=@{}; $previous=$null; $intervals=[Collections.Generic.List[double]]::new()
    foreach($frame in $e.FramesInOrder) {
        $q=Integer $frame.RenderPassQpc 1; $c=Integer $frame.CompletedQpc 1; $id=Integer $frame.BatchId 1
        Require ($passByTime.ContainsKey($q) -and $batchById.ContainsKey($id)) 'Frame has no matching actual render and batch'
        Require (-not $usedPass.ContainsKey($q) -and -not $usedBatch.ContainsKey($id)) 'Duplicate frame attribution'
        $usedPass[$q]=$true; $usedBatch[$id]=$true; $batch=$batchById[$id]
        Require ($c -ge $start -and $c -lt $end -and $q -ge $batch.RequestedQpc -and $q -le $c) 'Frame outside exact interval or request/render/completion order'
        SameNumber $c $batch.CompletedQpc; SameNumber $frame.RenderDurationMs $passByTime[$q].Value
        if($null -ne $previous) { Require ($c -gt $previous) 'Frame completions are not strictly ordered'; $intervals.Add(($c-$previous)*1000.0/$frequency) }
        $previous=$c
    }
    $fractional=60000.0/1001; $gapLimit=1.5*1000/$fractional
    $startGap=($e.FramesInOrder[0].CompletedQpc-$start)*1000.0/$frequency
    $endGap=($end-$e.FramesInOrder[-1].CompletedQpc)*1000.0/$frequency
    SameNumber $e.StartToFirstCompletionMs $startGap; SameNumber $e.LastCompletionToEndMs $endGap
    Require ($startGap -lt $gapLimit -and $endGap -lt $gapLimit) "Silent scroll endpoint exceeds $gapLimit ms: start=$startGap, end=$endGap"
    $expected=[Math]::Max(0,[Math]::Floor($duration*$fractional/1000)-1)
    Require ($e.FramesInOrder.Count -ge $expected) "Insufficient submitted frames: $($e.FramesInOrder.Count) < $expected"
    Require ($e.CompletionIntervalsMs -is [array] -and $e.CompletionIntervalsMs.Count -eq $intervals.Count) 'Incomplete completion interval array'
    for($i=0;$i -lt $intervals.Count;$i++) {
        SameNumber $e.CompletionIntervalsMs[$i] $intervals[$i]
        Require ($intervals[$i] -lt $gapLimit) "Internal missed presentation opportunity: $($intervals[$i])ms >= $gapLimit"
    }
    Require ($e.RenderPassIntervalsMs -is [array] -and $e.RenderPassIntervalsMs.Count -eq $passes.Count-1) 'Incomplete render interval array'
    for($i=1;$i -lt $passes.Count;$i++) { SameNumber $e.RenderPassIntervalsMs[$i-1] (($passes[$i].RecordedQpc-$passes[$i-1].RecordedQpc)*1000.0/$frequency) }
    SameNumber $e.SubmittedFramesPerSecond ($e.FramesInOrder.Count*1000.0/$duration)
    True $e.Gate.Pass 'Probe render gate failed'
    Require ($e.Gate.Errors -is [array] -and $e.Gate.Errors.Count -eq 0) 'Render gate errors or missing errors array'
    SameNumber $e.Gate.ExpectedMinimumFrames $expected; SameNumber $e.Gate.ObservedFrames $e.FramesInOrder.Count
    SameNumber $e.Gate.MissedInternalOpportunities 0; SameNumber $e.Gate.NominalFps 60
    SameNumber $e.Gate.FractionalRefreshHz $fractional; SameNumber $e.Gate.GapLimitMs $gapLimit
    return @{frames=$e.FramesInOrder.Count;durationMs=$duration;largestInternalGapMs=($intervals|Measure-Object -Maximum).Maximum;startEndpointGapMs=$startGap;endEndpointGapMs=$endGap;expectedMinimum=$expected}
}
$referenceHost=$null; $referenceFixture=$null; $referenceProducts=$null; $candidateProducts=$null; $summaryData=$null
$app=[IO.Path]::GetFullPath($AppPath); $appDirectory=[IO.Path]::GetDirectoryName($app)
Check summary 'identity-and-counts' {
    $script:summaryData=ReadReport $Summary; $s=$summaryData
    SameNumber $s.schema 1; HashEquals $s.appSHA256 $app
    Require ($s.renderer -ceq 'default') 'Formal acceptance requires default native renderer'
    $samples=Integer $s.samples 3; Require ($samples -le 15) 'Invalid sample count'
    Require ($s.reports -is [array] -and $s.reports.Count -eq 2*$samples) 'Missing or extra raw report rows'
    $seen=@{}; $files=@{}
    foreach($row in $s.reports) {
        Require ($row.mode -cin @('cold','large')) 'Invalid mode'
        $i=Integer $row.sample 1; Require ($i -le $samples) 'Sample outside declared range'
        $key="$($row.mode)/$i"; Require (-not $seen.ContainsKey($key)) 'Duplicate mode/sample'; $seen[$key]=$true
        Require ($row.file -is [string] -and $row.file -eq [IO.Path]::GetFileName($row.file) -and $row.file -notmatch '[/\\:]') 'Raw report must be a sibling filename'
        Require (-not $files.ContainsKey($row.file)) 'Duplicate raw report filename'; $files[$row.file]=$true
    }
    return @{samples=$samples;rawReports=$s.reports.Count;appSHA256=$s.appSHA256}
}
if($null -ne $summaryData -and $summaryData.Contains('reports') -and $summaryData['reports'] -is [array]) {
    $baseDirectory=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Summary))
    foreach($row in $summaryData.reports) {
        if($row -isnot [Collections.IDictionary]) { Check raw 'row-format' { throw 'Raw row must be an object' }; continue }
        $scope="raw:$($row['mode']):$($row['sample'])"; $raw=$null
        Check $scope 'integrity-and-completion' {
            Require ($row.file -is [string] -and $row.file -eq [IO.Path]::GetFileName($row.file) -and $row.file -notmatch '[/\\:]') 'Unsafe raw report filename'
            $path=Join-Path $baseDirectory $row.file; HashEquals $row.sha256 $path
            $script:raw=ReadReport $path; SameNumber $raw.schema 1; True $raw.completed 'Incomplete raw report'
            HostValid $raw
            ProductAssembliesValid $raw
            return $row.sha256
        }
        if($null -eq $raw) { continue }
        Check $scope 'startup-surface-ms<=1500' {
            $n=Limit $raw.startupSurfaceInteractiveMs 1500
            Require ((Number $raw.startupInteractiveAfterInitializationMs) -ge 0 -and $raw.startupInteractiveAfterInitializationMs -le $n) 'Surface startup predates initialization/RAF diagnostic'
            SurfaceValid $raw.startupSurfaceSubmission $raw $n;return $n
        }
        Check $scope 'resident-bytes<=300000000' { Limit $raw.workingSetBytes 300000000 }
        if($row['mode'] -ceq 'large') {
            Check $scope 'fixture-consistency' { FixtureValid $raw; return $raw.fixture.fingerprint }
            Check $scope 'first500-surface-ms<=1000' {
                $n=Limit $raw.openFirst500GraphSurfaceSubmissionMs 1000
                SameNumber $raw.openPhases.SurfaceSubmissionMs $n
                SameNumber $raw.openPhases.FrameCallbackMs $raw.openFirst500GraphFrameCallbackMs
                Require ((Number $raw.openPhases.PublishedMs) -ge 0 -and $raw.openPhases.PublishedMs -le $raw.openPhases.FrameCallbackMs -and $raw.openPhases.FrameCallbackMs -le $n -and (Number $raw.openPhases.TotalReadyMs) -ge $n) 'Invalid publication/RAF/surface/ready ordering'
                SurfaceValid $raw.openPhases.SurfaceSubmission $raw $n
                SameNumber $raw.openTotalReadyMs $raw.openPhases.TotalReadyMs
                Require ((Integer $raw.loadedCommitsDuringScroll 1000)%500 -eq 0) 'Invalid paged history size'
                return $n
            }
            Check $scope 'all-status-surface-ms<=300' {
                $values=Samples $raw.statusListSurfaceSubmissionMs 300
                $diagnostics=Samples $raw.statusListFrameCallbackMs ([double]::MaxValue)
                Require ($raw.statusPhases -is [array] -and $raw.statusPhases.Count -eq 3) 'Missing raw status phases'
                $sorted=@($raw.statusPhases | ForEach-Object { $null=Limit $_.PublishedMs 300; Require ($_.PublishedMs -le $_.FrameCallbackMs -and $_.FrameCallbackMs -le (Number $_.SurfaceSubmissionMs) -and $_.SurfaceSubmissionMs -le (Number $_.TotalReadyMs)) 'Invalid status publication/RAF/surface/ready ordering';SurfaceValid $_.SurfaceSubmission $raw $_.SurfaceSubmissionMs; $_.SurfaceSubmissionMs } | Sort-Object)
                $sortedValues=@($values|Sort-Object);for($i=0;$i -lt 3;$i++){SameNumber $sorted[$i] $sortedValues[$i]}
                $sortedDiagnostics=@($diagnostics|Sort-Object);$phaseDiagnostics=@($raw.statusPhases|ForEach-Object FrameCallbackMs|Sort-Object);for($i=0;$i -lt 3;$i++){SameNumber $sortedDiagnostics[$i] $phaseDiagnostics[$i]}
                return ,$values
            }
            Check $scope 'all-10k-diff-surface-ms<=200' {
                SameNumber $raw.diffSourceFileLines 10000;$null=Integer $raw.diffDisplayedPatchLines 1
                $values=Samples $raw.diffSelectionSurfaceSubmissionMs 200;$diagnostics=Samples $raw.diffSelectionFrameCallbackMs ([double]::MaxValue)
                Require ($raw.diffPhases -is [array] -and $raw.diffPhases.Count -eq 3) 'Missing raw diff phases'
                foreach($phase in $raw.diffPhases){Require ((Number $phase.FrameCallbackMs) -ge 0 -and $phase.FrameCallbackMs -le (Number $phase.SurfaceSubmissionMs)) 'Diff surface predates RAF';SurfaceValid $phase.SurfaceSubmission $raw $phase.SurfaceSubmissionMs}
                $sortedValues=@($values|Sort-Object);$sortedPhases=@($raw.diffPhases|ForEach-Object SurfaceSubmissionMs|Sort-Object)
                $sortedDiagnostics=@($diagnostics|Sort-Object);$phaseDiagnostics=@($raw.diffPhases|ForEach-Object FrameCallbackMs|Sort-Object)
                for($i=0;$i -lt 3;$i++){SameNumber $sortedValues[$i] $sortedPhases[$i];SameNumber $sortedDiagnostics[$i] $phaseDiagnostics[$i]}
                return ,$values
            }
            Check $scope 'native-render-and-surface-submission-cadence' { ScrollValid $raw }
            Check $scope 'all-steady-memory-bytes<=300000000' { MemoryValid $raw }
        } elseif($row['mode'] -ceq 'cold') { Check $scope 'cold-has-no-repository' { Require (-not $raw.Contains('fixture')) 'Cold startup contains repository fixture'; return $true } }
    }
}

# Inclusive nested spans are inspected individually, never added together. The
# three patched seams are the measured scope, not a claim about every COM/OS
# callback. Only product-operation overlap contributes to the UI 16ms gate;
# report/setup-only time stays in explicit diagnostic classifications.
$profileStart=$checks.Count
if($ProfileReports.Count -eq 0 -and $ProfileProbeReports.Count -eq 0) {
    $checks.Add(@{scope='ui-profiler';metric='external-evidence';status='unverified';reason='No phase-correlated external Dispatcher/native-window entry/exit profiler evidence supplied. Heartbeat and Meter histograms cannot establish full UI job duration.'})
} else {
    Check ui-profiler 'paired-inputs' {
        Require ($ProfileReports.Count -gt 0 -and $ProfileReports.Count -eq $ProfileProbeReports.Count) 'Profiler and probe arrays must be paired and nonempty'
        Require (-not [string]::IsNullOrWhiteSpace($ProfilerHookPath)) 'ProfilerHookPath is required to verify injected hook bytes'
        Require ($null -ne $referenceFixture -and $null -ne $referenceHost) 'No valid baseline fixture/host'
        return $ProfileReports.Count
    }
    for($profileIndex=0;$profileIndex -lt $ProfileReports.Count;$profileIndex++) {
        Check "ui-profiler:$profileIndex" 'identity-coverage-and-all-product-spans<=16ms' {
            Require ($profileIndex -lt $ProfileProbeReports.Count) 'Missing paired probe report'
            $p=ReadReport $ProfileReports[$profileIndex]; $pr=ReadReport $ProfileProbeReports[$profileIndex]
            SameNumber $p.schema 1; SameNumber $pr.schema 1; True $pr.completed 'Incomplete profiler probe'
            HostValid $pr; FixtureValid $pr
            ProductAssembliesValid $pr
            HashEquals $p.targetExecutableSha256 $app
            HashEquals $p.targetAppAssemblySha256 (Join-Path $appDirectory 'GitGui.App.dll')
            HashEquals $p.hookAssemblySha256 $ProfilerHookPath
            HashEquals $p.desktopProbeReportSha256 $ProfileProbeReports[$profileIndex]
            Require ([IO.Path]::GetFullPath($p.desktopProbeReportPath) -eq [IO.Path]::GetFullPath($ProfileProbeReports[$profileIndex])) 'Profiler linked a different probe path'
            $null=Integer $p.targetProcessId 1; $frequency=Integer $p.timestampFrequency 1
            SameNumber $frequency $pr.avaloniaMetrics.timestampFrequency
            $tid=Integer $p.uiNativeTid 1; SameNumber $tid $pr.avaloniaMetrics.uiNativeThreadId
            Require ($p.runtime -ceq $pr.host.framework) 'Profiler runtime mismatch'
            SameNumber $p.overflow 0; True $p.productPhasesCorrelated 'Missing product phase correlation'
            False $p.layoutActivitiesEnabled 'Heavy layout ActivityListener is not formal lightweight timing'
            False $p.canary.started 'Canary delays invalidate product acceptance'; Require ([string]::IsNullOrEmpty($p.canary.failure)) 'Profiler canary failed'
            True $p.gate.allSpansComplete 'Incomplete profiler spans'; SameNumber $p.gate.targetMs 16
            Require ($p.spans -is [array] -and $p.spans.Count -gt 0) 'No UI entry/exit samples'
            SameNumber $p.totalEntered $p.spans.Count
            foreach($method in @('Avalonia.Threading.DispatcherOperation.Execute','Avalonia.Threading.Dispatcher.Invoke','Avalonia.Win32.WindowImpl.AppWndProc')) {
                $patch=@($p.patches|Where-Object {$_.method -ceq $method}); Require ($patch.Count -eq 1) "Missing/duplicate hook: $method"
                $candidate=Join-Path $appDirectory ([IO.Path]::GetFileName($patch[0].module))
                HashEquals $patch[0].sha256 $candidate
                $null=Integer $patch[0].managedNativeHeaderSize 1
            }
            $origin=Integer $pr.timelineOriginQpc 1
            Require ($pr.phaseTimeline -is [array] -and $pr.phaseTimeline.Count -gt 0) 'Missing phase timeline'
            $phases=@();$last=-1.0
            foreach($phase in $pr.phaseTimeline) {
                $elapsed=Number $phase.elapsedMs;Require ($elapsed -ge 0 -and $elapsed -ge $last) 'Unordered phase timeline';$last=$elapsed
                Require (-not [string]::IsNullOrWhiteSpace($phase.phase)) 'Missing phase name'
                $phases+=@{at=$origin+[long][Math]::Truncate($elapsed*$frequency/1000);name=$phase.phase}
            }
            $productPattern='^(?:(?:open500|status[123]|diff[123])\.[a-z-]+|show-status-tab|load-more|graph-scroll(?:\.(?:draining|complete))?)$'
            foreach($required in @('open500.wait-publication','open500.complete','status1.wait-publication','status1.complete','status2.wait-publication','status2.complete','status3.wait-publication','status3.complete',
                'diff1.clear-selection','diff1.complete','diff2.clear-selection','diff2.complete','diff3.clear-selection','diff3.complete','load-more','graph-scroll','probe-report')) {
                Require (@($phases|Where-Object name -CEQ $required).Count -eq 1) "Missing/duplicate operation phase $required"
            }
            $ids=@{};$seams=@{};$classifications=@{};$maximum=0.0;$productCount=0;$crossingCount=0
            foreach($span in $p.spans) {
                $id=Integer $span.Id 1;Require (-not $ids.ContainsKey($id)) 'Duplicate span id';$ids[$id]=$true
                True $span.Completed 'Incomplete UI span';$enter=Integer $span.EnterQpc 1;$exit=Integer $span.ExitQpc 1
                Require ($exit -ge $enter) 'Negative UI span';$null=Integer $span.NativeTid 1
                Require ($span.Seam -cin @('DispatcherOperation.Execute','Dispatcher.Invoke UI Send','WindowImpl.AppWndProc')) 'Unknown profiler seam'
                $enterPhase=@($phases|Where-Object {$_.at -le $enter}|Select-Object -Last 1)
                $exitPhase=@($phases|Where-Object {$_.at -le $exit}|Select-Object -Last 1)
                $expectedEnter=if($enterPhase.Count){$enterPhase[0].name}else{$null};$expectedExit=if($exitPhase.Count){$exitPhase[0].name}else{$null}
                Require ($span.ProductPhaseAtEnter -ceq $expectedEnter -and $span.ProductPhaseAtExit -ceq $expectedExit) 'UI/product phase association mismatch'
                if($span.NativeTid -ne $tid){continue}
                $seams[$span.Seam]=$true;$overlap=0L
                for($j=0;$j -lt $phases.Count-1;$j++) {
                    if($phases[$j].name -cmatch $productPattern) { $overlap += [Math]::Max(0,[Math]::Min($exit,$phases[$j+1].at)-[Math]::Max($enter,$phases[$j].at)) }
                }
                $classification=if($overlap -gt 0){if($expectedEnter -cne $expectedExit){'cross-phase-product'}else{'product'}}else{'setup-startup-report-only'}
                if(-not $classifications.ContainsKey($classification)){$classifications[$classification]=0};$classifications[$classification]++
                if($overlap -gt 0){$productCount++;if($classification -eq 'cross-phase-product'){$crossingCount++};$ms=$overlap*1000.0/$frequency;$maximum=[Math]::Max($maximum,$ms);Require ($ms -le 16) "UI span $id ($($span.Seam), $classification) blocks product work for $ms ms >16"}
            }
            Require ($productCount -gt 0 -and $seams.ContainsKey('DispatcherOperation.Execute') -and $seams.ContainsKey('WindowImpl.AppWndProc')) 'No measured product dispatcher/native-window work'
            return @{productSpans=$productCount;crossPhaseProductSpans=$crossingCount;maximumProductOverlapMs=$maximum;classifications=$classifications;processId=$p.targetProcessId}
        }
    }
}
$profileChecks=@($checks|Select-Object -Skip $profileStart)
$uiStatus=if(@($profileChecks|Where-Object status -eq 'unverified').Count){'unverified'}elseif(@($profileChecks|Where-Object status -eq 'fail').Count){'fail'}else{'pass'}
$result=[ordered]@{schema=1;recordedAt=[DateTimeOffset]::UtcNow;pass=(@($checks|Where-Object status -ne 'pass').Count -eq 0);
    thresholds=@{startupMs=1500;first500Ms=1000;everyStatusMs=300;every10kDiffMs=200;workingSetBytes=300000000;uiProductSpanMs=16;nominalScrollHz=60;fractional60Hz=(60000.0/1001);maximumInternalFrameGapMultiplier=1.5};
    uiBlocking=@{status=$uiStatus;scope='Measured DispatcherOperation.Execute, UI Action Send fastpath, and main WindowImpl.AppWndProc entries/exits during recorded product phases. Nested spans inclusive; cross-phase spans classified by actual overlap. Generic Invoke<T>/other COM or native callbacks remain outside instrumented scope.'};
    scrollBoundary='Application rendered frames paired with distinct completed CompositionBatch notifications after native surface update disposal; no renderer errors. Not GPU completion, OS compositor completion, or physical display.';
    operationBoundary='Canonical startup/first500/status/diff timings end at a requested rendered batch with real histogram evidence, after native drawing sessions return. RAF values remain diagnostics.';
    regressionBoundary='This absolute native gate does not replace scripts/Compare-Performance.ps1 for the separate <=10% engine regression requirement, nor real git.git acceptance.';
    candidateProductAssemblies=$candidateProducts;inputs=@($inputs);checks=@($checks)}
$json=$result|ConvertTo-Json -Depth 40
$stream=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
try{$bytes=[Text.UTF8Encoding]::new($false).GetBytes($json);$stream.Write($bytes)}finally{$stream.Dispose()}
$json
if(-not $result.pass){throw "Native desktop acceptance failed or remains unverified; full decision: $output"}
