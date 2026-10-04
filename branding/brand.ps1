# XIV MCP's brand drawing, shared by the plugin icon (XivMcp/images/draw-icon.ps1) and the brand assets (draw-assets.ps1).
# The mark: a Final Fantasy crystal as the body of a satellite, tilted as if in orbit, with a solar panel on each side and waves sent
# from its tip. Drawn as rounded line art in a teal-to-blue gradient (in the style of the puni.sh icons), on a dark tile inside an
# unbroken gradient frame. Every coordinate is in a 512 x 512 design space.
Add-Type -AssemblyName System.Drawing

$Brand = @{
    From = '#3BEFC4'   # teal, top left
    To   = '#3F7BFF'   # blue, bottom right
    Tile = '#1C1C1E'   # the dark tile behind the mark
    Ink  = @(90, 50, 420, 470)   # the mark's own gradient line, so both colours show in it
    Tilt = -35         # degrees, as if in orbit
    Fit  = 0.96        # how much of the tile the mark fills
}

function Brand-Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function Brand-RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0) { $p.AddRectangle((New-Object System.Drawing.RectangleF ([single]$x), ([single]$y), ([single]$w), ([single]$h))); return $p }
    $d = [single]($r * 2)
    $p.AddArc([single]$x, [single]$y, $d, $d, 180, 90)
    $p.AddArc([single]($x + $w - $d), [single]$y, $d, $d, 270, 90)
    $p.AddArc([single]($x + $w - $d), [single]($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc([single]$x, [single]($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

function Brand-Pen($brush, [double]$width) {
    $p = New-Object System.Drawing.Pen $brush, ([single]$width)
    $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p.LineJoin = 'Round'; $p
}

function Brand-Points([double[]]$xy) {
    [System.Drawing.PointF[]]$(for ($i = 0; $i -lt $xy.Length; $i += 2) { New-Object System.Drawing.PointF ([single]$xy[$i]), ([single]$xy[$i + 1]) })
}

# The crystal's outline and facet cuts (cx 256, top 140, width 132, height 284).
$BrandCrystal = @{
    Outline = @(256, 140, 322, 213.84, 311.44, 344.48, 256, 424, 200.56, 344.48, 190, 213.84)
    Girdle  = @(197.92, 217.84, 256, 236.56, 314.08, 217.84)
    Spine   = @(256, 236.56, 256, 396)
    # The outline variant draws its facets instead of cutting them: girdle from edge to edge, spine to the tip, a highlight.
    OutlineGirdle = @(190, 213.84, 256, 236.56, 322, 213.84)
    OutlineSpine  = @(256, 236.56, 256, 424)
    Highlight     = @(229.6, 174.08, 213.76, 208.16)
}

<#
.SYNOPSIS
Draws the mark into $g, in the 512 design space mapped onto (x, y, size). $Tile: also draw the dark tile and the gradient frame.
$Background: the colour behind the mark where it cuts through (the tile colour unless drawn on something else).
#>
function Draw-BrandMark($g, [double]$x = 0, [double]$y = 0, [double]$size = 512, [switch]$Tile, [switch]$Outline, [string]$Background = $Brand.Tile) {
    $state = $g.Save()
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.TranslateTransform([single]$x, [single]$y); $g.ScaleTransform([single]($size / 512), [single]($size / 512))
    $back = New-Object System.Drawing.SolidBrush (Brand-Color $Background)
    if ($Tile) {
        $frame = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 512, 512), (Brand-Color $Brand.From), (Brand-Color $Brand.To)
        $g.FillPath($frame, (Brand-RoundRect 0 0 512 512 22))
        $g.FillPath($back, (Brand-RoundRect 18 18 476 476 12))
    }
    # The mark: fit into the tile, then tilted about the centre.
    $g.TranslateTransform(256, 256); $g.ScaleTransform([single]$Brand.Fit, [single]$Brand.Fit); $g.RotateTransform([single]$Brand.Tilt); $g.TranslateTransform(-256, -256)
    $i = $Brand.Ink
    $ink = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF $i[0], $i[1]), (New-Object System.Drawing.PointF $i[2], $i[3]), (Brand-Color $Brand.From), (Brand-Color $Brand.To)
    $line = Brand-Pen $ink 22
    $thin = Brand-Pen $ink 15
    $cut = Brand-Pen $back 12

    # Struts and solar panels.
    $g.DrawLine($line, 186, 268, 150, 268); $g.DrawLine($line, 326, 268, 362, 268)
    foreach ($px in 36, 360) {
        $panel = Brand-RoundRect $px 222 116 92 8
        $g.FillPath($back, $panel); $g.DrawPath($line, $panel)
        $g.DrawLine($thin, [single]($px + 58), 226, [single]($px + 58), 310)
        $g.DrawLine($thin, [single]($px + 4), 268, [single]($px + 112), 268)
    }
    # The crystal: filled with its facets cut out, or (outline variant) drawn in lines with its facets.
    $crystal = New-Object System.Drawing.Drawing2D.GraphicsPath
    $crystal.AddPolygon((Brand-Points $BrandCrystal.Outline))
    if ($Outline) {
        $g.FillPath($back, $crystal); $g.DrawPath($line, $crystal)
        $g.DrawLines($thin, (Brand-Points $BrandCrystal.OutlineGirdle))
        $s = $BrandCrystal.OutlineSpine; $g.DrawLine($thin, [single]$s[0], [single]$s[1], [single]$s[2], [single]$s[3])
        $h = $BrandCrystal.Highlight; $g.DrawLine($thin, [single]$h[0], [single]$h[1], [single]$h[2], [single]$h[3])
    } else {
        $g.FillPath($ink, $crystal); $g.DrawPath($line, $crystal)
        $g.DrawLines($cut, (Brand-Points $BrandCrystal.Girdle))
        $s = $BrandCrystal.Spine; $g.DrawLine($cut, [single]$s[0], [single]$s[1], [single]$s[2], [single]$s[3])
    }
    # Waves from the tip.
    foreach ($r in 44, 86) { $g.DrawArc($line, [single](256 - $r), [single](136 - $r), [single](2 * $r), [single](2 * $r), 225, 90) }
    $g.Restore($state)
}

<#
.SYNOPSIS
The mark as SVG. -Tile: the full icon (dark tile and gradient frame). Without it: the mark alone on a transparent background, for
logos on any page; the facet cuts and panel cells are then cut out of the mark instead of drawn in the tile colour.
#>
function Get-BrandSvg([switch]$Tile, [switch]$Outline) {
    $i = $Brand.Ink
    $o = ($BrandCrystal.Outline -join ' ')
    $girdle = ($BrandCrystal.Girdle -join ' ')
    $s = $BrandCrystal.Spine
    $panelFill = if ($Tile) { $Brand.Tile } else { 'none' }
    $cutColour = if ($Tile) { $Brand.Tile } else { 'black' }
    $crystal = if ($Outline) {
        $og = ($BrandCrystal.OutlineGirdle -join ' '); $os = $BrandCrystal.OutlineSpine; $h = $BrandCrystal.Highlight
        "      <polygon points=`"$o`" fill=`"$panelFill`" stroke-width=`"22`"/>`n" +
        "      <polyline points=`"$og`" stroke-width=`"15`"/>`n" +
        "      <path d=`"M$($os[0]) $($os[1])L$($os[2]) $($os[3])M$($h[0]) $($h[1])L$($h[2]) $($h[3])`" stroke-width=`"15`"/>"
    } else { "      <polygon points=`"$o`" fill=`"url(#ink)`" stroke-width=`"22`"/>" }
    $mark = @"
    <g transform="translate(256 256) scale($($Brand.Fit)) rotate($($Brand.Tilt)) translate(-256 -256)" fill="none" stroke="url(#ink)" stroke-linecap="round" stroke-linejoin="round">
      <path d="M186 268H150M326 268H362" stroke-width="22"/>
      <rect x="36" y="222" width="116" height="92" rx="8" fill="$panelFill" stroke-width="22"/>
      <rect x="360" y="222" width="116" height="92" rx="8" fill="$panelFill" stroke-width="22"/>
      <path d="M94 226V310M40 268H148M418 226V310M364 268H472" stroke-width="15"/>
$crystal
      <path d="$(Get-ArcPath 256 136 44 225 315) $(Get-ArcPath 256 136 86 225 315)" stroke-width="22"/>
    </g>
"@
    # Only the solid crystal has facets cut out of it.
    $cuts = if ($Outline) { "" } else { @"
    <g transform="translate(256 256) scale($($Brand.Fit)) rotate($($Brand.Tilt)) translate(-256 -256)" fill="none" stroke="$cutColour" stroke-width="12" stroke-linecap="round" stroke-linejoin="round">
      <polyline points="$girdle"/>
      <path d="M$($s[0]) $($s[1])L$($s[2]) $($s[3])"/>
    </g>
"@ }
    $defs = @"
  <defs>
    <linearGradient id="frame" x1="0" y1="0" x2="512" y2="512" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="$($Brand.From)"/><stop offset="1" stop-color="$($Brand.To)"/></linearGradient>
    <linearGradient id="ink" x1="$($i[0])" y1="$($i[1])" x2="$($i[2])" y2="$($i[3])" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="$($Brand.From)"/><stop offset="1" stop-color="$($Brand.To)"/></linearGradient>
"@
    if ($Tile) {
        return @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label="XIV MCP">
$defs  </defs>
  <rect width="512" height="512" rx="22" fill="url(#frame)"/>
  <rect x="18" y="18" width="476" height="476" rx="12" fill="$($Brand.Tile)"/>
$mark
$cuts
</svg>
"@
    }
    # Transparent: the cuts are a mask, so whatever is behind the logo shows through them.
    return @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label="XIV MCP">
$defs    <mask id="cuts" maskUnits="userSpaceOnUse" x="0" y="0" width="512" height="512">
      <rect width="512" height="512" fill="white"/>
$cuts
    </mask>
  </defs>
  <g mask="url(#cuts)">
$mark
  </g>
</svg>
"@
}

# An SVG arc path (centre, radius, from/to angle in degrees, clockwise as in GDI+).
function Get-ArcPath([double]$cx, [double]$cy, [double]$r, [double]$a1, [double]$a2) {
    $t1 = $a1 * [Math]::PI / 180; $t2 = $a2 * [Math]::PI / 180
    $x1 = [Math]::Round($cx + $r * [Math]::Cos($t1), 2); $y1 = [Math]::Round($cy + $r * [Math]::Sin($t1), 2)
    $x2 = [Math]::Round($cx + $r * [Math]::Cos($t2), 2); $y2 = [Math]::Round($cy + $r * [Math]::Sin($t2), 2)
    $large = if (($a2 - $a1) -gt 180) { 1 } else { 0 }
    "M$x1 $y1 A$r $r 0 $large 1 $x2 $y2"
}
