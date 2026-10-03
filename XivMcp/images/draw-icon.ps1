param([string]$Out = "icon.png", [int]$Size = 512)
Add-Type -AssemblyName System.Drawing
$S = $Size / 512.0
function P([double]$v) { [single]($v * $S) }
function Rect([double]$x, [double]$y, [double]$w, [double]$h) { New-Object System.Drawing.RectangleF (P $x), (P $y), (P $w), (P $h) }
function Col([string]$hex, [int]$a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B) }
function RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc((P $x), (P $y), (P $d), (P $d), 180, 90)
    $p.AddArc((P ($x + $w - $d)), (P $y), (P $d), (P $d), 270, 90)
    $p.AddArc((P ($x + $w - $d)), (P ($y + $h - $d)), (P $d), (P $d), 0, 90)
    $p.AddArc((P $x), (P ($y + $h - $d)), (P $d), (P $d), 90, 90)
    $p.CloseFigure(); $p
}
function VGrad([double]$x, [double]$y, [double]$w, [double]$h, [string]$c1, [string]$c2) {
    New-Object System.Drawing.Drawing2D.LinearGradientBrush (Rect $x $y $w $h), (Col $c1), (Col $c2), ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
}
function Glow($g, [double]$cx, [double]$cy, [double]$r, [string]$hex, [int]$alpha) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddEllipse((Rect ($cx - $r) ($cy - $r) ($r * 2) ($r * 2)))
    $b = New-Object System.Drawing.Drawing2D.PathGradientBrush $p
    $b.CenterColor = Col $hex $alpha
    $b.SurroundColors = @((Col $hex 0))
    $g.FillPath($b, $p)
}

$bmp = New-Object System.Drawing.Bitmap $Size, $Size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
$g.Clear([System.Drawing.Color]::Transparent)

# Background tile: deep night-blue with a soft aether glow
$bg = RoundRect 8 8 496 496 96
$g.FillPath((VGrad 8 8 496 496 '#24345c' '#121a30'), $bg)
Glow $g 256 250 230 '#4fd8ff' 70
$g.DrawPath((New-Object System.Drawing.Pen (Col '#5b79b8' 160), (P 6)), $bg)

# Signal arcs from the crystal (the "connection")
$arcPen = New-Object System.Drawing.Pen (Col '#7fe9ff' 200), (P 12)
$arcPen.StartCap = 'Round'; $arcPen.EndCap = 'Round'
foreach ($r in 42, 70) {
    $g.DrawArc($arcPen, (Rect (256 - $r) (78 - $r) ($r * 2) ($r * 2)), 205, 40)
    $g.DrawArc($arcPen, (Rect (256 - $r) (78 - $r) ($r * 2) ($r * 2)), 295, 40)
}

# Ground shadow
$g.FillEllipse((New-Object System.Drawing.SolidBrush (Col '#000000' 90)), (Rect 150 432 212 30))

# Legs
$legBrush = VGrad 0 380 512 70 '#9a6a34' '#5e3d1c'
$g.FillPath($legBrush, (RoundRect 194 380 44 62 16))
$g.FillPath($legBrush, (RoundRect 274 380 44 62 16))
$g.FillPath((VGrad 0 430 512 20 '#3a2a1c' '#241912'), (RoundRect 184 428 64 22 11))
$g.FillPath((VGrad 0 430 512 20 '#3a2a1c' '#241912'), (RoundRect 264 428 64 22 11))

# Arms
$armBrush = VGrad 0 300 512 90 '#c8944c' '#7d5428'
$g.FillPath($armBrush, (RoundRect 130 302 40 86 20))
$g.FillPath($armBrush, (RoundRect 342 302 40 86 20))
$g.FillEllipse((VGrad 0 370 512 30 '#e7c27e' '#a0703a'), (Rect 128 368 44 30))
$g.FillEllipse((VGrad 0 370 512 30 '#e7c27e' '#a0703a'), (Rect 340 368 44 30))

# Body (barrel)
$body = RoundRect 160 286 192 118 46
$g.FillPath((VGrad 160 286 192 118 '#d9a95c' '#8a5a2b'), $body)
$g.DrawPath((New-Object System.Drawing.Pen (Col '#5a3a1a' 200), (P 5)), $body)
# Belly aether core with gear ring
Glow $g 256 346 54 '#6ff0ff' 160
$gearPen = New-Object System.Drawing.Pen (Col '#5a3a1a'), (P 7)
$g.DrawEllipse($gearPen, (Rect 226 316 60 60))
for ($i = 0; $i -lt 8; $i++) {
    $a = $i * [Math]::PI / 4
    $x1 = 256 + [Math]::Cos($a) * 30; $y1 = 346 + [Math]::Sin($a) * 30
    $x2 = 256 + [Math]::Cos($a) * 40; $y2 = 346 + [Math]::Sin($a) * 40
    $g.DrawLine($gearPen, (P $x1), (P $y1), (P $x2), (P $y2))
}
$g.FillEllipse((VGrad 236 326 40 40 '#c9fbff' '#2fb6e8'), (Rect 238 328 36 36))

# Neck collar
$g.FillPath((VGrad 0 268 512 30 '#6e4a24' '#4a3018'), (RoundRect 200 266 112 28 12))

# Head (big rounded dome)
$head = RoundRect 132 130 248 150 70
$g.FillPath((VGrad 132 130 248 150 '#f0d090' '#b07c3e'), $head)
$g.DrawPath((New-Object System.Drawing.Pen (Col '#5a3a1a' 220), (P 6)), $head)
# Highlight on the dome
$g.FillEllipse((New-Object System.Drawing.SolidBrush (Col '#ffffff' 60)), (Rect 168 142 120 40))
# Visor band
$visor = RoundRect 158 186 196 64 30
$g.FillPath((VGrad 158 186 196 64 '#1e2740' '#0d1222'), $visor)
$g.DrawPath((New-Object System.Drawing.Pen (Col '#3a4a70'), (P 4)), $visor)
# Glowing eyes
foreach ($ex in 212, 300) {
    Glow $g $ex 218 34 '#5ff2ff' 200
    $g.FillEllipse((VGrad ($ex - 15) 203 30 30 '#e8ffff' '#39c8f0'), (Rect ($ex - 15) 203 30 30))
}
# Rivets
$rivet = New-Object System.Drawing.SolidBrush (Col '#7a5228')
foreach ($rx in 146, 354) { $g.FillEllipse($rivet, (Rect ($rx - 6) 208 12 12)) }
foreach ($rx in 178, 334) { $g.FillEllipse($rivet, (Rect ($rx - 5) 300 10 10)) }

# Antenna + aether crystal
$g.DrawLine((New-Object System.Drawing.Pen (Col '#6e4a24'), (P 9)), (P 256), (P 132), (P 256), (P 98))
Glow $g 256 76 44 '#7ff6ff' 190
$crystal = New-Object System.Drawing.Drawing2D.GraphicsPath
$crystal.AddPolygon(@(
    (New-Object System.Drawing.PointF (P 256), (P 44)),
    (New-Object System.Drawing.PointF (P 276), (P 76)),
    (New-Object System.Drawing.PointF (P 256), (P 104)),
    (New-Object System.Drawing.PointF (P 236), (P 76))))
$g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush (Rect 236 44 40 60), (Col '#eaffff'), (Col '#2c8fe0'), 60.0), $crystal)
$g.DrawPath((New-Object System.Drawing.Pen (Col '#1d5fa8'), (P 3)), $crystal)
$g.DrawLine((New-Object System.Drawing.Pen (Col '#ffffff' 170), (P 3)), (P 256), (P 50), (P 247), (P 76))

$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"saved $Out"
