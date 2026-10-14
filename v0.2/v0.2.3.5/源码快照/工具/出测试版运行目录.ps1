# 记账本 · 出测试版的「运行」目录（点开就能跑，不打包、不动 newest）
#
# 按 2026-10-09 商定的规则：测试版（D 位，例如 0.2.1.5）直接给一个**解压好、双击就能跑**的目录，
# 不做安装包、不动 newest：
#     v0.2\v<版本>\运行\    ← 程序 + 私有运行时（约 146MB），双击「记账本.exe」
#
# 正式版（C 位，例如 0.2.1、0.2.2）才另外走：
#     工具\打包-自研安装包.ps1（出安装包 + 便携包 → newest）
#     工具\存档版本.ps1       （连同安装包一起存档到 历史版本\）
#
# 用法：pwsh -ExecutionPolicy Bypass -File 出测试版运行目录.ps1
#       pwsh -ExecutionPolicy Bypass -File 出测试版运行目录.ps1 -版本 0.2.2 -跳过应用编译

param(
    [string]$版本,
    [switch]$跳过应用编译,
    [switch]$不出截图
)

$ErrorActionPreference = 'Stop'
Set-ExecutionPolicy -Scope Process Bypass -Force
$root = Split-Path $PSScriptRoot -Parent          # v0.2
$repo = Split-Path $root -Parent                  # 工程根
$toolchain = Join-Path $repo 'LB-构建工具链'

if (-not $版本) {
    $m = Select-String -Path (Join-Path $root 'Directory.Build.props') -Pattern '<Version>([^<]+)</Version>'
    if (-not $m) { throw '读不出版本号' }
    $版本 = $m.Matches[0].Groups[1].Value
}

$appDir = Join-Path $repo '临时\负载\程序'
$rtDir = Join-Path $repo '临时\负载\运行时-8.0.31'
$run = Join-Path $root "v$版本\运行"

Write-Host ("测试版运行目录：v$版本 → " + $run)

# 1) 编应用（框架依赖版）
if (-not $跳过应用编译) {
    . (Join-Path $toolchain 'dotnet_env.ps1') | Out-Null
    $fresh = Join-Path $repo '临时\负载\程序-新'
    if (Test-Path $fresh) { Remove-Item $fresh -Recurse -Force }
    & $env:LB_DOTNET publish (Join-Path $root 'src\LedgerBook.App\LedgerBook.App.csproj') `
        -c Release --no-self-contained -o $fresh /p:RestoreConfigFile=$env:LB_NUGET_CONFIG | Out-Null
    if (-not (Test-Path (Join-Path $fresh '记账本.exe'))) { throw '应用编译失败' }
    if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
    Move-Item $fresh $appDir -Force
}

if (-not (Test-Path (Join-Path $appDir '记账本.exe'))) { throw "程序负载不完整：$appDir（先跑一次 工具\打包-自研安装包.ps1 或去掉 -跳过应用编译）" }
if (-not (Test-Path (Join-Path $rtDir 'hostfxr.dll'))) { throw "运行时负载不完整：$rtDir（先跑一次 工具\打包-自研安装包.ps1 -只出负载）" }

  # 2) 组装前：**先保住运行目录里用户自己的东西**（数据/配置/导出/备份/日志）
  #    教训（2026-10-13）：这里原来是直接 Remove-Item 整个目录，把用户在"双击就能跑"的目录里
  #    记的账一起删了（数据不进回收站，删了就回不来）。现在改成"挪出来 → 重建 → 放回去"。
  $keepNames = @('数据', '配置', '导出', '备份', '日志')
  $stash = Join-Path $repo ('临时\保留-' + (Get-Date -Format 'HHmmss'))
  $kept = @()
  if (Test-Path $run) {
      foreach ($n in $keepNames) {
          $p = Join-Path $run $n
          if (Test-Path $p) {
              New-Item -ItemType Directory -Force -Path $stash | Out-Null
              Copy-Item $p (Join-Path $stash $n) -Recurse -Force
              $kept += $n
          }
      }
      if ($kept.Count -gt 0) { Write-Host ("  先保留用户目录：" + ($kept -join '、')) }
  }

# 2) 组装：运行时打底 + 程序覆盖（同名的程序文件以程序为准）
if (Test-Path $run) { Remove-Item $run -Recurse -Force }
New-Item -ItemType Directory -Force -Path $run | Out-Null
$null = robocopy $rtDir $run /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1
$null = robocopy $appDir $run /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1

  # 3) 把刚才保留的用户目录放回去（这样重建不会清掉运行目录里的账本）
  foreach ($n in $kept) {
      Copy-Item (Join-Path $stash $n) (Join-Path $run $n) -Recurse -Force
  }
  if ($kept.Count -gt 0) {
      Write-Host ("  已放回：" + ($kept -join '、'))
      Remove-Item $stash -Recurse -Force -ErrorAction SilentlyContinue
  }

$files = Get-ChildItem $run -Recurse -File
Write-Host ("  文件数 " + $files.Count + "，合计 " + [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1) + " MB")

# 2.5) 更新内容窗口的文本来源：把更新日志复制成 运行\更新内容.txt（用户要求：打包前复制更新日志即可）
$更新日志 = Join-Path $repo "v0.2\v$版本\本次修改.md"
if (-not (Test-Path $更新日志)) { $更新日志 = Join-Path $repo "v0.2\v$版本\开发日志.md" }
if (Test-Path $更新日志) {
    Copy-Item $更新日志 (Join-Path $run "更新内容.txt") -Force
    Write-Host ("  更新内容.txt ← " + (Split-Path $更新日志 -Leaf))
} else {
    Write-Host "  注意：没找到更新日志，更新内容窗口会显示内置兜底文字" -ForegroundColor Yellow
}

# 3) 真跑一遍（截图 + 确认用的是目录内的运行时）
if (-not $不出截图) {
    $shot = Join-Path $repo "截图\v$版本-应用.png"
    $trace = Join-Path $run 'trace.txt'
    $env:COREHOST_TRACE = '1'
    $env:COREHOST_TRACEFILE = $trace
    & (Join-Path $run '记账本.exe') --root (Join-Path $repo "临时\v$版本-检查根") --screenshot $shot | Out-Null
    $code = $LASTEXITCODE
    Remove-Item Env:\COREHOST_TRACE -ErrorAction SilentlyContinue
    Remove-Item Env:\COREHOST_TRACEFILE -ErrorAction SilentlyContinue
    $localRuntime = $false
    if (Test-Path $trace) {
        $localRuntime = (Get-Content $trace -Raw) -match 'Chose FX version \[.*v' + [regex]::Escape($版本) + '.*shared'
        Remove-Item $trace -Force -ErrorAction SilentlyContinue
    }
    Write-Host ("  跑起来退出码 " + $code + "；截图 " + (Test-Path $shot) + "；用目录内运行时 " + $localRuntime)
    if ($code -ne 0) { throw '应用没跑起来，检查上面的输出' }
}

$vi = (Get-Item (Join-Path $run '记账本.exe')).VersionInfo
Write-Host ("  版本：" + $vi.FileVersion + "（应等于 " + $版本 + "）")
Write-Host ("完成。双击这个文件即可： " + (Join-Path $run '记账本.exe')) -ForegroundColor Green
