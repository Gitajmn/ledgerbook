# 记账本 · 安装包自检（甲方案 / 自研安装器）—— 一条命令跑完，全是"真装真卸"的实测
#
#   1) 离线逻辑自检：卸载规则（保留数据目录、只删空目录、被占用时不误删）+ 负载切片
#   2) 甲安装器自检：
#        a. 静默装到"中文 + 空格"路径 → 文件 / 安装信息 / 快捷方式 / 退出码
#        b. 强制装运行时那条路 → hostfxr + shared\ + 版本标记齐全，且 trace 证明用的是**包内运行时**
#        c. 【回归】更新不许重写运行时：完整包再装 + 小包再装，运行时文件时间戳必须不变
#        d. 安装包改名后照样能装（名字不影响任何逻辑）
#        e. 卸载后程序文件没了、账本数据一个不少
#   3) 应用自检 87 项（可选：-含应用自检）
#
# 用法：pwsh -File 安装包自检-甲.ps1 [-含应用自检] [-包目录 <安装包所在目录>]
#
# 包目录默认 = v0.2\v<版本>\安装包\（正式版才有包；测试版没打包时会自动跳过"安装器真装真卸"那一段）

param(
    [switch]$含应用自检,
    [string]$包目录
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent          # v0.2
$repo = Split-Path $root -Parent                  # 工程根
$toolchain = Join-Path $repo 'LB-构建工具链'
$temp = Join-Path $repo '临时'
$failures = @()

# 版本号从 Directory.Build.props 读（别硬编码，否则发版要改多处）
$vm = Select-String -Path (Join-Path $root 'Directory.Build.props') -Pattern '<Version>([^<]+)</Version>'
$版本 = $vm.Matches[0].Groups[1].Value
if (-not $包目录) { $包目录 = Join-Path $root "v$版本\安装包" }
$full = Join-Path $包目录 "记账本-安装包-v$版本-单文件-完整.exe"
$small = Join-Path $包目录 "记账本-安装包-v$版本-单文件-小包.exe"
Write-Host ("被测版本：" + $版本 + "    包目录：" + $包目录)

function RunWait([string]$exe, [string[]]$argv) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.UseShellExecute = $false          # $true 在本沙箱会挂在 ShellExecuteEx
    $psi.CreateNoWindow = $true
    $psi.Arguments = ($argv | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $p = [System.Diagnostics.Process]::Start($psi)
    $null = $p.WaitForExit(900000)
    return [int]$p.ExitCode
}

function Check([string]$what, [bool]$ok, [string]$extra = '') {
    Write-Host ('  ' + $(if ($ok) { '[通过]' } else { '[失败]' }) + ' ' + $what + $(if ($extra) { '  ' + $extra } else { '' }))
    if (-not $ok) { $script:failures += $what }
}

# ---------------------------------------------------------------- 1) 离线逻辑自检
Write-Host ''
Write-Host '===== 1/3 离线逻辑自检（卸载规则 / 负载切片）=====' -ForegroundColor Cyan
. (Join-Path $toolchain 'dotnet_env.ps1') | Out-Null
& $env:LB_DOTNET build (Join-Path $root 'tools\LedgerBook.UninstallTest\LedgerBook.UninstallTest.csproj') `
    -c Release -o (Join-Path $temp '自检-卸载逻辑') /p:RestoreConfigFile=$env:LB_NUGET_CONFIG | Out-Null
if ($LASTEXITCODE -ne 0) { throw '离线自检工程编译失败' }
& (Join-Path $temp '自检-卸载逻辑\卸载逻辑自检.exe') (Join-Path $temp '卸载逻辑测试')
if ($LASTEXITCODE -ne 0) { $failures += '离线逻辑自检' }

# ---------------------------------------------------------------- 2) 甲安装器自检
Write-Host ''
Write-Host '===== 2/3 甲安装器自检（真装真卸）=====' -ForegroundColor Cyan
$跳过安装器自检 = $false
if (-not (Test-Path $full) -or -not (Test-Path $small)) {
    Write-Host '  （这一版还没打安装包 → 跳过"安装器真装真卸"这一段；测试版不打包，属正常）' -ForegroundColor Yellow
    $跳过安装器自检 = $true
}
if (-not $跳过安装器自检) {

# a) 静默装到"中文 + 空格"路径
$base = Join-Path $temp '甲安装自检'
$target = Join-Path $base '记账本 测试目录'
if (Test-Path $base) { Remove-Item $base -Recurse -Force }
$sw = [Diagnostics.Stopwatch]::StartNew()
$code = RunWait $full @('--silent', '--dir', $target, '--test-icons', '--no-launch')
$sw.Stop()
Check '静默安装退出码 0' ($code -eq 0) ('退出码 ' + $code + '，用时 ' + [math]::Round($sw.Elapsed.TotalSeconds, 1) + ' 秒')
Check '程序文件到位（记账本.exe / Core.dll）' ((Test-Path "$target\记账本.exe") -and (Test-Path "$target\LedgerBook.Core.dll"))
Check '卸载器已放置' (Test-Path "$target\卸载记账本.exe")
Check '安装信息.txt 已写' (Test-Path "$target\安装信息.txt")
Check '快捷方式已创建（自检模式建在安装目录内）' (Test-Path "$target\_测试快捷方式\记账本.lnk")
Check '安装信息.txt 里的版本号来自源头' ((Get-Content "$target\安装信息.txt" -Encoding UTF8 -Raw) -match [regex]::Escape($版本))
Check '没留下工作目录残留' (-not (Test-Path "$target\lb-setup"))

# b) 强制装运行时那条路（模拟"没装 .NET 的机器"）
$t2 = Join-Path $base '自带运行时'
$code = RunWait $full @('--silent', '--dir', $t2, '--force-runtime', '--test-icons', '--no-launch')
Check '带运行时安装退出码 0' ($code -eq 0)
Check 'hostfxr.dll 就位' (Test-Path "$t2\hostfxr.dll")
Check '运行时版本标记就位' (Test-Path "$t2\运行时版本.txt")
Check 'shared 两个框架就位' ((Test-Path "$t2\shared\Microsoft.NETCore.App\8.0.31\System.Private.CoreLib.dll") -and
                          (Test-Path "$t2\shared\Microsoft.WindowsDesktop.App\8.0.31\PresentationFramework.dll"))

$shot = Join-Path $t2 '自检截图.png'
$trace = Join-Path $t2 'trace.txt'
$appTrace = Join-Path $t2 'trace.log'

# 应用是单实例（Mutex "Local\LedgerBook-记账本"）：上一次运行若卡住留了残留实例，
# 后面的 --screenshot 会被挡在门外直接退出、还不报错 —— 所以先杀残留，再带超时跑，失败就把应用 trace 打出来。
Get-Process -Name 记账本 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Remove-Item $appTrace -Force -ErrorAction SilentlyContinue
$env:COREHOST_TRACE = '1'
$env:COREHOST_TRACEFILE = $trace
$appPsi = New-Object System.Diagnostics.ProcessStartInfo
$appPsi.FileName = "$t2\记账本.exe"
$appPsi.UseShellExecute = $false
$appPsi.CreateNoWindow = $true
$appPsi.WorkingDirectory = $t2
$appPsi.Arguments = "--root `"$(Join-Path $t2 'root')`" --screenshot `"$shot`" --trace"
$appProc = [System.Diagnostics.Process]::Start($appPsi)
$exited = $appProc.WaitForExit(120000)
if (-not $exited) {
    Write-Host '  应用 120 秒没退出（可能是弹了对话框卡住）→ 强制结束' -ForegroundColor Yellow
    Stop-Process -Id $appProc.Id -Force -ErrorAction SilentlyContinue
}
$code = if ($exited) { [int]$appProc.ExitCode } else { -1 }
Remove-Item Env:\COREHOST_TRACE -ErrorAction SilentlyContinue
Remove-Item Env:\COREHOST_TRACEFILE -ErrorAction SilentlyContinue
$traceText = if (Test-Path $trace) { [System.IO.File]::ReadAllText($trace) } else { '' }
Check '装好的程序能跑出截图' ((Test-Path $shot) -and ($code -eq 0))
if (-not (Test-Path $shot)) {
    Write-Host ("  诊断：应用退出码 " + $code) -ForegroundColor Yellow
    if (Test-Path $appTrace) { Get-Content $appTrace -Encoding UTF8 -Tail 8 | ForEach-Object { "    " + $_ } }
}
Check '运行时来自包内（trace 指向安装目录的 shared）' ($traceText -match 'Chose FX version \[.*shared\\Microsoft\.WindowsDesktop\.App')

# c) 【回归】更新不许重写运行时
$sample = Join-Path $t2 'shared\Microsoft.NETCore.App\8.0.31\System.Private.CoreLib.dll'
$before = (Get-Item $sample).LastWriteTimeUtc
$beforeRuntimeCount = (Get-ChildItem "$t2\shared" -Recurse -File).Count
$code = RunWait $full @('--silent', '--dir', $t2, '--test-icons', '--no-launch')
Check '完整包更新退出码 0' ($code -eq 0)
Check '完整包更新没重写运行时（时间戳不变）' ((Get-Item $sample).LastWriteTimeUtc -eq $before)
$code = RunWait $small @('--silent', '--dir', $t2, '--test-icons', '--no-launch')
Check '小包更新退出码 0' ($code -eq 0)
Check '小包更新没重写运行时（时间戳不变）' ((Get-Item $sample).LastWriteTimeUtc -eq $before)
Check '更新后运行时文件数没变' ((Get-ChildItem "$t2\shared" -Recurse -File).Count -eq $beforeRuntimeCount)

# d) 改名后照样能装
$renamed = Join-Path $base '被我改过名字的安装包.exe'
Copy-Item $full $renamed -Force
$t3 = Join-Path $base '改名测试'
$code = RunWait $renamed @('--silent', '--dir', $t3, '--test-icons', '--no-launch')
Check '安装包改名后照装不误' (($code -eq 0) -and (Test-Path "$t3\记账本.exe"))

# e) 卸载：程序没了、数据一个不少
New-Item -ItemType Directory -Force -Path "$target\数据\2026\10" | Out-Null
Set-Content "$target\数据\2026\10\2026-10-09.jsonl" '{"金额":"18.50"}' -Encoding UTF8
$code = RunWait "$target\卸载记账本.exe" @('--uninstall', '--silent', '--target', $target)
Start-Sleep -Seconds 8
Check '卸载退出码 0' ($code -eq 0)
Check '卸载后程序文件已删（记账本.exe）' (-not (Test-Path "$target\记账本.exe"))
Check '卸载后卸载器自己也被清掉' (-not (Test-Path "$target\卸载记账本.exe"))
Check '账本数据一个不少' (Test-Path "$target\数据\2026\10\2026-10-09.jsonl")

}

# ---------------------------------------------------------------- 3) 应用自检（可选）
if ($含应用自检) {
    Write-Host ''
    Write-Host '===== 3/3 应用自检 87 项 =====' -ForegroundColor Cyan
    $selfCheck = Join-Path $root 'tools\LedgerBook.SelfCheck\bin\Release\net8.0\自检.exe'
    if (-not (Test-Path $selfCheck)) {
        & $env:LB_DOTNET build (Join-Path $root 'tools\LedgerBook.SelfCheck\LedgerBook.SelfCheck.csproj') `
            -c Release /p:RestoreConfigFile=$env:LB_NUGET_CONFIG | Out-Null
    }
    # 每次都用全新的根目录：残留数据会把计数搞乱（我曾因此误报 9 项失败）
    $freshRoot = Join-Path $temp ('自检-全新根目录-甲-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Write-Host ('  全新根目录：' + $freshRoot)
    & $selfCheck $freshRoot
    if ($LASTEXITCODE -ne 0) { $failures += '应用自检' }
} else {
    Write-Host ''
    Write-Host '（跳过应用自检：加 -含应用自检 会一起跑那 87 项）'
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host '全部自检通过 ✅' -ForegroundColor Green
    Write-Host ('  完整包：' + $full)
    Write-Host ('  小包  ：' + $small)
    exit 0
}

Write-Host ('有失败项：' + ($failures -join '、')) -ForegroundColor Red
exit 1
