using System.IO;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>
/// 「更新内容」的文本来源 + "更新后首次打开才弹"的判断。
///
/// 文本来源（按顺序找，找到就用）：
///   ① 程序目录\更新内容.txt  ② 程序目录\本次修改.md  ③ 数据根目录\更新内容.txt
///   ④ 程序目录上一级\更新内容.txt  ⑤ 内置一句话兜底
/// 约定：**打包前把更新日志（本次修改.md）复制成 更新内容.txt 放进运行目录**
///（打包脚本是整目录复制，所以安装包/便携版都会带上这个文件）。
///
/// 弹窗判断依据：设置文件里的「上次运行版本」。
///   · 没有这个键   → **首次安装** → 不弹（用户要求首次安装不弹）
///   · 有且与当前不同 → **更新后首次打开** → 弹
///   · 有且与当前相同 → 已经看过，不弹
/// 用设置而不是运行日志：日志会被「清理文件」删掉，设置不会（更可靠）。
/// </summary>
public static class WhatsNew
{
    /// <summary>记住"见过哪个版本"的设置键。</summary>
    public const string SeenVersionKey = "上次运行版本";

    /// <summary>更新内容正文。</summary>
    public static string Text()
    {
        foreach (var path in Candidates())
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path).Trim();
                    if (text.Length > 0)
                    {
                        return text;
                    }
                }
            }
            catch (Exception)
            {
                // 读不到就试下一个位置
            }
        }

        return $"记账本 v{Paths.Version}\n\n（没有找到 更新内容.txt —— 打包前把更新日志复制成这个文件放到程序目录即可）";
    }

    /// <summary>正文文件名的候选位置。</summary>
    private static IEnumerable<string> Candidates()
    {
        var baseDir = AppContext.BaseDirectory;
        var names = new[] { "更新内容.txt", "本次修改.md", "更新日志.md" };
        foreach (var name in names)
        {
            yield return Path.Combine(baseDir, name);
        }

        yield return Path.Combine(Paths.Root, "更新内容.txt");

        var parent = Directory.GetParent(baseDir.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        if (!string.IsNullOrEmpty(parent))
        {
            yield return Path.Combine(parent, "更新内容.txt");
            yield return Path.Combine(parent, "本次修改.md");
        }
    }

    /// <summary>记住"见过哪个版本"的状态文件（配置目录下的一行文本）。</summary>
    public static string StateFile => Path.Combine(Paths.ConfigDir, "上次运行版本.txt");

    /// <summary>
    /// 启动时调用一次：判断"这是不是更新后的首次打开"，并把当前版本写进状态文件。
    /// 返回 true 表示调用方应该弹出「更新内容」。
    /// </summary>
    public static bool ConsumeUpdatedVersion(string currentVersion)
    {
        var seen = ReadState();
        var updated = seen.Length > 0 && !string.Equals(seen, currentVersion, StringComparison.Ordinal);

        try
        {
            Directory.CreateDirectory(Paths.ConfigDir);
            File.WriteAllText(StateFile, currentVersion, new System.Text.UTF8Encoding(false));
        }
        catch (Exception exc)
        {
            Log.Info("记录运行版本失败：" + exc.Message);
        }

        if (seen.Length == 0)
        {
            Log.Info($"首次安装（记录版本 {currentVersion}）→ 按约定不弹更新内容");
        }
        else if (updated)
        {
            Log.Info($"更新后首次打开：{seen} → {currentVersion} → 弹更新内容");
        }

        return updated;
    }

    /// <summary>读状态文件（读不到就当作"没见过任何版本"，也就是首次安装）。</summary>
    public static string ReadState()
    {
        try
        {
            return File.Exists(StateFile) ? File.ReadAllText(StateFile).Trim() : "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
