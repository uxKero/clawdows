<#
  Compila el .exe nativo (NativeAOT).
  Soluciona el detalle de que NativeAOT necesita 'vswhere.exe' en el PATH para
  localizar el linker de C++ (Build Tools de Visual Studio).
  En CI (GitHub Actions, runners de Windows) vswhere ya viene en el PATH.
#>
$ErrorActionPreference = "Stop"

# Asegurar vswhere en el PATH (vive en la carpeta del Installer de VS)
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
  $installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
  if (Test-Path (Join-Path $installer "vswhere.exe")) { $env:PATH = "$installer;$env:PATH" }
}

$proj = Join-Path $PSScriptRoot "src\ClaudeStatusBar.csproj"
$out  = Join-Path $PSScriptRoot "dist"
dotnet publish $proj -c Release -o $out
Write-Host "`nListo -> $out\clawdows.exe"
