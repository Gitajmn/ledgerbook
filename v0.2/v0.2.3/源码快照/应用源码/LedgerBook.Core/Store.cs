using System.Text;
using System.Text.Json.Nodes;

namespace LedgerBook.Core;

/// <summary>
/// 账本文件的新老格式读写（v3 = JSONL，v1/v2 = TSV）。
///
/// 三条宽容规则，保证「新版能读老数据、老版也不至于读不懂新数据的已知字段」：
///   1. **按字段名读写**，不按列位置 —— 老数据缺「详细备注」「来源」这些字段就直接留空；
///   2. **不认识的字段原样保留** —— 新版读到以后新增的字段，改写时不会被洗掉；
///   3. 认不出来的整行（手工加的垃圾行、写坏的行）**原样留着**，只报告不删除。
///
/// 写入一律用 JSONL：一行一条记录，坏一行只影响那一条。
/// </summary>
public static class Store
{
    /// <summary>当前格式的文件后缀。</summary>
    public const string Extension = ".jsonl";

    // ------------------------------------------------------------------ 写

    /// <summary>把一条记录编成一行 JSON（空字段不写；未知字段原样保留）。</summary>
    public static string BuildLine(Record record)
    {
        var obj = record.Raw is null ? new JsonObject() : (JsonObject)record.Raw.DeepClone();
        foreach (var name in Fields.DayHeader)
        {
            var value = Fields.Get(record, name);
            if (value.Length == 0)
            {
                obj.Remove(name);
                continue;
            }

            obj[name] = value;
        }

        foreach (var (name, value) in record.ExtraColumns)
        {
            if (!obj.ContainsKey(name))
            {
                obj[name] = value;
            }
        }

        return Json.Line(obj);
    }

    /// <summary>重写一整天的文件（按编号排序，认不出来的行原样保留在后面）。</summary>
    public static void WriteDay(DateOnly day, IEnumerable<Record> records, IEnumerable<RawRow>? unparsed = null)
    {
        var builder = new StringBuilder();
        foreach (var record in records.OrderBy(item => Valid.IdSequence(item.Id)))
        {
            builder.Append(BuildLine(record)).Append('\n');
        }

        if (unparsed is not null)
        {
            foreach (var row in unparsed)
            {
                if (row.Text.Trim().Length > 0)
                {
                    builder.Append(row.Text).Append('\n');
                }
            }
        }

        Table.AtomicWrite(Paths.DayFile(day), builder.ToString(), new UTF8Encoding(false));
    }

    /// <summary>追加一条（不重写整个文件；新格式下就是加一行）。</summary>
    public static void AppendRecord(DateOnly day, Record record)
    {
        var path = Paths.DayFile(day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new UTF8Encoding(false).GetBytes(BuildLine(record) + "\n");
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    // ------------------------------------------------------------------ 读

    /// <summary>读一整天：新格式优先，没有就找老格式；都没有返回空。</summary>
    public static DayData ReadDay(DateOnly day)
    {
        var data = new DayData { Day = day };
        var path = Paths.ExistingDayFile(day);
        if (path is null)
        {
            return data;
        }

        if (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            ReadJsonl(day, path, data);
        }
        else
        {
            ReadLegacyTsv(day, path, data);
        }

        data.Records.Sort((a, b) => Valid.IdSequence(a.Id).CompareTo(Valid.IdSequence(b.Id)));
        return data;
    }

    /// <summary>读 JSONL：一行一条，坏行原样留着。</summary>
    private static void ReadJsonl(DateOnly day, string path, DayData data)
    {
        var text = Table.Decode(File.ReadAllBytes(path));
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(trimmed) is not JsonObject obj)
                {
                    throw new LedgerException("这一行不是一条 JSON 记录");
                }

                data.Records.Add(FromJson(day, obj));
            }
            catch (Exception exc)
            {
                data.Unparsed.Add(new RawRow(lineNumber, trimmed, Array.Empty<string>()));
                data.Errors.Add($"第 {lineNumber} 行：{exc.Message}");
            }
        }
    }

    /// <summary>把一条 JSON 对象变成记录：缺字段留空，多余字段保留。</summary>
    public static Record FromJson(DateOnly day, JsonObject obj)
    {
        var record = new Record { Date = day, Raw = obj };
        foreach (var (name, node) in obj)
        {
            Fields.Set(record, name, NodeText(node));
        }

        Validate(day, record);
        return record;
    }

    private static string NodeText(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.TryGetValue<string>(out var text) => text ?? "",
        JsonValue value when value.TryGetValue<decimal>(out var number) => number.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        _ => node.ToJsonString()
    };

    /// <summary>读老 TSV（v0.1.0 的 7 列、v2 的 9 列都能读）。</summary>
    private static void ReadLegacyTsv(DateOnly day, string path, DayData data)
    {
        var (header, rows) = Table.Read(path);
        List<int> indexes;
        var lineNumber = 2;
        var extraNames = new List<(int Index, string Name)>();
        if (Storage.IsHeader(header))
        {
            indexes = Storage.FieldIndexes(header);
            for (var i = 0; i < header.Count; i++)
            {
                if (!Fields.IsKnown(header[i]))
                {
                    extraNames.Add((i, header[i]));
                }
            }
        }
        else
        {
            // 表头被删掉了：把首行当数据
            indexes = Enumerable.Range(0, Fields.DayHeader.Length).ToList();
            rows.Insert(0, header.ToArray());
            lineNumber = 1;
        }

        foreach (var raw in rows)
        {
            var cells = new string[Fields.DayHeader.Length];
            for (var i = 0; i < indexes.Count && i < cells.Length; i++)
            {
                var index = indexes[i];
                cells[i] = index >= 0 && index < raw.Length ? raw[index] : "";
            }

            if (cells.All(cell => string.IsNullOrWhiteSpace(cell)))
            {
                continue;
            }

            try
            {
                var record = Storage.FromRow(day, cells);
                foreach (var (index, name) in extraNames)
                {
                    if (index < raw.Length && raw[index].Length > 0)
                    {
                        record.ExtraColumns[name] = raw[index];
                    }
                }

                data.Records.Add(record);
            }
            catch (Exception exc)
            {
                data.Unparsed.Add(new RawRow(lineNumber, string.Join('\t', raw), raw));
                data.Errors.Add($"第 {lineNumber} 行：{exc.Message}");
            }

            lineNumber++;
        }
    }

    /// <summary>校验（和 TSV 路径同一套规则）。</summary>
    private static void Validate(DateOnly day, Record record)
    {
        record.Time = Valid.NormalizeTime(record.Time, day.ToDateTime(TimeOnly.MinValue));
        record.Kind = Valid.ParseKind(record.Kind);
        record.Amount = Valid.NormalizeAmount(record.Amount);
        record.Category = record.Category.Trim();
        record.Id = record.Id.Trim();
        if (record.Category.Length == 0)
        {
            throw new LedgerException("分类不能为空");
        }

        if (record.Id.Length > 0 && (!Valid.TryParseId(record.Id, out var idDay, out _) || idDay != day))
        {
            throw new LedgerException($"编号与文件日期不一致：{record.Id}");
        }
    }

    // ------------------------------------------------------------------ 升级

    /// <summary>这一天是不是还是老格式。</summary>
    public static bool IsLegacy(DateOnly day) => Paths.IsLegacy(day);

    /// <summary>
    /// 把一天的老 TSV 升级成 JSONL：**先备份、再写、写后校验、最后才删老文件**。
    /// 任何一步不对就保留老文件并报错，绝不冒险。
    /// </summary>
    public static (bool Ok, string Message, string? Backup) UpgradeDay(DateOnly day)
    {
        if (!IsLegacy(day))
        {
            return (true, "已经是新格式，无需升级", null);
        }

        var legacy = Paths.LegacyDayFile(day);
        var data = ReadDay(day);

        // 1) 备份老文件
        var backup = Storage.BackupLegacyFile(day, legacy);

        // 2) 写新格式
        WriteDay(day, data.Records, data.Unparsed);

        // 3) 校验：新文件必须能把同样多的记录读回来
        var check = ReadDay(day);
        if (check.Records.Count != data.Records.Count)
        {
            try
            {
                File.Delete(Paths.DayFile(day));   // 新文件不可信，删掉，老文件还在
            }
            catch (Exception)
            {
                // 删不掉也不影响老文件
            }

            return (false, $"升级后记录数不一致（{data.Records.Count} → {check.Records.Count}），已放弃升级，老文件保留", backup);
        }

        // 4) 删老文件（备份已存在）
        try
        {
            File.Delete(legacy);
        }
        catch (Exception exc)
        {
            return (true, $"已升级，但老文件删不掉（不影响使用）：{exc.Message}", backup);
        }

        return (true, $"已升级 {data.Records.Count} 条记录", backup);
    }

    /// <summary>
    /// 升级后的校验：新文件必须能把同样多的记录读回来，通过才删老文件；
    /// 不通过就删掉新文件、保留老文件并报错（老文件此时还在原地）。
    /// </summary>
    public static void VerifyUpgrade(DateOnly day, int expectedCount)
    {
        var check = ReadDay(day);
        if (check.Records.Count != expectedCount)
        {
            try
            {
                File.Delete(Paths.DayFile(day));
            }
            catch (Exception)
            {
                // 删不掉也只是多一个文件，不影响老数据
            }

            throw new LedgerException($"升级校验没通过（原来 {expectedCount} 条，新文件读到 {check.Records.Count} 条），已放弃升级、保留老文件");
        }

        try
        {
            var legacy = Paths.LegacyDayFile(day);
            if (File.Exists(legacy))
            {
                File.Delete(legacy);   // 备份已经有了，这里才真正删老文件
            }
        }
        catch (Exception)
        {
            // 老文件删不掉不影响使用（读的时候新格式优先）
        }
    }

    /// <summary>把所有还是老格式的天升级掉（按天逐个来，出错就跳过并记下来）。</summary>
    public static (int Total, int Upgraded, List<string> Problems) UpgradeAll()
    {
        var problems = new List<string>();
        var upgraded = 0;
        var days = Storage.LegacyDays();
        foreach (var day in days)
        {
            var (ok, message, _) = UpgradeDay(day);
            if (ok)
            {
                upgraded++;
            }
            else
            {
                problems.Add($"{day:yyyy-MM-dd}：{message}");
            }
        }

        return (days.Count, upgraded, problems);
    }

    /// <summary>格式说明文件（配置\数据格式.json）：写清当前格式与历史，方便你以后排查。</summary>
    public static void EnsureFormatFile()
    {
        try
        {
            var obj = new JsonObject
            {
                ["当前格式版本"] = Paths.DataFormatVersion,
                ["当前格式"] = Paths.FormatName,
                ["文件位置"] = @"数据\年\月\年-月-日.jsonl",
                ["每行格式"] = "一条记录，字段带名字，例如 {\"编号\":\"20261006-001\",\"时间\":\"09:12\",...}",
                ["读取规则"] = "按字段名读；缺的字段留空；不认识的字段原样保留；认不出的行原样保留",
                ["历史格式"] = new JsonArray
                {
                    new JsonObject { ["版本"] = 1, ["格式"] = "每天一个 TSV，7 列（v0.1.0）", ["文件位置"] = @"数据\年\月\年-月-日.tsv" },
                    new JsonObject { ["版本"] = 2, ["格式"] = "每天一个 TSV，9 列（末尾加 归属/来源）", ["文件位置"] = @"数据\年\月\年-月-日.tsv" },
                    new JsonObject { ["版本"] = 3, ["格式"] = Paths.FormatName, ["文件位置"] = @"数据\年\月\年-月-日.jsonl" }
                },
                ["升级说明"] = "老 TSV 文件仍然能读；某一天被修改时会自动升级成新格式（升级前备份到「备份」目录）。"
                              + "设置里也有「统一升级全部」按钮，不点就一直新旧混用。"
            };
            Table.AtomicWrite(Paths.FormatFile, Json.Pretty(obj));
        }
        catch (Exception)
        {
            // 说明文件写不了不影响记账
        }
    }
}
