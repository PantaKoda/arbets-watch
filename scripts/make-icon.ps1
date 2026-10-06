# Regenerates src/ArbetsWatch.Desktop/Assets/ArbetsWatch.ico (multi-size, PNG-compressed entries).
# The design follows Repo Watch's HUD mark: a deep-space rounded square with a cyan frame, here holding a
# briefcase and a green "new" dot. Windows only (System.Drawing). Run from the repository root:
#   pwsh scripts/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([System.Drawing.RectangleF]$r, [float]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90); $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90); $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90); $path.CloseFigure()
    return $path
}

function New-IconPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 64.0

    # Rounded square background with a vertical navy gradient and a cyan frame.
    $r = [System.Drawing.RectangleF]::new(2 * $s, 2 * $s, 60 * $s, 60 * $s)
    $path = New-RoundedRect $r (14 * $s)
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $r, ([System.Drawing.Color]::FromArgb(255, 0x14, 0x24, 0x40)), ([System.Drawing.Color]::FromArgb(255, 0x06, 0x0A, 0x14)), 90.0
    $g.FillPath($fill, $path)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(160, 0x3F, 0xE0, 0xFF)), ([Math]::Max(1, 1.5 * $s))), $path)

    # Briefcase: handle, body outline, and a clasp line.
    $cyan = [System.Drawing.Color]::FromArgb(255, 0x3F, 0xE0, 0xFF)
    $pen = New-Object System.Drawing.Pen $cyan, ([Math]::Max(1.3, 3.4 * $s))
    $pen.LineJoin = 'Round'
    $g.DrawPath($pen, (New-RoundedRect ([System.Drawing.RectangleF]::new(25 * $s, 16 * $s, 14 * $s, 9 * $s)) (2.5 * $s)))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x0B, 0x13, 0x22))), (New-RoundedRect ([System.Drawing.RectangleF]::new(15 * $s, 23 * $s, 34 * $s, 24 * $s)) (4 * $s)))
    $g.DrawPath($pen, (New-RoundedRect ([System.Drawing.RectangleF]::new(15 * $s, 23 * $s, 34 * $s, 24 * $s)) (4 * $s)))
    $g.DrawLine($pen, 15 * $s, 33 * $s, 49 * $s, 33 * $s)

    # "New ad" dot.
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x3D, 0xFF, 0xA2))), 42 * $s, 10 * $s, 11 * $s, 11 * $s)

    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
    return , $stream.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = $sizes | ForEach-Object { , (New-IconPng $_) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]; $data = $images[$i]
    $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size }))); $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Flush()
$target = Join-Path (Get-Location) 'src/ArbetsWatch.Desktop/Assets/ArbetsWatch.ico'
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"Wrote $target ($($out.Length) bytes, sizes $($sizes -join ', '))"
