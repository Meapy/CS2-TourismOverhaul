<#
.SYNOPSIS
    Converts the screenshots in Properties\Screenshots to JPEGs under the Paradox size limit.

.DESCRIPTION
    Paradox rejects any image over 2.1 MB. Game captures are full-resolution PNGs and land well
    over that, so this downscales to a sensible width and re-encodes as JPEG, stepping the quality
    down until each file fits.

    JPEG rather than PNG because these are photographic screenshots — PNG stays over the limit even
    at reduced resolution, and the artefacts are invisible at listing size. The zone icons and the
    thumbnail stay PNG; they are flat colour, where PNG is both smaller and sharper.

    Originals in mod-page-images are left untouched; this only rewrites the staged copies.

.EXAMPLE
    .\Optimize-Screenshots.ps1
#>
[CmdletBinding()]
param(
    [string]$Folder,
    [double]$MaxMB = 2.0,
    [int]$MaxWidth = 1920
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $Folder) {
    $Folder = Join-Path $PSScriptRoot 'Screenshots'
}

$maxBytes = $MaxMB * 1MB
$jpegCodec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
    Where-Object { $_.MimeType -eq 'image/jpeg' }

$pngs = @(Get-ChildItem -LiteralPath $Folder -Filter *.png -File)
if ($pngs.Count -eq 0) {
    Write-Host "No PNGs to convert in $Folder."
    return
}

foreach ($png in $pngs) {
    $img = [System.Drawing.Image]::FromFile($png.FullName)
    $out = [System.IO.Path]::ChangeExtension($png.FullName, '.jpg')

    try {
        $w = $img.Width
        $h = $img.Height
        if ($w -gt $MaxWidth) {
            $h = [int][Math]::Round([double]$h * $MaxWidth / [double]$w)
            $w = $MaxWidth
        }

        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.DrawImage($img, 0, 0, $w, $h)
        }
        finally {
            $g.Dispose()
        }

        $finalSize = 0
        foreach ($quality in 92, 85, 78, 70, 60) {
            $ep = New-Object System.Drawing.Imaging.EncoderParameters(1)
            $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
                [System.Drawing.Imaging.Encoder]::Quality, [long]$quality)
            try {
                $bmp.Save($out, $jpegCodec, $ep)
            }
            finally {
                $ep.Dispose()
            }

            $finalSize = (Get-Item -LiteralPath $out).Length
            if ($finalSize -le $maxBytes) {
                Write-Host ("{0} -> {1}  {2}x{3}  q{4}  {5:N2} MB" -f `
                    $png.Name, (Split-Path $out -Leaf), $w, $h, $quality, ($finalSize / 1MB))
                break
            }
        }

        if ($finalSize -gt $maxBytes) {
            Write-Warning ("{0} is still {1:N2} MB at quality 60. Re-run with a smaller -MaxWidth." -f `
                (Split-Path $out -Leaf), ($finalSize / 1MB))
        }

        $bmp.Dispose()
    }
    finally {
        $img.Dispose()
    }

    Remove-Item -LiteralPath $png.FullName -Force
}

Write-Host ''
Get-ChildItem -LiteralPath $Folder -File |
    Select-Object Name, @{ Name = 'MB'; Expression = { [Math]::Round($_.Length / 1MB, 2) } }
