# Generates XIV MCP's brand assets from brand.ps1: the README banner, the icons and logos (PNG and SVG), and the files the docs site
# (Fumadocs on Next.js) needs, named after the Next.js app-folder conventions. Run it again after changing brand.ps1.
#   powershell -File branding/draw-assets.ps1
. (Join-Path $PSScriptRoot 'brand.ps1')
$out = $PSScriptRoot
$web = Join-Path $out 'web'
New-Item -ItemType Directory -Force $web | Out-Null

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    @{ Bitmap = $bmp; G = $g }
}

function Save-Canvas($c, [string]$path) {
    $c.G.Dispose()
    $c.Bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $c.Bitmap.Dispose()
    Write-Output "  $([IO.Path]::GetFileName((Split-Path $path -Parent)))/$([IO.Path]::GetFileName($path))"
}

function Save-Utf8([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false))
    Write-Output "  $([IO.Path]::GetFileName((Split-Path $path -Parent)))/$([IO.Path]::GetFileName($path))"
}

# The icon (tile) at a size, as PNG bytes; for the .ico.
function Get-IconPng([int]$size, [switch]$Outline) {
    $c = New-Canvas $size $size
    Draw-BrandMark $c.G 0 0 $size -Tile -Outline:$Outline
    $c.G.Dispose()
    $ms = New-Object IO.MemoryStream
    $c.Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $c.Bitmap.Dispose()
    , $ms.ToArray()
}

# A Windows .ico holding PNG images (supported by every current browser).
function Save-Ico([string]$path, [int[]]$sizes) {
    $images = $sizes | ForEach-Object { , (Get-IconPng $_) }
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $bytes = $images[$i]
        $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$bytes.Length); $w.Write([uint32]$offset)
        $offset += $bytes.Length
    }
    foreach ($bytes in $images) { $w.Write([byte[]]$bytes) }
    $w.Flush(); [IO.File]::WriteAllBytes($path, $ms.ToArray())
    Write-Output "  web/$([IO.Path]::GetFileName($path))"
}

# Text as a path filled with the brand gradient (left to right across the text).
function Draw-GradientText($g, [string]$text, [string]$family, [System.Drawing.FontStyle]$style, [double]$px, [double]$x, [double]$y) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.FillMode = 'Winding'   # overlapping glyph outlines (the X, the P) stay filled
    $path.AddString($text, (New-Object System.Drawing.FontFamily $family), [int]$style, [single]$px, (New-Object System.Drawing.PointF ([single]$x), ([single]$y)), [System.Drawing.StringFormat]::GenericTypographic)
    $b = $path.GetBounds()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF $b.Left, $b.Top), (New-Object System.Drawing.PointF $b.Right, $b.Bottom), (Brand-Color $Brand.From), (Brand-Color $Brand.To)
    $g.FillPath($brush, $path)
    $b
}

function Draw-Text($g, [string]$text, [string]$family, [System.Drawing.FontStyle]$style, [double]$px, [string]$color, [double]$x, [double]$y) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.FillMode = 'Winding'   # overlapping glyph outlines (the X, the P) stay filled
    $path.AddString($text, (New-Object System.Drawing.FontFamily $family), [int]$style, [single]$px, (New-Object System.Drawing.PointF ([single]$x), ([single]$y)), [System.Drawing.StringFormat]::GenericTypographic)
    $g.FillPath((New-Object System.Drawing.SolidBrush (Brand-Color $color)), $path)
    $path.GetBounds()
}

function Measure-Text([string]$text, [string]$family, [System.Drawing.FontStyle]$style, [double]$px) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.FillMode = 'Winding'   # overlapping glyph outlines (the X, the P) stay filled
    $path.AddString($text, (New-Object System.Drawing.FontFamily $family), [int]$style, [single]$px, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
    $path.GetBounds()
}

# A dark card with the gradient frame, and faint waves in a corner: the background of the banner and the social image.
function Draw-Card($g, [int]$w, [int]$h, [double]$radius, [double]$border) {
    $frame = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $w, $h), (Brand-Color $Brand.From), (Brand-Color $Brand.To)
    $g.FillPath($frame, (Brand-RoundRect 0 0 $w $h $radius))
    $g.FillPath((New-Object System.Drawing.SolidBrush (Brand-Color '#141518')), (Brand-RoundRect $border $border ($w - 2 * $border) ($h - 2 * $border) ($radius - $border * 0.6)))
    # Faint waves spreading from the bottom right, clipped to the card.
    $state = $g.Save()
    $g.SetClip((Brand-RoundRect $border $border ($w - 2 * $border) ($h - 2 * $border) ($radius - $border * 0.6)))
    $wave = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(22, 63, 123, 255)), ([single]($h * 0.05))
    $wave.StartCap = 'Round'; $wave.EndCap = 'Round'
    foreach ($k in 1..4) { $r = $h * 0.34 * $k; $g.DrawArc($wave, [single]($w - $r * 0.55), [single]($h - $r * 0.55), [single](2 * $r), [single](2 * $r), 180, 90) }
    $g.Restore($state)
}

Write-Output "branding/"

# ---- icons and logos
foreach ($variant in @(@{ Suffix = ''; Outline = $false }, @{ Suffix = '-outline'; Outline = $true })) {
    $c = New-Canvas 512 512; Draw-BrandMark $c.G 0 0 512 -Tile -Outline:$variant.Outline; Save-Canvas $c (Join-Path $out "icon$($variant.Suffix).png")
    Save-Utf8 (Join-Path $out "icon$($variant.Suffix).svg") (Get-BrandSvg -Tile -Outline:$variant.Outline)
    Save-Utf8 (Join-Path $out "logo$($variant.Suffix).svg") (Get-BrandSvg -Outline:$variant.Outline)
    # The mark alone on transparent, as PNG (e.g. for places that don't take SVG). Solid crystals cut through to transparency.
    $c = New-Canvas 512 512; Draw-BrandMark $c.G 0 0 512 -Outline:$variant.Outline -Background '#00000000'; Save-Canvas $c (Join-Path $out "logo$($variant.Suffix).png")
}

# ---- README banner: 1600 x 400, shown at 800 x 200
$w = 1600; $h = 400
$c = New-Canvas $w $h
Draw-Card $c.G $w $h 36 6
Draw-BrandMark $c.G 72 64 272 -Tile
$name = Draw-GradientText $c.G 'XIV MCP' 'Bahnschrift SemiBold' ([System.Drawing.FontStyle]::Regular) 148 404 70
Draw-Text $c.G 'Uplink AI assistants to Final Fantasy XIV' 'Segoe UI Semibold' ([System.Drawing.FontStyle]::Regular) 46 '#E6EDF3' 410 ($name.Bottom + 34) | Out-Null
Draw-Text $c.G "Dalamud plugin  $([char]0x00B7)  Model Context Protocol server" 'Segoe UI' ([System.Drawing.FontStyle]::Regular) 32 '#8B949E' 412 ($name.Bottom + 104) | Out-Null
Save-Canvas $c (Join-Path $out 'banner.png')

# ---- docs site (Next.js app folder conventions; see README.md in this folder)
Write-Output "branding/web/"
Save-Ico (Join-Path $web 'favicon.ico') @(16, 32, 48)
$c = New-Canvas 512 512; Draw-BrandMark $c.G 0 0 512 -Tile; Save-Canvas $c (Join-Path $web 'icon.png')
# Apple touch icon: opaque (iOS rounds the corners itself), the tile filling the square.
$c = New-Canvas 180 180
$c.G.FillRectangle((New-Object System.Drawing.SolidBrush (Brand-Color $Brand.Tile)), 0, 0, 180, 180)
Draw-BrandMark $c.G 0 0 180 -Tile
Save-Canvas $c (Join-Path $web 'apple-icon.png')
Copy-Item (Join-Path $out 'logo.svg') (Join-Path $web 'logo.svg') -Force; Write-Output "  web/logo.svg"
Copy-Item (Join-Path $out 'icon.svg') (Join-Path $web 'icon.svg') -Force; Write-Output "  web/icon.svg"

# Social preview (Open Graph / Twitter): 1200 x 630, everything centred.
$w = 1200; $h = 630
$c = New-Canvas $w $h
Draw-Card $c.G $w $h 0 8
Draw-BrandMark $c.G (($w - 236) / 2) 92 236 -Tile
$m = Measure-Text 'XIV MCP' 'Bahnschrift SemiBold' ([System.Drawing.FontStyle]::Regular) 112
$name = Draw-GradientText $c.G 'XIV MCP' 'Bahnschrift SemiBold' ([System.Drawing.FontStyle]::Regular) 112 (($w - $m.Width) / 2 - $m.Left) 352
$t = 'Uplink AI assistants to Final Fantasy XIV'
$m = Measure-Text $t 'Segoe UI Semibold' ([System.Drawing.FontStyle]::Regular) 40
Draw-Text $c.G $t 'Segoe UI Semibold' ([System.Drawing.FontStyle]::Regular) 40 '#E6EDF3' (($w - $m.Width) / 2 - $m.Left) ($name.Bottom + 34) | Out-Null
Save-Canvas $c (Join-Path $web 'opengraph-image.png')
Copy-Item (Join-Path $web 'opengraph-image.png') (Join-Path $web 'twitter-image.png') -Force; Write-Output "  web/twitter-image.png"
