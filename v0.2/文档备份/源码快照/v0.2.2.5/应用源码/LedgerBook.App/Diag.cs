using System.IO;

namespace LedgerBook.App;

/// <summary>
/// 临时诊断：把执行到哪一步写进日志（排查「界面卡住」这类问题用）。
/// 只有带 --trace 启动时才写，正常使用不产生任何文件。
/// </summary>
public static class Diag
{
    public static bool Enabled { get; set; }

    /// <summary>进程启动时刻（算启动耗时用）。</summary>
    public static long StartTick { get; set; }

    public static void Trace(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            File.AppendAllText(
                Path.Combine(Core.Paths.AppDir, "trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 诊断日志写不了就算了
        }
    }
}
