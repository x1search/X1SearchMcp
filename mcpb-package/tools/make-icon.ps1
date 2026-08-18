# Copyright (c) 2026 X1 Discovery, Inc.
#
# Licensed under the MIT License (copyright only). See the LICENSE file in
# the repository root for the full license text.
#
# This license does not grant, and shall not be construed as granting, any
# patent rights. See the PATENTS file in the repository root.

#Requires -Version 5.1
<#
.SYNOPSIS
  Regenerates mcpb-package\icon.png from the official X1 logo mark.

.DESCRIPTION
  Source is X1UI2.Base\Icons\X1.svg - the vector master for the "X1" wordmark used across the
  product (same mark as X1.png / mainLogo.png in that folder, and as the logo baked into
  x1search.ico). Rendering from the vector rather than upscaling a raster ICO frame is the whole
  point: X1.svg's largest embedded raster equivalent tops out at 256x256, so any raster source
  needs upscaling to reach the 512x512 PNG the MCPB manifest expects, while the vector renders
  crisp at any size.

  This is a minimal, hand-rolled parser for exactly this SVG's shape - two flat-filled <polygon>
  elements, no groups/transforms/gradients - not a general SVG renderer. If X1.svg is ever
  replaced with something more complex (curves, gradients, multiple colors), this script will need
  rewriting or a real SVG rasterizer swapped in.

  Static output, not part of build-mcpb.ps1: icon.png is checked into git like manifest.json, and
  only needs regenerating when the source art changes, not on every build.
#>

$ErrorActionPreference = "Stop"

$svgPath = Join-Path $PSScriptRoot "..\..\..\X1UI2\X1UI2.Base\Icons\X1.svg"
if (-not (Test-Path $svgPath)) {
    Write-Error "Source logo not found: $svgPath"
    exit 1
}

Add-Type -AssemblyName System.Drawing

[xml]$svg = Get-Content $svgPath -Raw
$polygons = $svg.svg.g.g.polygon
if (-not $polygons) {
    Write-Error "No <polygon> elements found in $svgPath - source shape changed, see script header."
    exit 1
}

# Each polygon's "points" attribute is a flat "x1,y1 x2,y2 ..." list in the SVG's own 0-0-240-240
# viewBox coordinate space.
$allPoints = @()
$polygonPointSets = foreach ($poly in $polygons) {
    $pts = ($poly.points -split '\s+') | Where-Object { $_ -match ',' } | ForEach-Object {
        $parts = $_ -split ','
        [System.Drawing.PointF]::new([float]$parts[0], [float]$parts[1])
    }
    $allPoints += $pts
    ,$pts
}

# Fit the shapes' own tight bounding box into the 512x512 canvas with a margin, rather than
# scaling the full 240x240 viewBox as-is - the viewBox has generous built-in whitespace (the mark's
# bbox is roughly 199x112 within it) meant for inline UI use, not for filling a square icon slot.
$minX = ($allPoints | ForEach-Object { $_.X } | Measure-Object -Minimum).Minimum
$maxX = ($allPoints | ForEach-Object { $_.X } | Measure-Object -Maximum).Maximum
$minY = ($allPoints | ForEach-Object { $_.Y } | Measure-Object -Minimum).Minimum
$maxY = ($allPoints | ForEach-Object { $_.Y } | Measure-Object -Maximum).Maximum
$bboxW = $maxX - $minX
$bboxH = $maxY - $minY

$canvasSize = 512
$marginFraction = 0.12   # 12% margin on the longer axis, matching mainLogo.png's proportions
$targetSize = $canvasSize * (1 - 2 * $marginFraction)
$scale = [Math]::Min($targetSize / $bboxW, $targetSize / $bboxH)

$scaledW = $bboxW * $scale
$scaledH = $bboxH * $scale
$offsetX = ($canvasSize - $scaledW) / 2
$offsetY = ($canvasSize - $scaledH) / 2

function Transform-Point([System.Drawing.PointF]$p) {
    return [System.Drawing.PointF]::new(
        $offsetX + ($p.X - $minX) * $scale,
        $offsetY + ($p.Y - $minY) * $scale
    )
}

$dst = New-Object System.Drawing.Bitmap($canvasSize, $canvasSize)
try {
    $g = [System.Drawing.Graphics]::FromImage($dst)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml("#075489"))
        try {
            foreach ($pts in $polygonPointSets) {
                $transformed = $pts | ForEach-Object { Transform-Point $_ }
                $g.FillPolygon($brush, [System.Drawing.PointF[]]$transformed)
            }
        }
        finally { $brush.Dispose() }
    }
    finally { $g.Dispose() }

    $outPath = Join-Path $PSScriptRoot "..\icon.png"
    $dst.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Wrote $outPath (512x512, rendered from the X1.svg vector logo, transparent background)."
}
finally { $dst.Dispose() }
