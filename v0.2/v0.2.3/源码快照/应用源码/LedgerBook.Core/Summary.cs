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
    public static MonthSummary Build(int year, int month)
    {
        var summary = new MonthSummary { Year = year, Month = month };
        var byDay = new Dictionary<DateOnly, DaySummary>();
        var last = DateTime.DaysInMonth(year, month);

        foreach (var day in Storage.DaysInMonth(year, month))
        {
            foreach (var record in Storage.ReadDay(day).Records)
            {
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
    /// </summary>
    public static string ExportCsv(MonthSummary summary, string symbol = "¥")
    {
        var builder = new StringBuilder();
        builder.Append($"月度汇总,{Paths.MonthName(summary.Year, summary.Month)}\r\n");
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
        var path = Path.Combine(Paths.ExportDir, $"月度汇总-{Paths.MonthName(summary.Year, summary.Month)}.csv");
        Table.AtomicWrite(path, builder.ToString(), new UTF8Encoding(true));
        return path;
    }
}
