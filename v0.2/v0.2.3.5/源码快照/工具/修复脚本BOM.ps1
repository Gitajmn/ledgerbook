# 记账本 · 修脚本 BOM
#
# 为什么需要它：PowerShell 5.1 读**不带 BOM** 的 UTF-8 脚本时按 GBK 解码 → 中文变量名/字符串全乱 → 直接语法错误。
# `edit`/`write` 这类编辑器保存时常常把 BOM 丢掉，所以每次改完 .ps1 都跑一下这个脚本最保险。
#
# 用法：pwsh -File 修复脚本BOM.ps1 [-检查]      # -检查 只看不改（退出码 1 表示有缺 BOM 的）

param([switch]$检查)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent          # v0.2

$files = Get-ChildItem $root -Recurse -File -Include *.ps1 -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|源码快照)\\' }

$fixed = 0
$missing = 0
foreach ($f in $files) {
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    if ($hasBom) { continue }

    $missing++
    if ($检查) {
        Write-Host ("  缺 BOM：" + $f.FullName.Replace((Split-Path $root -Parent), '')) -ForegroundColor Yellow
        continue
    }

    $text = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($f.FullName, $text, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host ("  已补 BOM：" + $f.FullName.Replace((Split-Path $root -Parent), ''))
    $fixed++
}

Write-Host ("检查 " + $files.Count + " 个 .ps1：补了 " + $fixed + " 个，仍缺 " + ($missing - $fixed) + " 个")
if ($检查 -and $missing -gt 0) { exit 1 }
