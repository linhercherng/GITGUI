param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [string]$NativeHostPath,
    [switch]$Desktop
)
$ErrorActionPreference = 'Stop'
if (-not ('GitGui.SmokePipeIdentity' -as [type])) {
    Add-Type -TypeDefinition @"
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace GitGui { public static class SmokePipeIdentity {
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
} }
"@
}
$appExe = (Resolve-Path -LiteralPath $AppPath).Path
$pipeName = 'gitgui-smoke-' + [Guid]::NewGuid().ToString('N')
$pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut, 1,
    [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
$waiting = $pipe.WaitForConnectionAsync()
$start = [Diagnostics.ProcessStartInfo]::new($appExe)
$start.WorkingDirectory = [IO.Path]::GetTempPath()
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
if($NativeHostPath) {
    $nativeHost=(Resolve-Path -LiteralPath $NativeHostPath).Path
    # rundll32 requires quotes around the DLL path only, before the entry-point comma.
    # ArgumentList would quote the entire path,RunW token and change its parser input.
    $start.Arguments='"'+$nativeHost+'",RunW --askpass "Smoke credential prompt"'
} else {
    $start.ArgumentList.Add('--askpass')
    $start.ArgumentList.Add('Smoke credential prompt')
}
$start.Environment['GITGUI_ASKPASS_PIPE'] = $pipeName
$child = [Diagnostics.Process]::Start($start)
$stdout = $child.StandardOutput.ReadToEndAsync()
$stderr = $child.StandardError.ReadToEndAsync()
try {
    $connectionWatch = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $remaining = 10000 - [int]$connectionWatch.ElapsedMilliseconds
        if ($remaining -le 0 -or -not $waiting.Wait($remaining)) { throw 'AskPass pipe did not connect' }
        $peer = [uint32]0
        if (-not [GitGui.SmokePipeIdentity]::GetNamedPipeClientProcessId($pipe.SafePipeHandle, [ref]$peer)) {
            throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
        }
        if ($peer -eq $child.Id) { break }
        # Only exercise the launched helper; an unrelated process can connect first.
        $pipe.Disconnect()
        $waiting = $pipe.WaitForConnectionAsync()
    }
    $encoding = [Text.UTF8Encoding]::new($false)
    $reader = [IO.StreamReader]::new($pipe, $encoding, $false, 1024, $true)
    $writer = [IO.StreamWriter]::new($pipe, $encoding, 1024, $true)
    $writer.AutoFlush = $true
    $line = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
    if ($null -eq $line) {
        if (-not $child.WaitForExit(10000)) { throw 'AskPass helper closed its request pipe without exiting' }
        throw "AskPass helper closed before its request (exit $($child.ExitCode)): $($stderr.GetAwaiter().GetResult())"
    }
    $request = $line | ConvertFrom-Json
    $writer.WriteLine(('smoke-密碼' | ConvertTo-Json -Compress))
    if (-not $child.WaitForExit(10000)) { throw 'AskPass helper did not exit' }
    $answer = $stdout.GetAwaiter().GetResult().Trim()
    if ($request -ne 'Smoke credential prompt' -or $answer -ne 'smoke-密碼' -or $child.ExitCode -ne 0) {
        throw 'AskPass helper UTF-8 protocol mismatch'
    }
    Write-Output 'Windows AskPass helper: PASS (UTF-8 request/response and stdout)'
} finally {
    if (-not $child.HasExited) { $child.Kill($true) }
    $pipe.Dispose()
    $child.Dispose()
}
if ($Desktop) {
    $window = Start-Process -FilePath $appExe -WindowStyle Hidden -PassThru
    try {
        # This checks eventual desktop readiness, not the strict performance gate.
        # Poll the real window instead of assuming every machine is ready at 4 s.
        $startup = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 250
            $window.Refresh()
            if ($window.HasExited -or ($window.Responding -and $window.MainWindowTitle -like '*GITGUI*')) { break }
        } while ($startup.Elapsed.TotalSeconds -lt 30)
        if ($window.HasExited -or -not $window.Responding -or $window.MainWindowTitle -notlike '*GITGUI*') {
            throw "Desktop startup failed after $([Math]::Round($startup.Elapsed.TotalSeconds, 2))s: Exited=$($window.HasExited), Responding=$($window.Responding), Title=$($window.MainWindowTitle)"
        }
        Write-Output ('Windows desktop launch: PASS (' + $window.MainWindowTitle + ', Responding=True, ReadinessMs=' + [Math]::Round($startup.Elapsed.TotalMilliseconds, 2) + ')')
        $null = $window.CloseMainWindow()
        if (-not $window.WaitForExit(10000)) { throw 'Desktop did not close normally' }
    } finally {
        if (-not $window.HasExited) { $window.Kill($true) }
        $window.Dispose()
    }
}
