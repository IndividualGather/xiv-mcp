param([string]$Out = "icon.png", [int]$Size = 512)
# The plugin icon: line art on a dark square with a gradient frame (in the style of the puni.sh icons, in teal and blue).
# An aether crystal wired to three nodes: the game, connected.
Add-Type -AssemblyName System.Drawing
$S = $Size / 512.0
function P([double]$v) { [single]($v * $S) }
function Pt([double]$x, [double]$y) { New-Object System.Drawing.PointF (P $x), (P $y) }
function Rect([double]$x, [double]$y, [double]$w, [double]$h) { New-Object System.Drawing.RectangleF (P $x), (P $y), (P $w), (P $h) }
function Col([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
function RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc((P $x), (P $y), (P $d), (P $d), 180, 90)
    $p.AddArc((P ($x + $w - $d)), (P $y), (P $d), (P $d), 270, 90)
    $p.AddArc((P ($x + $w - $d)), (P ($y + $h - $d)), (P $d), (P $d), 0, 90)
    $p.AddArc((P $x), (P ($y + $h - $d)), (P $d), (P $d), 90, 90)
    $p.CloseFigure(); $p
}
function Poly($g, $pen, [double[]]$xy) {
    $pts = for ($i = 0; $i -lt $xy.Length; $i += 2) { Pt $xy[$i] $xy[$i + 1] }
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
}

$from = '#3BEFC4'   # teal, top left
$to = '#3F7BFF'     # blue, bottom right
$tile = '#1C1C1E'

$bmp = New-Object System.Drawing.Bitmap $Size, $Size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
$g.Clear([System.Drawing.Color]::Transparent)

# Tile: the gradient frame, then the dark square inside it. No corner mark: the frame runs unbroken all the way round.
$frame = New-Object System.Drawing.Drawing2D.LinearGradientBrush (Pt 0 0), (Pt 512 512), (Col $from), (Col $to)
$g.FillPath($frame, (RoundRect 0 0 512 512 22))
$g.FillPath((New-Object System.Drawing.SolidBrush (Col $tile)), (RoundRect 18 18 476 476 12))

# The drawing: shrunk a little around the centre so it has room inside the frame, with its own gradient across its extent (so both
# colours show in it, not just the middle of the frame's).
$g.TranslateTransform((P 256), (P 258)); $g.ScaleTransform(0.86, 0.86); $g.TranslateTransform((P -256), (P -256))
$ink = New-Object System.Drawing.Drawing2D.LinearGradientBrush (Pt 130 70), (Pt 390 440), (Col $from), (Col $to)
function Pen([double]$w) {
    $p = New-Object System.Drawing.Pen $ink, (P $w)
    $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p.LineJoin = 'Round'; $p
}
$line = Pen 22
$thin = Pen 16

# The crystal: a long six-sided gem with its facet lines.
$crystal = New-Object System.Drawing.Drawing2D.GraphicsPath
$crystal.AddPolygon(@((Pt 256 84), (Pt 318 182), (Pt 300 330), (Pt 256 424), (Pt 212 330), (Pt 194 182)))
$g.DrawPath($line, $crystal)
Poly $g $thin @(194, 182, 256, 222, 318, 182)
$g.DrawLine($thin, (Pt 256 222), (Pt 256 424))

# Wires from its sides to two nodes below...
Poly $g $line @(203, 256, 126, 256, 126, 318)
$g.DrawEllipse($line, (Rect 104 318 44 44))
Poly $g $line @(309, 256, 386, 256, 386, 318)
$g.DrawEllipse($line, (Rect 364 318 44 44))
# ...and from its upper edge to a filled node: the one that's live.
Poly $g $line @(293, 143, 352, 143, 352, 116)
$g.FillEllipse($ink, (Rect 328 68 48 48))

$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out ($Size x $Size)"
