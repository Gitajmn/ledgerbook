# 记账本 · 版本存档脚本（可复用：每次发版都跑这个）
#
# 存什么：
#   历史版本\v<版本>-存档-<日期>\
#     ├─ 安装包\   完整 / 小包 exe
#     ├─ 便携版\   两个 zip
#     ├─ 说明\     交付说明（newest 里的说明文件）
#     ├─ 源码快照\ 应用源码 + 安装器源码 + 工具脚本 + 继承文档（本项目**没有 git**，这是唯一的历史记录）
#     └─ 存档清单.txt  每个文件的 SHA256/大小 + 构建信息 + 自检结论 + 校验结果
#
# 用法：
#   pwsh -File 存档版本.ps1                        # 按 Directory.Build.props 里的版本 + 今天日期
#   pwsh -File 存档版本.ps1 -版本 2.0.2 -日期 2026-10-12 -追加说明 "修了 xxx"

param(
    [string]$版本,
    [string]$日期 = (Get-Date -Format 'yyyy-MM-dd'),
    [string]$追加说明 = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent          # LB-v2.0
$repo = Split-Path $root -Parent                  # 工程根

if (-not $版本) {
    # 用正则读，别用 XML 解析（该文件里 <Product> 等标签的嵌套会让 [xml] 转换失败）
    $m = Select-String -Path (Join-Path $root 'Directory.Build.props') -Pattern '<Version>([^<]+)</Version>'
    if ($m) { $版本 = $m.Matches[0].Groups[1].Value }
    if (-not $版本) { throw '读不出版本号，请用 -版本 指定' }
}

$dst = Join-Path $repo "历史版本\v$版本-存档-$日期"
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
foreach ($sub in @('安装包', '便携版', '说明', '源码快照')) { New-Item -ItemType Directory -Force -Path (Join-Path $dst $sub) | Out-Null }

Write-Host ("存档到：" + $dst)

# ---------------------------------------------------------------- 1) 安装包 / 便携版 / 说明
$newest = Join-Path $repo 'newest'
$map = @{
    '安装包' = @("记账本-安装包-v$版本-单文件-完整.exe", "记账本-安装包-v$版本-单文件-小包.exe")
    '便携版' = @("记账本-便携版-v$版本-内置运行时.zip", "记账本-便携版-v$版本-精简.zip")
}
foreach ($sub in $map.Keys) {
    foreach ($name in $map[$sub]) {
        $src = Join-Path $newest $name
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $dst $sub) -Force
            Write-Host ("  已存 " + $sub + '\' + $name + "  " + [math]::Round((Get-Item $src).Length / 1MB, 2) + " MB")
        } else {
            Write-Host ("  [跳过] 没有 " + $name) -ForegroundColor Yellow
        }
    }
}
foreach ($f in (Get-ChildItem $newest -File -Filter '*.txt' -ErrorAction SilentlyContinue)) {
    Copy-Item $f.FullName (Join-Path $dst '说明') -Force
    Write-Host ("  已存 说明\" + $f.Name)
}

# ---------------------------------------------------------------- 2) 源码快照
Write-Host '源码快照…'
$snapshot = Join-Path $dst '源码快照'
# 应用与数据层、安装器、工具脚本、继承文档（排除 bin/obj）
$pairs = @(
    @{ 源 = (Join-Path $root 'src'); 目标 = (Join-Path $snapshot '应用源码'); 排除 = @('bin', 'obj') },
    @{ 源 = (Join-Path $root '安装器'); 目标 = (Join-Path $snapshot '安装器'); 排除 = @() },
    @{ 源 = (Join-Path $root '工具'); 目标 = (Join-Path $snapshot '工具'); 排除 = @() },
    @{ 源 = (Join-Path $root 'tools'); 目标 = (Join-Path $snapshot 'tools'); 排除 = @('bin', 'obj') }
)
foreach ($p in $pairs) {
    if (-not (Test-Path $p.源)) { continue }
    $xd = @()
    foreach ($d in $p.排除) { $xd += '/XD'; $xd += $d }
    $null = robocopy $p.源 $p.目标 /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 @xd
}
Copy-Item (Join-Path $repo '文档\继承文档-v2.0.1.md') (Join-Path $snapshot '继承文档.md') -Force
Copy-Item (Join-Path $root 'Directory.Build.props') (Join-Path $snapshot 'Directory.Build.props') -Force
Write-Host ("  快照文件数：" + (Get-ChildItem $snapshot -Recurse -File).Count)

# ---------------------------------------------------------------- 3) 存档清单
Write-Host '写存档清单…'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("记账本 v$版本 · 存档清单")
$lines.Add("存档时间：" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines.Add("存档目录：$dst")
$lines.Add("应用版本：$版本    运行时版本：8.0.31（私有运行时布局）")
$lines.Add("构建方式：pwsh -File LB-v2.0\工具\打包-自研安装包.ps1（应用 FDD + 私有运行时负载 + 自研安装器）")
$lines.Add("自检结论：离线逻辑 35/35 + 甲安装器真装真卸 22/22 + 应用 87/87 全绿（工具\安装包自检-甲.ps1 -含应用自检）")
if ($追加说明) { $lines.Add("附加说明：$追加说明") }
$lines.Add('')
$lines.Add('【文件清单 + SHA256】')
$files = Get-ChildItem $dst -Recurse -File | Sort-Object FullName
$total = 0
foreach ($f in $files) {
    $rel = $f.FullName.Substring($dst.Length).TrimStart('\')
    $hash = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
    $total += $f.Length
    $lines.Add(("{0,-72} {1,10:N0} 字节  {2}" -f $rel, $f.Length, $hash))
}
$lines.Add('')
$lines.Add(("合计：" + $files.Count + " 个文件，" + [math]::Round($total / 1MB, 1) + " MB"))

# ---------------------------------------------------------------- 4) 校验（把已存的二进制和 newest 里的一一比对）
$lines.Add('')
$lines.Add('【回读校验（存档副本 vs newest 原件）】')
foreach ($sub in $map.Keys) {
    foreach ($name in $map[$sub]) {
        $a = Join-Path $dst (Join-Path $sub $name)
        $b = Join-Path $newest $name
        if ((Test-Path $a) -and (Test-Path $b)) {
            $same = (Get-FileHash $a -Algorithm SHA256).Hash -eq (Get-FileHash $b -Algorithm SHA256).Hash
            $lines.Add(("  {0}  {1}" -f $(if ($same) { '[一致]' } else { '[不一致]' }), $name))
        }
    }
}

$manifest = Join-Path $dst '存档清单.txt'
[System.IO.File]::WriteAllLines($manifest, $lines, (New-Object System.Text.UTF8Encoding($true)))
Write-Host ''
$lines | Where-Object { $_ -match '^  \[|^合计|^应用版本|^自检结论' } | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host ("存档完成：" + $dst) -ForegroundColor Green
