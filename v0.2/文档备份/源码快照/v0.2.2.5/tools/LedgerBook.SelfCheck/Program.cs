using System.Text;
using LedgerBook.Core;

namespace LedgerBook.SelfCheck;

/// <summary>
/// 数据层自检：不弹窗、纯逻辑，退出码 0 = 全绿。
/// 用法：自检 [数据根目录]     （不给根目录就用 数据根目录 环境变量或默认位置）
/// 重点验证「和 v0.1.0 读写同一个账本」：格式、编号规则、金额精度、原子写、容错。
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0)
        {
            Paths.OverrideRoot(args[0]);
        }

        Console.WriteLine($"记账本数据层自检  根目录：{Paths.Root}");
        Console.WriteLine();

        CleanTestDays();
        CheckPaths();
        CheckBackupLimit();
        CheckValidation();
        CheckRoundTrip();
        CheckCompatibility();
        CheckQuery();
        CheckSummary();
        CheckJsonlFormat();
        CheckExchange();
        CheckUpgradeDrill();

        Console.WriteLine();
        Console.WriteLine($"通过 {_passed} / {_passed + _failed}");
        if (_failed == 0)
        {
            Console.WriteLine("全部通过 ✅");
            return 0;
        }

        Console.WriteLine("失败项：");
        foreach (var failure in Failures)
        {
            Console.WriteLine("  " + failure);
        }

        return 1;
    }

    private static void Section(string title) => Console.WriteLine($"=== {title} ===");

    private static void Check(string name, bool ok, object? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [通过] {name}");
        }
        else
        {
            _failed++;
            var text = detail is null ? name : $"{name} -> {detail}";
            Failures.Add(text);
            Console.WriteLine($"  [失败] {text}");
        }
    }

    /// <summary>
    /// 自检会往账本里写测试数据，所以每次开跑前先把用到的这几天清掉，
    /// 这样连跑两次结果一致（否则计数会翻倍，看着像失败）。
    /// </summary>
    /// <summary>自动备份占用上限：超了要删最旧的，至少留最新一份。</summary>
    private static void CheckBackupLimit()
    {
        Section("自动备份占用上限");
        var settings = Settings.Load();
        var original = settings.BackupLimitMb;
        var dir = settings.BackupDir;
        try
        {
            Directory.CreateDirectory(dir);
            var now = DateTime.UtcNow;
            for (var i = 0; i < 40; i++)
            {
                var file = Path.Combine(dir, $"测试-上限-{i:D3}.bak");
                File.WriteAllBytes(file, new byte[100 * 1024]);      // 每个 100KB，共约 4MB
                File.SetLastWriteTimeUtc(file, now.AddMinutes(-i));  // 时间递减：i 越大越旧
            }

            settings.BackupLimitMb = 1;                              // 压到 1MB
            settings.Save("备份上限MB", 1);
            var removed = Storage.PruneBackups();
            var usage = Storage.BackupUsage();
            var left = Directory.EnumerateFiles(dir, "测试-上限-*.bak").Count();

            Check("超上限会删最旧的备份", removed > 0, $"删了 {removed} 个");
            Check("清理后占用降到上限附近", usage <= 2L * 1024 * 1024, $"{usage / 1024} KB");
            Check("至少保留最新一份", left >= 1, $"还剩 {left} 个");
            Check("删的是最旧的（最旧那个没了、最新那个还在）",
                !File.Exists(Path.Combine(dir, "测试-上限-039.bak")) && File.Exists(Path.Combine(dir, "测试-上限-000.bak")));
        }
        finally
        {
            settings.BackupLimitMb = original;
            settings.Save("备份上限MB", original);
            foreach (var file in Directory.EnumerateFiles(dir, "测试-上限-*.bak"))
            {
                try { File.Delete(file); } catch (Exception) { }
            }
        }
    }
    private static void CleanTestDays()
    {
        foreach (var day in new[]
                 {
                     new DateOnly(2026, 2, 14), new DateOnly(2026, 3, 1),
                     new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 3),
                     new DateOnly(2026, 5, 6), new DateOnly(2026, 5, 7)
                 })
        {
            foreach (var file in new[] { Paths.DayFile(day), Paths.LegacyDayFile(day) })
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception)
                {
                    // 删不掉就算了，检查项会如实报出来
                }
            }
        }

        foreach (var day in new[] { new DateOnly(2026, 4, 2), new DateOnly(2026, 5, 6) })
        {
            try
            {
                var exported = Path.Combine(Paths.ExportDir, $"月度汇总-{Paths.MonthName(day.Year, day.Month)}.csv");
                if (File.Exists(exported))
                {
                    File.Delete(exported);
                }
            }
            catch (Exception)
            {
                // 同上
            }
        }
    }

    private static void CheckPaths()
    {
        Section("目录与格式");
        Paths.EnsureDirs();
        Check("数据目录按 数据\\年\\月 归档（每天一个 jsonl）",
            Paths.DayFile(new DateOnly(2026, 2, 14)).EndsWith(Path.Combine("数据", "2026", "02", "2026-02-14.jsonl")),
            Paths.DayFile(new DateOnly(2026, 2, 14)));
        Check("老格式路径也认得出来（兼容读取用）",
            Paths.LegacyDayFile(new DateOnly(2026, 2, 14)).EndsWith(Path.Combine("数据", "2026", "02", "2026-02-14.tsv")),
            Paths.LegacyDayFile(new DateOnly(2026, 2, 14)));
        Check("当前字段表：7 列 + 详细备注 + 归属/来源",
            Fields.DayHeader.Length == 10 && Fields.DayHeader[7] == "详细备注"
            && Fields.DayHeader[8] == "归属" && Fields.DayHeader[9] == "来源",
            string.Join(",", Fields.DayHeader));
        Check("老字段表都还在（老文件按名读取靠它）",
            Fields.DayHeaderV1.Length == 7 && Fields.DayHeaderV2.Length == 9);
        Check("数据格式版本 = 3（JSONL）", Paths.DataFormatVersion == 3);
    }

    private static void CheckValidation()
    {
        Section("输入校验");
        Check("编号规则 YYYYMMDD-NNN", Valid.MakeId(new DateOnly(2026, 2, 14), 7) == "20260214-007");
        Check("金额统一两位小数", Valid.NormalizeAmount("18.5") == "18.50" && Valid.NormalizeAmount(" 1,234.567 ") == "1234.57",
            Valid.NormalizeAmount("18.5"));
        Check("金额必须大于 0", Throws(() => Valid.NormalizeAmount("0")));
        Check("时间留空用当前时间", Valid.NormalizeTime("") .Length == 5);
        Check("时间格式错误会被拒绝", Throws(() => Valid.NormalizeTime("25:99")));
        Check("日期格式错误会被拒绝", Throws(() => Valid.ParseDate("2026/2/14")));
        Check("类型只认 支出/收入", Throws(() => Valid.ParseKind("花销")));
        Check("上个月同一天遇到短月收到月末", Valid.MonthBefore(new DateOnly(2026, 3, 31)) == new DateOnly(2026, 2, 28),
            Valid.MonthBefore(new DateOnly(2026, 3, 31)));
    }

    private static void CheckRoundTrip()
    {
        Section("写盘与读回");
        var day = new DateOnly(2026, 2, 14);
        var existing = Storage.ReadDay(day).Records;
        var owner = "uTEST00000000";
        var record = Storage.Append(day, "09:12", Kinds.Expense, "餐饮", "18.5", "微信", "自检早餐", owner, "自检设备");
        Check("新增后编号自动递增", record.Id == Valid.MakeId(day, existing.Count + 1), record.Id);
        Check("金额落盘是两位小数", record.Amount == "18.50", record.Amount);

        var back = Storage.ReadDay(day);
        var found = back.Records.FirstOrDefault(item => item.Id == record.Id);
        Check("能读回刚写的记录", found is not null);
        Check("归属/来源两个预留列写进去也读得回来", found?.Owner == owner && found?.Source == "自检设备",
            $"{found?.Owner}/{found?.Source}");

        var path = Paths.DayFile(day);
        var firstBytes = File.ReadAllBytes(path).Take(3).ToArray();
        Check("新格式是 UTF-8 不带 BOM（JSON 惯例）", !firstBytes.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }),
            string.Join(",", firstBytes));

        var updated = Storage.Update(record.Id, item => item.Note = "自检改过");
        Check("按编号能改记录", updated.Note == "自检改过" && Storage.ReadDay(day).Records.Any(item => item.Note == "自检改过"));
        Check("按编号能删记录", Storage.Delete(record.Id) && Storage.ReadDay(day).Records.All(item => item.Id != record.Id));
    }

    private static void CheckCompatibility()
    {
        Section("与 v0.1.0 / v2 的兼容");
        // 手工造一个「v0.1.0 写的」7 列文件（没有归属/来源列），确认还能读
        var day = new DateOnly(2026, 3, 1);
        var legacy = string.Join("\r\n", new[]
        {
            "编号\t时间\t类型\t分类\t金额\t支付方式\t备注",
            "20260301-001\t08:00\t支出\t交通\t4.00\t支付宝\t老版写的",
            "这一行是手工加的垃圾数据\tx",
            string.Empty
        });
        var legacyPath = Paths.LegacyDayFile(day);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, legacy, new UTF8Encoding(true));

        var data = Storage.ReadDay(day);
        Check("老版 7 列文件照样能读", data.Records.Count == 1 && data.Records[0].Category == "交通", data.Records.Count);
        Check("老文件里的记录归属/来源为空（视为本机）", data.Records[0].Owner.Length == 0);
        Check("认不出来的行保留下来不丢", data.Unparsed.Count == 1, data.Unparsed.Count);

        // 改动这一天的老文件：升级成新格式，老行还在，老文件进备份
        Storage.WriteDay(day, data.Records, data.Unparsed);
        var again = Storage.ReadDay(day);
        Check("重写后升级成新格式（.jsonl）", File.Exists(Paths.DayFile(day)) && !File.Exists(legacyPath));
        Check("升级后记录还是 1 条", again.Records.Count == 1, again.Records.Count);
        Check("升级后垃圾行还在", again.Unparsed.Count == 1, again.Unparsed.Count);
        var backupFolder = Path.Combine(Settings.Load().BackupDir, "2026-03-01", "老格式");
        Check("升级前备份了老文件",
            Directory.Exists(backupFolder) && Directory.EnumerateFiles(backupFolder, "*.tsv").Any(), backupFolder);
    }

    private static void CheckQuery()
    {
        Section("查询与统计");
        var filter = new QueryFilter { Start = new DateOnly(2026, 2, 1), End = new DateOnly(2026, 2, 28) };
        var result = Query.Run(filter);
        Check("合计只算命中记录", result.TotalCount >= 0);
        Check("结余 = 收入 - 支出", result.Balance == result.Income - result.Expense, $"{result.Income}/{result.Expense}");

        var span = Query.ResolvePreset("上月", new DateOnly(2026, 3, 15));
        Check("快捷范围「上月」算得准", span.Start == new DateOnly(2026, 2, 1) && span.End == new DateOnly(2026, 2, 28),
            $"{span.Start}~{span.End}");
        var month = Query.ResolvePreset("本月", new DateOnly(2026, 2, 15));
        Check("快捷范围「本月」算得准", month.Start == new DateOnly(2026, 2, 1) && month.End == new DateOnly(2026, 2, 28),
            $"{month.Start}~{month.End}");
    }

    private static void CheckSummary()
    {
        Section("月度汇总");
        // 造两天数据：一天两笔（一收一支），另一天一笔
        var first = new DateOnly(2026, 4, 2);
        var second = new DateOnly(2026, 4, 3);
        Storage.Append(first, "09:00", Kinds.Expense, "餐饮", "10.00", "现金", "汇总一", "uTEST", "自检");
        Storage.Append(first, "12:00", Kinds.Income, "兼职", "100.00", "现金", "汇总二", "uTEST", "自检");
        Storage.Append(second, "18:00", Kinds.Expense, "购物", "20.00", "微信", "汇总三", "uTEST", "自检");

        var summary = Summary.Build(2026, 4);
        var day2 = summary.Days.First(day => day.Day == first);
        Check("按天汇总：笔数与收支算得对", day2.Count == 2 && day2.Income == 100m && day2.Expense == 10m,
            $"{day2.Count}/{day2.Income}/{day2.Expense}");
        Check("按天汇总：结余 = 收入 - 支出", day2.Balance == 90m, day2.Balance);
        Check("按天汇总：没有记录的日子也在列表里（笔数为 0）",
            summary.Days.Count == DateTime.DaysInMonth(2026, 4) && summary.Days.Any(day => day.Count == 0));
        Check("月度合计等于各天之和",
            summary.Count == 3 && summary.Income == 100m && summary.Expense == 30m,
            $"{summary.Count}/{summary.Income}/{summary.Expense}");
        Check("星期换算正确（2026-04-02 是周四）", day2.Weekday == "周四", day2.Weekday);

        var exported = Summary.ExportCsv(summary);
        Check("导出 CSV 文件存在", File.Exists(exported), exported);
        var text = Table.ReadText(exported);
        Check("CSV 是 UTF-8 带 BOM（Excel 双击不乱码）",
            File.ReadAllBytes(exported).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Check("CSV 里有明细行和合计行", text.Contains("2026-04-02") && text.Contains("合计"), text.Split('\n').Length);
        Check("CSV 只写有记录的天（不是整月 30 行）",
            text.Split('\n').Count(line => line.Contains("2026-04-")) == 2,
            text.Split('\n').Count(line => line.Contains("2026-04-")));
    }

    private static void CheckJsonlFormat()
    {
        Section("新存储格式（JSONL：一行一条，字段带名字）");
        var day = new DateOnly(2026, 5, 6);
        var first = Storage.Append(day, "09:00", Kinds.Expense, "餐饮", "18.50", "微信", "早餐", "uTEST", "");
        Storage.Append(day, "12:30", Kinds.Income, "兼职", "300.00", "支付宝", "半天", "uTEST", "");

        var jsonl = Paths.DayFile(day);
        Check("新数据写在 .jsonl 文件里", File.Exists(jsonl) && !File.Exists(Paths.LegacyDayFile(day)), jsonl);
        var lines = File.ReadAllLines(jsonl).Where(line => line.Trim().Length > 0).ToList();
        Check("一行一条记录", lines.Count == 2, lines.Count);
        Check("每行都带字段名（不是靠位置）",
            lines[0].Contains("\"编号\"") && lines[0].Contains("\"金额\"") && lines[0].Contains("\"分类\""),
            lines[0]);
        Check("写的是 UTF-8 不带 BOM（JSON 惯例）",
            !File.ReadAllBytes(jsonl).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Check("空字段不写进文件（省体积，读回来仍是空）",
            !lines[0].Contains("\"来源\"") && !lines[0].Contains("\"详细备注\""), lines[0]);

        // 详细备注能存能取
        Storage.Update(first.Id, target => target.Detail = "和同事一起，AA");
        var back = Storage.ReadDay(day).Records.First(record => record.Id == first.Id);
        Check("详细备注能存能取", back.Detail == "和同事一起，AA", back.Detail);

        // 未知字段：新版本写的字段，老版本读到也不该丢
        File.AppendAllText(jsonl, "{\"编号\":\"" + Valid.MakeId(day, 99) + "\",\"时间\":\"20:00\",\"类型\":\"支出\",\"分类\":\"测试\",\"金额\":\"1.00\",\"未来字段\":\"别丢我\"}\n");
        var withFuture = Storage.ReadDay(day);
        Check("不认识的字段能读进来（不报错）", withFuture.Records.Count == 3, withFuture.Records.Count);
        Check("不认识的字段被记住", withFuture.Records.Any(record => record.ExtraColumns.ContainsKey("未来字段")), "未来字段");
        Storage.WriteDay(day, withFuture.Records, withFuture.Unparsed);
        var rewritten = File.ReadAllText(jsonl);
        Check("改写后不认识的字段还在（没有被洗掉）", rewritten.Contains("未来字段"), "未来字段");

        // 坏行：认得出来的是记录，认不出来的原样保留
        File.AppendAllText(jsonl, "这一行是手工加的垃圾数据\n");
        var withJunk = Storage.ReadDay(day);
        Check("认不出的行被单独收起来（不丢）", withJunk.Unparsed.Count == 1, withJunk.Unparsed.Count);
        Storage.WriteDay(day, withJunk.Records, withJunk.Unparsed);
        Check("改写后垃圾行还在原文件里", File.ReadAllText(jsonl).Contains("这一行是手工加的垃圾数据"));

        // 老 TSV 自动升级
        var oldDay = new DateOnly(2026, 5, 7);
        var legacyPath = Paths.LegacyDayFile(oldDay);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath,
            "编号\t时间\t类型\t分类\t金额\t支付方式\t备注\r\n"
            + "20260507-001\t08:00\t支出\t交通\t4.00\t现金\t老文件\r\n",
            new UTF8Encoding(true));
        var legacyRead = Storage.ReadDay(oldDay);
        Check("老 TSV 仍然能读（v1 的 7 列）",
            legacyRead.Records.Count == 1 && legacyRead.Records[0].Note == "老文件", legacyRead.Records.Count);
        Check("老数据缺的字段读成空（详细备注/归属/来源）",
            legacyRead.Records[0].Detail.Length == 0 && legacyRead.Records[0].Owner.Length == 0
            && legacyRead.Records[0].Source.Length == 0);
        Check("这一天被识别为老格式", Store.IsLegacy(oldDay));

        Storage.Append(oldDay, "09:30", Kinds.Expense, "餐饮", "12.00", "微信", "升级后新记的", "uTEST", "");
        Check("改过之后自动升级成 .jsonl", File.Exists(Paths.DayFile(oldDay)), Paths.DayFile(oldDay));
        Check("升级后老 .tsv 已删除", !File.Exists(legacyPath));
        var afterUpgrade = Storage.ReadDay(oldDay);
        Check("升级不丢记录（老 1 条 + 新 1 条）", afterUpgrade.Records.Count == 2, afterUpgrade.Records.Count);
        var backupFolder = Path.Combine(Settings.Load().BackupDir, "2026-05-07", "老格式");
        Check("升级前把老文件备份了",
            Directory.Exists(backupFolder) && Directory.EnumerateFiles(backupFolder, "*.tsv").Any(),
            backupFolder);

        // 同一个月里新旧混用也要都能枚举到
        var days = Storage.DaysInMonth(2026, 5);
        Check("同月新旧格式混用都能枚举到", days.Contains(new DateOnly(2026, 5, 6)) && days.Contains(new DateOnly(2026, 5, 7)),
            string.Join(",", days.Select(item => item.ToString("MM-dd"))));

        var formatFile = Paths.FormatFile;
        Check("配置里有格式说明文件", File.Exists(formatFile), formatFile);
        Check("格式版本号是 3", Paths.DataFormatVersion == 3, Paths.DataFormatVersion);
    }

    private static void CheckExchange()
    {
        Section("导入导出（CSV 交换文件）");
        var day = new DateOnly(2026, 6, 3);
        Storage.Append(day, "10:00", Kinds.Expense, "餐饮", "30.00", "微信", "导出测试", "uAAA11111111", "");
        Storage.Append(day, "18:00", Kinds.Income, "兼职", "200.00", "支付宝", "导出测试2", "uBBB22222222", "旧电脑");

        var filter = new QueryFilter { Start = day, End = day, Limit = 0 };
        var (path, count) = Exchange.Export(filter, "自检");
        Check("导出文件已生成", File.Exists(path) && count == 2, $"{path} / {count}");
        var text = File.ReadAllText(path);
        Check("导出带编号列（可往返）", text.Contains("编号") && text.Contains("20260603-"), text.Split('\n')[0].Trim());
        Check("导出包含详细备注列", text.Contains("详细备注"));
        Check("导出是 UTF-8 带 BOM", File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));

        var preview = Exchange.Preview(path);
        Check("能读回导出文件（2 条）", preview.Records.Count == 2, preview.Records.Count);
        Check("设备名清单：只有带设备名的那条", preview.Devices.Count == 1 && preview.Devices[0].Name == "旧电脑"
            && preview.Devices[0].Count == 1, string.Join(",", preview.Devices.Select(item => $"{item.Name}×{item.Count}")));
        Check("折叠短名不超过 12 字", preview.Devices[0].ShortName.Length <= 12, preview.Devices[0].ShortName);
        Check("归属清单收集到两个用户码", preview.Owners.Count == 2, string.Join(",", preview.Owners));

        // 手工造一份「别人给的」CSV（带日期列），每个场景用独立日期，贴近真实的导入用法
        var incoming = WriteCsv("incoming.csv", "2026-06-10", new[]
        {
            ("2026-06-10", "10:00", "支出", "餐饮", "30.00", "微信", "别人给的1", "", "uFRIEND00001", "朋友的电脑"),
            ("2026-06-10", "18:00", "收入", "兼职", "200.00", "支付宝", "别人给的2", "详细说明", "uFRIEND00001", "朋友的电脑"),
        });
        var preview2 = Exchange.Preview(incoming);
        Check("别人的 CSV 能读（2 条）", preview2.Records.Count == 2, preview2.Records.Count);
        Check("日期列能读出来", preview2.Records[0].Date == new DateOnly(2026, 6, 10),
            preview2.Records[0].Date.ToString("yyyy-MM-dd"));

        var skip = Exchange.Apply(preview2, new ImportOptions { Strategy = "跳过重复", Source = SourceMode.Keep });
        Check("新记录能导入（2 条）", skip.Added == 2, $"{skip.Added}/{skip.Skipped}");

        var duplicate = Exchange.Apply(Exchange.Preview(incoming), new ImportOptions { Strategy = "跳过重复" });
        Check("重复导入按「跳过重复」处理", duplicate.Skipped == 2 && duplicate.Added == 0,
            $"{duplicate.Added}/{duplicate.Skipped}");

        var overwrite = Exchange.Apply(Exchange.Preview(incoming), new ImportOptions { Strategy = "按编号覆盖" });
        Check("「按编号覆盖」会更新而不是新增", overwrite.Updated == 2 && overwrite.Added == 0,
            $"{overwrite.Added}/{overwrite.Updated}");

        // 归属 / 来源选项（换一天，避免和上面重复）
        var incoming2 = WriteCsv("incoming2.csv", "2026-06-11", new[]
        {
            ("2026-06-11", "10:00", "支出", "餐饮", "30.00", "微信", "别人的账1", "", "uFRIEND00001", "朋友的电脑"),
            ("2026-06-11", "18:00", "收入", "兼职", "200.00", "支付宝", "别人的账2", "", "uFRIEND00001", "朋友的电脑"),
        });
        var preview2b = Exchange.Preview(incoming2);

        var result = Exchange.Apply(preview2b, new ImportOptions
        {
            Strategy = "跳过重复",
            Owner = OwnerMode.Replace,
            OwnerCode = "uLOCAL000001",
            Source = SourceMode.Clear,
        });
        var imported = Storage.ReadDay(new DateOnly(2026, 6, 11)).Records;
        Check("导入可以统一改成指定用户码",
            result.Added == 2 && imported.All(record => record.Owner == "uLOCAL000001"),
            string.Join(",", imported.Select(record => record.Owner)));
        Check("选择「不记设备来源」后设备名为空", imported.All(record => record.Source.Length == 0),
            string.Join(",", imported.Select(record => record.Source)));

        var incoming3 = WriteCsv("incoming3.csv", "2026-06-12", new[]
        {
            ("2026-06-12", "10:00", "支出", "餐饮", "30.00", "微信", "空设备", "", "uFRIEND00001", ""),
            ("2026-06-12", "18:00", "收入", "兼职", "200.00", "支付宝", "有设备", "", "uFRIEND00001", "朋友的电脑"),
        });
        var preview3 = Exchange.Preview(incoming3);
        Exchange.Apply(preview3, new ImportOptions
        {
            Strategy = "跳过重复",
            Source = SourceMode.FillLocal,
            LocalDevice = "我的笔记本",
        });
        var filled = Storage.ReadDay(new DateOnly(2026, 6, 12)).Records;
        Check("「补上当前设备名」只补空的，已有的保留",
            filled.Count(record => record.Source == "我的笔记本") == 1
            && filled.Count(record => record.Source == "朋友的电脑") == 1,
            string.Join(",", filled.Select(record => record.Source)));

        // 设备名和本机一样 → 自动清空
        var incoming4 = WriteCsv("incoming4.csv", "2026-06-13", new[]
        {
            ("2026-06-13", "10:00", "支出", "餐饮", "30.00", "微信", "同设备", "", "uFRIEND00001", "我的笔记本"),
        });
        Exchange.Apply(Exchange.Preview(incoming4), new ImportOptions
        {
            Strategy = "跳过重复",
            Source = SourceMode.Keep,
            ClearOwnDevice = true,
            LocalDevice = "我的笔记本",
        });
        var cleared = Storage.ReadDay(new DateOnly(2026, 6, 13)).Records;
        Check("设备名和本机一样时自动清空", cleared.All(record => record.Source.Length == 0),
            string.Join(",", cleared.Select(record => record.Source)));

        // 导出时补设备名
        var (devicePath, _, deviceFilled) = Exchange.ExportWithDevice(
            new QueryFilter { Start = day, End = day, Limit = 0 }, "补设备", SourceMode.FillLocal, "我的笔记本");
        var deviceText = File.ReadAllText(devicePath);
        Check("导出时给设备为空的记录补上当前设备名", deviceFilled == 1 && deviceText.Contains("我的笔记本"),
            $"{deviceFilled} 条");
        Check("导出补设备名不改账本原始数据",
            Storage.ReadDay(day).Records.Count(record => record.Source.Length == 0) == 1);
    }

    /// <summary>造一份交换文件（带日期列）给导入测试用。</summary>
    private static string WriteCsv(string fileName, string label,
        IEnumerable<(string Date, string Time, string Kind, string Category, string Amount, string Pay, string Note, string Detail, string Owner, string Source)> rows)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(",", Exchange.ExportHeader)).Append("\r\n");
        foreach (var row in rows)
        {
            builder.Append(string.Join(",", new[]
            {
                row.Date, "", row.Time, row.Kind, row.Category, row.Amount, row.Pay, row.Note, row.Detail, row.Owner, row.Source
            })).Append("\r\n");
        }

        var path = Path.Combine(Paths.ExportDir, fileName);
        Directory.CreateDirectory(Paths.ExportDir);
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        _ = label;
        return path;
    }

    /// <summary>演练：一批老 TSV 数据整体升级后，条数/金额/垃圾行都要一模一样。</summary>
    private static void CheckUpgradeDrill()
    {
        Section("演练：老数据整体升级");
        var days = new[] { new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 2), new DateOnly(2026, 7, 3) };
        var index = 1;
        foreach (var day in days)
        {
            var path = Paths.LegacyDayFile(day);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new List<string> { string.Join('\t', Fields.DayHeaderV1) };
            for (var i = 0; i < 3; i++)
            {
                lines.Add($"{day:yyyyMMdd}-{index++:D3}\t{8 + i:D2}:00\t支出\t餐饮\t{i + 1}.50\t微信\t老数据{i}");
            }

            if (day.Day == 2)
            {
                lines.Add("手工加的一行垃圾\tx");   // 升级时不能丢
            }

            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true));
        }

        decimal Total()
        {
            var sum = 0m;
            foreach (var day in days)
            {
                foreach (var record in Storage.ReadDay(day).Records)
                {
                    sum += record.AmountValue;
                }
            }

            return sum;
        }

        var before = Total();
        var countBefore = days.Sum(day => Storage.ReadDay(day).Records.Count);
        var junkBefore = days.Sum(day => Storage.ReadDay(day).Unparsed.Count);

        var (total, upgraded, problems) = Store.UpgradeAll();
        Check("演练：所有老格式的天都被处理", total >= 3 && problems.Count == 0, $"{upgraded}/{total} {string.Join(";", problems)}");
        Check("演练：升级后都是新格式（没有 .tsv 了）",
            days.All(day => File.Exists(Paths.DayFile(day)) && !File.Exists(Paths.LegacyDayFile(day))));
        Check("演练：条数不变", days.Sum(day => Storage.ReadDay(day).Records.Count) == countBefore,
            $"{countBefore} → {days.Sum(day => Storage.ReadDay(day).Records.Count)}");
        Check("演练：金额合计不变", Total() == before, $"{before} → {Total()}");
        Check("演练：垃圾行没丢", days.Sum(day => Storage.ReadDay(day).Unparsed.Count) == junkBefore,
            $"{junkBefore} → {days.Sum(day => Storage.ReadDay(day).Unparsed.Count)}");
        Check("演练：老文件都进备份了",
            days.All(day => Directory.Exists(Path.Combine(Settings.Load().BackupDir, day.ToString("yyyy-MM-dd"), "老格式"))));
        Check("演练：升级后再跑一次不会重复处理", Store.UpgradeAll().Total == 0);
    }

    private static bool Throws(Action action)    {
        try
        {
            action();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
