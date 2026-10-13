using System.Globalization;
using System.Text;

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
}

/// <summary>
/// 交换文件（CSV）：导出给别的程序/别的版本看，导入把别人的账合并进来。
///
/// 导出带「编号」列，所以**导出再导入能对得上**（编号相同视为同一笔，按策略覆盖或跳过）。
/// 编码一律 UTF-8 带 BOM + CRLF，Excel 双击不乱码。
/// </summary>
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
            builder.Append(string.Join(',', Fields.Pairs(record).Select(pair => Escape(pair.Value)))).Append("\r\n");
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
            var values = ExportHeader
                .Select(name => name == Fields.Source ? source : Fields.Get(record, name))
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
    public static Record? FindDuplicate(IEnumerable<Record> existing, Record candidate)
        => existing.FirstOrDefault(item => DedupeKey(item) == DedupeKey(candidate));

    public static string DedupeKey(Record record) => string.Join('\u0001', new[]
    {
        record.Date.ToString("yyyy-MM-dd"), record.Time, record.Kind, record.Category,
        record.Amount, record.Pay, record.Note, record.Owner
    });

    private static string Escape(string cell)
    {
        if (cell.Contains('"') || cell.Contains(',') || cell.Contains('\n') || cell.Contains('\r'))
        {
            return "\"" + cell.Replace("\"", "\"\"") + "\"";
        }

        return cell;
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
