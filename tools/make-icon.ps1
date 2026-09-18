# 用 Windows 内置的 Segoe Fluent Icons 字体生成托盘图标
#
# 用法：
#   pwsh -NoProfile -File make-icon.ps1 -Preview                 # 生成候选字形对照表 icon-preview.png
#   pwsh -NoProfile -File make-icon.ps1 -Glyph E765 -Out icon.ico  # 生成多尺寸 ICO
#
# 托盘图标的尺寸要求：Windows 按 DPI 选用 16/20/24/32 像素（100%/125%/150%/200%），
# 48 和 256 是给资源管理器大图标视图用的。ICO 里必须把这几档都放进去，只放一张会糊。
param(
    [string]$Glyph = 'E765',          # 字形码位（十六进制，Segoe Fluent Icons 的私用区）
    [string]$Out = 'icon.ico',
    [string]$FontName = 'Segoe Fluent Icons',
    [switch]$Preview
)

Add-Type -AssemblyName System.Drawing

$Sizes = @(16, 20, 24, 32, 48, 256)

function New-GlyphBitmap([int]$size, [int]$code, [string]$font) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        # 小尺寸用 GridFit 更清晰；这里统一用 AntiAliasGridFit
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $emSize = [double]$size * 0.92          # 留一点边距，字形不要顶到边
        $f = New-Object System.Drawing.Font($font, $emSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        try {
            $fmt = New-Object System.Drawing.StringFormat
            $fmt.Alignment = [System.Drawing.StringAlignment]::Center
            $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
            $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
            $g.DrawString([char]$code, $f, [System.Drawing.Brushes]::Black, $rect, $fmt)
        } finally { $f.Dispose() }
    } finally { $g.Dispose() }
    return $bmp
}

function Write-Ico([System.Collections.ArrayList]$bitmaps, [string]$path) {
    $pngs = @()
    foreach ($b in $bitmaps) {
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , $ms.ToArray()
        $ms.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    try {
        $bw = New-Object System.IO.BinaryWriter($fs)
        $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$bitmaps.Count)   # ICONDIR
        $offset = 6 + 16 * $bitmaps.Count
        for ($i = 0; $i -lt $bitmaps.Count; $i++) {
            $w = $bitmaps[$i].Width
            $dim = [byte]$(if ($w -ge 256) { 0 } else { $w })                            # 256 要写 0
            $bw.Write($dim)                                                             # 宽
            $bw.Write($dim)                                                             # 高
            $bw.Write([byte]0); $bw.Write([byte]0)                                      # 调色板数、保留
            $bw.Write([uint16]1); $bw.Write([uint16]32)                                 # 平面数、位深
            $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
            $offset += $pngs[$i].Length
        }
        foreach ($p in $pngs) { $bw.Write($p) }
        $bw.Flush()
    } finally { $fs.Dispose() }
}

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $dir          # icon.ico 要和 exe 放一起（托盘运行时也读它）

if ($Preview) {
    # 候选字形对照表：每格上面是字形，下面是码位，方便挑一个
    $cands = @(
        'E765','E766','E767','E768','E769','E76A','E76B','E76C',
        'E774','E775','E776','E777','E778','E779','E77A','E77B',
        'E713','E715','E721','E72D','E8AB','E895','E8C8','E8BD',
        'E7C4','E8F1','E8F4','E945','EA80','E9D9','E704','E71D'
    )
    $cell = 110; $cols = 8
    $rows = [math]::Ceiling($cands.Count / $cols)
    $bmp = [System.Drawing.Bitmap]::new(($cell * $cols), ($cell * $rows))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::White)
    $font = New-Object System.Drawing.Font($FontName, 54, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $label = New-Object System.Drawing.Font('Consolas', 13, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    for ($i = 0; $i -lt $cands.Count; $i++) {
        $x = ($i % $cols) * $cell; $y = [math]::Floor($i / $cols) * $cell
        $rect = [System.Drawing.RectangleF]::new($x, ($y + 8), $cell, 64)
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString([char][Convert]::ToInt32($cands[$i], 16), $font, [System.Drawing.Brushes]::Black, $rect, $fmt)
        $lr = [System.Drawing.RectangleF]::new($x, ($y + 76), $cell, 20)
        $g.DrawString($cands[$i], $label, [System.Drawing.Brushes]::Gray, $lr, $fmt)
    }
    $g.Dispose(); $font.Dispose(); $label.Dispose()
    $out = Join-Path $root 'icon-preview.png'
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "候选字形对照表已生成: $out"
    return
}

$code = [Convert]::ToInt32($Glyph, 16)
$bitmaps = New-Object System.Collections.ArrayList
foreach ($s in $Sizes) { [void]$bitmaps.Add((New-GlyphBitmap $s $code $FontName)) }
$target = Join-Path $root $Out
Write-Ico $bitmaps $target
foreach ($b in $bitmaps) { $b.Dispose() }

$fi = Get-Item $target
Write-Output ("已生成 $($fi.FullName)  ($($fi.Length) 字节)")
Write-Output ("含尺寸: " + ($Sizes -join ', ') + " px")
