# Creates test folders, sample files, config pair and seeds pending entries in state.db
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$sourceDir = Join-Path $root "test-folders\Source"
$targetDir = Join-Path $root "test-folders\Target"

Write-Host "Creating test folders..."
New-Item -ItemType Directory -Force -Path (Join-Path $sourceDir "Документи") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $sourceDir "Проєкт") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $sourceDir "Креслення") | Out-Null

New-Item -ItemType Directory -Force -Path (Join-Path $targetDir "Документи") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $targetDir "Нові_матеріали") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $targetDir "Креслення") | Out-Null

Write-Host "Creating sample files..."
# 1. Modified
$srcDoc = Join-Path $sourceDir "Документи\Звіт_2026.docx"
$dstDoc = Join-Path $targetDir "Документи\Звіт_2026.docx"
[System.IO.File]::WriteAllBytes($srcDoc, [byte[]]::new(10240))
[System.IO.File]::WriteAllBytes($dstDoc, [byte[]]::new(15360))

# 2. Deleted in Target
$srcDel = Join-Path $sourceDir "Проєкт\Архів_нотаток.txt"
[System.IO.File]::WriteAllBytes($srcDel, [byte[]]::new(2048))
$dstDel = Join-Path $targetDir "Проєкт\Архів_нотаток.txt"
if (Test-Path $dstDel) { Remove-Item $dstDel -Force }

# 3. Added in Target
$dstAdd = Join-Path $targetDir "Нові_матеріали\Договір_поставки.docx"
[System.IO.File]::WriteAllBytes($dstAdd, [byte[]]::new(24576))
$srcAdd = Join-Path $sourceDir "Нові_матеріали\Договір_поставки.docx"
if (Test-Path $srcAdd) { Remove-Item $srcAdd -Force }

# 4. Renamed: Схема_v1.pdf -> Схема_v2.pdf
$srcRen = Join-Path $sourceDir "Креслення\Схема_v1.pdf"
$dstRen = Join-Path $targetDir "Креслення\Схема_v2.pdf"
[System.IO.File]::WriteAllBytes($srcRen, [byte[]]::new(40960))
[System.IO.File]::WriteAllBytes($dstRen, [byte[]]::new(40960))

# 5. Unchanged
$srcReg = Join-Path $sourceDir "Регламент.pdf"
$dstReg = Join-Path $targetDir "Регламент.pdf"
[System.IO.File]::WriteAllBytes($srcReg, [byte[]]::new(8192))
[System.IO.File]::WriteAllBytes($dstReg, [byte[]]::new(8192))

Write-Host "Updating configs..."
$pairName = "Тестове завдання (Погодження)"
$testPair = [pscustomobject]@{
    Name = $pairName
    Enabled = $true
    Source = $sourceDir
    Target = $targetDir
    Mode = "twoWay"
    Links = "follow"
    Interval = "00:05:00"
    Watch = "manual"
    CloudFiles = "skip"
    RequireApproval = $true
    ApprovalSide = "target"
    Versioning = $true
    VersionsPath = ".wfsversions"
    VersionsKeepDays = 30
    VersionsMaxGb = 5
}

$configLocations = @(
    (Join-Path $root "config.json"),
    (Join-Path $root "src\WorkFlowSync.App\bin\Debug\net8.0\win-x64\config.json"),
    (Join-Path $root "publish\portable\config.json")
)

foreach ($cfgPath in $configLocations) {
    $dir = Split-Path -Parent $cfgPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    $cfg = $null
    if (Test-Path $cfgPath) {
        try {
            $raw = Get-Content $cfgPath -Raw -Encoding UTF8
            $cfg = $raw | ConvertFrom-Json
        } catch { $cfg = $null }
    }

    if ($null -eq $cfg) {
        $cfg = [pscustomobject]@{
            Pairs = @()
            StatePath = "state.db"
            LogPath = "logs"
            LogKeepDays = 30
            Interval = "00:05:00"
            Theme = "light"
            Language = "uk"
            Notifications = "all"
            RecentLimit = 40
            QuietHoursFrom = 0
            QuietHoursTo = 0
            ScanBufferSize = 262144
            ScanParallelism = 8
            CpuLoad = "balanced"
        }
    }

    # Replace or add pair
    $pairsList = [System.Collections.Generic.List[object]]::new()
    if ($cfg.Pairs) {
        foreach ($p in $cfg.Pairs) {
            if ($p.Name -ne $pairName) {
                $pairsList.Add($p)
            }
        }
    }
    $pairsList.Add($testPair)
    $cfg.Pairs = $pairsList.ToArray()

    $json = $cfg | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($cfgPath, $json, [System.Text.Encoding]::UTF8)
    Write-Host "Updated: $cfgPath"
}

Write-Host "Seeding state.db..."
$epoch = [DateTimeOffset]::new(1970, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
function To-UnixMs([DateTimeOffset]$dto) {
    return [long]($dto - $epoch).TotalMilliseconds
}

$now = [DateTimeOffset]::UtcNow
$tNow = To-UnixMs $now
$t25m = To-UnixMs ($now.AddMinutes(-25))
$t15m = To-UnixMs ($now.AddMinutes(-15))
$t40m = To-UnixMs ($now.AddMinutes(-40))
$t5m  = To-UnixMs ($now.AddMinutes(-5))

$srcDocInfo = Get-Item $srcDoc
$dstDocInfo = Get-Item $dstDoc
$srcDelInfo = Get-Item $srcDel
$dstAddInfo = Get-Item $dstAdd
$srcRenInfo = Get-Item $srcRen
$dstRenInfo = Get-Item $dstRen

$srcDocMtime = To-UnixMs ([DateTimeOffset]$srcDocInfo.LastWriteTimeUtc)
$dstDocMtime = To-UnixMs ([DateTimeOffset]$dstDocInfo.LastWriteTimeUtc)
$srcDelMtime = To-UnixMs ([DateTimeOffset]$srcDelInfo.LastWriteTimeUtc)
$dstAddMtime = To-UnixMs ([DateTimeOffset]$dstAddInfo.LastWriteTimeUtc)
$srcRenMtime = To-UnixMs ([DateTimeOffset]$srcRenInfo.LastWriteTimeUtc)
$dstRenMtime = To-UnixMs ([DateTimeOffset]$dstRenInfo.LastWriteTimeUtc)

$dbLocations = @(
    (Join-Path $root "state.db"),
    (Join-Path $root "src\WorkFlowSync.App\bin\Debug\net8.0\win-x64\state.db"),
    (Join-Path $root "publish\portable\state.db")
)

$sqlLines = [System.Collections.Generic.List[string]]::new()
$sqlLines.Add("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);")
$sqlLines.Add("INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '3');")
$sqlLines.Add("CREATE TABLE IF NOT EXISTS entries (pair TEXT NOT NULL, path TEXT NOT NULL, kind INTEGER NOT NULL, status INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, src_size INTEGER, src_mtime INTEGER, copied_size INTEGER, copied_mtime INTEGER, via_link INTEGER NOT NULL DEFAULT 0, status_changed INTEGER, remote_id TEXT, remote_version TEXT, PRIMARY KEY (pair, path)) STRICT, WITHOUT ROWID;")
$sqlLines.Add("CREATE TABLE IF NOT EXISTS pending (id INTEGER PRIMARY KEY, pair TEXT NOT NULL, path TEXT NOT NULL, from_path TEXT, change INTEGER NOT NULL, kind INTEGER NOT NULL, src_size INTEGER, src_mtime INTEGER, dst_size INTEGER, dst_mtime INTEGER, detected INTEGER NOT NULL, status INTEGER NOT NULL, resolved INTEGER, version_path TEXT) STRICT;")
$sqlLines.Add("CREATE INDEX IF NOT EXISTS pending_open ON pending (pair, status, detected);")
$sqlLines.Add("CREATE UNIQUE INDEX IF NOT EXISTS pending_one_per_path ON pending (pair, path) WHERE status = 0;")
$sqlLines.Add("DELETE FROM pending WHERE pair = '$pairName';")

$dstAddLen = $dstAddInfo.Length
$srcDocLen = $srcDocInfo.Length
$dstDocLen = $dstDocInfo.Length
$srcDelLen = $srcDelInfo.Length
$srcRenLen = $srcRenInfo.Length
$dstRenLen = $dstRenInfo.Length

$sqlLines.Add("INSERT INTO pending (pair, path, from_path, change, kind, src_size, src_mtime, dst_size, dst_mtime, detected, status, resolved, version_path) VALUES ('$pairName', 'Нові_матеріали/Договір_поставки.docx', NULL, 1, 0, NULL, NULL, $dstAddLen, $dstAddMtime, $t25m, 0, NULL, NULL);")
$sqlLines.Add("INSERT INTO pending (pair, path, from_path, change, kind, src_size, src_mtime, dst_size, dst_mtime, detected, status, resolved, version_path) VALUES ('$pairName', 'Документи/Звіт_2026.docx', NULL, 2, 0, $srcDocLen, $srcDocMtime, $dstDocLen, $dstDocMtime, $t15m, 0, NULL, NULL);")
$sqlLines.Add("INSERT INTO pending (pair, path, from_path, change, kind, src_size, src_mtime, dst_size, dst_mtime, detected, status, resolved, version_path) VALUES ('$pairName', 'Проєкт/Архів_нотаток.txt', NULL, 3, 0, $srcDelLen, $srcDelMtime, NULL, NULL, $t40m, 0, NULL, NULL);")
$sqlLines.Add("INSERT INTO pending (pair, path, from_path, change, kind, src_size, src_mtime, dst_size, dst_mtime, detected, status, resolved, version_path) VALUES ('$pairName', 'Креслення/Схема_v2.pdf', 'Креслення/Схема_v1.pdf', 4, 0, $srcRenLen, $srcRenMtime, $dstRenLen, $dstRenMtime, $t5m, 0, NULL, NULL);")

$sqlFile = Join-Path ([System.IO.Path]::GetTempPath()) "seed_pending.sql"
[System.IO.File]::WriteAllLines($sqlFile, $sqlLines, [System.Text.Encoding]::UTF8)

foreach ($dbPath in $dbLocations) {
    $dir = Split-Path -Parent $dbPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    & sqlite3 "$dbPath" ".read '$($sqlFile.Replace('\', '/'))'"
    Write-Host "Seeded: $dbPath"
}

Remove-Item $sqlFile -Force
Write-Host "Setup complete!"
