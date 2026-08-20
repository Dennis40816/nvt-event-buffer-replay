param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\Nvt.Replay.Avalonia\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = [System.Collections.Generic.List[byte[]]]::new()

function New-RoundedRectanglePath([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

foreach ($size in $sizes) {
    $scale = $size / 64.0
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $backgroundPath = New-RoundedRectanglePath (3 * $scale) (3 * $scale) (58 * $scale) (58 * $scale) (13 * $scale)
        $backgroundBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#0E1317'))
        $borderPen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#263139'), [Math]::Max(1, 2 * $scale))
        try {
            $graphics.FillPath($backgroundBrush, $backgroundPath)
            $graphics.DrawPath($borderPen, $backgroundPath)
        }
        finally {
            $backgroundPath.Dispose()
            $backgroundBrush.Dispose()
            $borderPen.Dispose()
        }

        $tracePen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#B9E653'), [Math]::Max(1.5, 5.5 * $scale))
        try {
            $tracePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $tracePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $tracePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $points = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new(17 * $scale, 45 * $scale),
                [System.Drawing.PointF]::new(17 * $scale, 19 * $scale),
                [System.Drawing.PointF]::new(47 * $scale, 45 * $scale),
                [System.Drawing.PointF]::new(47 * $scale, 19 * $scale)
            )
            $graphics.DrawLines($tracePen, $points)
        }
        finally {
            $tracePen.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $bytes = $stream.ToArray()
            $images.Add($bytes)
            if ($size -eq 256) {
                [System.IO.File]::WriteAllBytes((Join-Path $resolvedOutput 'NvtReplayIcon.png'), $bytes)
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$iconPath = Join-Path $resolvedOutput 'NvtReplayIcon.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($image in $images) {
        $writer.Write($image)
    }
}
finally {
    $writer.Dispose()
}

Write-Output "Generated $iconPath"
