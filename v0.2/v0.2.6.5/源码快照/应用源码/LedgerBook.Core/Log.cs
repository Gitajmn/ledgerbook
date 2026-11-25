using System.Text;

namespace LedgerBook.Core;

/// <summary>
/// 运行日志：**默认开启**，占空间很小（单文件上限 256 KB，超了滚动成「运行日志.1.log」，最多两份 ≈ 0.5 MB）。
/// 记录"用户操作 + 报错"，用户可以直接把日志发来定位问题。
/// 隐私约定：**不写备注/详细备注的原文**，只写类型、分类、金额这类定位必需的信息。
/// </summary>
public static class Log
{
    /// <summary>单文件上限（字节）：256 KB。</summary>
    public const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();
    private static bool _enabled = true;

    /// <summary>关掉后一行都不写（设置里可关）。</summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static string Directory => Path.Combine(Paths.Root, "日志");

    public static string FilePath => Path.Combine(Directory, "运行日志.log");

    public static string PreviousPath => Path.Combine(Directory, "运行日志.1.log");

    public static void Info(string message) => Write("信息", message);

    public static void Warn(string message) => Write("警告", message);

    public static void Error(string message, Exception? exception = null)
    {
        var text = message;
        if (exception is not null)
        {
            text += Environment.NewLine
                + "      异常：" + exception.GetType().Name + "：" + exception.Message
                + Environment.NewLine
                + "      " + (exception.StackTrace ?? "").Replace(Environment.NewLine, Environment.NewLine + "      ");
        }

        Write("错误", text);
    }

    private static void Write(string level, string message)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                Fallback();

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {message}{Environment.NewLine}";
                File.AppendAllText(FilePath, line, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 日志写不了绝不能影响记账
            }
        }
    }

    /// <summary>超过上限就把当前文件滚成 .1（旧的 .1 丢掉，一共最多两份）。</summary>
    private static void Fallback()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length <= MaxBytes)
        {
            return;
        }

        try
        {
            File.Copy(FilePath, PreviousPath, true);
            File.Delete(FilePath);
        }
        catch (Exception)
        {
            // 滚不动就把当前文件清空，至少不让它无限长大
            try
            {
                File.WriteAllText(FilePath, "");
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>当前日志占用（两份加起来，字节）。</summary>
    public static long SizeBytes()
    {
        long total = 0;
        foreach (var path in new[] { FilePath, PreviousPath })
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    total += info.Length;
                }
            }
            catch (Exception)
            {
            }
        }

        return total;
    }

    /// <summary>取最近 N 行（给"复制日志"用）。</summary>
    public static string Tail(int lines = 200)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return "";
            }

            var all = File.ReadAllLines(FilePath);
            var take = Math.Max(1, Math.Min(lines, all.Length));
            return string.Join(Environment.NewLine, all.Skip(all.Length - take));
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>清空日志（设置里的按钮用）。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            foreach (var path in new[] { FilePath, PreviousPath })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}