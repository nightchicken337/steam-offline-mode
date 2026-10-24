# Generates src\icon.ico - a monochrome power mark on a charcoal rounded square.
# Multi-size ICO with PNG payloads (supported by Windows Vista and later).

Add-Type -AssemblyName System.Drawing

$out   = Join-Path $PSScriptRoot 'icon.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-Glyph {
    param([int]$S)

    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    # charcoal rounded square
    $r    = [Math]::Max(2, [int]($S * 0.22))
    $rect = New-Object System.Drawing.RectangleF(0, 0, $S, $S)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d    = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 17, 17, 17))
    $g.FillPath($ink, $path)

    # power mark: open ring with a stem through the gap
    $stroke = [Math]::Max(1.0, $S * 0.085)
    $pen    = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 251, 251, 250)), $stroke
    $pen.StartCap = 'Round'
    $pen.EndCap   = 'Round'

    # NOTE: PowerShell's comma binds tighter than arithmetic, so constructor
    # arguments must be pre-computed rather than written inline.
    $inset = [single]($S * 0.30)
    $span  = [single]($S - 2 * $inset)
    $g.DrawArc($pen, $inset, $inset, $span, $span, -60, 300)

    $cx = [single]($S / 2.0)
    $y0 = [single]($S * 0.22)
    $y1 = [single]($S * 0.48)
    $g.DrawLine($pen, $cx, $y0, $cx, $y1)

    $g.Dispose(); $ink.Dispose(); $pen.Dispose(); $path.Dispose()
    return $bmp
}

# Classic DIB payload. GDI+ (System.Drawing.Icon) cannot decode PNG-compressed
# icon entries, so only the 256px entry uses PNG - everything else stays DIB.
function ConvertTo-Dib {
    param([System.Drawing.Bitmap]$Bmp)

    $w = $Bmp.Width; $h = $Bmp.Height
    $maskStride = [int](([Math]::Floor(($w + 31) / 32)) * 4)
    $maskBytes  = $maskStride * $h
    $xorBytes   = $w * $h * 4

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $bw.Write([UInt32]40)            # biSize
    $bw.Write([Int32]$w)             # biWidth
    $bw.Write([Int32]($h * 2))       # biHeight: XOR image + AND mask
    $bw.Write([UInt16]1)             # biPlanes
    $bw.Write([UInt16]32)            # biBitCount
    $bw.Write([UInt32]0)             # BI_RGB
    $bw.Write([UInt32]($xorBytes + $maskBytes))
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    # XOR bitmap, bottom-up, BGRA
    $data = $Bmp.LockBits(
        (New-Object System.Drawing.Rectangle(0, 0, $w, $h)),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $row = New-Object byte[] ($w * 4)
        for ($y = $h - 1; $y -ge 0; $y--) {
            $ptr = [IntPtr]::Add($data.Scan0, $y * $data.Stride)
            [System.Runtime.InteropServices.Marshal]::Copy($ptr, $row, 0, $row.Length)
            $bw.Write($row)
        }
    } finally {
        $Bmp.UnlockBits($data)
    }

    # AND mask stays zeroed; the 32bpp alpha channel carries transparency.
    $bw.Write((New-Object byte[] $maskBytes))

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    # Comma stops PowerShell from unrolling the byte[] into the pipeline.
    return , $bytes
}

$entries = @()
foreach ($s in $sizes) {
    $bmp = New-Glyph -S $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
        $ms.Dispose()
    } else {
        $bytes = [byte[]](ConvertTo-Dib -Bmp $bmp)
    }
    $entries += , @{ Size = $s; Bytes = [byte[]]$bytes }
    $bmp.Dispose()
}
$pngs = $entries

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type: icon
$bw.Write([UInt16]$pngs.Count)

$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([Byte]$dim)            # width
    $bw.Write([Byte]$dim)            # height
    $bw.Write([Byte]0)               # palette
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # colour planes
    $bw.Write([UInt16]32)            # bits per pixel
    $bw.Write([UInt32]$p.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p.Bytes, 0, $p.Bytes.Length) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()
Write-Host "icon.ico written ($((Get-Item $out).Length) bytes, $($pngs.Count) sizes)" -ForegroundColor Green
