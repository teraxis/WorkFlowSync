# Builds the portable executables (publish.ps1) and packs them with the user README into
#   publish\WorkFlowSync-<version>-win-x64.zip
# — one file to copy onto a PC, unpack and run. No installer, no admin rights.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish\portable"

& (Join-Path $PSScriptRoot "publish.ps1") | Out-Host

Copy-Item -LiteralPath (Join-Path $root "portable\README.txt") -Destination $out -Force

# Anything a previous run of the published exe left here is somebody's live data, not part of the
# product: starting WorkFlowSync.exe once writes config.json, state.db and logs\ next to itself.
# Shipping those would hand the recipient our folder pairs and hide that config.example.json is
# meant to be copied. Removed before packing, never packed and cleaned up afterwards.
foreach ($leftover in "config.json", "state.db", "logs") {
    $path = Join-Path $out $leftover
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

$version = (Get-Item (Join-Path $out "WorkFlowSync.exe")).VersionInfo.ProductVersion
if ($version -match '^(\d+\.\d+\.\d+)') { $version = $Matches[1] } else { $version = "0.0.0" }

$zip = Join-Path $root "publish\WorkFlowSync-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal

"{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
