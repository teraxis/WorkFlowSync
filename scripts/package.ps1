# Builds the portable executables (publish.ps1) and packs them with the user README into
#   publish\WorkFlowSync-<version>-win-x64.zip
# — one file to copy onto a PC, unpack and run. No installer, no admin rights.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish\portable"

& (Join-Path $PSScriptRoot "publish.ps1") | Out-Host

Copy-Item -LiteralPath (Join-Path $root "portable\README.txt") -Destination $out -Force

# Strict security & privacy checks: prevent any live user data, personal paths, or logs from leaking into the release package.
# Releases are packaged without config.example.json or user-specific files; config.json is created automatically on first run.
foreach ($leftover in "config.example.json", "config.json", "state.db", "state.db-wal", "state.db-shm", "logs", ".wfsversions") {
    $path = Join-Path $out $leftover
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

$exampleConfig = Join-Path $out "config.example.json"
if (Test-Path $exampleConfig) {
    throw "Security check failed: config.example.json must not be included in the release package."
}

$version = (Get-Item (Join-Path $out "WorkFlowSync.exe")).VersionInfo.ProductVersion
if ($version -match '^(\d+\.\d+\.\d+)') { $version = $Matches[1] } else { $version = "0.0.0" }

$zip = Join-Path $root "publish\WorkFlowSync-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal

"{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
