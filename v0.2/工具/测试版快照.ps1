# 记账本 · 测试版快照（按 2026-10-09 商定的版本规则）
#
# 测试版（D 位，例如 0.2.1.5）**不打安装包、不动 newest**，只在 v0.2\v<版本>\ 留一份快照：
#     v<版本>\
#       素材\       设计素材（立绘/图标/对比图）—— 原有内容保留，本脚本不动它
#       源码快照\   应用源码 + 安装器源码 + 工具脚本 + 继承文档（排除 bin/obj 与 *.exe）
#       README.txt  这个文件夹是什么、怎么用
# 正式版（C 位）才另外走：工具\打包-自研安装包.ps1（出包）+ 工具\存档版本.ps1（出包并存档到 历史版本\）
#
# 用法：pwsh -File 测试版快照.ps1                 # 版本号读 Directory.Build.props
#       pwsh -File 测试版快照.ps1 -版本 0.2.2

param(
    [string]$版本
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent          # v0.2
$repo = Split-Path $root -Parent                  # 工程根

if (-not $版本) {
    $m = Select-String -Path (Join-Path $root 'Directory.Build.props') -Pattern '<Version>([^<]+)</Version>'
    if (-not $m) { throw '读不出版本号' }
    $版本 = $m.Matches[0].Groups[1].Value
}

$dst = Join-Path $root "v$版本"
$snapshot = Join-Path $dst '源码快照'
New-Item -ItemType Directory -Force -Path $snapshot | Out-Null
if (Test-Path $snapshot) { Remove-Item (Join-Path $snapshot '*') -Recurse -Force }

Write-Host ("测试版快照：v$版本 → " + $dst)

# 源码快照（排除 bin/obj 和所有 exe：exe 都能从源码重新编出来，不进快照）
$pairs = @(
    @{ 源 = (Join-Path $root 'src'); 目标 = (Join-Path $snapshot '应用源码') },
    @{ 源 = (Join-Path $root '安装\安装器'); 目标 = (Join-Path $snapshot '安装器') },
    @{ 源 = (Join-Path $root '工具'); 目标 = (Join-Path $snapshot '工具') },
    @{ 源 = (Join-Path $root 'tools'); 目标 = (Join-Path $snapshot 'tools') }
)
foreach ($p in $pairs) {
    if (-not (Test-Path $p.源)) { continue }
    $null = robocopy $p.源 $p.目标 /E /XF *.exe /XD bin obj /NFL /NDL /NJH /NJS /NP /R:1 /W:1
}
Copy-Item (Join-Path $repo '文档\继承文档-v2.0.1.md') (Join-Path $snapshot '继承文档.md') -Force
Copy-Item (Join-Path $root '开发日志.md') (Join-Path $snapshot '开发日志.md') -Force
Copy-Item (Join-Path $root 'Directory.Build.props') (Join-Path $snapshot 'Directory.Build.props') -Force
Copy-Item (Join-Path $root '交接文档.md') (Join-Path $snapshot '交接文档.md') -Force -ErrorAction SilentlyContinue

$readme = @"
v$版本 —— 测试版快照（不是安装包）

这是 v$版本（测试版）的源码快照，按 2026-10-09 商定的规则保留：

  · D 位（.5 这种）= 测试版：**不打安装包、不动 newest**，只在这里留快照；
  · 只有 C 位（0.2.1、0.2.2 这种）= 正式版才出安装包并存档到 历史版本\；
  · 下一版（用户提出的修改）放进 **v<下一版>\** 文件夹，两个文件夹并排就能逐项对比差异；
  · 版本号唯一源头：Directory.Build.props 的 <Version>（快照里也留了一份）。

目录说明：
  素材\       设计素材（立绘、图标、对比图）；工具\生成图标.py 用的就是这里的图
  源码快照\   应用源码 + 安装器源码 + 工具脚本 + 继承文档/开发日志（排除 bin/obj 与 *.exe）
  README.txt  本文件

快照时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@
[System.IO.File]::WriteAllText((Join-Path $dst 'README.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

$files = Get-ChildItem $snapshot -Recurse -File
Write-Host ("  快照文件数：" + $files.Count + "，合计 " + [math]::Round(($files | Measure-Object Length -Sum).Sum / 1KB, 0) + " KB")
Write-Host ("  目录：" + $dst) -ForegroundColor Green
