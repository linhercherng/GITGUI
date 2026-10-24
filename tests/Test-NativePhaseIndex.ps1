$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(-not ('GitGui.Acceptance.NativeSpanEvaluation' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot '../scripts/NativeSpanEvaluation.cs')
}
$random=[Random]::new(72839);$checked=0
foreach($length in @(1,2,3,8,31,64,129)) {
    for($trial=0;$trial -lt 8;$trial++) {
        $at=100000000000L;$timeline=@()
        for($i=0;$i -lt $length;$i++) {
            $at+=$random.Next(0,11) # Includes duplicate timestamps.
            $timeline+=@{at=$at}
        }
        $timestamps=@(0L,99999999999L,$at+100,9007199254740991L)
        foreach($entry in $timeline){$timestamps+=@(($entry.at-1),$entry.at,($entry.at+1))}
        foreach($timestamp in $timestamps) {
            $expected=-1
            for($i=0;$i -lt $timeline.Count;$i++){if($timeline[$i].at -le $timestamp){$expected=$i}}
            $actual=[GitGui.Acceptance.NativeSpanEvaluation]::PhaseIndex([long[]]@($timeline.at),$timestamp)
            if($actual -ne $expected){throw "Phase boundary mismatch: $timestamp, $actual versus $expected"}
            $checked++
        }
    }
}
"PASS: $checked indexed/linear phase comparisons, including ties, boundaries and long QPC values"
