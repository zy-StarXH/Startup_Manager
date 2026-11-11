# Generates the multi-size StartupManager.ico (bolt mark, #2b88d8 -> #005a9e).
# The generated .ico is committed; this script only needs to run when the mark changes.
[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Split-Path -Parent $scriptRoot) 'src\StartupManager.App\Assets\StartupManager.ico'
}
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = New-Object 'System.Collections.Generic.List[byte[]]'

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile with the prototype's accent gradient.
    $pad = [Math]::Max(0.0, [float]$size * 0.012)
    $side = [float]$size - (2 * $pad)
    $tile = New-RoundedRectPath $pad $pad $side $side ([float]$size * 0.22)
    $p1 = New-Object System.Drawing.PointF(0, 0)
    $p2 = New-Object System.Drawing.PointF([float]$size, [float]$size)
    $tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $p1, $p2,
        [System.Drawing.Color]::FromArgb(255, 43, 136, 216),
        [System.Drawing.Color]::FromArgb(255, 0, 90, 158))
    $g.FillPath($tileBrush, $tile)

    # Bolt: prototype symbol path (viewBox 24), centered and scaled.
    $scale = [float]$size * 0.72 / 20.0
    $ox = ([float]$size / 2.0) - (11.2 * $scale)
    $oy = ([float]$size / 2.0) - (12.0 * $scale)
    $points = @(
        (New-Object System.Drawing.PointF([float]($ox + 13.0 * $scale), [float]($oy + 2.0 * $scale))),
        (New-Object System.Drawing.PointF([float]($ox + 4.6 * $scale), [float]($oy + 13.4 * $scale))),
        (New-Object System.Drawing.PointF([float]($ox + 10.3 * $scale), [float]($oy + 13.4 * $scale))),
        (New-Object System.Drawing.PointF([float]($ox + 9.2 * $scale), [float]($oy + 22.0 * $scale))),
        (New-Object System.Drawing.PointF([float]($ox + 17.8 * $scale), [float]($oy + 10.5 * $scale))),
        (New-Object System.Drawing.PointF([float]($ox + 12.2 * $scale), [float]($oy + 10.5 * $scale)))
    )
    $bolt = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bolt.AddPolygon($points)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillPath($white, $bolt)

    # Subtle inner top highlight, matching the prototype's brand tile.
    if ($size -ge 32) {
        $highlightPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(46, 255, 255, 255), [float]([Math]::Max(1.0, $size * 0.012)))
        $inset = $pad + [float]([Math]::Max(1.0, $size * 0.02))
        $highlight = New-RoundedRectPath $inset $inset ([float]$side - 2 * ([Math]::Max(1.0, $size * 0.02))) ([float]$side - 2 * ([Math]::Max(1.0, $size * 0.02))) ([float]$size * 0.20)
        $g.DrawPath($highlightPen, $highlight)
        $highlight.Dispose()
        $highlightPen.Dispose()
    }

    # Build a classic 32bpp DIB entry (XOR pixels + AND mask). PNG-compressed
    # icon entries are not decoded reliably by System.Drawing's icon reader.
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $locked = $bmp.LockBits(
        $rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rowBytes = $size * 4
    $pixels = New-Object byte[] ($locked.Stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($locked)

    $xor = New-Object byte[] ($rowBytes * $size)
    for ($y = 0; $y -lt $size; $y++) {
        $srcRow = ($size - 1 - $y) * $locked.Stride
        [Array]::Copy($pixels, $srcRow, $xor, ($y * $rowBytes), $rowBytes)
    }

    $maskStride = [int]([Math]::Floor(($size + 31) / 32) * 4)
    $andMask = New-Object byte[] ($maskStride * $size)
    for ($y = 0; $y -lt $size; $y++) {
        $srcRow = ($size - 1 - $y) * $locked.Stride
        $dstRow = $y * $maskStride
        for ($x = 0; $x -lt $size; $x++) {
            if ($pixels[$srcRow + ($x * 4) + 3] -eq 0) {
                $index = $dstRow + [int][Math]::Floor($x / 8)
                $andMask[$index] = $andMask[$index] -bor (0x80 -shr ($x % 8))
            }
        }
    }

    $dib = New-Object System.IO.MemoryStream
    $dibWriter = New-Object System.IO.BinaryWriter($dib)
    $dibWriter.Write([UInt32]40)                 # biSize
    $dibWriter.Write([Int32]$size)               # biWidth
    $dibWriter.Write([Int32]($size * 2))         # biHeight (XOR + AND)
    $dibWriter.Write([UInt16]1)                  # biPlanes
    $dibWriter.Write([UInt16]32)                 # biBitCount
    $dibWriter.Write([UInt32]0)                  # biCompression (BI_RGB)
    $dibWriter.Write([UInt32]($xor.Length + $andMask.Length))
    $dibWriter.Write([Int32]0); $dibWriter.Write([Int32]0)
    $dibWriter.Write([UInt32]0); $dibWriter.Write([UInt32]0)
    $dibWriter.Write($xor)
    $dibWriter.Write($andMask)
    $dibWriter.Flush()
    $images.Add($dib.ToArray())
    $dibWriter.Dispose()
    $dib.Dispose()

    $white.Dispose()
    $bolt.Dispose()
    $tileBrush.Dispose()
    $tile.Dispose()
    $g.Dispose()
    $bmp.Dispose()
}

$outDir = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$fs = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([UInt16]0)              # reserved
    $bw.Write([UInt16]1)              # type: icon
    $bw.Write([UInt16]$sizes.Count)   # image count

    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $bw.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
        $bw.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
        $bw.Write([byte]0)            # palette
        $bw.Write([byte]0)            # reserved
        $bw.Write([UInt16]1)          # planes
        $bw.Write([UInt16]32)         # bpp
        $bw.Write([UInt32]$images[$i].Length)
        $bw.Write([UInt32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) {
        $bw.Write($bytes)
    }
}
finally {
    $bw.Dispose()
    $fs.Dispose()
}

Write-Host ("Wrote {0} ({1} bytes, {2} sizes)" -f $OutputPath, (Get-Item -LiteralPath $OutputPath).Length, $sizes.Count)
