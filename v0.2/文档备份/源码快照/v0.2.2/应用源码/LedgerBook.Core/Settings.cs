using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LedgerBook.Core;

/// <summary>JSON 输出的小工具。</summary>
public static class Json
{
    /// <summary>
    /// 生成带缩进的 JSON，中文保持原样（不转成 \uXXXX），方便用户手改配置文件。
    /// 这里用 Utf8JsonWriter 而不是 JsonSerializer，避免 .NET 8 里
    /// 「JsonSerializerOptions 只读后缺少 TypeInfoResolver」那个坑。
    /// </summary>
    public static string Pretty(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Indented = true,
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
               }))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>生成紧凑的单行 JSON（JSONL 用），中文同样保持原样。</summary>
    public static string Line(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Indented = false,
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
               }))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>账户（v2 预留）：用户码 + 昵称 + 设备名。</summary>
public sealed class Account
{
    public string UserCode { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Device { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public bool Customized { get; set; }
}

/// <summary>界面设置（读的是 v0.1.0 同一个 设置.json，改一个键不影响其它键）。</summary>
public sealed class Settings
{
    public const int SettingsVersion = 2;

    public string CurrencySymbol { get; set; } = "¥";
    public string DefaultKind { get; set; } = Kinds.Expense;
    public string DefaultPay { get; set; } = "微信";
    public bool ClearAfterSave { get; set; } = true;

    /// <summary>记一笔页要不要显示「本次打开后已记」列表（用户要求：功能保留，设置里可关）。</summary>
    public bool ShowSessionList { get; set; } = true;
    public string QueryDefaultRange { get; set; } = "本月";
    public int DetailLimit { get; set; } = 500;
    public List<string> StatsViews { get; set; } = new() { "表格", "饼状图" };

    /// <summary>月度汇总页显示哪些统计视图（和查询统计分开设置，用户要求 2026-10-10）。</summary>
    public List<string> SummaryStatsViews { get; set; } = new() { "饼状图" };
    public List<string> PayMethods { get; set; } = new();
    public string BackupDirSetting { get; set; } = "";
    /// <summary>界面字号（WPF 用设备无关像素，13 ≈ 常规正文）。</summary>
    public int FontSize { get; set; } = 13;
    public string ImportStrategy { get; set; } = "跳过重复";
    public bool AutoBackup { get; set; } = true;

    /// <summary>
    /// 「自动备份占用空间」上限（MB）：超过就自动删**最旧**的备份（至少保留最新一份）。**0 = 不限**。
    /// 依据（2026-10-10 实测）：一条记录约 137~214 字节 → 一天 20 笔约 3KB、一年原始数据约 1~3MB；
    /// 备份是"每次改动留一份当天文件"，所以默认 200MB 对普通使用等于永远够用。
    /// </summary>
    public int BackupLimitMb { get; set; } = 200;
    /// <summary>主题：浅色 / 深色 / 跟随系统。</summary>
    public string Theme { get; set; } = "浅色";

    /// <summary>本机设备名（账户里可改）：导出补设备名、导入判定「自己设备」用。</summary>
    public string DeviceName => Config.LoadAccount().Device;

    /// <summary>实际的备份目录：设置为空时用「程序目录\备份」。</summary>
    public string BackupDir => string.IsNullOrWhiteSpace(BackupDirSetting)
        ? Paths.BackupDir
        : Path.GetFullPath(BackupDirSetting);

    public static Dictionary<string, string> DefaultPayMethods() => new()
    {
        ["微信"] = "微信",
        ["支付宝"] = "支付宝",
        ["现金"] = "现金",
        ["银行卡"] = "银行卡"
    };

    private SettingsDocument? _document;

    private static Settings? _cached;

    /// <summary>清缓存（测试/换根目录后用）。</summary>
    public static void InvalidateCache() => _cached = null;

    public static Settings Load()
    {
        if (_cached is not null)
        {
            return _cached;   // 配置读取很频繁（图例、设备名…），缓存起来避免反复读盘
        }

        var settings = new Settings();
        var doc = SettingsDocument.Load(Paths.SettingsFile);
        settings._document = doc;
        settings.CurrencySymbol = doc.GetString("货币符号", settings.CurrencySymbol);
        settings.DefaultKind = doc.GetString("默认记账类型", settings.DefaultKind);
        settings.DefaultPay = doc.GetString("默认支付方式", settings.DefaultPay);
        settings.ClearAfterSave = doc.GetBool("保存后清空", settings.ClearAfterSave);
        settings.ShowSessionList = doc.GetBool("显示本次已记", settings.ShowSessionList);
        settings.QueryDefaultRange = doc.GetString("查询默认范围", settings.QueryDefaultRange);
        settings.DetailLimit = (int)doc.GetNumber("明细最多显示", settings.DetailLimit);
        settings.ImportStrategy = doc.GetString("导入默认策略", settings.ImportStrategy);
        settings.BackupDirSetting = doc.GetString("备份目录", "");
        settings.FontSize = ParseFontSize(doc.GetString("字号", "标准（13）"), 13);
        settings.AutoBackup = doc.GetBool("自动备份", settings.AutoBackup);
        settings.BackupLimitMb = doc.GetInt("备份上限MB", settings.BackupLimitMb);
        settings.Theme = doc.GetString("主题", settings.Theme);

        var views = doc.GetStringList("分类统计显示");
        if (views.Count > 0)
        {
            settings.StatsViews = views;
        }

            
var summaryViews = doc.GetStringList("月度汇总统计显示");
            
if (summaryViews.Count > 0)
            {
                settings.SummaryStatsViews = summaryViews;
            }

        var pay = doc.GetStringList("支付方式");
        settings.PayMethods = pay.Count > 0 ? pay : DefaultPayMethods().Keys.ToList();
        _cached = settings;
        return settings;
    }

    private static int ParseFontSize(string label, int fallback)
    {
        var digits = new string(label.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var size) ? Math.Clamp(size, 8, 22) : fallback;
    }

    /// <summary>改一个键并落盘（保留文件里其它键，包括 v0.1.0 才认识的键）。</summary>
    public void Save(string key, object? value)
    {
        _document ??= SettingsDocument.Load(Paths.SettingsFile);
        _document.Set(key, value);
        _document.Save(Paths.SettingsFile);
    }
}

/// <summary>设置文件的读写（保留未知键，避免两版互相覆盖）。</summary>
public sealed class SettingsDocument
{
    private JsonObject _root = new();

    public static SettingsDocument Load(string path)
    {
        var doc = new SettingsDocument();
        if (File.Exists(path))
        {
            try
            {
                var node = JsonNode.Parse(Table.ReadText(path));
                if (node is JsonObject obj)
                {
                    doc._root = obj;
                }
            }
            catch (JsonException)
            {
                // 文件坏了就用默认值，不阻塞启动
            }
        }

        if (!doc._root.ContainsKey("版本"))
        {
            doc._root["版本"] = Settings.SettingsVersion;
        }

        return doc;
    }

    public void Save(string path) =>
        Table.AtomicWrite(path, Json.Pretty(_root));

    private JsonNode? Value(string key) => _root.TryGetPropertyValue(key, out var node) ? node : null;

    public string GetString(string key, string fallback)
    {
        var node = Value(key);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (Exception)
        {
            return node.ToJsonString().Trim('"');
        }
    }

    public int GetInt(string key, int fallback)
    {
        var node = Value(key);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    public bool GetBool(string key, bool fallback)
    {
        var node = Value(key);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (Exception)
        {
            var text = GetString(key, "").Trim().ToLowerInvariant();
            return text switch
            {
                "true" or "1" or "是" or "开" or "on" => true,
                "false" or "0" or "否" or "关" or "off" => false,
                _ => fallback
            };
        }
    }

    public decimal GetNumber(string key, decimal fallback)
    {
        var node = Value(key);
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<decimal>();
        }
        catch (Exception)
        {
            return decimal.TryParse(GetString(key, ""), out var parsed) ? parsed : fallback;
        }
    }

    public List<string> GetStringList(string key)
    {
        var node = Value(key);
        if (node is JsonArray array)
        {
            return array.Select(item => item?.ToString() ?? "").Where(text => text.Length > 0).ToList();
        }

        var single = GetString(key, "");
        return single.Length == 0
            ? new List<string>()
            : single.Split(new[] { ',', '，', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public void Set(string key, object? value)
    {
        _root[key] = value switch
        {
            null => null,
            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            IEnumerable<string> list => new JsonArray(list.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            _ => JsonValue.Create(value.ToString())
        };
    }
}

/// <summary>分类与账户（配置目录里的两个 json）。</summary>
public static class Config
{
    public static readonly string[] DefaultExpense =
    {
        "餐饮", "交通", "购物", "居住", "医疗", "娱乐", "通讯", "人情", "其他"
    };

    public static readonly string[] DefaultIncome = { "工资", "奖金", "兼职", "投资收益", "红包", "其他" };

    /// <summary>读分类；文件不存在就返回内置默认并落盘。</summary>
    public static Dictionary<string, List<string>> LoadCategories()
    {
        if (_categoryCache is not null)
        {
            return _categoryCache;
        }

        var path = Paths.CategoriesFile;
        if (File.Exists(path))
        {
            try
            {
                var node = JsonNode.Parse(Table.ReadText(path));
                if (node is JsonObject obj)
                {
                    var result = new Dictionary<string, List<string>>();
                    foreach (var kind in Kinds.All)
                    {
                        if (obj.TryGetPropertyValue(kind, out var listNode) && listNode is JsonArray array)
                        {
                            var items = array.Select(item => item?.ToString() ?? "")
                                .Where(text => text.Trim().Length > 0)
                                .Select(text => text.Trim())
                                .Distinct()
                                .ToList();
                            if (items.Count > 0)
                            {
                                result[kind] = items;
                            }
                        }
                    }

                    if (result.Count == Kinds.All.Length)
                    {
                        _categoryCache = result;
            return result;
                    }
                }
            }
            catch (JsonException)
            {
                // 坏了就用默认
            }
        }

        var defaults = new Dictionary<string, List<string>>
        {
            [Kinds.Expense] = DefaultExpense.ToList(),
            [Kinds.Income] = DefaultIncome.ToList()
        };
        SaveCategories(defaults);
        return defaults;
    }

    public static void SaveCategories(Dictionary<string, List<string>> categories)
    {
        var root = new JsonObject { ["版本"] = 1 };
        foreach (var kind in Kinds.All)
        {
            var array = new JsonArray();
            foreach (var name in categories.TryGetValue(kind, out var list) ? list : new List<string>())
            {
                array.Add(name);
            }

            root[kind] = array;
        }

        Table.AtomicWrite(Paths.CategoriesFile, Json.Pretty(root));
        _categoryCache = null;
    }

    /// <summary>读账户；不存在就生成一个用户码并落盘（首次运行只发生一次）。</summary>
    private static Account? _accountCache;
    private static Dictionary<string, List<string>>? _categoryCache;

    /// <summary>账户读取缓存（设备名/用户码在界面上被频繁取用）。</summary>
    public static void InvalidateAccountCache() => _accountCache = null;

    /// <summary>分类读取缓存。</summary>
    public static void InvalidateCategoryCache() => _categoryCache = null;

    public static Account LoadAccount()
    {
        if (_accountCache is not null)
        {
            return _accountCache;
        }

        var path = Paths.AccountFile;
        if (File.Exists(path))
        {
            try
            {
                var node = JsonNode.Parse(Table.ReadText(path));
                if (node is JsonObject obj)
                {
                    var account = new Account
                    {
                        UserCode = obj["用户码"]?.ToString() ?? "",
                        Nickname = obj["昵称"]?.ToString() ?? "",
                        Device = obj["设备"]?.ToString() ?? "",
                        CreatedAt = obj["创建时间"]?.ToString() ?? "",
                        Customized = obj["是否自定义"] is JsonValue value && value.TryGetValue(out bool flag) && flag
                    };
                    if (account.UserCode.Length > 0)
                    {
                        if (account.Device.Length == 0)
                        {
                            account.Device = DefaultDeviceName();
                        }

                        _accountCache = account;
        return account;
                    }
                }
            }
            catch (JsonException)
            {
                // 坏了就重建
            }
        }

        var created = new Account
        {
            UserCode = NewUserCode(),
            Nickname = "",
            Device = DefaultDeviceName(),
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Customized = false
        };
        SaveAccount(created);
        return created;
    }

    public static void SaveAccount(Account account)
    {
        var root = new JsonObject
        {
            ["格式版本"] = Paths.DataFormatVersion,
            ["用户码"] = account.UserCode,
            ["昵称"] = account.Nickname,
            ["设备"] = account.Device,
            ["创建时间"] = account.CreatedAt,
            ["是否自定义"] = account.Customized
        };
        Table.AtomicWrite(Paths.AccountFile, Json.Pretty(root));
        _accountCache = null;
    }

    public static string DefaultDeviceName() => $"{Environment.MachineName}的电脑";

    /// <summary>
    /// 生成用户码：前缀 u + 12 位 Base32（去掉容易看错的 0/O/1/I）。
    /// 以后还预留 t（测试）与 s（系统）前缀，见设计文档 9.2。
    /// </summary>
    public static string NewUserCode()
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var random = new Random(unchecked(Environment.TickCount * 31 + Guid.NewGuid().GetHashCode()));
        var builder = new System.Text.StringBuilder("u");
        for (var i = 0; i < 12; i++)
        {
            builder.Append(alphabet[random.Next(alphabet.Length)]);
        }

        return builder.ToString();
    }
}
