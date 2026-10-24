param([Parameter(Mandatory)][string]$AppDirectory,[Parameter(Mandatory)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The native Windows host requires MSVC on Windows.' }
$app=[IO.Path]::GetFullPath($AppDirectory)
$build=[IO.Path]::GetFullPath($BuildDirectory)
if (Test-Path $build) { throw 'BuildDirectory must be new.' }
$null=New-Item -ItemType Directory $build
$file=Join-Path $app 'GitGui.App.exe'
$stream=[IO.File]::OpenRead($file)
try {
    $first=[byte[]]::new(4096);$stream.ReadExactly($first,0,$first.Length)
    $pe=[BitConverter]::ToInt32($first,60);$count=[BitConverter]::ToInt16($first,$pe+6);$optional=[BitConverter]::ToInt16($first,$pe+20)
    $end=0
    for($i=0;$i -lt $count;$i++) {
        $section=$pe+24+$optional+40*$i
        $sectionEnd=[BitConverter]::ToInt32($first,$section+16)+[BitConverter]::ToInt32($first,$section+20)
        $end=[Math]::Max($end,$sectionEnd)
    }
    if($end -le 0 -or $end -gt 64MB -or $end -gt $stream.Length){throw 'Invalid native bundle prefix.'}
    $stream.Position=0;$head=[byte[]]::new($end);$stream.ReadExactly($head,0,$head.Length)
} finally {$stream.Dispose()}
$text=[Text.Encoding]::Latin1.GetString($head)
$marker=[Text.Encoding]::Latin1.GetString([Convert]::FromHexString('8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE'))
$index=$text.IndexOf($marker,[StringComparison]::Ordinal)
if($index -lt 8 -or $text.IndexOf($marker,$index+1,[StringComparison]::Ordinal) -ge 0){throw 'Ambiguous native SDK bundle marker.'}
$offset=[BitConverter]::ToInt64($head,$index-8)
if($offset -lt $end -or $offset -ge (Get-Item $file).Length){throw 'Invalid SDK bundle header offset.'}
"#define BUNDLE_HEADER_OFFSET $($offset)LL"|Set-Content "$build/BundleOffset.h" -Encoding ascii
$source=Join-Path (Split-Path $PSScriptRoot -Parent) 'tools/WindowsNativeHost'
Copy-Item "$source/GitGui.Host.cpp" $build
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$visualStudio=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
if($LASTEXITCODE -or -not $visualStudio){throw 'Install the Visual Studio C++ x64 build tools to build the native host.'}
@"
@call "$visualStudio\Common7\Tools\VsDevCmd.bat" -host_arch=x64 -arch=x64 >nul
@if errorlevel 1 exit /b 1
@cl /nologo /O2 /MT /W4 /WX /LD "$build\GitGui.Host.cpp" /Fo:"$build\GitGui.Host.obj" /Fe:"$app\GitGui.Host.dll" /link shell32.lib /IMPLIB:"$build\GitGui.Host.lib"
"@|Set-Content "$build/BuildHost.cmd" -Encoding utf8
& "$build/BuildHost.cmd" *> "$build/build.log"
if($LASTEXITCODE){Get-Content "$build/build.log" -Tail 10;throw 'Native host compilation failed.'}
Copy-Item "$source/GITGUI.vbs" $app
@{bundleHeaderOffset=$offset;hostSha256=(Get-FileHash "$app/GitGui.Host.dll").Hash;launcherSha256=(Get-FileHash "$app/GITGUI.vbs").Hash}|
    ConvertTo-Json|Set-Content "$build/verification.json"
