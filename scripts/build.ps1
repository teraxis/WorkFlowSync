# Build the whole solution (Debug). Run from any folder.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet build "$root\WorkFlowSync.sln" -c Debug --nologo
