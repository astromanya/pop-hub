# DisplayDock ikonunu üretir (koyu yuvarlak kare + iki monitör, biri mavi vurgulu).
param([string]$Out = "$PSScriptRoot\..\Assets\DisplayDock.ico")
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-RoundRect 0 0 ($s - 1) ($s - 1) ($s * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x2C, 0x2C, 0x28))), $bg)

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0xEC, 0xEC, 0xE6)), ([Math]::Max(1.2, $s * 0.055))
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xA9, 0xB9, 0xFF))
    $light = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xEC, 0xEC, 0xE6))

    $mw = $s * 0.33; $mh = $s * 0.24; $top = $s * 0.30
    $x1 = $s * 0.13; $x2 = $s * 0.54
    # Sol monitör (mavi dolu = açık)
    $g.FillPath($blue, (New-RoundRect $x1 $top $mw $mh ($s * 0.04)))
    # Sağ monitör (çerçeve)
    $g.DrawPath($pen, (New-RoundRect $x2 $top $mw $mh ($s * 0.04)))
    # Ayaklar
    foreach ($x in @($x1, $x2)) {
        $cx = $x + $mw / 2
        $g.FillRectangle($light, [float]($cx - $s * 0.02), [float]($top + $mh), [float]($s * 0.04), [float]($s * 0.09))
        $g.FillRectangle($light, [float]($cx - $s * 0.09), [float]($top + $mh + $s * 0.09), [float]($s * 0.18), [float]([Math]::Max(1, $s * 0.035)))
    }
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) { , (New-IconPng $sz) }

New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"Icon: $((Resolve-Path $Out).Path)"
