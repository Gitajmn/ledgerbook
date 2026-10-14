# 记账本 · 打包"真自带运行时"的免 NET 负载（payload-full / 便携版 / wizard.zip）
#
# 为什么要这个脚本：以前的"内置运行时"是把系统 shared 目录平铺复制出来的，既没有 hostfxr.dll、
# runtimeconfig 里写的又是 frameworks，实测（COREHOST_TRACE）走的是系统 .NET →
# 没装 .NET 8 的机器上根本起不来。现在改成**真正的自包含发布**：
#   dotnet publish -r win-x64 /p:SelfContained=true
# 产出的目录里 hostfxr.dll / coreclr.dll / deps.json（带 runtime pack 资产）/ runtimeconfig
# （includedFrameworks）齐全，实测会打印 "Executing as a self-contained app as per config file"。
#
# 做四件事：
#   1) 自包含发布【应用】 → 临时\发布-内置运行时 → 压成 临时\安装包源3\payload-full.zip
#      并刷新 newest\便携版-内置运行时\ + newest\记账本-便携版-v<版本>-内置运行时.zip
#   2) 自包含发布【向导】 → 临时\发布-向导 → wizard.zip 只装 lbsetup.*
#      （运行时的那些文件由 payload-full 提供，这样不会让安装包体积翻倍）
#   3) 重新组装单文件安装包（调 打包单文件安装包.ps1）
#   4) 打印每个产物的校验值，并把"免 NET"结论（运行时来自包内）留给 安装包自检.ps1 去实测
#
# 前提：离线源 临时\nuget-离线\ 里要有两个 runtime pack（本机连不上 nuget.org）：
#   microsoft.netcore.app.runtime.win-x64.8.0.31.nupkg
#   microsoft.windowsdesktop.app.runtime.win-x64.8.0.31.nupkg
# 没有就先手动下载（版本要与 SDK 的运行时版本一致，见 继承文档 §7 第 1 条）。

param(
    [string]$后缀 = "真自带运行时",
    [switch]$跳过发布
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent          # v0.2
$repo = Split-Path $root -Parent                  # 工程根
$toolchain = Join-Path $repo 'LB-构建工具链'
$temp = Join-Path $repo '临时'
$offlineDir = Join-Path $temp 'nuget-离线'
$offlineConfig = Join-Path $temp 'NuGet-离线.Config'
$appOut = Join-Path $temp '发布-内置运行时'
$wizOut = Join-Path $temp '发布-向导'
$sourceDir = Join-Path $temp '安装包源3'
$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw '找不到 python' }

# 版本号（正则读，别用 [xml]：PS 5.1 读无 BOM 的中文文件会乱码）
$propsText = [System.IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'), [System.Text.Encoding]::UTF8)
$version = if ($propsText -match '<Version>\s*([^<\s]+)\s*</Version>') { $Matches[1] } else { '2.0.1' }

# 运行时版本（8.0.xx）要从工具链里读 —— 它和应用版本号(2.0.1)是两回事，别混用
$sharedRoot = Join-Path $toolchain 'dotnet\shared\Microsoft.NETCore.App'
$runtimeVersion = (Get-ChildItem $sharedRoot -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1).Name
if (-not $runtimeVersion) { throw "读不出运行时版本：$sharedRoot" }
Write-Host ("应用版本 $version ／ 运行时版本 $runtimeVersion")

if (-not $跳过发布) {
    # 离线源自检：缺包就直接停下，别让 dotnet 报一堆 NU1101
    $need = @(
        "microsoft.netcore.app.runtime.win-x64.$runtimeVersion",
        "microsoft.windowsdesktop.app.runtime.win-x64.$runtimeVersion"
    )
    foreach ($id in $need) {
        $found = Get-ChildItem $offlineDir -Filter "*$id.nupkg" -ErrorAction SilentlyContinue
        if (-not $found) {
            throw "离线源里缺 $id.nupkg（放进 $offlineDir），版本必须和 SDK 运行时版本一致"
        }
    }

    . (Join-Path $toolchain 'dotnet_env.ps1') | Out-Null
    Write-Host '[1/4] 自包含发布应用（真自带运行时）…'
    if (Test-Path $appOut) { Remove-Item -Recurse -Force $appOut }
    & $env:LB_DOTNET publish (Join-Path $root 'src\LedgerBook.App\LedgerBook.App.csproj') `
        -c Release -r win-x64 --self-contained true -o $appOut `
        /p:RestoreConfigFile=$offlineConfig /p:Version=$version
    if ($LASTEXITCODE -ne 0) { throw '应用自包含发布失败' }

    # 把「更新公告」作为更新内容放进应用负载（安装后更新内容窗口显示的就是它）
    $公告源 = Join-Path $repo "v0.2\v$version\更新公告.md"
    if (Test-Path $公告源) {
        Copy-Item $公告源 (Join-Path $appOut '更新内容.txt') -Force
        Write-Host '  更新内容.txt ← 更新公告.md'
    } else {
        Write-Host '  注意：没找到 更新公告.md，更新内容窗口会退回内置兜底文字' -ForegroundColor Yellow
    }

    Write-Host '[2/4] 自包含发布向导…'
    if (Test-Path $wizOut) { Remove-Item -Recurse -Force $wizOut }
    & $env:LB_DOTNET publish (Join-Path $root 'src\LedgerBook.Setup\LedgerBook.Setup.csproj') `
        -c Release -r win-x64 --self-contained true -o $wizOut `
        /p:RestoreConfigFile=$offlineConfig /p:Version=$version
    if ($LASTEXITCODE -ne 0) { throw '向导自包含发布失败' }
}

# 发布产物自检：少了这些文件就说明不是自包含（白折腾）
$mustHave = @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', '记账本.exe', '记账本.runtimeconfig.json', '记账本.deps.json')
foreach ($f in $mustHave) {
    if (-not (Test-Path (Join-Path $appOut $f))) { throw "应用发布目录里缺 $f（不是自包含产物）" }
}
$appCfg = Get-Content -Raw -Encoding UTF8 (Join-Path $appOut '记账本.runtimeconfig.json')
if ($appCfg -notmatch 'includedFrameworks') { throw '应用 runtimeconfig 里没有 includedFrameworks（不是自包含产物）' }
$wizCfg = Get-Content -Raw -Encoding UTF8 (Join-Path $wizOut 'lbsetup.runtimeconfig.json')
if ($wizCfg -notmatch 'includedFrameworks') { throw '向导 runtimeconfig 里没有 includedFrameworks（向导没做自包含发布）' }

Write-Host '[3/4] 打负载与便携包…'
# 3.1 payload-full.zip = 应用自包含产物全部文件
& $python (Join-Path $root '工具\打包zip.py') --源 $appOut --输出 (Join-Path $sourceDir 'payload-full.zip')
if ($LASTEXITCODE -ne 0) { throw 'payload-full.zip 打包失败' }

# 3.2 便携版-内置运行时：刷新 newest 里的目录 + 便携包
$portableDir = Join-Path $repo 'newest\便携版-内置运行时'
if (Test-Path $portableDir) { Remove-Item -Recurse -Force $portableDir }
New-Item -ItemType Directory -Force -Path $portableDir | Out-Null
& robocopy $appOut $portableDir /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
$portableZip = Join-Path $repo "newest\记账本-便携版-v$version-内置运行时.zip"
& $python (Join-Path $root '工具\打包zip.py') --源 $appOut --输出 $portableZip
if ($LASTEXITCODE -ne 0) { throw '便携版-内置运行时.zip 打包失败' }
# 注：wizard-slim.zip / wizard-full.zip 由 打包单文件安装包.ps1 负责压（它同时要编框架依赖版向导）

Write-Host '[4/4] 重新组装单文件安装包（wizard-slim 用 build 产物、wizard-full 用这份自包含产物）…'
& (Join-Path $root '工具\打包单文件安装包.ps1') -后缀 $后缀 -向导目录 $wizOut
if ($LASTEXITCODE -ne 0) { throw '组装安装包失败' }
