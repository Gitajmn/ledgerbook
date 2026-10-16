# 记账本 · 用 Inno Setup 打包（路线"丙"）：单文件安装器、原生卸载、不靠 %TEMP%
#
# 做三件事：
#   1) 用系统自带 csc 编「记账本小工具.exe」（.NET Framework 4.8，不需要任何运行时下载）
#      —— 它同时是卸载器（安装目录里的「卸载记账本.exe」）和安装后置（写安装信息.txt / 配置证书）
#   2) 调 ISCC.exe 编 安装包\记账本.iss
#   3) 打印产物大小与 SHA256；-自检 时额外用 TestIcons 变体在沙箱里静默装一遍再卸一遍
#
# 用法：
#   pwsh -File 打包安装包-Inno.ps1               # 正式包 → newest\记账本-安装包-v2.0.1-单文件.exe
#   pwsh -File 打包安装包-Inno.ps1 -自检         # 只编自检变体（快捷方式建在安装目录里，沙箱能跑）

param(
    [switch]$自检
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent          # LB-v2.0
$repo = Split-Path $root -Parent                  # 工程根
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$iscc = 'D:\Inno Setup 6\ISCC.exe'
$helper = Join-Path $root '安装器\记账本小工具.exe'
$iss = Join-Path $root '安装包\记账本.iss'
$icon = Join-Path $root '安装包\appicon.ico'

if (-not (Test-Path $csc)) { throw "找不到 csc.exe：$csc" }
if (-not (Test-Path $iscc)) { throw "找不到 ISCC.exe：$iscc（Inno Setup 6 装在哪了？）" }

Write-Host '[1/3] 编记账本小工具（卸载器 + 安装后置，.NET Framework 4.8）…'
& $csc /nologo /target:winexe /out:$helper /win32icon:$icon `
    /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    (Join-Path $root '安装器\记账本小工具.cs')
if ($LASTEXITCODE -ne 0) { throw '小工具编译失败' }
Write-Host ("      产物：" + $helper + "（" + [math]::Round((Get-Item $helper).Length/1KB,1) + " KB）")

Write-Host '[2/3] 调 ISCC 编安装包…'
if ($自检) {
    & $iscc '/DTestIcons' $iss | Select-Object -Last 4
} else {
    & $iscc $iss | Select-Object -Last 4
}
if ($LASTEXITCODE -ne 0) { throw 'Inno 编译失败' }

Write-Host '[3/3] 结果…'
$name = if ($自检) { '记账本-安装包-v2.0.1-单文件.exe' } else { '记账本-安装包-v2.0.1-单文件.exe' }
$out = Join-Path $repo "newest\$name"
if (Test-Path $out) {
    $info = Get-Item $out
    Write-Host ("      产物：" + $info.FullName)
    Write-Host ("      大小：{0:N1} MB" -f ($info.Length / 1MB))
    Write-Host ("      SHA256：" + (Get-FileHash $out -Algorithm SHA256).Hash)
} else {
    Write-Warning "没找到产物 $out"
}
