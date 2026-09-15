# Builds the portable executables (publish.ps1) and packs them with the user README into
#   publish\WorkFlowSync-<version>-win-x64.zip
# — one file to copy onto a PC, unpack and run. No installer, no admin rights.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish\portable"

& (Join-Path $PSScriptRoot "publish.ps1") | Out-Host

Copy-Item -LiteralPath (Join-Path $root "portable\README.txt") -Destination $out -Force

$version = (Get-Item (Join-Path $out "WorkFlowSync.exe")).VersionInfo.ProductVersion
if ($version -match '^(\d+\.\d+\.\d+)') { $version = $Matches[1] } else { $version = "0.0.0" }

$zip = Join-Path $root "publish\WorkFlowSync-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal

"{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
