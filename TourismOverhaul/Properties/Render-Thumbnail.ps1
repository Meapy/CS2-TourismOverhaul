<#
.SYNOPSIS
    Renders Thumbnail.png from the same artwork as Thumbnail.svg, without a browser.

.DESCRIPTION
    make-thumbnail.html needs a person to open it and click a button, which makes the publish step
    impossible to run unattended. This draws the identical composition with GDI+ instead, so the
    thumbnail can be produced from the same script that builds and publishes.

    Coordinates below are the 512-space coordinates from Thumbnail.svg. The hotel and motel are the
    32-space zone icons from ui/src/images, placed with a translate and a scale exactly as the SVG
    does. Keep all three files in step when the artwork changes.

    Polygons are passed as flat x,y,x,y arrays rather than jagged [double[][]] ones. PowerShell's
    coercion of nested array literals into a jagged parameter is unreliable and fails with
    "does not contain a method named 'op_Multiply'"; a flat [double[]] binds predictably.

.EXAMPLE
    .\Render-Thumbnail.ps1
    .\Render-Thumbnail.ps1 -Size 1024
#>
[CmdletBinding()]
param(
    [ValidateRange(64, 4096)]
    [int]$Size = 512,

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $OutputPath) {
    $OutputPath = Join-Path $PSScriptRoot 'Thumbnail.png'
}

function New-Color {
    param([string]$Hex, [double]$Alpha = 1.0)
    return [System.Drawing.Color]::FromArgb(
        [int][Math]::Round(255.0 * $Alpha),
        [Convert]::ToInt32($Hex.Substring(1, 2), 16),
        [Convert]::ToInt32($Hex.Substring(3, 2), 16),
        [Convert]::ToInt32($Hex.Substring(5, 2), 16))
}

function New-RoundedPath {
    param(
        [double]$X, [double]$Y,
        [double]$W, [double]$H,
        [double]$R
    )
    $d = [single]($R * 2.0)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc([single]$X, [single]$Y, $d, $d, [single]180, [single]90)
    $p.AddArc([single]($X + $W - ($R * 2.0)), [single]$Y, $d, $d, [single]270, [single]90)
    $p.AddArc([single]($X + $W - ($R * 2.0)), [single]($Y + $H - ($R * 2.0)), $d, $d, [single]0, [single]90)
    $p.AddArc([single]$X, [single]($Y + $H - ($R * 2.0)), $d, $d, [single]90, [single]90)
    $p.CloseFigure()
    return $p
}

# $Coords is a flat x,y,x,y,... list in icon space, offset by $Tx/$Ty and scaled by $Sc.
function Add-Poly {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush,
        [double]$Tx, [double]$Ty, [double]$Sc,
        [double[]]$Coords
    )
    $n = [int]($Coords.Length / 2)
    $pts = New-Object 'System.Drawing.PointF[]' $n
    for ($i = 0; $i -lt $n; $i++) {
        $x = $Tx + ($Coords[$i * 2] * $Sc)
        $y = $Ty + ($Coords[$i * 2 + 1] * $Sc)
        $pts[$i] = New-Object System.Drawing.PointF([single]$x, [single]$y)
    }
    $Graphics.FillPolygon($Brush, $pts)
}

# SVG quadratic curve as a GDI+ cubic: control points lie two thirds of the way to the quad handle.
function Add-QuadCurve {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Pen]$Pen,
        [double]$X0, [double]$Y0,
        [double]$Qx, [double]$Qy,
        [double]$X2, [double]$Y2
    )
    $c1x = $X0 + (2.0 / 3.0) * ($Qx - $X0)
    $c1y = $Y0 + (2.0 / 3.0) * ($Qy - $Y0)
    $c2x = $X2 + (2.0 / 3.0) * ($Qx - $X2)
    $c2y = $Y2 + (2.0 / 3.0) * ($Qy - $Y2)
    $Graphics.DrawBezier($Pen,
        (New-Object System.Drawing.PointF([single]$X0, [single]$Y0)),
        (New-Object System.Drawing.PointF([single]$c1x, [single]$c1y)),
        (New-Object System.Drawing.PointF([single]$c2x, [single]$c2y)),
        (New-Object System.Drawing.PointF([single]$X2, [single]$Y2)))
}

# GDI+ has no letter-spacing, so glyphs are placed individually.
function Add-TrackedText {
    param(
        [System.Drawing.Graphics]$Graphics,
        [string]$Text,
        [System.Drawing.Font]$Font,
        [System.Drawing.Brush]$Brush,
        [double]$CenterX, [double]$Baseline, [double]$Spacing,
        [System.Drawing.StringFormat]$Format,
        [double]$Ascent
    )
    $widths = New-Object 'double[]' $Text.Length
    $total = 0.0
    for ($i = 0; $i -lt $Text.Length; $i++) {
        $w = $Graphics.MeasureString([string]$Text[$i], $Font, [System.Drawing.PointF]::Empty, $Format).Width
        $widths[$i] = [double]$w
        $total += [double]$w
    }
    $total += $Spacing * ($Text.Length - 1)

    $x = $CenterX - ($total / 2.0)
    $top = $Baseline - $Ascent

    for ($i = 0; $i -lt $Text.Length; $i++) {
        $Graphics.DrawString([string]$Text[$i], $Font, $Brush, [single]$x, [single]$top, $Format)
        $x += $widths[$i] + $Spacing
    }
}

$bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)

try {
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $scale = [single]([double]$Size / 512.0)
    $g.ScaleTransform($scale, $scale)

    # Background, and a clip so nothing spills past the rounded corners.
    $bg = New-RoundedPath -X 0 -Y 0 -W 512 -H 512 -R 96
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF([single]0, [single]0)),
        (New-Object System.Drawing.PointF([single]0, [single]512)),
        (New-Color '#12313f'), (New-Color '#0a1a24'))
    $g.FillPath($bgBrush, $bg)
    $g.SetClip($bg)

    # City attractiveness.
    $glow = New-Object System.Drawing.SolidBrush((New-Color '#3ddc84' 0.16))
    $g.FillEllipse($glow, [single]66, [single]200, [single]380, [single]172)

    # Arrival trails.
    $trail = New-Object System.Drawing.Pen((New-Color '#8fe9f5' 0.85), [single]9)
    $trail.StartCap  = [System.Drawing.Drawing2D.LineCap]::Round
    $trail.EndCap    = [System.Drawing.Drawing2D.LineCap]::Round
    $trail.DashCap   = [System.Drawing.Drawing2D.DashCap]::Round
    $trail.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Custom

    $trail.DashPattern = [single[]]@([single]2.889, [single]2.222)
    Add-QuadCurve -Graphics $g -Pen $trail -X0 40 -Y0 112 -Qx 146 -Qy 40 -X2 264 -Y2 92

    $trail.DashPattern = [single[]]@([single]2.222, [single]1.778)
    Add-QuadCurve -Graphics $g -Pen $trail -X0 498 -Y0 396 -Qx 452 -Qy 348 -X2 402 -Y2 344

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    # Plane.
    $state = $g.Save()
    $g.TranslateTransform([single]54, [single]118)
    $g.RotateTransform([single]26)
    Add-Poly -Graphics $g -Brush $white -Tx 0 -Ty 0 -Sc 1 -Coords @(
        0,0, 44,-11, 55,-4, 21,11, 10,32, 2,30, 6,11, -13,17, -17,9)
    $g.Restore($state)

    # Ferry.
    $state = $g.Save()
    $g.TranslateTransform([single]384, [single]340)
    $g.ScaleTransform([single]1.35, [single]1.35)
    $mast = New-RoundedPath -X -5 -Y -28 -W 8 -H 24 -R 3
    $g.FillPath($white, $mast)
    $mast.Dispose()
    Add-Poly -Graphics $g -Brush $white -Tx 0 -Ty 0 -Sc 1 -Coords @(
        -32,-4, 32,-4, 21,18, -21,18)
    $g.Restore($state)

    # Palette shared by both buildings.
    $plate  = New-Object System.Drawing.SolidBrush((New-Color '#00c1ff'))
    $roof   = New-Object System.Drawing.SolidBrush((New-Color '#faf7ec'))
    $lit    = New-Object System.Drawing.SolidBrush((New-Color '#e4d8b3'))
    $shade  = New-Object System.Drawing.SolidBrush((New-Color '#0071a9'))
    $winA   = New-Object System.Drawing.SolidBrush((New-Color '#03243e'))
    $winB   = New-Object System.Drawing.SolidBrush((New-Color '#001209'))
    $accent = New-Object System.Drawing.SolidBrush((New-Color '#73e0fc'))
    $post   = New-Object System.Drawing.SolidBrush((New-Color '#b3a887'))

    # Hotel, then motel in front of it. Icon space, exactly as ui/src/images.
    $hx = 196.0; $hy = 96.0; $hs = 8.8
    Add-Poly -Graphics $g -Brush $plate  -Tx $hx -Ty $hy -Sc $hs -Coords @(1.5,18, 16,25.5, 30.5,18, 16,10.5)
    Add-Poly -Graphics $g -Brush $roof   -Tx $hx -Ty $hy -Sc $hs -Coords @(16,2, 24,6, 16,10, 8,6)
    Add-Poly -Graphics $g -Brush $lit    -Tx $hx -Ty $hy -Sc $hs -Coords @(8,6, 16,10, 16,21, 8,17)
    Add-Poly -Graphics $g -Brush $shade  -Tx $hx -Ty $hy -Sc $hs -Coords @(16,10, 24,6, 24,17, 16,21)
    Add-Poly -Graphics $g -Brush $winA   -Tx $hx -Ty $hy -Sc $hs -Coords @(8,8, 16,12, 16,13.5, 8,9.5)
    Add-Poly -Graphics $g -Brush $winA   -Tx $hx -Ty $hy -Sc $hs -Coords @(8,11, 16,15, 16,16.5, 8,12.5)
    Add-Poly -Graphics $g -Brush $winA   -Tx $hx -Ty $hy -Sc $hs -Coords @(8,14, 16,18, 16,19.5, 8,15.5)
    Add-Poly -Graphics $g -Brush $winB   -Tx $hx -Ty $hy -Sc $hs -Coords @(16,12, 24,8, 24,9.5, 16,13.5)
    Add-Poly -Graphics $g -Brush $winB   -Tx $hx -Ty $hy -Sc $hs -Coords @(16,15, 24,11, 24,12.5, 16,16.5)
    Add-Poly -Graphics $g -Brush $winB   -Tx $hx -Ty $hy -Sc $hs -Coords @(16,18, 24,14, 24,15.5, 16,19.5)
    Add-Poly -Graphics $g -Brush $roof   -Tx $hx -Ty $hy -Sc $hs -Coords @(17,11, 18.2,10.4, 18.2,16.9, 17,17.5)
    Add-Poly -Graphics $g -Brush $accent -Tx $hx -Ty $hy -Sc $hs -Coords @(12.5,19, 16,20.75, 19.5,19, 16,17.25)

    $mx = 24.0; $my = 158.0; $ms = 7.0
    Add-Poly -Graphics $g -Brush $plate  -Tx $mx -Ty $my -Sc $ms -Coords @(1.5,18, 16,25.5, 30.5,18, 16,10.5)
    Add-Poly -Graphics $g -Brush $post   -Tx $mx -Ty $my -Sc $ms -Coords @(2.9,8.8, 3.9,8.8, 3.9,19, 2.9,19)
    Add-Poly -Graphics $g -Brush $accent -Tx $mx -Ty $my -Sc $ms -Coords @(1.2,4, 5.6,4, 5.6,8.8, 1.2,8.8)
    Add-Poly -Graphics $g -Brush $winA   -Tx $mx -Ty $my -Sc $ms -Coords @(2,4.8, 4.8,4.8, 4.8,8, 2,8)
    Add-Poly -Graphics $g -Brush $roof   -Tx $mx -Ty $my -Sc $ms -Coords @(13,5, 25,11, 18,14.5, 6,8.5)
    Add-Poly -Graphics $g -Brush $lit    -Tx $mx -Ty $my -Sc $ms -Coords @(6,8.5, 18,14.5, 18,20.5, 6,14.5)
    Add-Poly -Graphics $g -Brush $shade  -Tx $mx -Ty $my -Sc $ms -Coords @(18,14.5, 25,11, 25,17, 18,20.5)
    Add-Poly -Graphics $g -Brush $winA   -Tx $mx -Ty $my -Sc $ms -Coords @(7.68,10.14, 8.88,10.74, 8.88,12.54, 7.68,11.94)
    Add-Poly -Graphics $g -Brush $winA   -Tx $mx -Ty $my -Sc $ms -Coords @(10.08,11.34, 11.28,11.94, 11.28,13.74, 10.08,13.14)
    Add-Poly -Graphics $g -Brush $winA   -Tx $mx -Ty $my -Sc $ms -Coords @(12.48,12.54, 13.68,13.14, 13.68,14.94, 12.48,14.34)
    Add-Poly -Graphics $g -Brush $winA   -Tx $mx -Ty $my -Sc $ms -Coords @(14.88,13.74, 16.08,14.34, 16.08,16.14, 14.88,15.54)
    Add-Poly -Graphics $g -Brush $post   -Tx $mx -Ty $my -Sc $ms -Coords @(6.72,11.46, 17.28,16.74, 17.28,17.54, 6.72,12.26)
    Add-Poly -Graphics $g -Brush $winA   -Tx $mx -Ty $my -Sc $ms -Coords @(7.68,12.74, 8.88,13.34, 8.88,15.14, 7.68,14.54)
    Add-Poly -Graphics $g -Brush $winA   -Tx $mx -Ty $my -Sc $ms -Coords @(10.08,13.94, 11.28,14.54, 11.28,16.34, 10.08,15.74)
    Add-Poly -Graphics $g -Brush $winA   -Tx $mx -Ty $my -Sc $ms -Coords @(12.48,15.14, 13.68,15.74, 13.68,17.54, 12.48,16.94)
    Add-Poly -Graphics $g -Brush $winA   -Tx $mx -Ty $my -Sc $ms -Coords @(14.88,16.34, 16.08,16.94, 16.08,18.74, 14.88,18.14)
    Add-Poly -Graphics $g -Brush $winB   -Tx $mx -Ty $my -Sc $ms -Coords @(20.1,15.45, 21.85,14.575, 21.85,16.575, 20.1,17.45)

    # Tourist markers: cx, cy, rx, ry.
    $ring = New-Object System.Drawing.Pen((New-Color '#6ff0ff' 0.95), [single]3.5)
    $rings = @(352,306,11,5.5, 392,290,11,5.5, 300,318,11,5.5, 150,326,8.5,4.5, 114,311,8.5,4.5)
    for ($i = 0; $i -lt $rings.Length; $i += 4) {
        $cx = [double]$rings[$i]
        $cy = [double]$rings[$i + 1]
        $rx = [double]$rings[$i + 2]
        $ry = [double]$rings[$i + 3]
        $g.DrawEllipse($ring, [single]($cx - $rx), [single]($cy - $ry), [single]($rx * 2.0), [single]($ry * 2.0))
    }

    # Wordmark.
    $family = New-Object System.Drawing.FontFamily('Arial')
    $style = [System.Drawing.FontStyle]::Bold
    $font = New-Object System.Drawing.Font($family, [single]52, $style, [System.Drawing.GraphicsUnit]::Pixel)
    $ascent = 52.0 * ([double]$family.GetCellAscent($style)) / ([double]$family.GetEmHeight($style))

    $fmt = [System.Drawing.StringFormat]::GenericTypographic.Clone()
    $fmt.FormatFlags = $fmt.FormatFlags -bor [System.Drawing.StringFormatFlags]::MeasureTrailingSpaces

    $cyan = New-Object System.Drawing.SolidBrush((New-Color '#4fd6e8'))
    Add-TrackedText -Graphics $g -Text 'TOURISM'  -Font $font -Brush $white -CenterX 256 -Baseline 428 -Spacing 7 -Format $fmt -Ascent $ascent
    Add-TrackedText -Graphics $g -Text 'OVERHAUL' -Font $font -Brush $cyan  -CenterX 256 -Baseline 480 -Spacing 3 -Format $fmt -Ascent $ascent

    $g.ResetClip()
    $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Wrote $OutputPath at $Size x $Size."
}
finally {
    $g.Dispose()
    $bmp.Dispose()
}
