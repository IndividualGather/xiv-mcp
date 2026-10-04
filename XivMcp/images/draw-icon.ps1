param([string]$Out = "icon.png", [int]$Size = 512, [switch]$Outline)
# The plugin icon: the crystal satellite on its tile (see branding/brand.ps1, shared with the banner and the docs assets).
# -Outline draws the outline variant of the crystal.
. (Join-Path $PSScriptRoot '..\..\branding\brand.ps1')
$bmp = New-Object System.Drawing.Bitmap $Size, $Size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Transparent)
Draw-BrandMark $g 0 0 $Size -Tile -Outline:$Outline
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out ($Size x $Size)"
