using System.Text;

namespace LedgerBook.Core;

/// <summary>
/// 路径规则（和 v0.1.0 完全一致，两版读写同一个账本）。
///
/// - 数据根目录默认 = 程序所在目录（绿色便携：数据跟着程序走）；
/// - 若程序目录不可写（例如装在 C:\Program Files），退到 %LOCALAPPDATA%\记账本；
/// - 环境变量 JIZHANG_ROOT / 记账本根目录 优先，可把数据放到网盘等任意位置。
/// </summary>
public static class Paths
{
    public const string AppName = "记账本";
    public const string Version = "2.0.0";
    /// <summary>
    /// 数据格式版本：
    ///   1 = 每天一个 TSV，7 列（v0.1.0）
    ///   2 = 每天一个 TSV，9 列（末尾加「归属」「来源」）
    ///   3 = 每天一个 JSONL，一行一条、字段带名字（当前版本；老 TSV 仍可读）
    /// </summary>
    public const int DataFormatVersion = 3;
    public const string FormatName = "每日一个 JSONL 文件（一行一条，字段带名字）";

    public const string RootEnv = "JIZHANG_ROOT";
    public const string RootEnvCn = "记账本根目录";

    private static string? _rootOverride;
    private static string? _resolvedRoot;

    /// <summary>程序所在目录（exe 同级）。</summary>
    public static string AppDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>数据根目录。</summary>
    public static string Root
    {
        get
        {
            if (_rootOverride is not null)
            {
                return _rootOverride;
            }

            var fromEnv = Environment.GetEnvironmentVariable(RootEnv)
                          ?? Environment.GetEnvironmentVariable(RootEnvCn);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return Path.GetFullPath(fromEnv);
            }

            return _resolvedRoot ??= ResolveDefaultRoot();
        }
    }

    /// <summary>测试/命令行可强制指定根目录。</summary>
    public static void OverrideRoot(string? root)
    {
        _rootOverride = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
        _resolvedRoot = null;
    }

    public static string DataDir => Path.Combine(Root, "数据");
    public static string ConfigDir => Path.Combine(Root, "配置");
    public static string ExportDir => Path.Combine(Root, "导出");
    public static string BackupDir => Path.Combine(Root, "备份");

    public static string CategoriesFile => Path.Combine(ConfigDir, "分类.json");
    public static string SettingsFile => Path.Combine(ConfigDir, "设置.json");
    public static string AccountFile => Path.Combine(ConfigDir, "账户.json");
    public static string FormatFile => Path.Combine(ConfigDir, "数据格式.json");

    public static string MonthDir(int year, int month) =>
        Path.Combine(DataDir, year.ToString("D4"), month.ToString("D2"));

    public static string MonthName(int year, int month) => $"{year:D4}-{month:D2}";

    /// <summary>当天账本文件（当前格式：JSONL）。写入一律写这里。</summary>
    public static string DayFile(DateOnly day) =>
        Path.Combine(MonthDir(day.Year, day.Month), $"{day:yyyy-MM-dd}.jsonl");

    /// <summary>老格式的当天文件（v0.1.0 / v2 的 TSV）。只读不写。</summary>
    public static string LegacyDayFile(DateOnly day) =>
        Path.Combine(MonthDir(day.Year, day.Month), $"{day:yyyy-MM-dd}.tsv");

    /// <summary>当天实际存在的文件（优先新格式；都没有则 null）。</summary>
    public static string? ExistingDayFile(DateOnly day)
    {
        var jsonl = DayFile(day);
        if (File.Exists(jsonl))
        {
            return jsonl;
        }

        var tsv = LegacyDayFile(day);
        return File.Exists(tsv) ? tsv : null;
    }

    /// <summary>某天是不是还是老格式（需要升级）。</summary>
    public static bool IsLegacy(DateOnly day) => !File.Exists(DayFile(day)) && File.Exists(LegacyDayFile(day));

    public static string SummaryFile(int year, int month) =>
        Path.Combine(MonthDir(year, month), "_月度汇总.csv");

    public static void EnsureDirs()
    {
        foreach (var dir in new[] { DataDir, ConfigDir, ExportDir, BackupDir })
        {
            Directory.CreateDirectory(dir);
        }

        Store.EnsureFormatFile();   // 配置\数据格式.json：写清当前格式与历史
    }

    /// <summary>程序目录能不能写（决定数据放旁边还是用户目录）。</summary>
    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".写权限检测-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "x", Encoding.UTF8);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ResolveDefaultRoot()
    {
        if (CanWrite(AppDir))
        {
            return AppDir;
        }

        // 装在受保护目录（Program Files 等）时退到用户目录，界面上会显示实际位置
        // 和安装向导里的提示保持一致：受保护目录下装 → 数据放「文档\记账本」
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AppName);
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public static Dictionary<string, string> Describe() => new()
    {
        ["root"] = Root,
        ["data"] = DataDir,
        ["config"] = ConfigDir,
        ["export"] = ExportDir,
        ["backup"] = BackupDir,
    };
}
