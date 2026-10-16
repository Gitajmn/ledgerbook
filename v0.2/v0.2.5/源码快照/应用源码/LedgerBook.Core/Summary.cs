using System.Globalization;
using System.Text;

namespace LedgerBook.Core;

/// <summary>月度汇总里的一行（按天）。</summary>
public sealed class DaySummary
{
    public DateOnly Day { get; init; }
    public int Count { get; set; }
    public decimal Income { get; set; }
    public decimal Expense { get; set; }
    public decimal Balance => Income - Expense;
    public string Weekday => Day.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };
}

public sealed class MonthSummary
{
    public int Year { get; init; }
    public int Month { get; init; }
    public List<DaySummary> Days { get; } = new();
    public int Count { get; set; }
    public decimal Income { get; set; }
    public decimal Expense { get; set; }
    public decimal Balance => Income - Expense;
}

/// <summary>月度汇总：按天列出笔数与收支，并可导出 CSV（汇总文件是衍生数据，可随时重算）。</summary>
public static class Summary
{
    /// <summary>
    /// 月度汇总。unit 为空 = 全部单位（含没有单位的老账）；否则只算这个单位。
    /// 老账没有单位 → 按默认单位（设置里的货币符号）看待。
    /// 注意：结余是 Income − Expense 派生的，所以按单位过滤后**结余自动跟着同一个口径**。
    /// </summary>
    public static MonthSummary Build(int year, int month, string? unit = null)
    {
        var summary = new MonthSummary { Year = year, Month = month };
        var byDay = new Dictionary<DateOnly, DaySummary>();
        var last = DateTime.DaysInMonth(year, month);
        var fallback = unit is null ? "" : Settings.Load().CurrencySymbol;

        foreach (var day in Storage.DaysInMonth(year, month))
        {
            foreach (var record in Storage.ReadDay(day).Records)
            {
                if (unit is not null)
                {
                    var recordUnit = record.Unit.Length > 0 ? record.Unit : fallback;
                    if (!string.Equals(recordUnit, unit, StringComparison.Ordinal))
                    {
                        continue;   // 单位不符：不计入笔数/收支 → 结余与图表自然也是同一个口径
                    }
                }

                if (!byDay.TryGetValue(record.Date, out var item))
                {
                    item = new DaySummary { Day = record.Date };
                    byDay[record.Date] = item;
                }

                item.Count++;
                if (record.Kind == Kinds.Income)
                {
                    item.Income += record.AmountValue;
                }
                else
                {
                    item.Expense += record.AmountValue;
                }
            }
        }

        for (var dayNumber = 1; dayNumber <= last; dayNumber++)
        {
            var day = new DateOnly(year, month, dayNumber);
            var item = byDay.TryGetValue(day, out var found) ? found : new DaySummary { Day = day };
            summary.Days.Add(item);
            summary.Count += item.Count;
            summary.Income += item.Income;
            summary.Expense += item.Expense;
        }

        return summary;
    }

    /// <summary>
    /// 导出月度汇总 CSV 到导出目录（UTF-8 带 BOM，Excel 双击不乱码）。
    /// 只写「有记录的天」和最后一行合计，和 v0.1.0 的汇总文件格式一致。
    /// unit 为空 = 这份汇总含全部单位（文件名里会写明）；
    /// 指定 unit 时文件名也带上单位，避免"先导全部、再导某个单位"互相覆盖。
    /// </summary>
    public static string ExportCsv(MonthSummary summary, string symbol = "¥", string? unit = null)
    {
        var builder = new StringBuilder();
        builder.Append($"月度汇总,{Paths.MonthName(summary.Year, summary.Month)},{((unit ?? "").Length > 0 ? unit : "全部单位")}\r\n");
        builder.Append($"日期,星期,笔数,收入,支出,结余\r\n");
        foreach (var day in summary.Days.Where(item => item.Count > 0))
        {
            builder.Append(string.Join(',', new[]
            {
                day.Day.ToString("yyyy-MM-dd"),
                day.Weekday,
                day.Count.ToString(CultureInfo.InvariantCulture),
                day.Income.ToString("0.00", CultureInfo.InvariantCulture),
                day.Expense.ToString("0.00", CultureInfo.InvariantCulture),
                day.Balance.ToString("0.00", CultureInfo.InvariantCulture)
            })).Append("\r\n");
        }

        builder.Append(string.Join(',', new[]
        {
            "合计", "", summary.Count.ToString(CultureInfo.InvariantCulture),
            summary.Income.ToString("0.00", CultureInfo.InvariantCulture),
            summary.Expense.ToString("0.00", CultureInfo.InvariantCulture),
            summary.Balance.ToString("0.00", CultureInfo.InvariantCulture)
        })).Append("\r\n");

        Directory.CreateDirectory(Paths.ExportDir);
        var suffix = (unit ?? "").Length > 0 ? unit : "全部单位";
        var path = Path.Combine(Paths.ExportDir, $"月度汇总-{Paths.MonthName(summary.Year, summary.Month)}（{suffix}）.csv");
        Table.AtomicWrite(path, builder.ToString(), new UTF8Encoding(true));
        return path;
    }
}
