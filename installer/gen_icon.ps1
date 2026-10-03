# Generate app.ico (multi-size PNG-based ICO) for both the app and the installer.
# Usage: gen_icon.ps1 <outIco>
# NOTE: keep this script ASCII-only. Windows PowerShell 5.1 reads .ps1 as ANSI
#       without BOM, so UTF-8 literals would be mis-decoded. The Chinese glyph is
#       therefore built from its code point (U+6587) instead of a literal.
param(
    [Parameter(Mandatory=$true)][string]$OutIco
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pixels = @()

foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $sz, $sz
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square with 45-degree gradient (matches the in-app logo)
    $r = $sz * 0.12
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(1.0, $r * 2)
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($sz - $d, 0, $d, $d, 270, 90)
    $path.AddArc($sz - $d, $sz - $d, $d, $d, 0, 90)
    $path.AddArc(0, $sz - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle 0, 0, $sz, $sz
    $c1 = [System.Drawing.Color]::FromArgb(0x6C, 0xD4, 0xFF)
    $c2 = [System.Drawing.Color]::FromArgb(0x00, 0x67, 0xC0)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, 45.0
    $g.FillPath($brush, $path)

    # White glyph (U+6587) centered; skip on tiny sizes where it would be mud
    if ($sz -ge 24) {
        $glyph = [string][char]0x6587
        $fontSize = $sz * 0.56
        $font = New-Object System.Drawing.Font "Microsoft YaHei UI", $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $g.DrawString($glyph, $font, $white, (New-Object System.Drawing.RectangleF 0, 0, $sz, $sz), $fmt)
        $font.Dispose(); $white.Dispose(); $fmt.Dispose()
    }

    $brush.Dispose(); $path.Dispose(); $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pixels += , ($ms.ToArray())
    $ms.Dispose()
}

# Assemble ICO container (PNG-compressed entries; supported by Windows Vista+)
$fs = [System.IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]$sizes.Count)      # image count

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $data = $pixels[$i]
    $bw.Write([Byte]$(if ($sz -ge 256) { 0 } else { $sz }))   # width (0 = 256)
    $bw.Write([Byte]$(if ($sz -ge 256) { 0 } else { $sz }))   # height
    $bw.Write([Byte]0)               # palette colors
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # color planes
    $bw.Write([UInt16]32)            # bits per pixel
    $bw.Write([UInt32]$data.Length)  # size of image data
    $bw.Write([UInt32]$offset)       # offset
    $offset += $data.Length
}
foreach ($data in $pixels) { $bw.Write($data) }

$bw.Flush(); $bw.Close(); $fs.Close()
Write-Output "generated $OutIco ($($sizes.Count) sizes, $((Get-Item $OutIco).Length) bytes)"
