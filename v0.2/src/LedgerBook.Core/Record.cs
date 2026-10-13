using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LedgerBook.Core;

/// <summary>记录类型：支出 / 收入。</summary>
public static class Kinds
{
    public const string Expense = "支出";
    public const string Income = "收入";
    public static readonly string[] All = { Expense, Income };

    public static bool IsValid(string? kind) => kind is Expense or Income;
}

/// <summary>校验失败（给界面显示中文原因）。</summary>
public class LedgerException : Exception
{
    public LedgerException(string message) : base(message) { }
}

/// <summary>一笔记录。金额始终是正的两位小数字符串，类型区分收支。</summary>
public sealed class Record
{
    public string Id { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Time { get; set; } = "";
    public string Kind { get; set; } = Kinds.Expense;
    public string Category { get; set; } = "";
    public string Amount { get; set; } = "0.00";
    public string Pay { get; set; } = "";
    /// <summary>短备注（列表里显示的那一句）。</summary>
    public string Note { get; set; } = "";
    /// <summary>详细备注（不占列表位置，点「详情」才看）。</summary>
    public string Detail { get; set; } = "";
    /// <summary>归属：用户码。老数据没有这一项就是空。</summary>
    public string Owner { get; set; } = "";
    /// <summary>来源：设备名。本机记的默认留空，导出时才补。</summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// 读进来的原始 JSON 对象（JSONL 格式才有）。
    /// 作用：**新版本不认识的字段原样保留**，改写时不会把人家新加的字段洗掉。
    /// </summary>
    public JsonObject? Raw { get; set; }

    /// <summary>老 TSV 里多出来的列（同上，保留不丢）。</summary>
    public Dictionary<string, string> ExtraColumns { get; set; } = new();

    public decimal AmountValue => decimal.Parse(Amount, CultureInfo.InvariantCulture);

    public Record Clone() => (Record)MemberwiseClone();
}

/// <summary>
/// 字段表：读写账本、导入导出都按「字段名」来，**不按位置**。
/// 这样老数据缺字段就留空、多出来的字段能原样保留。
/// </summary>
public static class Fields
{
    /// <summary>日期：只在交换文件（CSV 导入导出）里出现，每日账本文件靠文件名表达。</summary>
    public const string Date = "日期";
    public const string Id = "编号";
    public const string Time = "时间";
    public const string Kind = "类型";
    public const string Category = "分类";
    public const string Amount = "金额";
    public const string Pay = "支付方式";
    public const string Note = "备注";
    public const string Detail = "详细备注";
    public const string Owner = "归属";
    public const string Source = "来源";

    /// <summary>当前格式（v3）的字段顺序：v1 的 7 列 + 详细备注 + 归属 + 来源。</summary>
    public static readonly string[] DayHeader =
    {
        Id, Time, Kind, Category, Amount, Pay, Note, Detail, Owner, Source
    };

    /// <summary>v2 的 9 列。</summary>
    public static readonly string[] DayHeaderV2 = { Id, Time, Kind, Category, Amount, Pay, Note, Owner, Source };

    /// <summary>v0.1.0 的 7 列（读老文件时按这些名字找列）。</summary>
    public static readonly string[] DayHeaderV1 = { Id, Time, Kind, Category, Amount, Pay, Note };

    /// <summary>取某个字段的值（不认识的字段返回空）。</summary>
    public static string Get(Record record, string name) => name switch
    {
        Date => record.Date == default ? "" : record.Date.ToString("yyyy-MM-dd"),
        Id => record.Id,
        Time => record.Time,
        Kind => record.Kind,
        Category => record.Category,
        Amount => record.Amount,
        Pay => record.Pay,
        Note => record.Note,
        Detail => record.Detail,
        Owner => record.Owner,
        Source => record.Source,
        _ => record.ExtraColumns.TryGetValue(name, out var extra) ? extra : ""
    };

    /// <summary>写入/导入某个字段（不认识的字段收进 ExtraColumns 留着）。</summary>
    public static void Set(Record record, string name, string value)
    {
        switch (name)
        {
            case Date:
                if (value.Trim().Length > 0)
                {
                    record.Date = Valid.ParseDate(value);
                }

                break;
            case Id: record.Id = value; break;
            case Time: record.Time = value; break;
            case Kind: record.Kind = value; break;
            case Category: record.Category = value; break;
            case Amount: record.Amount = value; break;
            case Pay: record.Pay = value; break;
            case Note: record.Note = value; break;
            case Detail: record.Detail = value; break;
            case Owner: record.Owner = value; break;
            case Source: record.Source = value; break;
            default:
                if (name.Length > 0 && !record.ExtraColumns.ContainsKey(name))
                {
                    record.ExtraColumns[name] = value;
                }

                break;
        }
    }

    /// <summary>是不是当前版本认识的字段。</summary>
    public static bool IsKnown(string name) => name == Date || Array.IndexOf(DayHeader, name) >= 0;

    /// <summary>这一行的字段名 + 值（用于导出 CSV / 老格式写回）。</summary>
    public static IEnumerable<(string Name, string Value)> Pairs(Record record)
    {
        yield return (Date, Get(record, Date));
        foreach (var name in DayHeader)
        {
            yield return (name, Get(record, name));
        }

        foreach (var (name, value) in record.ExtraColumns)
        {
            yield return (name, value);
        }
    }
}


public static class Valid
{
    private static readonly Regex IdPattern = new(@"^(\d{4})(\d{2})(\d{2})-(\d{3,})$", RegexOptions.Compiled);
    private static readonly Regex TimePattern = new(@"^(\d{1,2}):(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex DatePattern = new(@"^(\d{4})-(\d{1,2})-(\d{1,2})$", RegexOptions.Compiled);

    public static string MakeId(DateOnly day, int seq) => $"{day:yyyyMMdd}-{seq:D3}";

    public static bool TryParseId(string? id, out DateOnly day, out int seq)
    {
        day = default;
        seq = 0;
        var match = IdPattern.Match((id ?? "").Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!DateOnly.TryParseExact($"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}",
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
        {
            return false;
        }

        seq = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        return true;
    }

    public static int IdSequence(string id) => TryParseId(id, out _, out var seq) ? seq : 0;

    public static DateOnly ParseDate(string? value)
    {
        var text = (value ?? "").Trim();
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return exact;
        }

        var match = DatePattern.Match(text);
        if (match.Success)
        {
            var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
            {
                return new DateOnly(year, month, day);
            }
        }

        throw new LedgerException($"日期格式应为 2026-02-14，收到：{text}");
    }

    /// <summary>时间统一成 HH:MM；留空则用当前时间。</summary>
    public static string NormalizeTime(string? value, DateTime? fallback = null)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            var moment = fallback ?? DateTime.Now;
            return $"{moment.Hour:D2}:{moment.Minute:D2}";
        }

        var match = TimePattern.Match(text);
        if (!match.Success)
        {
            throw new LedgerException($"时间格式应为 HH:MM，收到：{text}");
        }

        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            throw new LedgerException($"时间超出范围：{text}");
        }

        return $"{hour:D2}:{minute:D2}";
    }

    /// <summary>金额统一成两位小数的正数字符串。</summary>
    public static string NormalizeAmount(string? value)
    {
        var text = (value ?? "").Trim().Replace(",", "").Replace("，", "").Replace("¥", "").Replace("￥", "");
        if (text.Length == 0)
        {
            throw new LedgerException("请输入金额");
        }

        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            throw new LedgerException($"金额只能是数字，收到：{value}");
        }

        if (amount <= 0)
        {
            throw new LedgerException("金额必须大于 0（收入/支出由「类型」决定）");
        }

        return amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    public static string ParseKind(string? value)
    {
        var text = (value ?? "").Trim();
        if (!Kinds.IsValid(text))
        {
            throw new LedgerException($"类型只能是「支出」或「收入」，收到：{text}");
        }

        return text;
    }

    public static string Money(string amount, string symbol = "¥")
    {
        if (!decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            value = 0m;
        }

        return symbol + value.ToString("#,##0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>上个月的「同一天」，遇到短月收到月末。</summary>
    public static DateOnly MonthBefore(DateOnly day, int months = 1)
    {
        var cursor = day;
        for (var i = 0; i < Math.Max(1, months); i++)
        {
            var year = cursor.Year;
            var month = cursor.Month - 1;
            if (month < 1)
            {
                year -= 1;
                month = 12;
            }

            var lastDay = DateTime.DaysInMonth(year, month);
            cursor = new DateOnly(year, month, Math.Min(cursor.Day, lastDay));
        }

        return cursor;
    }
}
