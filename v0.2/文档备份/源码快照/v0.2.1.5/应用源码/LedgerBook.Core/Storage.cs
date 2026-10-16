namespace LedgerBook.Core;

/// <summary>读到的原始行（认不出来的行也要保住，绝不丢数据）。</summary>
public sealed record RawRow(int LineNumber, string Text, string[] Cells);

/// <summary>一天的数据：能认出来的记录 + 认不出来的原始行。</summary>
public sealed class DayData
{
    public DateOnly Day { get; init; }
    public List<Record> Records { get; } = new();
    public List<RawRow> Unparsed { get; } = new();
    public List<string> Errors { get; } = new();
}

/// <summary>
/// 账本存储：每天一个 tsv 文件，格式与 v0.1.0 完全一致（表头 7 列 + v2 追加的 2 列预留）。
/// 按列名定位字段，所以旧文件（7 列）、手工改过的文件、多出来的列都能正常读。
/// </summary>
public static class Storage
{
    // 读盘缓存：切页/刷新状态栏不必反复解析同一批文件（写盘时按天失效）
    private static readonly Dictionary<DateOnly, (DateTime Stamp, DayData Data)> DayCache = new();

    /// <summary>读一整天（新格式 JSONL 优先，兼容老的 TSV）。缺文件返回空；带内存缓存。</summary>
    public static DayData ReadDay(DateOnly day)
    {
        var path = Paths.ExistingDayFile(day);
        if (path is null)
        {
            return new DayData { Day = day };
        }

        var stamp = File.GetLastWriteTimeUtc(path);
        if (DayCache.TryGetValue(day, out var hit) && hit.Stamp == stamp)
        {
            return hit.Data;
        }

        var data = Store.ReadDay(day);
        DayCache[day] = (stamp, data);
        return data;
    }

    /// <summary>写盘后清缓存（不传 day 就整体清空）。</summary>
    public static void InvalidateCache(DateOnly? day = null)
    {
        if (day is null)
        {
            DayCache.Clear();
            return;
        }

        DayCache.Remove(day.Value);
    }

    /// <summary>读老 TSV 文件（升级时用）。</summary>
    public static DayData ReadLegacy(DateOnly day) => Store.ReadDay(day);

    private static DayData ReadOldTsv(DateOnly day)
    {
        var data = new DayData { Day = day };
        var path = Paths.DayFile(day);
        if (!File.Exists(path))
        {
            return data;
        }

        var (header, rows) = Table.Read(path);
        List<int> indexes;
        var lineNumber = 2;
        if (IsHeader(header))
        {
            indexes = FieldIndexes(header);
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
            for (var i = 0; i < indexes.Count; i++)
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
                data.Records.Add(FromRow(day, cells));
            }
            catch (Exception exc)
            {
                data.Unparsed.Add(new RawRow(lineNumber, string.Join('\t', raw), raw));
                data.Errors.Add($"第 {lineNumber} 行：{exc.Message}");
            }

            lineNumber++;
        }

        data.Records.Sort((a, b) => Valid.IdSequence(a.Id).CompareTo(Valid.IdSequence(b.Id)));
        return data;
    }

    public static bool IsHeader(List<string> row)
    {
        if (row.Count == 0)
        {
            return false;
        }

        if (Valid.TryParseId(row[0].Trim(), out _, out _))
        {
            return false;
        }

        var joined = string.Concat(row);
        return Fields.DayHeaderV1.Any(field => joined.Contains(field, StringComparison.Ordinal));
    }

    public static List<int> FieldIndexes(List<string> header)
    {
        var indexes = new List<int>();
        foreach (var field in Fields.DayHeader)
        {
            var found = -1;
            for (var i = 0; i < header.Count; i++)
            {
                if (header[i] == field)
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                for (var i = 0; i < header.Count; i++)
                {
                    if (header[i].Contains(field, StringComparison.Ordinal))
                    {
                        found = i;
                        break;
                    }
                }
            }

            indexes.Add(found);
        }

        return indexes.All(index => index < 0)
            ? Enumerable.Range(0, Fields.DayHeader.Length).ToList()
            : indexes;
    }

    public static Record FromRow(DateOnly day, string[] cells)
    {
        var record = new Record
        {
            Id = cells[0].Trim(),
            Time = Valid.NormalizeTime(cells[1], day.ToDateTime(TimeOnly.MinValue)),
            Kind = Valid.ParseKind(cells[2]),
            Category = cells[3].Trim(),
            Amount = Valid.NormalizeAmount(cells[4]),
            Pay = cells[5].Trim(),
            Note = cells[6].Trim(),
            Date = day,
        };

        if (cells.Length > 7)
        {
            record.Owner = cells[7].Trim();
        }

        if (cells.Length > 8)
        {
            record.Source = cells[8].Trim();
        }

        if (record.Category.Length == 0)
        {
            throw new LedgerException("分类不能为空");
        }

        if (record.Id.Length > 0 && (!Valid.TryParseId(record.Id, out var idDay, out _) || idDay != day))
        {
            throw new LedgerException($"编号与文件日期不一致：{record.Id}");
        }

        return record;
    }

    public static string[] ToRow(Record record) => new[]
    {
        record.Id, record.Time, record.Kind, record.Category, record.Amount,
        record.Pay, record.Note, record.Owner, record.Source
    };

    /// <summary>下一天的编号：当天最大序号 + 1（不回收编号）。</summary>
    public static string NextId(DateOnly day, IEnumerable<Record> existing)
    {
        var max = 0;
        foreach (var record in existing)
        {
            var seq = Valid.IdSequence(record.Id);
            if (seq > max)
            {
                max = seq;
            }
        }

        return Valid.MakeId(day, max + 1);
    }

    /// <summary>
    /// 重写一整天的文件。新格式直接写 JSONL；
    /// 如果这一天还是老 TSV，会先自动备份再升级（升级后校验，不通过就回退保留老文件）。
    /// </summary>
    public static void WriteDay(DateOnly day, IEnumerable<Record> records, IEnumerable<RawRow>? unparsed = null)
    {
        var list = records.ToList();
        if (Store.IsLegacy(day))
        {
            var legacyData = Store.ReadDay(day);
            foreach (var record in legacyData.Records)
            {
                if (list.All(item => item.Id != record.Id))
                {
                    list.Add(record);   // 老文件里已有的记录不能被丢掉
                }
            }

            var merged = unparsed?.ToList() ?? new List<RawRow>();
            foreach (var row in legacyData.Unparsed)
            {
                // 去重：调用方通常已经读过这一天，别把垃圾行抄成两份
                if (merged.All(item => item.Text != row.Text))
                {
                    merged.Add(row);
                }
            }
            BackupLegacyFile(day, Paths.LegacyDayFile(day));
            Store.WriteDay(day, list, merged);
            Store.VerifyUpgrade(day, list.Count);   // 比对「打算写入的条数」
            return;
        }

        Store.WriteDay(day, list, unparsed);
        InvalidateCache(day);
    }

    /// <summary>追加一条记录（返回写好的记录）。</summary>
    public static Record Append(DateOnly day, string time, string kind, string category, string amount,
        string pay, string note, string owner, string source, string detail = "")
    {
        var data = ReadDay(day);
        var record = new Record
        {
            Id = NextId(day, data.Records),
            Date = day,
            Time = Valid.NormalizeTime(time),
            Kind = Valid.ParseKind(kind),
            Category = category.Trim(),
            Amount = Valid.NormalizeAmount(amount),
            Pay = pay.Trim(),
            Note = note.Trim(),
            Detail = detail.Trim(),
            Owner = owner.Trim(),
            Source = source.Trim(),
        };

        if (record.Category.Length == 0)
        {
            throw new LedgerException("请选择分类");
        }

        MaybeBackup(day);
        if (Store.IsLegacy(day))
        {
            var merged = data.Records.ToList();
            merged.Add(record);
            WriteDay(day, merged, data.Unparsed);   // 会先备份再升级
        }
        else
        {
            Store.AppendRecord(day, record);
            InvalidateCache(day);
        }

        return record;
    }

    /// <summary>改一条记录（按编号定位，改完重写当天文件）。</summary>
    public static Record Update(string id, Action<Record> apply)
    {
        if (!Valid.TryParseId(id, out var day, out _))
        {
            throw new LedgerException($"编号格式应为 20260214-001，收到：{id}");
        }

        var data = ReadDay(day);
        var target = data.Records.FirstOrDefault(record => record.Id == id)
                     ?? throw new LedgerException($"找不到编号 {id} 的记录");
        MaybeBackup(day);
        apply(target);
        WriteDay(day, data.Records, data.Unparsed);
        return target;
    }

    /// <summary>删一条记录。</summary>
    public static bool Delete(string id)
    {
        if (!Valid.TryParseId(id, out var day, out _))
        {
            throw new LedgerException($"编号格式应为 20260214-001，收到：{id}");
        }

        var data = ReadDay(day);
        var removed = data.Records.RemoveAll(record => record.Id == id) > 0;
        if (removed)
        {
            MaybeBackup(day);
            WriteDay(day, data.Records, data.Unparsed);
        }

        return removed;
    }

    /// <summary>某个月里存在的日期（新旧两种格式都算，同一天只算一次）。</summary>
    public static List<DateOnly> DaysInMonth(int year, int month)
    {
        var days = new SortedSet<DateOnly>();
        var dir = Paths.MonthDir(year, month);
        if (!Directory.Exists(dir))
        {
            return new List<DateOnly>();
        }

        foreach (var pattern in new[] { "*.jsonl", "*.tsv" })
        {
            foreach (var file in Directory.EnumerateFiles(dir, pattern))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (DateOnly.TryParseExact(name, "yyyy-MM-dd", out var day) && day.Year == year && day.Month == month)
                {
                    days.Add(day);
                }
            }
        }

        return days.ToList();
    }

    /// <summary>所有还是老格式（只有 .tsv、没有 .jsonl）的天，按时间排好。</summary>
    public static List<DateOnly> LegacyDays()
    {
        var days = new SortedSet<DateOnly>();
        if (!Directory.Exists(Paths.DataDir))
        {
            return new List<DateOnly>();
        }

        foreach (var file in Directory.EnumerateFiles(Paths.DataDir, "*.tsv", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", out var day) && Paths.IsLegacy(day))
            {
                days.Add(day);
            }
        }

        return days.ToList();
    }

    /// <summary>把老 TSV 备份到备份目录（升级前必须做）。</summary>
    public static string? BackupLegacyFile(DateOnly day, string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var folder = Path.Combine(Settings.Load().BackupDir, day.ToString("yyyy-MM-dd"), "老格式");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(path)}");
            File.Copy(path, target, overwrite: true);
            return target;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>读一个日期区间（含首尾）。</summary>
    public static List<DayData> ReadRange(DateOnly start, DateOnly end, int maxDays = 4000)
    {
        var result = new List<DayData>();
        if (end < start)
        {
            return result;
        }

        var cursor = start;
        var guard = 0;
        while (cursor <= end && guard++ < maxDays)
        {
            var data = ReadDay(cursor);
            if (data.Records.Count > 0 || data.Unparsed.Count > 0)
            {
                result.Add(data);
            }

            cursor = cursor.AddDays(1);
        }

        return result;
    }

    /// <summary>写之前备份当天文件（自动备份开关打开时）。失败不影响写入。</summary>
    private static void MaybeBackup(DateOnly day)
    {
        try
        {
            if (Settings.Load().AutoBackup)
            {
                BackupDay(day);
            }
        }
        catch (Exception)
        {
            // 备份失败绝不能挡住记账
        }
    }

    /// <summary>把某一天的账本复制到备份目录（返回备份文件路径）。</summary>
    public static string? BackupDay(DateOnly day, string? stamp = null)
    {
        var source = Paths.DayFile(day);
        if (!File.Exists(source))
        {
            return null;
        }

        try
        {
            var folder = Path.Combine(Settings.Load().BackupDir, day.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"{stamp ?? DateTime.Now.ToString("yyyyMMdd-HHmmss")}-{Path.GetFileName(source)}");
            File.Copy(source, target, overwrite: true);
            return target;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>旧接口：按编号推导日期再备份。</summary>
    public static string? Backup(string id, string stamp)
        => Valid.TryParseId(id, out var day, out _) ? BackupDay(day, stamp) : null;
}
