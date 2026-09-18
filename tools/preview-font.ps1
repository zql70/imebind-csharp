# 查看/预览 Windows 内置的图标字体（Segoe Fluent Icons / Segoe MDL2 Assets）
#
# 用法：
#   pwsh -NoProfile -File preview-font.ps1 -Info              # 字体文件位置、格式、许可信息
#   pwsh -NoProfile -File preview-font.ps1 -Survey            # 统计各码位区间的字形数量
#   pwsh -NoProfile -File preview-font.ps1 -Sheet E700        # 生成某个 256 码位区间的字形对照表
#
# 图标字体是 TrueType(.ttf)，字形放在 Unicode 私用区(PUA, U+E000–U+F8FF)，
# 所以既不能像普通文字那样输入，也不能靠"输入某个字"来找到想要的图标 —— 必须按码位渲染。

param(
    [switch]$Info,
    [switch]$Survey,
    [string]$Sheet = '',
    [string]$FontName = 'Segoe Fluent Icons',
    [int]$Start = 0xE700,
    [int]$End = 0xF8FF
)

Add-Type -AssemblyName System.Drawing

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $dir          # 生成的对照表放到仓库根目录

function Get-FontFile([string]$font) {
    # 从注册表拿字体文件名，再去 C:\Windows\Fonts 确认
    $map = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts'
    $out = @()
    foreach ($p in $map.PSObject.Properties) {
        if ($p.Name -like 'PS*') { continue }
        if ($p.Name -match [regex]::Escape($font)) {
            $file = [string]$p.Value
            $path = if ([System.IO.Path]::IsPathRooted($file)) { $file } else { Join-Path $env:WINDIR "Fonts\$file" }
            $out += [pscustomobject]@{ Name = $p.Name; Path = $path; Exists = (Test-Path $path) }
        }
    }
    return $out
}

# TTF/OTF 采用大端序，而 .NET 的 BinaryReader 是小端序，必须自己按字节拼
function ReadBE16($br) { $b = $br.ReadBytes(2); return ([int]$b[0] -shl 8) -bor [int]$b[1] }
function ReadBE32($br) {
    $b = $br.ReadBytes(4)
    return ([uint32]$b[0] -shl 24) -bor ([uint32]$b[1] -shl 16) -bor ([uint32]$b[2] -shl 8) -bor [uint32]$b[3]
}

function Get-SfntInfo([string]$path) {
    # 读 sfnt 版本标签和 name 表里的许可/描述（name ID 5/13/14）
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $ver = $br.ReadBytes(4)
        $flavor = switch ([System.Text.Encoding]::ASCII.GetString($ver)) {
            'OTTO' { 'OpenType/CFF (OTTO)' }
            'true' { 'TrueType (Apple)' }
            default { if ($ver[0] -eq 0 -and $ver[1] -eq 1) { 'TrueType (glyf)' } else { '未知: ' + ($ver -join ',') } }
        }
        $numTables = ReadBE16 $br
        $fs.Position = 12                                 # 表目录从偏移 12 开始
        $nameOff = 0
        for ($i = 0; $i -lt $numTables; $i++) {
            $tag = [System.Text.Encoding]::ASCII.GetString($br.ReadBytes(4))
            [void](ReadBE32 $br)                              # checksum
            $off = ReadBE32 $br
            [void](ReadBE32 $br)                              # length
            if ($tag -eq 'name') { $nameOff = $off }
        }
        $license = ''; $licUrl = ''; $verStr = ''
        if ($nameOff -gt 0 -and $nameOff -lt $fs.Length) {
            $fs.Position = $nameOff
            [void](ReadBE16 $br)                              # format
            $count = ReadBE16 $br
            $strOff = ReadBE16 $br
            for ($i = 0; $i -lt $count; $i++) {
                [void](ReadBE16 $br); [void](ReadBE16 $br); [void](ReadBE16 $br)   # platform, encoding, language
                $id = ReadBE16 $br
                $len = ReadBE16 $br
                $off = ReadBE16 $br
                if (($id -notin 5, 13, 14) -or ($id -eq 0)) { continue }
                $save = $fs.Position
                $fs.Position = $nameOff + $strOff + $off
                $txt = [System.Text.Encoding]::BigEndianUnicode.GetString($br.ReadBytes($len))
                if ($id -eq 5) { $verStr = $txt }
                elseif ($id -eq 13) { $license = $txt }
                elseif ($id -eq 14) { $licUrl = $txt }
                $fs.Position = $save
            }
        }
        return [pscustomobject]@{
            Flavor = $flavor; Tables = $numTables; SizeKB = [math]::Round((Get-Item $path).Length / 1KB)
            Version = $verStr; License = $license; LicenseUrl = $licUrl
        }
    } finally { $fs.Dispose() }
}

function Get-BmpSignature([System.Drawing.Bitmap]$bmp) {
    # 取像素字节的签名，用来和"缺字方块"比对
    $rect = [System.Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $len = [Math]::Abs($data.Stride) * $bmp.Height
        $bytes = New-Object byte[] $len
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $len)
        return [Convert]::ToBase64String($bytes)
    } finally { $bmp.UnlockBits($data) }
}

function New-GlyphBitmap([int]$code, [string]$font, [int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $f = [System.Drawing.Font]::new($font, [single]($size * 0.92), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = [System.Drawing.StringFormat]::new(); $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString([char]$code, $f, [System.Drawing.Brushes]::Black, [System.Drawing.RectangleF]::new(0, 0, $size, $size), $fmt)
    $g.Dispose(); $f.Dispose()
    return $bmp
}

# 图标字体里没有汉字，所以渲染一个汉字得到的就是"缺字方块"的基准签名
$script:NotdefSig = $null

function Test-GlyphExists([int]$code, [string]$font, [int]$size = 24) {
    if ($null -eq $script:NotdefSig) {
        $b = New-GlyphBitmap 0x4E00 $font $size          # U+4E00 汉字"一"
        $script:NotdefSig = Get-BmpSignature $b
        $b.Dispose()
    }
    $bmp = New-GlyphBitmap $code $font $size
    try { return ((Get-BmpSignature $bmp) -ne $script:NotdefSig) }
    finally { $bmp.Dispose() }
}

if (-not ($Info -or $Survey -or $Sheet)) { $Info = $true }

if ($Info) {
    Write-Output "=== $FontName ==="
    $files = Get-FontFile $FontName
    if (-not $files) { Write-Output "  注册表里没找到该字体" }
    foreach ($f in $files) {
        Write-Output ("  注册名 : " + $f.Name)
        Write-Output ("  文件   : " + $f.Path + "   (存在=" + $f.Exists + ")")
        if ($f.Exists) {
            $sf = Get-SfntInfo $f.Path
            Write-Output ("  格式   : " + $sf.Flavor + "，表数量 " + $sf.Tables + "，文件大小 " + $sf.SizeKB + " KB")
            Write-Output ("  版本   : " + $sf.Version)
            if ($sf.License) { Write-Output ("  许可   : " + ($sf.License -replace "\s+", ' ').Substring(0, [Math]::Min(300, $sf.License.Length))) }
            if ($sf.LicenseUrl) { Write-Output ("  许可链接: " + $sf.LicenseUrl) }
        }
    }
    Write-Output ""
    Write-Output "另外可预览的内置图标字体：Segoe MDL2 Assets（Win10/Win11 都有，兼容性更好）"
}

if ($Survey) {
    Write-Output "=== 各 256 码位区间的字形数量（$FontName）==="
    $total = 0
    for ($base = 0xE000; $base -le 0xF800; $base += 0x100) {
        $n = 0
        for ($c = $base; $c -lt $base + 0x100; $c++) {
            if (Test-GlyphExists $c $FontName 24) { $n++ }
        }
        if ($n -gt 0) {
            Write-Output ("  U+{0:X4}-U+{1:X4} : {2} 个" -f $base, ($base + 0xFF), $n)
            $total += $n
        }
    }
    Write-Output ("  合计 $total 个字形")
}

if ($Sheet) {
    $base = [Convert]::ToInt32($Sheet, 16)
    $cols = 16; $cell = 56
    $bmp = [System.Drawing.Bitmap]::new(($cell * $cols), ($cell * 16 + 24))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::White)
    $label = [System.Drawing.Font]::new('Consolas', 9)
    $f = [System.Drawing.Font]::new($FontName, 26, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = [System.Drawing.StringFormat]::new(); $fmt.Alignment = 'Center'
    for ($i = 0; $i -lt 256; $i++) {
        $code = $base + $i
        if (-not (Test-GlyphExists $code $FontName 24)) { continue }   # 空码位不画
        $x = ($i % $cols) * $cell; $y = [math]::Floor($i / $cols) * $cell + 14
        $g.DrawString([char]$code, $f, [System.Drawing.Brushes]::Black,
            [System.Drawing.RectangleF]::new($x, $y, $cell, 34), $fmt)
        $g.DrawString(('{0:X4}' -f $code), $label, [System.Drawing.Brushes]::Silver,
            [System.Drawing.RectangleF]::new($x, ($y + 36), $cell, 12), $fmt)
    }
    $g.Dispose(); $f.Dispose(); $label.Dispose()
    $out = Join-Path $root ("font-sheet-{0:X4}.png" -f $base)
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "已生成 $out"
}
