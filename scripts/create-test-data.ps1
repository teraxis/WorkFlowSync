$ErrorActionPreference = "Stop"

$enc = [System.Text.Encoding]::UTF8
function D($b) { return $enc.GetString([System.Convert]::FromBase64String($b)) }

$src = "D:\Projects\WorkFlowSync\test-folders\Source"
$dst = "D:\Projects\WorkFlowSync\test-folders\Target"

# Remove any old test-folders
if (Test-Path $src) { Remove-Item $src -Recurse -Force }
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }

$fileDoc  = D("0JTQvtC60YPQvNC10L3RgtC4XNCX0LLRltGCXzIwMjYuZG9jeA==")           # Документи\Звіт_2026.docx
$fileDel  = D("0J/RgNC+0ZTQutGCL9CQ0YDRhdGW0LJf0L3QvtGC0LDRgtC+0LoudHh0")         # Проєкт\Архів_нотаток.txt
$fileAdd  = D("0J3QvtCy0ZYg0LzQsNGC0LXRgNGW0LDQu9C4XNCU0L7Qs9C+0LLRltGAX9C/0L7RgdGC0LDQstC60LguZG9jeA==") # Нові матеріали\Договір_поставки.docx
$fileRen1 = D("0JrRgNC10YHQu9C10L3QvdGPXNCh0YXQtdC80LBfdjEucGRm")                 # Креслення\Схема_v1.pdf
$fileRen2 = D("0JrRgNC10YHQu9C10L3QvdGPXNCh0YXQtdC80LBfdjIucGRm")                 # Креслення\Схема_v2.pdf
$fileReg  = D("0KDQtdCz0LvQsNC80LXQvdGCLnBkZg==")                                 # Регламент.pdf

$pSrcDoc = Join-Path $src $fileDoc
$pDstDoc = Join-Path $dst $fileDoc
$pSrcDel = Join-Path $src $fileDel
$pDstDel = Join-Path $dst $fileDel
$pSrcAdd = Join-Path $src $fileAdd
$pDstAdd = Join-Path $dst $fileAdd
$pSrcRen = Join-Path $src $fileRen1
$pDstRen = Join-Path $dst $fileRen2
$pSrcReg = Join-Path $src $fileReg
$pDstReg = Join-Path $dst $fileReg

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $pSrcDoc) | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $pSrcDel) | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $pSrcRen) | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $pDstDoc) | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $pDstAdd) | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $pDstRen) | Out-Null

# 1. Modified
[System.IO.File]::WriteAllBytes($pSrcDoc, [byte[]]::new(10240))
[System.IO.File]::WriteAllBytes($pDstDoc, [byte[]]::new(15360))

# 2. Deleted in Target
[System.IO.File]::WriteAllBytes($pSrcDel, [byte[]]::new(2048))

# 3. Added in Target
[System.IO.File]::WriteAllBytes($pDstAdd, [byte[]]::new(24576))

# 4. Renamed
[System.IO.File]::WriteAllBytes($pSrcRen, [byte[]]::new(40960))
[System.IO.File]::WriteAllBytes($pDstRen, [byte[]]::new(40960))

# 5. Unchanged
[System.IO.File]::WriteAllBytes($pSrcReg, [byte[]]::new(8192))
[System.IO.File]::WriteAllBytes($pDstReg, [byte[]]::new(8192))

Write-Host "Sample files created successfully."
