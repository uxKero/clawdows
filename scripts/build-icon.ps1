<#
  Genera el icono de la aplicacion (claude.ico) a partir de Clawd (frame 0),
  multi-resolucion y con escalado pixel-art (nearest neighbor).
  Salida: src\assets\claude.ico  (+ preview opcional via -PreviewDir)
#>
param(
  [string]$Frame = (Join-Path $PSScriptRoot "..\assets\crab\frame-00.png"),
  [string]$OutPath = (Join-Path $PSScriptRoot "..\src\assets\claude.ico"),
  [string]$PreviewDir = ""
)
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$src = [System.Drawing.Image]::FromFile((Resolve-Path $Frame))

$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $g.Clear([System.Drawing.Color]::Transparent)
  $scale = ($s * 0.92) / [Math]::Max($src.Width, $src.Height)
  $w = [int]($src.Width * $scale); $h = [int]($src.Height * $scale)
  $g.DrawImage($src, [int](($s - $w) / 2), [int](($s - $h) / 2), $w, $h)
  $g.Dispose()
  $m = New-Object System.IO.MemoryStream
  $bmp.Save($m, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += , ($m.ToArray())
  if ($PreviewDir -and $s -eq 256) { $bmp.Save((Join-Path $PreviewDir "appicon-256.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
  $bmp.Dispose(); $m.Dispose()
}
$src.Dispose()

# Ensamblar formato ICO (entradas PNG, soportado en Windows Vista+)
$icoMs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter -ArgumentList $icoMs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)  # ICONDIR
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $pngs[$i].Length
  $dim = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$dim); $bw.Write([byte]$dim)   # ancho, alto
  $bw.Write([byte]0); $bw.Write([byte]0)         # colores, reservado
  $bw.Write([uint16]1); $bw.Write([uint16]32)    # planos, bpp
  $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
  $offset += $len
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Flush()

$dir = Split-Path $OutPath -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutPath, $icoMs.ToArray())
$bw.Dispose(); $icoMs.Dispose()
"claude.ico generado: $OutPath  ($((Get-Item $OutPath).Length) bytes, $($sizes.Count) tamanos)"
