# Run unit tests. Run from any folder.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet test "$root\WorkFlowSync.sln" -c Debug --nologo
