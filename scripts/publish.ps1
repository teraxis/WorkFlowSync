# Publish portable single-file executables (self-contained, win-x64) into publish\portable:
#   WorkFlowSync.exe  - GUI (folder pairs + settings)
#   wfs.exe           - console (sync/status/import for Task Scheduler and scripts)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish\portable"
dotnet publish "$root\src\WorkFlowSync.App\WorkFlowSync.App.csproj" -c Release -r win-x64 -o $out --nologo
dotnet publish "$root\src\WorkFlowSync.Cli\WorkFlowSync.Cli.csproj" -c Release -r win-x64 -o $out --nologo
# Do not include config.example.json in the release package (config.json is auto-generated on first launch)
Get-ChildItem $out | Select-Object Name, @{n="MB";e={[math]::Round($_.Length/1MB,1)}}
