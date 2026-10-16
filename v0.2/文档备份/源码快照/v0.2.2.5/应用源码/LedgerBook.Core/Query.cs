namespace LedgerBook.Core;

/// <summary>查询条件。</summary>
public sealed class QueryFilter
{
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public string? Kind { get; set; }
    public string? Category { get; set; }
    public string? Keyword { get; set; }
    public string? Owner { get; set; }
    /// <summary>0 = 不限。</summary>
    public int Limit { get; set; } = 500;
}

/// <summary>分类小计的一行。</summary>
public sealed class CategoryTotal
{
    public string Kind { get; init; } = "";
    public string Category { get; init; } = "";
    public int Count { get; set; }
    public decimal Amount { get; set; }
    public double Share { get; set; }
}

public sealed class QueryResult
{
    public List<Record> Records { get; } = new();
    public int TotalCount { get; set; }
    public decimal Income { get; set; }
    public decimal Expense { get; set; }
    public decimal Balance => Income - Expense;
    public List<CategoryTotal> ByCategory { get; } = new();
    public List<string> Errors { get; } = new();
    public bool Truncated { get; set; }
}

/// <summary>查询与统计（合计永远按全部命中记录算，Limit 只影响显示多少行）。</summary>
public static class Query
{
    public static readonly string[] Presets =
    {
        "今天", "昨天", "本周", "上周", "本月", "上月", "近7天", "近30天", "今年", "全部"
    };

    /// <summary>把预设范围换算成起止日期；「全部」返回数据本身的范围。</summary>
    public static (DateOnly Start, DateOnly End) ResolvePreset(string name, DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.Now);
        switch ((name ?? "").Trim())
        {
            case "今天":
                return (day, day);
            case "昨天":
                var yesterday = day.AddDays(-1);
                return (yesterday, yesterday);
            case "本周":
                var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
                return (monday, day);
            case "上周":
                var thisMonday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
                return (thisMonday.AddDays(-7), thisMonday.AddDays(-1));
            case "本月":
                return (new DateOnly(day.Year, day.Month, 1), new DateOnly(day.Year, day.Month, DateTime.DaysInMonth(day.Year, day.Month)));
            case "上月":
                var lastMonth = Valid.MonthBefore(new DateOnly(day.Year, day.Month, 1));
                return (lastMonth, new DateOnly(lastMonth.Year, lastMonth.Month, DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month)));
            case "近7天":
                return (day.AddDays(-6), day);
            case "近30天":
                return (day.AddDays(-29), day);
            case "今年":
                return (new DateOnly(day.Year, 1, 1), new DateOnly(day.Year, 12, 31));
            default:
                return DataSpan(day);
        }
    }

    /// <summary>数据本身的日期范围（没有任何文件时用今天）。</summary>
    public static (DateOnly Start, DateOnly End) DataSpan(DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.Now);
        var first = DateOnly.MaxValue;
        var last = DateOnly.MinValue;
        var dataDir = Paths.DataDir;
        if (Directory.Exists(dataDir))
        {
            foreach (var yearDir in Directory.EnumerateDirectories(dataDir))
            {
                if (!int.TryParse(Path.GetFileName(yearDir), out var year))
                {
                    continue;
                }

                foreach (var monthDir in Directory.EnumerateDirectories(yearDir))
                {
                    if (!int.TryParse(Path.GetFileName(monthDir), out var month) || month is < 1 or > 12)
                    {
                        continue;
                    }

                    foreach (var inMonth in Storage.DaysInMonth(year, month))
                    {
                        if (inMonth < first)
                        {
                            first = inMonth;
                        }

                        if (inMonth > last)
                        {
                            last = inMonth;
                        }
                    }
                }
            }
        }

        if (first == DateOnly.MaxValue)
        {
            return (day, day);
        }

        return (first, last);
    }

    public static QueryResult Run(QueryFilter filter)
    {
        var result = new QueryResult();
        var matched = new List<Record>();
        foreach (var day in Storage.ReadRange(filter.Start, filter.End))
        {
            result.Errors.AddRange(day.Errors);
            foreach (var record in day.Records)
            {
                if (!Matches(record, filter))
                {
                    continue;
                }

                matched.Add(record);
            }
        }

        matched.Sort((a, b) => a.Date == b.Date
            ? Valid.IdSequence(a.Id).CompareTo(Valid.IdSequence(b.Id))
            : a.Date.CompareTo(b.Date));

        result.TotalCount = matched.Count;
        foreach (var record in matched)
        {
            if (record.Kind == Kinds.Income)
            {
                result.Income += record.AmountValue;
            }
            else
            {
                result.Expense += record.AmountValue;
            }
        }

        var limit = filter.Limit <= 0 ? matched.Count : filter.Limit;
        if (matched.Count > limit)
        {
            result.Truncated = true;
        }

        // 明细默认新的在前，看最近几笔更顺眼
        for (var i = matched.Count - 1; i >= 0 && result.Records.Count < limit; i--)
        {
            result.Records.Add(matched[i]);
        }

        BuildCategoryTotals(result, matched);
        return result;
    }

    private static bool Matches(Record record, QueryFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Kind) && filter.Kind != "全部" && record.Kind != filter.Kind)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Category) && filter.Category != "全部" && record.Category != filter.Category)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Owner) && filter.Owner != "全部" && record.Owner != filter.Owner)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            var keyword = filter.Keyword.Trim();
            var hit = record.Note.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                      || record.Category.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                      || record.Pay.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            if (!hit)
            {
                return false;
            }
        }

        return true;
    }

    private static void BuildCategoryTotals(QueryResult result, List<Record> matched)
    {
        var groups = matched
            .GroupBy(record => (record.Kind, record.Category))
            .Select(group => new CategoryTotal
            {
                Kind = group.Key.Kind,
                Category = group.Key.Category,
                Count = group.Count(),
                Amount = group.Sum(record => record.AmountValue)
            })
            .ToList();

        var total = groups.Sum(item => item.Amount);
        foreach (var item in groups)
        {
            item.Share = total > 0 ? (double)(item.Amount / total) : 0;
        }

        // 收入在前（和界面习惯一致），同类内金额从大到小
        result.ByCategory.AddRange(groups
            .OrderBy(item => item.Kind == Kinds.Income ? 0 : 1)
            .ThenByDescending(item => item.Amount));
    }
}
