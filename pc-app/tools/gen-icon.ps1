# CaYaControl uygulama ikonu uretici.
# Koyu yuvarlak rozet uzerine cyan pervane (fan) + beyaz gobek. Cok boyutlu .ico uretir.
# Calistir:  powershell -ExecutionPolicy Bypass -File tools\gen-icon.ps1
Add-Type -AssemblyName System.Drawing

function New-FanBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    $rad = $s * 0.21

    # Yuvarlak kare govde yolu
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $rad * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    # Arka plan dikey gradyan (koyu slate)
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $c1 = [System.Drawing.Color]::FromArgb(255, 38, 42, 58)
    $c2 = [System.Drawing.Color]::FromArgb(255, 18, 20, 28)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 90.0)
    $g.FillPath($bg, $path)

    # Ince ust kenar isigi (hafif cizgi)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 120, 170, 220), [float]([Math]::Max(1.0, $s * 0.012)))
    $g.DrawPath($pen, $path)

    $cx = $s / 2; $cy = $s / 2
    $R = $s * 0.34

    # Yumusak ic parilti
    $glow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(45, 56, 189, 248))
    $gr = $R * 1.22
    $g.FillEllipse($glow, $cx - $gr, $cy - $gr, $gr * 2, $gr * 2)

    # Pervane: 3 kanat (radyal disa, hafif egik -> donme hissi)
    $petal = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.RectangleF(0, 0, $s, $s)),
        [System.Drawing.Color]::FromArgb(255, 94, 234, 212),
        [System.Drawing.Color]::FromArgb(255, 56, 160, 248), 90.0)
    $pw = $R * 0.66
    $ph = $R * 1.18
    $dist = $R * 0.54
    for ($i = 0; $i -lt 3; $i++) {
        $st = $g.Save()
        $g.TranslateTransform($cx, $cy)
        $g.RotateTransform([float]($i * 120))
        $g.TranslateTransform(0, -$dist)
        $g.RotateTransform(24.0)
        $g.FillEllipse($petal, -$pw / 2, -$ph / 2, $pw, $ph)
        $g.Restore($st)
    }

    # Gobek (beyaz) + ince halka
    $hubR = $R * 0.27
    $hub = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 236, 248, 255))
    $g.FillEllipse($hub, $cx - $hubR, $cy - $hubR, $hubR * 2, $hubR * 2)
    $ring = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(140, 30, 120, 170), [float]([Math]::Max(1.0, $s * 0.014)))
    $g.DrawEllipse($ring, $cx - $hubR, $cy - $hubR, $hubR * 2, $hubR * 2)

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($sz in $sizes) {
    $b = New-FanBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , ($ms.ToArray())
    $b.Dispose(); $ms.Dispose()
}

# .ico paketle (PNG-sikistirilmis girisler, Vista+ destekli)
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $pngs[$i]
    $dim = [Byte]($(if ($sz -ge 256) { 0 } else { $sz }))
    $bw.Write($dim); $bw.Write($dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $bw.Write($data) }
$bw.Flush()
$icoPath = Join-Path $PSScriptRoot "..\appicon.ico"
[System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath (Split-Path $icoPath)).Path + "\appicon.ico", $out.ToArray())
$bw.Dispose(); $out.Dispose()

# Onizleme PNG'leri (256 + 32) - inceleme icin, sonra silinir
(New-FanBitmap 256).Save((Join-Path $PSScriptRoot "..\_preview256.png"), [System.Drawing.Imaging.ImageFormat]::Png)
(New-FanBitmap 32).Save((Join-Path $PSScriptRoot "..\_preview32.png"), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output "appicon.ico ve onizlemeler uretildi."
