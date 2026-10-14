# 记账本 · 甲方案（自研安装器）一条命令全流程：出负载 → 出安装包 → 出便携包
#
# 产物（都在 newest\）：
#   记账本-安装包-v2.0.1-单文件-完整.exe   约 62MB：程序 + 内置运行时 + 卸载器（没装 .NET 的机器也能装）
#   记账本-安装包-v2.0.1-单文件-小包.exe   约 0.7MB：只有程序 + 卸载器（日常更新包 / 已装 .NET 的机器）
#   记账本-便携版-v2.0.1-内置运行时.zip     约 61MB：免安装（解压双击，任何 Win10/11）
#   记账本-便携版-v2.0.1-精简.zip           约 0.4MB：免安装（需已装 .NET 8）
#
# 关键设计（"更新只写 0.4MB"的来源）：
#   · 程序负载 = 框架依赖的 7 个文件（两种机器通用）
#   · 运行时负载 = hostfxr.dll + shared\Microsoft.*\<版本>\（私有运行时布局，实测 trace 会用包内运行时）
#     只有"系统没 .NET 8 且目标里没有同版本运行时"时才解，且带版本标记，更新时整段跳过
#   · 因此：不需要自包含发布、不需要联网 NuGet、不需要 AspNetCore 那套绕法了
#
# 用法：pwsh -File 打包-自研安装包.ps1 [-跳过应用编译] [-只出负载] [-输出目录 <目录>]
#
# 输出（默认按版本各自的文件夹，不再用 newest）：
#   v0.2\v<版本>\安装包\   两个安装器（完整 / 小包）
#   v0.2\v<版本>\便携版\   两个便携 zip（内置运行时 / 精简）

param(
    [switch]$跳过应用编译,
    [switch]$只出负载,
    [string]$输出目录
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent          # v0.2
$repo = Split-Path $root -Parent                  # 工程根
$toolchain = Join-Path $repo 'LB-构建工具链'
$sdk = Join-Path $toolchain 'dotnet'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$load = Join-Path $repo '临时\负载'
$appDir = Join-Path $load '程序'
$rtDir = Join-Path $load '运行时-8.0.31'
$appZip = Join-Path $load 'payload-app.zip'
$rtZip = Join-Path $load 'payload-runtime.zip'
$runtimeList = Join-Path $load 'runtime-files.txt'
$src = Join-Path $root '安装\安装器'
$helper = Join-Path $src '记账本小工具.exe'
$icon = Join-Path $root '安装\素材\appicon.ico'
$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw '找不到 python' }

# 运行时版本从工具链里读（它和应用版本号是两回事）
$runtimeVersion = (Get-ChildItem (Join-Path $sdk 'shared\Microsoft.NETCore.App') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1).Name
if (-not $runtimeVersion) { throw '读不出工具链里的运行时版本' }

# 应用版本只有一个源头：Directory.Build.props 的 <Version>（用正则读，别用 [xml]：该文件里中文标签的
# 嵌套赋值会让 XmlDocument 转换失败）
$m = Select-String -Path (Join-Path $root 'Directory.Build.props') -Pattern '<Version>([^<]+)</Version>'
if (-not $m) { throw '读不出应用版本号（Directory.Build.props 里没有 <Version>）' }
$版本 = $m.Matches[0].Groups[1].Value
$rtDir = Join-Path $load "运行时-$runtimeVersion"
Write-Host ("应用版本：$版本    运行时版本：$runtimeVersion")

# 每个版本自己的文件夹：v0.2\v<版本>\安装包\ 与 v0.2\v<版本>\便携版\（不再往 newest 写）
$版本目录 = Join-Path $root "v$版本"
$安装包目录 = if ($输出目录) { $输出目录 } else { Join-Path $版本目录 '安装包' }
$便携版目录 = if ($输出目录) { Join-Path $输出目录 '便携版' } else { Join-Path $版本目录 '便携版' }
New-Item -ItemType Directory -Force -Path $安装包目录, $便携版目录 | Out-Null

# 生成 BuildInfo（安装器与小工具都引用它，避免版本号散落多份）
$genDir = Join-Path $repo '临时\生成'
New-Item -ItemType Directory -Force -Path $genDir | Out-Null
$versionFile = Join-Path $genDir '版本信息.cs'
$gen = @"
// 本文件由 工具\打包-自研安装包.ps1 自动生成，请勿手改。
// 版本号源头：v0.2\Directory.Build.props 的 <Version>
internal static class BuildInfo
{
    public const string AppVersion = "$版本";
    public const string RuntimeVersion = "$runtimeVersion";
}
"@
[System.IO.File]::WriteAllText($versionFile, $gen, (New-Object System.Text.UTF8Encoding($true)))

# ---------------------------------------------------------------- 1) 程序负载
if (-not $跳过应用编译) {
    Write-Host '[1/5] 编程序负载（框架依赖版，7 个文件）…'
    . (Join-Path $toolchain 'dotnet_env.ps1') | Out-Null
    if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
    & $env:LB_DOTNET publish (Join-Path $root 'src\LedgerBook.App\LedgerBook.App.csproj') `
        -c Release --no-self-contained -o $appDir /p:RestoreConfigFile=$env:LB_NUGET_CONFIG | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '应用编译失败' }

# 把「更新公告.md」作为更新内容放进应用负载（装好后"更新内容"窗口显示的就是它）
$公告源 = Join-Path $repo "v0.2\v$版本\更新公告.md"
if (Test-Path $公告源) {
    Copy-Item $公告源 (Join-Path $appDir '更新内容.txt') -Force
    Write-Host '  更新内容.txt ← 更新公告.md'
}
}

# ---------------------------------------------------------------- 2) 运行时负载（私有运行时布局）
Write-Host '[2/5] 准备运行时负载（hostfxr + shared\，实测会走包内运行时）…'
if (-not (Test-Path $rtDir)) { New-Item -ItemType Directory -Force -Path $rtDir | Out-Null }
foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    $target = Join-Path $rtDir "shared\$name\$runtimeVersion"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    # 注意：robocopy 默认不递归，必须 /E（源目录里既有文件也有卫星子目录）
    $null = robocopy (Join-Path $sdk "shared\$name\$runtimeVersion") $target /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1
}
Copy-Item (Join-Path $sdk "host\fxr\$runtimeVersion\hostfxr.dll") $rtDir -Force

# 裁掉用不到的语言卫星包（只留中文 zh-Hans），省下 ~10MB；名字用显式清单最稳
$dropLanguages = @('cs', 'de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr', 'zh-Hant')
$trimmed = 0
foreach ($name in $dropLanguages) {
    foreach ($dir in (Get-ChildItem $rtDir -Recurse -Directory -Filter $name -ErrorAction SilentlyContinue)) {
        $trimmed += (Get-ChildItem $dir.FullName -Recurse -File -ErrorAction SilentlyContinue).Count
        Remove-Item $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host ("  裁掉语言卫星包 " + $trimmed + " 个文件（只留 zh-Hans）")

# 合法性自检：这几样缺一个，装出来就跑不起来
foreach ($f in @('hostfxr.dll',
                 "shared\Microsoft.NETCore.App\$runtimeVersion\System.Private.CoreLib.dll",
                 "shared\Microsoft.WindowsDesktop.App\$runtimeVersion\PresentationFramework.dll")) {
    if (-not (Test-Path (Join-Path $rtDir $f))) { throw "运行时负载缺文件：$f" }
}
foreach ($f in @('记账本.exe', '记账本.dll', '记账本.deps.json', '记账本.runtimeconfig.json', 'LedgerBook.Core.dll')) {
    if (-not (Test-Path (Join-Path $appDir $f))) { throw "程序负载缺文件：$f" }
}

# ---------------------------------------------------------------- 3) 打 zip + 运行时清单
Write-Host '[3/5] 打两个负载 zip…'
& $python (Join-Path $root '工具\打包zip.py') --源 $appDir --输出 $appZip
if ($LASTEXITCODE -ne 0) { throw '程序负载打包失败' }
& $python (Join-Path $root '工具\打包zip.py') --源 $rtDir --输出 $rtZip
if ($LASTEXITCODE -ne 0) { throw '运行时负载打包失败' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($rtZip)
try {
    $names = $zip.Entries | Where-Object { $_.Name.Length -gt 0 } | ForEach-Object { $_.FullName }
    [System.IO.File]::WriteAllLines($runtimeList, $names, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("  运行时清单 " + @($names).Count + " 条")
} finally { $zip.Dispose() }

# ---------------------------------------------------------------- 4) 便携包
Write-Host '[4/5] 打便携包…'
$portableMix = Join-Path $repo '临时\便携版-内置运行时'
if (Test-Path $portableMix) { Remove-Item $portableMix -Recurse -Force }
New-Item -ItemType Directory -Force -Path $portableMix | Out-Null
$null = robocopy $rtDir $portableMix /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1
$null = robocopy $appDir $portableMix /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1
& $python (Join-Path $root '工具\打包zip.py') --源 $portableMix --输出 (Join-Path $便携版目录 "记账本-便携版-v$版本-内置运行时.zip")
if ($LASTEXITCODE -ne 0) { throw '便携版-内置运行时.zip 失败' }
& $python (Join-Path $root '工具\打包zip.py') --源 $appDir --输出 (Join-Path $便携版目录 "记账本-便携版-v$版本-精简.zip")
if ($LASTEXITCODE -ne 0) { throw '便携版-精简.zip 失败' }

if ($只出负载) {
    Write-Host '（-只出负载：跳过编安装器）'
    exit 0
}

# ---------------------------------------------------------------- 5) 编安装器（完整 + 小包）
Write-Host '[5/5] 编安装器（完整 + 小包）…'
& $csc /nologo /target:winexe /out:$helper /win32icon:$icon `
    /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    $versionFile `
    (Join-Path $src '记账本小工具.cs')
if ($LASTEXITCODE -ne 0) { throw '小工具编译失败' }

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('System.dll', 'System.Core.dll', 'Microsoft.CSharp.dll', 'System.Drawing.dll',
          'System.Windows.Forms.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll')
foreach ($variant in @(@('完整', $true), @('小包', $false))) {
    $suffix = $variant[0]
    $withRuntime = $variant[1]
    $out = Join-Path $安装包目录 "记账本-安装包-v$版本-单文件-$suffix.exe"
    $cargs = @('/nologo', '/target:winexe', "/out:$out", "/win32icon:$icon",
               "/resource:$appZip,payload-app.zip",
               "/resource:$runtimeList,runtime-files.txt",
               "/resource:$icon,appicon.ico",
               "/resource:$helper,helper.exe")
    if ($withRuntime) { $cargs += "/resource:$rtZip,payload-runtime.zip" }
    $cargs += ($refs | ForEach-Object { "/r:" + (Join-Path $fw $_) })
    $cargs += $versionFile
    $cargs += (Join-Path $src '记账本安装器.cs')
    & $csc @cargs
    if ($LASTEXITCODE -ne 0) { throw "安装器（$suffix）编译失败" }
}

Write-Host ''
Write-Host '==== 产物 ===='
Get-ChildItem $安装包目录, $便携版目录 -File | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-46} {1,8:N1} MB  {2}" -f $_.Name, ($_.Length / 1MB),
        (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16))
}
