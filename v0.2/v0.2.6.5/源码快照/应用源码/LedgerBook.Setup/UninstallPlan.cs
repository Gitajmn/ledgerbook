using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace LedgerBook.Setup;

/// <summary>
/// 卸载时"删哪些、留哪些"的纯逻辑（不依赖 WPF / 不弹窗），所以能单独编译、离线跑测试。
///
/// 硬规则（用户 2026-10-09 拍板）：
///   安装目录里属于账本数据的 数据 / 配置 / 导出 / 备份 四个目录【一个都不删】，
///   只删程序文件（根目录下的文件和别的子目录），最后让延迟脚本去删那个空目录。
///   数据在安装目录里时目录不会整个消失 —— 这是刻意的：宁留文件夹，不丢账本。
///
/// 为什么清理脚本是"纯 ASCII + 参数传值"：
///   cmd.exe 是按系统代码页读 .cmd 文件字节的，中文路径写进文件里很容易变乱码（设计文档 3.5 的坑）；
///   而命令行参数是 Unicode 转过来的，中文路径当参数传进去不会乱。脚本正文因此一个中文都不需要。
/// </summary>
public static class UninstallPlan
{
    /// <summary>卸载时保留的目录名（账本数据）。</summary>
    public static readonly string[] KeepDirs = { "数据", "配置", "导出", "备份" };

    /// <summary>安装目录里实际存在的账本数据目录。</summary>
    public static string[] ExistingKeepDirs(string exeDir)
    {
        var list = new List<string>();
        foreach (var name in KeepDirs)
        {
            if (Directory.Exists(Path.Combine(exeDir, name)))
            {
                list.Add(name);
            }
        }

        return list.ToArray();
    }

    /// <summary>盘根、系统目录、用户目录这种地方绝不自动删（装到那儿是用户自己选的，得他自己确认）。</summary>
    public static bool IsUnsafeToDelete(string exeDir)
    {
        try
        {
            var full = Path.GetFullPath(exeDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length <= 3 || Path.GetDirectoryName(full) is null)
            {
                return true;            // "D:\" 之类
            }

            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.DesktopDirectory,
                         Environment.SpecialFolder.MyDocuments,
                         Environment.SpecialFolder.UserProfile,
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86,
                         Environment.SpecialFolder.Windows,
                         Environment.SpecialFolder.System,
                         Environment.SpecialFolder.LocalApplicationData,
                         Environment.SpecialFolder.ApplicationData,
                     })
            {
                var special = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(special) &&
                    string.Equals(full, special.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// 删掉安装目录里的程序文件：根目录下的文件全删（skipFiles 里点名的除外，例如正在运行的卸载器本身），
    /// 子目录除 数据 / 配置 / 导出 / 备份 外整棵删。返回没删掉的项（一般是还被占用的）。
    /// </summary>
    public static List<string> RemoveProgramFiles(string exeDir, IEnumerable<string> skipFiles)
    {
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in skipFiles ?? Array.Empty<string>())
        {
            var name = Path.GetFileName(file);
            if (!string.IsNullOrEmpty(name))
            {
                skipped.Add(name);
            }
        }

        var failed = new List<string>();
        foreach (var file in Directory.GetFiles(exeDir))
        {
            if (skipped.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception)
            {
                failed.Add(file);
            }
        }

        foreach (var dir in Directory.GetDirectories(exeDir))
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (KeepDirs.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception)
            {
                failed.Add(dir);
            }
        }

        return failed;
    }

    /// <summary>
    /// 延迟清理脚本：删掉安装目录里剩下的文件（主要就是卸载器自己的 exe，它被占用时会自动重试）
    /// → 目录空了就删目录 → 自删。
    ///
    /// 两个刻意的设计：
    ///   ① 判断"卸载器退出了没有"用的是 **del 自己的 exe 能不能删掉**（文件锁），不是 tasklist
    ///      —— tasklist 在某些环境枚举进程会被拒（实测报 "ERROR: Access denied"），
    ///      一旦匹配不到就会被误判成"已退出"，反而更不可靠；文件能不能删是骗不了人的。
    ///   ② 只用 **不带 /s 的 rd**（只删空目录）：即使脚本出了任何意外，
    ///      也绝不可能连 数据 / 配置 / 导出 / 备份 一起删掉 —— 数据目录在，rd 就一定失败，数据原地留着。
    /// 纯 ASCII：%1 = 安装目录。
    /// </summary>
    public static string BuildScript()
    {
        var text = new StringBuilder();
        text.Append("@echo off\r\n");
        text.Append("setlocal\r\n");
        text.Append("cd /d \"%SystemRoot%\"\r\n");            // 绝不能把待删目录当自己的 cwd，否则删不掉
        text.Append("set \"T=%~1\"\r\n");
        text.Append("set /a n=0\r\n");
        text.Append(":retry\r\n");
        text.Append("set /a n+=1\r\n");
        text.Append("del /f /q \"%T%\\*\" >nul 2>&1\r\n");
        text.Append("del /f /q /a \"%T%\\*\" >nul 2>&1\r\n");
        text.Append("if exist \"%T%\\*.exe\" if %n% LSS 20 (ping 127.0.0.1 -n 4 >nul & goto retry)\r\n");
        text.Append("rd \"%T%\" >nul 2>&1\r\n");               // 只删空目录：有数据目录在就删不掉，正合期望
        text.Append("del /f /q \"%~f0\" >nul 2>&1\r\n");
        text.Append("exit /b 0\r\n");
        return text.ToString();
    }

    /// <summary>cmd.exe 的参数：/c ""脚本" "安装目录""。外层那对引号是 cmd 对带引号路径的固定要求。</summary>
    public static string CmdArguments(string scriptPath, string exeDir) =>
        "/c \"\"" + scriptPath + "\" \"" + exeDir + "\"\"";

    /// <summary>写脚本文件（正文纯 ASCII，UTF-8 无 BOM 也不会被代码页坑到）。</summary>
    public static string WriteScript(string scriptDir)
    {
        Directory.CreateDirectory(scriptDir);
        var path = Path.Combine(scriptDir, $"卸载-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, BuildScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>把清理脚本跑起来（不等待它结束；它会一直重试到能删掉为止）。</summary>
    public static void StartScript(string scriptPath, string exeDir)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = CmdArguments(scriptPath, exeDir),
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
