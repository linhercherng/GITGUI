param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputPath) { $OutputPath = Join-Path $repo 'docs/USER-GUIDE.zh-TW.html' }
$body = (ConvertFrom-Markdown -Path (Join-Path $repo 'docs/USER-GUIDE.zh-TW.md')).Html
$toc = [Collections.Generic.List[string]]::new()
$headings = [regex]::Matches($body, '<h2[^>]*>(.*?)</h2>', [Text.RegularExpressions.RegexOptions]::Singleline)
for ($index = 0; $index -lt $headings.Count; $index++) {
    $heading = $headings[$index]
    $body = $body.Replace($heading.Value, "<h2 id=`"section-$index`">$($heading.Groups[1].Value)</h2>")
    $toc.Add("<li><a href=`"#section-$index`">$($heading.Groups[1].Value)</a></li>")
}
$navigation = "<nav aria-label=`"操作手冊目錄`"><strong>快速前往</strong><ol>$($toc -join '')</ol></nav>"
$introEnd = $body.IndexOf('</p>') + 4
if ($introEnd -lt 4) { throw 'Guide introduction is missing' }
$body = $body.Insert($introEnd, $navigation)
$html = @"
<!doctype html>
<html lang="zh-Hant"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>GITGUI 操作手冊</title>
<style>
:root{color-scheme:light dark;--bg:#f6f7f9;--panel:#fff;--ink:#202834;--muted:#536276;--line:#d7dfe9;--link:#185ac5}
@media(prefers-color-scheme:dark){:root{--bg:#14181e;--panel:#1c232d;--ink:#e1e8f2;--muted:#a4b2c5;--line:#374352;--link:#8ebaff}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.85 "Segoe UI","Microsoft JhengHei",sans-serif}
main{max-width:1000px;margin:32px auto;padding:40px 48px;background:var(--panel);border:1px solid var(--line);border-radius:12px}
.brand{color:var(--muted);letter-spacing:.12em;font-size:12px}h1{font-size:32px;line-height:1.4}h2{font-size:23px;margin-top:48px;padding-top:24px;border-top:1px solid var(--line);scroll-margin-top:20px}
a{color:var(--link);text-underline-offset:3px}nav{padding:20px 24px;border:1px solid var(--line);border-radius:8px}nav ol{columns:2;margin:10px 0 0;padding:0;list-style:none}nav li{break-inside:avoid;font-size:14px}
img{max-width:100%;height:auto;border:1px solid var(--line);border-radius:8px}table{width:100%;border-collapse:collapse;font-size:14px}th,td{padding:10px 12px;text-align:left;border:1px solid var(--line);vertical-align:top}
pre{padding:16px;overflow:auto;border:1px solid var(--line);border-radius:6px;line-height:1.6}code{font-family:"Cascadia Code",Consolas,monospace;font-size:.9em;overflow-wrap:anywhere}li{margin:5px 0}footer{margin-top:40px;color:var(--muted);font-size:13px}
@media(max-width:700px){main{margin:0;padding:24px 18px;border:0;border-radius:0}nav ol{columns:1}h1{font-size:27px}table{display:block;overflow:auto}}
@media print{body,main{background:white;color:black}main{margin:0;padding:0;border:0}nav{break-inside:avoid}h2{break-after:avoid}pre,img,table{break-inside:avoid}a{color:inherit}}
</style>
<main><div class="brand">GITGUI · WINDOWS PORTABLE · M1–M3</div>
$body
<footer>離線手冊隨同 GITGUI 提供。原始可編輯版本：<a href="USER-GUIDE.zh-TW.md">USER-GUIDE.zh-TW.md</a></footer></main></html>
"@
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $html, [Text.UTF8Encoding]::new($false))
Write-Output "Guide HTML: $([IO.Path]::GetFullPath($OutputPath))"
