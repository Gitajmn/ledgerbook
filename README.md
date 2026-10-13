# 记账本 LedgerBook

一个**本地优先**的个人记账本：数据全部存在自己电脑上（JSONL 明文，可随时用记事本打开），
自带三维统计图（饼图 / 柱状图 / 方格图）、导入导出、自动备份与运行日志。

- **平台**：Windows 10 / 11（x64）
- **技术**：C# / .NET 8 / WPF（界面全部用 C# 代码构建，不写 XAML）
- **数据格式**：`数据\年\月\YYYY-MM-DD.jsonl`（一行一条记录，字段带名字，UTF-8 无 BOM）
- **版本号规则**：`A.B.C.D` = 平台(0) . 架构世代(2) . 正式版号 . 测试位

## 目录

```
v0.1/            第一代（历史）
v0.2/            第二代
  Directory.Build.props   版本号单一来源
  src/
    LedgerBook.Core/      数据层：存储、查询、汇总、导入导出、设置、日志
    LedgerBook.App/       WPF 界面：设计系统(Ui.cs)、四个页面、图表(Charts.cs)
    LedgerBook.Setup/     自研安装器（WPF，单文件自解包）
  tools/
    LedgerBook.SelfCheck/ 数据层自检（96 项）
  工具/                   打包 / 自检 / 存档 / 快照等 PowerShell 脚本
  开发日志.md             每个版本的决策、踩坑与教训
  交接文档.md             架构、约定、目录结构说明
  v0.2.3/                 本版发布目录（安装包 / 便携包 / 说明 / 源码快照）
```

## 构建

需要 .NET 8 SDK 与 PowerShell：

```powershell
# 只编应用
dotnet build v0.2/src/LedgerBook.App/LedgerBook.App.csproj -c Release

# 跑数据层自检（96 项）
dotnet run --project v0.2/tools/LedgerBook.SelfCheck -c Release -- <自检用的空目录>

# 一条命令出安装包 + 便携包（产物在 v0.2/v<版本>/ 下）
pwsh -File v0.2/工具/打包-自研安装包.ps1
```

## 安装包说明（v0.2.3）

| 包 | 大小 | 用途 |
|---|---|---|
| `…-单文件-完整.exe` | 约 62 MB | 内含 .NET 运行时，任何 Win10/11 都能装（**推荐 / 更新也用它**） |
| `…-单文件-小包.exe` | 约 0.8 MB | 只含程序本体，需机器已装 .NET 8 桌面运行时 |
| `…-内置运行时.zip` | 约 62 MB | 便携版（解压双击即用） |
| `…-精简.zip` | 约 0.4 MB | 便携版（需已装 .NET 8） |

**已装旧版本怎么更新**：退出正在运行的记账本 → 跑**完整包** → 安装路径选成**和原来相同**那一个 →
完成。程序文件被覆盖，**数据与设置（昵称、主题、字号、统计视图等）原样保留**；
安装目录里已有同版本运行时不会被重写。

## 数据放在哪

按优先级：程序目录（可写时）→ `%LOCALAPPDATA%\记账本`（装在 `C:\Program Files` 等受保护目录时）
→ 环境变量 `JIZHANG_ROOT` / `记账本根目录`（可把账本放到网盘等任意位置）。
应用内「设置 → 通用 → 当前数据目录」随时可查。
