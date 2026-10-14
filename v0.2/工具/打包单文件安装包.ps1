# 记账本 · 单文件安装包组装（容器 v2：stub + 两套向导 + 两个负载）
#
# 产物：newest\记账本-安装包-v<版本>-免NET[-后缀].exe
#
# 做四件事：
#   1) 准备两套向导：
#        wizard-slim（框架依赖，0.4MB）—— 已装 .NET 8 的机器用它，秒解、秒开，不用解 146MB 运行时
#        wizard-full（自包含，0.4MB）  —— 没装 .NET 的机器用它，运行时由 payload-full 提供
#      wizard-slim 用 dotnet build 现编；wizard-full 由 -向导目录 指定（打包免NET负载.ps1 的自包含发布产物）
#   2) 压成 临时\安装包源3\wizard-slim.zip / wizard-full.zip
#   3) 用系统自带 csc 编解包器 安装器\Installer.cs → 安装器\stub.exe（.NET Framework 4.8，C# 5 语法！）
#   4) 用 工具\组装安装包.py 拼成单文件 exe，并回读校验尾部四段长度
#
# 负载默认复用 临时\安装包源3\ 里已有的 payload-*.zip（应用本体没改时不用重打）。
#
# 用法：pwsh -File 打包单文件安装包.ps1 [-后缀 真自带运行时] [-向导目录 临时\发布-向导] [-跳过向导构建]

param(
    [string]$后缀 = "",
    [string]$向导目录 = "",
    [switch]$跳过向导构建
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent          # v0.2
$repo = Split-Path $root -Parent                  # 工程根
$toolchain = Join-Path $repo 'LB-构建工具链'
$buildOut = Join-Path $repo '临时\安装包构建'
$sourceDir = Join-Path $repo '临时\安装包源3'
$slimWizardZip = Join-Path $sourceDir 'wizard-slim.zip'
$fullWizardZip = Join-Path $sourceDir 'wizard-full.zip'
$slimZip = Join-Path $sourceDir 'payload-slim.zip'
$fullZip = Join-Path $sourceDir 'payload-full.zip'
$stubExe = Join-Path $root '安装器\stub.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

# 版本号从 Directory.Build.props 里读（用正则 + 显式 UTF-8 读，别用 [xml]：
# PS 5.1 的 Get-Content 会按系统代码页读无 BOM 文件，中文注释一乱码 XML 就报错）
$propsText = [System.IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'), [System.Text.Encoding]::UTF8)
$version = if ($propsText -match '<Version>\s*([^<\s]+)\s*</Version>') { $Matches[1] } else { '2.0.1' }
$name = "记账本-安装包-v$version-免NET" + $(if ($后缀) { "-$后缀" } else { "" }) + ".exe"
$outExe = Join-Path $repo "newest\$name"

# ---------- 1) 两套向导 ----------
# slim：框架依赖版（build 产物）
if (-not $跳过向导构建) {
    Write-Host '[1/4] 编框架依赖版向导（wizard-slim）…'
    . (Join-Path $toolchain 'dotnet_env.ps1') | Out-Null
    & $env:LB_DOTNET build (Join-Path $root 'src\LedgerBook.Setup\LedgerBook.Setup.csproj') `
        -c Release -o $buildOut /p:RestoreConfigFile=$env:LB_NUGET_CONFIG /p:Version=$version | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '向导构建失败' }
}
$slimWizardSource = $buildOut
if (-not (Test-Path (Join-Path $slimWizardSource 'lbsetup.exe'))) { throw "没找到框架依赖版向导：$slimWizardSource" }

# full：自包含发布产物（由 打包免NET负载.ps1 传来）
$fullWizardSource = if ($向导目录) { $向导目录 } else { $slimWizardSource }
if (-not (Test-Path (Join-Path $fullWizardSource 'lbsetup.exe'))) { throw "没找到自包含版向导：$fullWizardSource" }
$fullWizardSelfContained = (Get-Content -Raw -Encoding UTF8 (Join-Path $fullWizardSource 'lbsetup.runtimeconfig.json')) -match 'includedFrameworks'
if (-not $fullWizardSelfContained) {
    Write-Warning '传给 -向导目录 的不是自包含发布产物（runtimeconfig 里没有 includedFrameworks）→ 没装 .NET 的机器上向导会起不来'
}
$slimWizardSelfContained = (Get-Content -Raw -Encoding UTF8 (Join-Path $slimWizardSource 'lbsetup.runtimeconfig.json')) -match 'includedFrameworks'
if ($slimWizardSelfContained) {
    Write-Warning '框架依赖版向导居然是自包含产物 → 快路上（已装 .NET）它会因为找不到运行时文件而起不来'
}

# ---------- 2) 压两套向导 ----------
Write-Host '[2/4] 压两套向导 → wizard-slim.zip / wizard-full.zip…'
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($pair in @(@($slimWizardZip, $slimWizardSource), @($fullWizardZip, $fullWizardSource))) {
    $zipPath = $pair[0]
    $source = $pair[1]
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        Get-ChildItem $source -Filter 'lbsetup.*' -File | ForEach-Object {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $_.Name, 'Optimal') | Out-Null
        }
    } finally { $zip.Dispose() }
}

# ---------- 3) 编解包器 ----------
Write-Host '[3/4] 编解包器（csc，C# 5）…'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe：$csc" }
& $csc /nologo /target:winexe /out:$stubExe `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    (Join-Path $root '安装器\Installer.cs')
if ($LASTEXITCODE -ne 0) { throw '解包器编译失败' }

# ---------- 4) 组装 ----------
Write-Host '[4/4] 组装单文件安装包…'
$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw '找不到 python（组装脚本是 Python 写的），请把 python 加进 PATH' }
& $python (Join-Path $root '工具\组装安装包.py') --stub $stubExe `
    --wizard-slim $slimWizardZip --wizard-full $fullWizardZip `
    --slim $slimZip --full $fullZip --out $outExe
if ($LASTEXITCODE -ne 0) { throw '组装失败' }

$info = Get-Item $outExe
$hash = (Get-FileHash $outExe -Algorithm SHA256).Hash
Write-Host ''
Write-Host ("完成：" + $info.FullName)
Write-Host ("  大小  ：{0:N1} MB" -f ($info.Length / 1MB))
Write-Host ("  SHA256：$hash")
