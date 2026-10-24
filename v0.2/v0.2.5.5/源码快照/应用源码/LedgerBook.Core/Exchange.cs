using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LedgerBook.Core;

/// <summary>导入预览里的一个设备名（用于「设备名列表」：折叠显示短名、条数始终完整）。</summary>
public sealed class DeviceEntry
{
    public string Name { get; init; } = "";
    public int Count { get; set; }
    /// <summary>折叠时显示的名字（太长就截断，条数另算，不影响完整显示）。</summary>
    public string ShortName => Name.Length <= 12 ? Name : Name[..11] + "…";
}

/// <summary>导入时对「归属（谁的账）」和「来源（设备）」的处理方式。</summary>
public enum OwnerMode
{
    /// <summary>用文件里自带的归属（不选就用文件里自带的）。</summary>
    Keep,
    /// <summary>统一改成指定的用户码。</summary>
    Replace,
    /// <summary>清空归属。</summary>
    Clear
}

public enum SourceMode
{
    /// <summary>用文件里自带的设备名。</summary>
    Keep,
    /// <summary>清空（导入不保存设备来源）。</summary>
    Clear,
    /// <summary>统一补成本机设备名（导出时「设备为空的补上当前设备」用的就是它）。</summary>
    FillLocal,
    /// <summary>统一记成这份文件的**主设备**（= 导出该文件的设备，见文件里「导出设备」列）。</summary>
    FillMain,
    /// <summary>统一改成手动输入的名字。</summary>
    Replace
}

/// <summary>导入选项。</summary>
public sealed class ImportOptions
{
    public string Strategy { get; set; } = "跳过重复";
    public OwnerMode Owner { get; set; } = OwnerMode.Keep;
    public string OwnerCode { get; set; } = "";
    public SourceMode Source { get; set; } = SourceMode.Clear;
    public string SourceName { get; set; } = "";
    /// <summary>这份文件的**主设备**（文件里「导出设备」列；老文件为空则按出现最多的来源兜底）。</summary>
    public string MainDevice { get; set; } = "";
    /// <summary>导入时如果发现设备名和本机一样，自动清空（你的要求）。</summary>
    public bool ClearOwnDevice { get; set; } = true;
    public string LocalDevice { get; set; } = "";
}

/// <summary>导入预览：解析出的记录 + 认不出的行 + 设备名清单。</summary>
public sealed class ImportPreview
{
    public string Path { get; init; } = "";
    public List<Record> Records { get; } = new();
    public List<RawRow> Problems { get; } = new();
    public List<DeviceEntry> Devices { get; } = new();
    public List<string> Owners { get; } = new();
    /// <summary>这份文件的主设备（「导出设备」列；老文件没有则按出现最多的来源兜底）。</summary>
    public string MainDevice { get; set; } = "";
    public int TotalRows => Records.Count + Problems.Count;
}

/// <summary>导入结果。</summary>
public sealed class ImportResult
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Days { get; } = new();

    /// <summary>真正新增进去的记录（导入完成后要放进「本次已记」列表，所以带上）。</summary>
    public List<Record> AddedRecords { get; } = new();
}

/// <summary>
/// 交换文件（CSV）：导出给别的程序/别的版本看，导入把别人的账合并进来。
///
/// 导出带「编号」列，所以**导出再导入能对得上**（编号相同视为同一笔，按策略覆盖或跳过）。
/// 编码一律 UTF-8 带 BOM + CRLF，Excel 双击不乱码。
/// </summary>
    /// <summary>一条记录与账本里已有数据相比的预判结果。</summary>
    public enum RowVerdict
    {
        /// <summary>账本里没有 → 会导入。</summary>
        New,
        /// <summary>与已录入的是同一条（日期时间、类型、分类、金额、支付方式、备注都对得上）→ 会跳过。</summary>
        Duplicate,
        /// <summary>疑似同一笔（相似但不完全相同，如备注带渠道后缀、时间差几分钟）→ 默认也跳过，可手动改。</summary>
        Similar,
    }

    /// <summary>界面上每行的三态（自动 / 导入锁定 / 不导入锁定）。</summary>
    public enum RowAction
    {
        Auto,
        ForceImport,
        ForceSkip,
    }

    /// <summary>一行记录的导入计划：界面按它渲染，导入时也按它执行（所见即所导）。</summary>
    public sealed class RowPlan
    {
        public Record Record { get; init; } = new();

        public RowVerdict Verdict { get; set; } = RowVerdict.New;

        public RowAction Action { get; set; } = RowAction.Auto;

        /// <summary>是不是"完全同一条"（只有这种才允许被「按编号覆盖」）。</summary>
        public bool Exact { get; set; }

        /// <summary>和账本里哪一条相似（写给人看的一句话，便于核对）。</summary>
        public string Matched { get; set; } = "";

        /// <summary>按当前状态，这一条最终会不会写进账本。</summary>
        public bool WillImport => Action switch
        {
            RowAction.ForceImport => true,
            RowAction.ForceSkip => false,
            // 只有"确实重复"才不导入；"疑似同一笔"（Similar）**默认导入** ——
            // 用户 2026-10-16 反馈：连续几天同额同备注的转账被当成重复全划掉了，不能这样自作主张。
            _ => Verdict != RowVerdict.Duplicate,
        };
    }
public static class Exchange
{
    /// <summary>交换文件的列：日期 + 每日账本的十个字段（带日期才能自描述、可往返）。</summary>
    public static readonly string[] ExportHeader = new[] { Fields.Date }.Concat(Fields.DayHeader).ToArray();

    /// <summary>
    /// 交换文件里额外带的一列：**导出这份文件的设备名（主设备）**。
    /// 老文件没有这一列 → 读出来是空，此时按"出现最多的来源"兜底推断。
    /// </summary>
    public const string MainDeviceColumn = "导出设备";

    public static string FileName(string label) => $"记账本-{label}.csv";

    /// <summary>导出：把命中的记录写成 CSV（含编号列，可往返）。</summary>
    public static (string Path, int Count) Export(QueryFilter filter, string label)
    {
        var result = Query.Run(filter);
        Directory.CreateDirectory(Paths.ExportDir);
        var path = Path.Combine(Paths.ExportDir, FileName(label));

        var builder = new StringBuilder();
        builder.Append(string.Join(',', ExportHeader.Select(Escape))).Append("\r\n");
        foreach (var record in result.Records)
        {
            // 逐行带"这条账自己的单位"：老账（没有单位）导出时补成默认单位，
            // 这样导出的文件用 Excel 打开时每一行都有单位可看（落盘数据不动）。
            builder.Append(string.Join(',', Fields.Pairs(record)
                .Select(pair => Escape(pair.Name == Fields.Unit && pair.Value.Length == 0
                    ? Settings.Load().CurrencySymbol
                    : pair.Value)))).Append("\r\n");
        }

        Table.AtomicWrite(path, builder.ToString(), new UTF8Encoding(true));
        return (path, result.Records.Count);
    }

    /// <summary>
    /// 导出时补设备名：文件里「来源」为空的记录，补上当前设备名。
    /// 同时**每一行都写上「导出设备」列**（= 这份文件的主设备），导入方据此知道"这是哪台机器导出的"。
    /// 只影响导出的这一份文件，不动账本里的原始数据。
    /// </summary>
    public static (string Path, int Count, int Filled) ExportWithDevice(QueryFilter filter, string label,
        SourceMode mode, string deviceName)
    {
        var result = Query.Run(filter);
        Directory.CreateDirectory(Paths.ExportDir);
        var path = Path.Combine(Paths.ExportDir, FileName(label));

        var filled = 0;
        var builder = new StringBuilder();
        builder.Append(string.Join(',', ExportHeader.Append(MainDeviceColumn).Select(Escape))).Append("\r\n");
        foreach (var record in result.Records)
        {
            var source = record.Source;
            if (mode == SourceMode.FillLocal && source.Length == 0)
            {
                source = deviceName;
                filled++;
            }

            // 主设备列：无论来源怎么处理，都写"导出这份文件的设备"
            // 单位列：老账（没有单位）补成默认单位 —— 同理，只影响导出文件，不动落盘数据
            var values = ExportHeader
                .Select(name => name == Fields.Source
                    ? source
                    : name == Fields.Unit && Fields.Get(record, name).Length == 0
                        ? Settings.Load().CurrencySymbol
                        : Fields.Get(record, name))
                .Append(deviceName);
            builder.Append(string.Join(',', values.Select(Escape))).Append("\r\n");
        }

        Table.AtomicWrite(path, builder.ToString(), new UTF8Encoding(true));
        return (path, result.Records.Count, filled);
    }

    /// <summary>读一个交换文件（CSV 或 TSV 都认，按首行猜分隔符），生成预览。</summary>
    public static ImportPreview Preview(string path)
    {
        var preview = new ImportPreview { Path = path };
        if (!File.Exists(path))
        {
            throw new LedgerException($"找不到文件：{path}");
        }

        var text = Table.Decode(File.ReadAllBytes(path));
        var delimiter = Table.SniffDelimiter(text);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var header = new List<string>();
        var started = false;
        var lineNumber = 0;
        var devices = new Dictionary<string, int>();
        var owners = new HashSet<string>();

        foreach (var line in lines)
        {
            lineNumber++;
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var cells = Table.SplitLine(line, delimiter).Select(cell => cell.Trim().TrimStart('\uFEFF')).ToList();
            if (!started)
            {
                started = true;
                if (Storage.IsHeader(cells))
                {
                    header = cells;
                    continue;   // 这一行是表头
                }

                header = Fields.DayHeader.ToList();   // 没有表头：按当前字段顺序当数据
            }

            try
            {
                var record = RowToRecord(header, cells, lineNumber);
                preview.Records.Add(record);

                if (record.Source.Length > 0)
                {
                    devices[record.Source] = devices.TryGetValue(record.Source, out var count) ? count + 1 : 1;
                }

                if (record.Owner.Length > 0)
                {
                    owners.Add(record.Owner);
                }

                // 主设备：文件里「导出设备」列（所有行都一样，取第一条非空的）
                if (preview.MainDevice.Length == 0)
                {
                    var index = header.IndexOf(MainDeviceColumn);
                    if (index >= 0 && index < cells.Count)
                    {
                        preview.MainDevice = cells[index];
                    }
                }
            }
            catch (Exception exc)
            {
                preview.Problems.Add(new RawRow(lineNumber, line, cells.ToArray()));
                _ = exc;
            }
        }

        foreach (var (name, count) in devices.OrderByDescending(pair => pair.Value))
        {
            preview.Devices.Add(new DeviceEntry { Name = name, Count = count });
        }

        // 老文件（没有「导出设备」列）兜底：把出现最多的来源当主设备
        if (preview.MainDevice.Length == 0 && preview.Devices.Count > 0)
        {
            preview.MainDevice = preview.Devices[0].Name;
        }

        preview.Owners.AddRange(owners.OrderBy(name => name));
        return preview;
    }

    private static Record RowToRecord(List<string> header, List<string> cells, int lineNumber)
    {
        var record = new Record();
        for (var i = 0; i < header.Count && i < cells.Count; i++)
        {
            Fields.Set(record, header[i], cells[i]);
        }

        // 必须有日期与金额才能算一条记录
        var date = Fields.Get(record, "日期");
        if (date.Length == 0 && record.Id.Length >= 8)
        {
            date = $"{record.Id[..4]}-{record.Id.Substring(4, 2)}-{record.Id.Substring(6, 2)}";
        }

        if (date.Length == 0)
        {
            throw new LedgerException($"第 {lineNumber} 行缺少日期");
        }

        record.Date = Valid.ParseDate(date);
        record.Time = Valid.NormalizeTime(record.Time, record.Date.ToDateTime(TimeOnly.MinValue));
        record.Kind = Valid.ParseKind(record.Kind);
        record.Amount = Valid.NormalizeAmount(record.Amount);
        record.Category = record.Category.Trim();
        if (record.Category.Length == 0)
        {
            throw new LedgerException($"第 {lineNumber} 行缺少分类");
        }

        return record;
    }

    /// <summary>按选项把预览里的记录落到账本里。</summary>
    public static ImportResult Apply(ImportPreview preview, ImportOptions options)
    {
        var result = new ImportResult();
        var days = new Dictionary<DateOnly, DayData>();
        var dirty = new HashSet<DateOnly>();

        foreach (var record in preview.Records)
        {
            try
            {
                ApplyOptions(record, options);

                if (!days.TryGetValue(record.Date, out var data))
                {
                    data = Storage.ReadDay(record.Date);
                    days[record.Date] = data;

                    // 导入会保留来源文件的编号（这样导出/导入能往返）；
                    // 但如果**这一天的账本里已经有同一个编号**（同一个文件导了两次、或两个来源编号撞车），
                    // 就必须换一个新编号 —— 否则一天里出现重复编号，而删除是按编号删的，
                    // 会把"兄弟"一起删掉（用户 2026-10-16 报的 bug）。
                    if (data.Records.Any(item => item.Id == record.Id))
                    {
                        record.Id = Storage.NextId(record.Date, data.Records);
                    }
                }

                var duplicate = FindDuplicate(data.Records, record);
                if (duplicate is not null)
                {
                    switch (options.Strategy)
                    {
                        case "按编号覆盖":
                            duplicate.Kind = record.Kind;
                            duplicate.Category = record.Category;
                            duplicate.Amount = record.Amount;
                            duplicate.Pay = record.Pay;
                            duplicate.Note = record.Note;
                            duplicate.Detail = record.Detail;
                            duplicate.Owner = record.Owner;
                            duplicate.Source = record.Source;
                            result.Updated++;
                            dirty.Add(record.Date);
                            break;
                        case "全部追加":
                            data.Records.Add(record);
                            result.Added++;
                            result.AddedRecords.Add(record);
                            dirty.Add(record.Date);
                            break;
                        default:
                            result.Skipped++;
                            break;
                    }

                    continue;
                }

                data.Records.Add(record);
                result.Added++;
                result.AddedRecords.Add(record);
                dirty.Add(record.Date);
            }
            catch (Exception)
            {
                result.Failed++;
            }
        }

        foreach (var day in dirty.OrderBy(item => item))
        {
            var data = days[day];

            // 外来记录可能没有编号：补上当天不重复的编号
            foreach (var record in data.Records.Where(item => item.Id.Length == 0))
            {
                record.Id = Storage.NextId(day, data.Records);
            }

            Storage.WriteDay(day, data.Records, data.Unparsed);
            result.Days.Add(day.ToString("yyyy-MM-dd"));
        }

        return result;
    }

    /// <summary>按选项变换一条记录（预览和真正导入共用同一套规则，保证「所见即所导」）。</summary>
    /// <summary>
    /// 按"先一步检查"的计划执行导入（界面走的就是这条路径）：
    ///   · 不导入锁定 → 直接跳过；导入锁定 → 一定写入；
    ///   · 自动 → 与已有"完全同一条"按冲突策略处理（跳过/按编号覆盖/全部追加）；"疑似同一笔"则默认跳过。
    /// 判重不看归属/来源/编号，所以同一个文件重复导入、或换了「保存为谁」再导入，都会被正确识别。
    /// </summary>
    public static ImportResult Apply(ImportPreview preview, ImportOptions options, IReadOnlyList<RowPlan> plans)
    {
        var result = new ImportResult();
        var days = new Dictionary<DateOnly, DayData>();
        var dirty = new HashSet<DateOnly>();

        foreach (var plan in plans)
        {
            var record = plan.Record;
            try
            {
                ApplyOptions(record, options);

                if (!days.TryGetValue(record.Date, out var data))
                {
                    data = Storage.ReadDay(record.Date);
                    days[record.Date] = data;
                }

                if (!plan.WillImport)
                {
                    result.Skipped++;
                    continue;
                }

                // 兜底：判定可能是旧的（比如界面上的 plans 是打开窗口时算的）。
                // 只对"自动"行做这件事 —— 用户手动锁了"导入锁定"的行必须照办。
                if (plan.Action == RowAction.Auto && !plan.Exact)
                {
                    var again = data.Records.FirstOrDefault(item =>
                        Fingerprint0(item) == Fingerprint0(record)
                        || (Fingerprint1(item) == Fingerprint1(record) && NoteAlike(NormalizeNote(item.Note), NormalizeNote(record.Note))));
                    if (again != null)
                    {
                        result.Skipped++;
                        continue;
                    }
                }

                // 导入保留来源编号（导出/导入可往返）；但这一天若**已有同一编号**（同一文件导两次、或来源撞车），
                // 必须换新编号 —— 否则一天里出现重复编号，而删除是按编号删的，会连累"兄弟"（用户 2026-10-16 报的 bug）。
                if (data.Records.Any(item => item.Id == record.Id))
                {
                    record.Id = Storage.NextId(record.Date, data.Records);
                }

                if (plan.Verdict == RowVerdict.Duplicate && plan.Exact)
                {
                    var duplicate = FindDuplicate(data.Records, record);
                    if (duplicate is not null)
                    {
                        switch (options.Strategy)
                        {
                            case "按编号覆盖":
                                duplicate.Kind = record.Kind;
                                duplicate.Category = record.Category;
                                duplicate.Amount = record.Amount;
                                duplicate.Pay = record.Pay;
                                duplicate.Note = record.Note;
                                duplicate.Detail = record.Detail;
                                duplicate.Owner = record.Owner;
                                duplicate.Source = record.Source;
                                result.Updated++;
                                dirty.Add(record.Date);
                                continue;
                            case "全部追加":
                                break;   // 落到下面当新增处理
                            default:
                                result.Skipped++;
                                continue;
                        }
                    }
                }

                data.Records.Add(record);
                result.Added++;
                result.AddedRecords.Add(record);
                dirty.Add(record.Date);
            }
            catch (Exception)
            {
                result.Failed++;
            }
        }

        foreach (var day in dirty.OrderBy(item => item))
        {
            var data = days[day];

            foreach (var record in data.Records.Where(item => item.Id.Length == 0))
            {
                record.Id = Storage.NextId(day, data.Records);
            }

            Storage.WriteDay(day, data.Records, data.Unparsed);
            result.Days.Add(day.ToString("yyyy-MM-dd"));
        }

        return result;
    }

    public static void ApplyOptions(Record record, ImportOptions options)
    {
        // 归属（谁的账）
        switch (options.Owner)
        {
            case OwnerMode.Replace:
                record.Owner = options.OwnerCode;
                break;
            case OwnerMode.Clear:
                record.Owner = "";
                break;
        }

        // 来源（设备）
        switch (options.Source)
        {
            case SourceMode.Clear:
                record.Source = "";
                break;
            case SourceMode.FillMain:
                // 统一记成这份文件的「主设备」；老文件没有主设备就退回本机设备名
                record.Source = options.MainDevice.Length > 0 ? options.MainDevice : options.LocalDevice;
                break;
            case SourceMode.FillLocal:
                record.Source = record.Source.Length > 0 ? record.Source : options.LocalDevice;
                break;
            case SourceMode.Replace:
                record.Source = options.SourceName;
                break;
        }

        // 设备名和自己一样 → 自动清空（自己机器记的不用标注）
        // 例外：用户**显式**选了「补上当前设备名 / 统一记为主设备」时以用户选择为准（否则两条规则会互相抵消）
        if (options.ClearOwnDevice && options.Source != SourceMode.FillLocal && options.Source != SourceMode.FillMain
            && options.LocalDevice.Length > 0 && record.Source == options.LocalDevice)
        {
            record.Source = "";
        }
    }

    /// <summary>按选项变换一份预览里的全部记录（给预览表格刷新用，不动原文件）。</summary>
    public static List<Record> Transformed(ImportPreview preview, ImportOptions options)
    {
        var list = new List<Record>();
        foreach (var record in preview.Records)
        {
            var copy = record.Clone();
            ApplyOptions(copy, options);
            list.Add(copy);
        }

        return list;
    }

    /// <summary>重复判定：日期 + 时间 + 类型 + 分类 + 金额 + 支付方式 + 备注（有归属的还算上归属）。</summary>
    // ------------------------------------------------------------------ 先一步检查（导入预判）


    /// <summary>
    /// 备注归一化：判重前先用它抹掉"同一笔账的不同写法"。
    /// 处理：全角转半角、去首尾空白、去掉**尾部括号内容**（如「（储蓄卡5632）」「(数字人民币0088)」）、折叠空格。
    /// </summary>
    public static string NormalizeNote(string note)
    {
        if (string.IsNullOrEmpty(note))
        {
            return "";
        }

        var builder = new System.Text.StringBuilder(note.Length);
        foreach (var ch in note)
        {
            if (ch == (char)0x3000)
            {
                builder.Append(" ");
            }
            else if (ch >= (char)0xFF01 && ch <= (char)0xFF5E)
            {
                builder.Append((char)(ch - 0xFEE0));
            }
            else
            {
                builder.Append(ch);
            }
        }

        var text = builder.ToString().Trim();

        // 去掉尾部的括号内容（可能连续多个）
        while (text.Length > 0 && (text.EndsWith((char)0xFF09) || text.EndsWith((char)0x29)))
        {
            var open = text.LastIndexOfAny(new[] { (char)0xFF08, (char)0x28 });
            if (open < 0)
            {
                break;
            }

            text = text[..open].Trim();
        }

        return string.Join(" ", text.Split(new[] { (char)32 }, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>FP0 同一记录：日期 + 时间 + 类型 + 分类 + 金额 + 支付方式 + 归一化备注（**不含归属/来源/编号**）。</summary>
    public static string Fingerprint0(Record record) => string.Join("\u0001", new[]
    {
        record.Date.ToString("yyyy-MM-dd"), record.Time, record.Kind, record.Category,
        record.Amount, record.Pay, NormalizeNote(record.Note)
    });

    /// <summary>FP1 同一笔交易：日期 + 类型 + 金额 + 支付方式（备注另外比：相等或互相包含才算同一笔）。</summary>
    public static string Fingerprint1(Record record) => string.Join("\u0001", new[]
    {
        record.Date.ToString("yyyy-MM-dd"), record.Kind, record.Amount, record.Pay
    });

    /// <summary>FP2 疑似同一笔：类型 + 金额 + 支付方式 + 归一化备注（日期/时间另外比）。</summary>
    public static string Fingerprint2(Record record) => string.Join("\u0001", new[]
    {
        record.Kind, record.Amount, record.Pay, NormalizeNote(record.Note)
    });

    private static bool NoteAlike(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return left.Length == right.Length;
        }

        if (left == right)
        {
            return true;
        }

        var shortSide = left.Length <= right.Length ? left : right;
        var longSide = left.Length <= right.Length ? right : left;
        return shortSide.Length >= 2 && longSide.Contains(shortSide, StringComparison.Ordinal);
    }

    private static int Minutes(string time)
    {
        var parts = (time ?? "").Split((char)58);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
        {
            return -1;   // 时间未知
        }

        return hour * 60 + minute;
    }

    /// <summary>
    /// 时间上是不是"挨得很近"（用来判断 FP2 命中的两笔到底是不是同一笔）。
    /// 用户 2026-10-16 报过一次误判：连续几天都给同一个人转 ¥15.00、备注一模一样，
    /// 结果被全部当成重复划掉 —— 根因就是这里原来"日期相差 ≤1 天就直接算同一笔"。
    /// 现在的规则：
    ///   · 同一天：两边都有时间 → 相差 ≤2 小时算近；有一边没填时间 → 算近（同天同额同备注，多半是同一笔）；
    ///   · 差一天：两边**都**要有时间，且相差 ≤2 小时才算近（跨零点重复出账的账单常见）；
    ///   · 差两天以上：一律不算。
    /// </summary>
    /// <summary>两笔的"实际相隔分钟数"（跨零点按真正的经过时间算；任一方没填时间返回 -1）。</summary>
    private static int ElapsedMinutes(Record left, Record right)
    {
        var a = Minutes(left.Time);
        var b = Minutes(right.Time);
        if (a < 0 || b < 0)
        {
            return -1;
        }

        var dayDiff = Math.Abs(left.Date.DayNumber - right.Date.DayNumber) * 1440;
        return dayDiff + Math.Abs(a - b);
    }

    /// <summary>
    /// 时间上是不是"挨得很近"（判断 FP2 命中的两笔到底是不是同一笔）。
    /// 用户 2026-10-16 报过一次误判：连续几天都给同一个人转 ¥15.00、备注一模一样，被全部当成重复划掉。
    /// 根因是原来"日期相差 ≤1 天就直接算同一笔"。现在按**实际相隔分钟数**判断：
    ///   · 两边都有时间 → 相隔 ≤ 120 分钟才算近（跨零点按真实经过时间，23:50 与 00:10 只差 20 分钟）；
    ///   · 有一边没填时间 → 只有在**同一天**才算近。
    /// </summary>
    private static bool CloseMatch(Record left, Record right)
    {
        var elapsed = ElapsedMinutes(left, right);
        if (elapsed < 0)
        {
            return left.Date == right.Date;
        }

        if (elapsed <= 120)
        {
            return true;
        }

        // 隔一天但"钟点几乎相同"（账单把同一笔在次日又列了一次）→ 也提示"疑似"，
        // 但**仍然会导入**（Similar 默认导入），不会像以前那样直接划掉。
        var dayGap = Math.Abs(left.Date.DayNumber - right.Date.DayNumber);
        var clockDiff = Math.Abs(Minutes(left.Time) - Minutes(right.Time));
        return dayGap == 1 && (clockDiff <= 10 || clockDiff >= 1430);
    }

    /// <summary>是不是"几乎同一时刻"（≤10 分钟）：这种才允许判为"确实重复"而不导入。</summary>
    private static bool TightMatch(Record left, Record right)
    {
        var elapsed = ElapsedMinutes(left, right);
        if (elapsed < 0)
        {
            return left.Date == right.Date;
        }

        return elapsed <= 10;
    }

    /// <summary>
    /// **先一步检查**：把待导入的记录与账本里已有的记录快速比一遍，逐条给出预判。
    /// 做法：给已有记录建 FP0/FP1/FP2 三张字典 → 逐条查表，整体 O(n)，几万条也是瞬间。
    /// 判重**不看归属/来源/编号**（它们只是元数据）—— 这正是 0.2.3 那次"重复导入"的病根。
    /// </summary>
    public static List<RowPlan> Plan(IReadOnlyList<Record> candidates, IEnumerable<Record> existing)
    {
        var exact = new Dictionary<string, Record>(StringComparer.Ordinal);
        // 同一条在账本里可能有多份（例如 56 笔一模一样的红包）→ 判重必须**按次数**，不能"见过就丢"。
        var exactCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var sameDeal = new Dictionary<string, List<Record>>(StringComparer.Ordinal);
        var alike = new Dictionary<string, List<Record>>(StringComparer.Ordinal);

        void Remember(Record record, bool countAsExisting = true)
        {
            if (countAsExisting)
            {
            var key0 = Fingerprint0(record);
            exact[key0] = record;
            exactCount[key0] = exactCount.TryGetValue(key0, out var seen) ? seen + 1 : 1;
            }

            var key1 = Fingerprint1(record);
            if (!sameDeal.TryGetValue(key1, out var list1))
            {
                list1 = new List<Record>();
                sameDeal[key1] = list1;
            }

            list1.Add(record);

            var key2 = Fingerprint2(record);
            if (!alike.TryGetValue(key2, out var list2))
            {
                list2 = new List<Record>();
                alike[key2] = list2;
            }

            list2.Add(record);
        }

        foreach (var record in existing)
        {
            Remember(record);
        }

        var plans = new List<RowPlan>();
        foreach (var candidate in candidates)
        {
            var plan = new RowPlan { Record = candidate };
            var note = NormalizeNote(candidate.Note);

            var key0 = Fingerprint0(candidate);
            // 只有账本里"还剩下"一条一模一样的，才算重复（按次数扣减 → 56 笔相同红包不会被当成重复丢掉）
            if (exact.TryGetValue(key0, out var same) && exactCount.TryGetValue(key0, out var remaining) && remaining > 0)
            {
                exactCount[key0] = remaining - 1;
                plan.Verdict = RowVerdict.Duplicate;
                plan.Exact = true;
                plan.Matched = $"{same.Date:yyyy-MM-dd} {same.Time} {same.Kind}/{same.Category} {same.Amount}";
            }
            else if (sameDeal.TryGetValue(Fingerprint1(candidate), out var sameDay)
                     && sameDay.FirstOrDefault(item => NoteAlike(NormalizeNote(item.Note), note)) is { } twin && TightMatch(twin, candidate))
            {
                plan.Verdict = RowVerdict.Similar;   // 同一天、同额同支付、备注相近 → 只提示"疑似"，仍然导入
                plan.Matched = $"{twin.Date:yyyy-MM-dd} {twin.Time} {twin.Kind}/{twin.Category} {twin.Amount}（备注写法不同）";
            }
            else if (alike.TryGetValue(Fingerprint2(candidate), out var alikeList)
                     && alikeList.FirstOrDefault(item => CloseMatch(item, candidate)) is { } near)
            {
                plan.Verdict = RowVerdict.Similar;
                plan.Matched = $"{near.Date:yyyy-MM-dd} {near.Time} {near.Kind}/{near.Category} {near.Amount}（疑似同一笔；默认仍会导入，不想导就点「不导入锁定」）";
            }

            plans.Add(plan);

            // 同一批文件内部也要去重：把"会导入"的那条也记进索引
            if (plan.WillImport)
            {
                Remember(candidate, countAsExisting: false);   // 同一批内部只做"疑似"提示，不算账本里已有
            }
        }

        return plans;
    }

    public static Record? FindDuplicate(IEnumerable<Record> existing, Record candidate)
        => existing.FirstOrDefault(item => DedupeKey(item) == DedupeKey(candidate));

    public static string DedupeKey(Record record) => string.Join('\u0001', new[]
    {
        record.Date.ToString("yyyy-MM-dd"), record.Time, record.Kind, record.Category,
        record.Amount, record.Pay, record.Note, record.Owner
    });

    /// <summary>
    /// 修好"粘贴回来丢了换行"的 CSV 文本（用户 2026-10-16 报的问题）。
    /// 某些对话软件 / 聊天窗口会把整段文本压成一行（换行被吃掉），那样导入器就分不出记录了。
    /// 这里只做必要的事：
    ///   ① 统一换行（\r\n / \r → \n）；
    ///   ② 逐行看：一行里出现"日期 , 时间"（或"日期 , ,"时间那一格为空）且不在行首，
    ///      就认为那是下一条记录的开头，在那里补一个换行；正常的多行 CSV 不会被改动；
    ///   ③ 丢掉表头之前的前言文字（对话软件常先说一句"好的，结果如下"）。
    /// </summary>
    public static string RepairPastedText(string? raw)
    {
        var text = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Trim().Length == 0)
        {
            return "";
        }

        var rebuilt = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            // 记录开头必须是「日期 , 时间」或「日期 , ,」（时间为空）才算 ——
            // 这样备注里出现的日期（后面跟的是单位或空）不会被误拆。
            var matches = Regex.Matches(line, @"(?<!^)(?<!\n)(\d{4}-\d{1,2}-\d{1,2}),(?=\d{1,2}:\d{2}|,)");
            if (matches.Count == 0)
            {
                rebuilt.Add(line);
                continue;
            }

            var start = 0;
            foreach (Match match in matches)
            {
                rebuilt.Add(line.Substring(start, match.Index - start));
                start = match.Index;
            }

            rebuilt.Add(line.Substring(start));
        }

        var header = rebuilt.FindIndex(line => line.Contains("日期") && line.Contains(','));
        if (header > 0)
        {
            rebuilt = rebuilt.Skip(header).ToList();
        }

        while (rebuilt.Count > 0 && rebuilt[0].Trim().Length == 0)
        {
            rebuilt.RemoveAt(0);
        }

        while (rebuilt.Count > 0 && rebuilt[^1].Trim().Length == 0)
        {
            rebuilt.RemoveAt(rebuilt.Count - 1);
        }

        return string.Join("\n", rebuilt);
    }

    private static string Escape(string cell)
    {
        // 防"公式注入"：Excel / WPS 打开 CSV 时，以 = + @ 开头的单元格会被当**公式**执行，
        // 但以 - 开头且**能当数字看**的（金额、负数）要原样保留。
        // 做法（业界常规）：在最前面加一个单引号，Excel 会把它当"纯文本"。
        // 这样导出给别人的文件、或自己双击用 Excel 打开，都不会执行备注/分类/单位里的内容。
        var text = cell ?? "";
        if (text.Length > 0)
        {
            var head = text[0];
            var risky = head == '=' || head == '+' || head == '@' || head == '\t' || head == '\r';
            if (!risky && head == '-' && !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            {
                risky = true;
            }

            if (risky)
            {
                text = "'" + text;
            }
        }

        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
        {
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }

    /// <summary>导出目录里最近导出的文件（给界面显示用）。</summary>
    public static List<string> RecentFiles(int max = 5)
    {
        if (!Directory.Exists(Paths.ExportDir))
        {
            return new List<string>();
        }

        return Directory.EnumerateFiles(Paths.ExportDir, "*.csv")
            .OrderByDescending(File.GetLastWriteTime)
            .Take(max)
            .ToList();
    }

    public static string Describe(ImportPreview preview)
        => $"共 {preview.TotalRows} 行：能导入 {preview.Records.Count} 条，"
           + $"认不出 {preview.Problems.Count} 条，涉及 {preview.Devices.Sum(item => item.Count)} 条设备信息";
}
