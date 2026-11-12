# Genera corbolsys.ico (multi-risoluzione, PNG interni) e un'anteprima icon-preview.png
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

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

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(0.5, $s * 0.03)          # margine
    $w = $s - 2 * $m
    $rect = New-Object System.Drawing.RectangleF $m, $m, $w, $w
    $bg = New-RoundRect $m $m $w $w ($s * 0.22)

    # Sfondo: gradiente diagonale verde acqua -> blu
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 16, 185, 129)),
        ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)), 45.0
    $g.FillPath($grad, $bg)

    # Riflesso morbido in alto
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)),
        ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
    $g.FillPath($hl, $bg)

    # Barre (CPU, RAM, disco) e linea del battito
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
    $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 255, 255, 255))
    $bw = $s * 0.14; $gap = $s * 0.075; $base = $s * 0.80
    $x0 = ($s - (3 * $bw + 2 * $gap)) / 2
    $hs = 0.30, 0.52, 0.40
    for ($i = 0; $i -lt 3; $i++) {
        $bx = $x0 + $i * ($bw + $gap)
        $g.FillRectangle($soft, $bx, $s * 0.22, $bw, $base - $s * 0.22)
        $bh = $s * $hs[$i]
        $g.FillRectangle($white, $bx, $base - $bh, $bw, $bh)
    }
    if ($s -ge 24) {
        $pw = [Math]::Max(1.2, $s * 0.055)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), $pw
        $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $sh = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 10, 30, 90)), ($pw * 1.8)
        $sh.LineJoin = 'Round'
        $pts = @(
            (New-Object System.Drawing.PointF ($s * 0.12), ($s * 0.42)),
            (New-Object System.Drawing.PointF ($s * 0.34), ($s * 0.42)),
            (New-Object System.Drawing.PointF ($s * 0.44), ($s * 0.22)),
            (New-Object System.Drawing.PointF ($s * 0.56), ($s * 0.60)),
            (New-Object System.Drawing.PointF ($s * 0.66), ($s * 0.42)),
            (New-Object System.Drawing.PointF ($s * 0.88), ($s * 0.42))
        )
        $g.DrawLines($sh, $pts)
        $g.DrawLines($pen, $pts)
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $b.Dispose()
}

# Scrive il file .ico
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $dir 'corbolsys.ico'), $out.ToArray())

# Anteprima: tutte le dimensioni affiancate su sfondo chiaro e scuro
$pw = 20; foreach ($s in $sizes) { $pw += $s + 20 }
$prev = New-Object System.Drawing.Bitmap $pw, 620
$pg = [System.Drawing.Graphics]::FromImage($prev)
$pg.Clear([System.Drawing.Color]::FromArgb(245, 245, 245))
$pg.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 32))), 0, 310, $pw, 310)
$x = 20
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $pg.DrawImage($b, $x, 290 - $s); $pg.DrawImage($b, $x, 600 - $s)
    $x += $s + 20; $b.Dispose()
}
$prev.Save((Join-Path $dir 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output "Creati corbolsys.ico e icon-preview.png"
