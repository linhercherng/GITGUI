# Original branch artwork; no downloaded icon/font asset. Run on Windows.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$destination = Join-Path $PSScriptRoot '../src/GitGui.UI/Assets'
$null = New-Item -ItemType Directory -Path $destination -Force
$images = foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform($size / 256.0, $size / 256.0)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc(0, 0, 96, 96, 180, 90); $shape.AddArc(160, 0, 96, 96, 270, 90)
    $shape.AddArc(160, 160, 96, 96, 0, 90); $shape.AddArc(0, 160, 96, 96, 90, 90); $shape.CloseFigure()
    $blue = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#255CAE'))
    $pen = [Drawing.Pen]::new([Drawing.Color]::White, 12)
    $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $graphics.FillPath($blue, $shape)
    $graphics.DrawLine($pen, 86, 82, 86, 174)
    $graphics.DrawBezier($pen, 86, 136, 144, 136, 174, 124, 174, 82)
    foreach ($point in @(@(86, 64), @(174, 64), @(86, 192))) {
        $graphics.FillEllipse($blue, $point[0] - 17, $point[1] - 17, 34, 34)
        $graphics.DrawEllipse($pen, $point[0] - 17, $point[1] - 17, 34, 34)
    }
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $destination 'gitgui.png'), $stream.ToArray()) }
    [pscustomobject]@{ Size = $size; Data = $stream.ToArray() }
    $stream.Dispose(); $pen.Dispose(); $blue.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$ico = [IO.MemoryStream]::new(); $writer = [IO.BinaryWriter]::new($ico)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($entry in $images) {
    $dimension = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$entry.Data.Length); $writer.Write([uint32]$offset); $offset += $entry.Data.Length
}
foreach ($entry in $images) { $writer.Write([byte[]]$entry.Data) }
[IO.File]::WriteAllBytes((Join-Path $destination 'gitgui.ico'), $ico.ToArray())
$writer.Dispose(); $ico.Dispose()
