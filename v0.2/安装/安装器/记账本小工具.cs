using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

// 记账本小工具（.NET Framework 4.8，Windows 预装，不需要任何运行时下载）—— 一个 exe 两个角色：
//
//   ① 卸载（默认；文件名以「卸载」开头或带 --uninstall）：
//      确认 → 结束主程序 → 删程序文件（**保留 数据/配置/导出/备份**）→ 删快捷方式 → 尽力删注册表项
//      → 写一个"延迟清理脚本"（放安装目录外面）收拾自己那个 exe 和空目录 → 退出。
//      为什么要自带卸载器：Inno 的 Uninstallable 会往 HKCU\...\Uninstall 写卸载项，
//      而**本机实测这个分支被安全软件/策略挡住**（连 Inno 自己的安装器都报"拒绝访问"、退出码 4），
//      所以安装包把 Inno 的卸载器关掉，用这个不依赖注册表、按目录规则删的小工具。
//
//   ② 安装后置（--post-install）：由安装包在装完后调用，写 安装信息.txt，可选配置签名证书。
//      证书之前写在向导里、失败信息是一坨 CLIXML 垃圾，这里顺手修干净（只显示一行原因）。
//
// 注意：csc.exe（C# 5）编译 → 不能用 => 表达式体、$"" 插值、?.、nameof、out var。
internal static class Program
{
    private const string AppName = "记账本";
    // 版本号源头：v0.2\Directory.Build.props（打包脚本据此生成 BuildInfo）
    private const string AppVersion = BuildInfo.AppVersion;
    private const string AppExeName = "记账本.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\记账本";

    /// <summary>卸载时保留的目录名（账本数据，一个都不删）。</summary>
    private static readonly string[] KeepDirs = { "数据", "配置", "导出", "备份" };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string self = Process.GetCurrentProcess().MainModule.FileName;
            string selfDir = Path.GetDirectoryName(self);
            bool silent = HasFlag(args, "--silent");
            bool elevated = HasFlag(args, "--elevated");
            string target = Value(args, "--target");
            if (string.IsNullOrEmpty(target)) target = selfDir;

            if (HasFlag(args, "--post-install"))
            {
                return PostInstall(target, Value(args, "--payload"), HasFlag(args, "--trust"));
            }

            // 文件名以「卸载」开头也算卸载（双击就走这里）
            bool byName = Path.GetFileNameWithoutExtension(self).StartsWith("卸载", StringComparison.Ordinal);
            if (byName || HasFlag(args, "--uninstall"))
            {
                return Uninstall(target, self, silent, elevated);
            }

            MessageBox.Show("这个程序由记账本安装包使用：\n· 双击它就是卸载记账本\n· 安装包会用它写安装信息",
                AppName + " 小工具", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            try { MessageBox.Show(ex.Message, AppName + " 出错", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch (Exception) { }
            return 1;
        }
    }

    // ---------------------------------------------------------------- 卸载

    private static int Uninstall(string target, string self, bool silent, bool elevated)
    {
        if (!Directory.Exists(target))
        {
            if (!silent) MessageBox.Show("找不到安装目录：" + target, AppName + " 卸载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        if (Unsafe(target))
        {
            if (!silent)
            {
                MessageBox.Show("为安全起见没有自动删除这个目录（它看起来是盘根或系统/用户目录）：\n" + target,
                    AppName + " 卸载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return 1;
        }

        if (!silent)
        {
            var kept = ExistingKeepDirs(target);
            var answer = MessageBox.Show(
                "确定卸载「" + AppName + "」吗？\n\n安装位置：" + target + "\n"
                + (kept.Length > 0
                    ? "账本数据不会删除，会留在安装目录里（" + string.Join(" / ", kept) + "）。"
                    : "这里没有账本数据。"),
                "卸载" + AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return 0;
        }

        // 删不掉（比如装在 Program Files）且不是管理员 → 征得同意后提权重来
        if (!elevated && !CanWrite(target))
        {
            if (silent) return 2;
            var again = MessageBox.Show("这个位置需要管理员权限才能删除，是否以管理员身份重新卸载？",
                "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (again != DialogResult.Yes) return 0;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = self,
                    Arguments = "--uninstall --elevated \"" + target + "\"",
                    UseShellExecute = true,
                    Verb = "runas",
                });
                return 0;
            }
            catch (Exception)
            {
                return 1;   // 用户拒绝了 UAC
            }
        }

        KillApp();

        var failed = RemoveProgramFiles(target, self);
        RemoveShortcuts();
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }

        // 自己这个 exe 还占着 → 交给"安装目录外面"的延迟脚本：等我们退出 → 删干净 → 目录空了才删目录
        try
        {
            string scriptDir = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(scriptDir)) scriptDir = target;
            string script = Path.Combine(scriptDir, "卸载-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(script, BuildScript(), new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"\"" + script + "\" \"" + target + "\"\"",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception)
        {
            // 起不来就只留一个 exe，用户手动删也行
        }

        if (!silent)
        {
            var kept = ExistingKeepDirs(target);
            MessageBox.Show(
                kept.Length > 0
                    ? "卸载完成：程序文件已清理。\n\n你的账本数据保留在：\n" + target + "\n（" + string.Join(" / ", kept) + " 不会被删除）"
                    : "卸载完成：程序目录已清理。",
                "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return 0;
    }

    /// <summary>删程序文件：根目录文件全删（除了正在运行的自己），子目录除四个数据目录外整棵删。</summary>
    private static string[] RemoveProgramFiles(string target, string self)
    {
        var failed = new System.Collections.Generic.List<string>();
        var skip = Path.GetFileName(self);
        foreach (var file in Directory.GetFiles(target))
        {
            if (string.Equals(Path.GetFileName(file), skip, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (Exception) { failed.Add(file); }
        }

        foreach (var dir in Directory.GetDirectories(target))
        {
            string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (IsKeep(name)) continue;
            try { Directory.Delete(dir, true); } catch (Exception) { failed.Add(dir); }
        }

        return failed.ToArray();
    }

    private static void RemoveShortcuts()
    {
        string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "程序");
        string menuEn = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (var folder in new[] { menu, menuEn, desktop })
        {
            foreach (var name in new[] { AppName + ".lnk", "卸载记账本.lnk" })
            {
                try
                {
                    string link = Path.Combine(folder, name);
                    if (File.Exists(link)) File.Delete(link);
                }
                catch (Exception)
                {
                    // 快捷方式删不掉不影响卸载
                }
            }
        }
    }

    private static void KillApp()
    {
        try
        {
            using (var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = "/f /im " + AppExeName,
                UseShellExecute = false,
                CreateNoWindow = true,
            }))
            {
                if (killer != null) killer.WaitForExit(8000);
            }
        }
        catch (Exception) { }

        for (var i = 0; i < 20; i++)
        {
            var still = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName));
            if (still.Length == 0) return;
            foreach (var p in still) p.Dispose();
            System.Threading.Thread.Sleep(300);
        }
    }

    private static string[] ExistingKeepDirs(string target)
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (var name in KeepDirs)
        {
            if (Directory.Exists(Path.Combine(target, name))) list.Add(name);
        }

        return list.ToArray();
    }

    private static bool IsKeep(string name)
    {
        foreach (var keep in KeepDirs)
        {
            if (string.Equals(keep, name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>盘根、系统目录、用户目录不自动删。</summary>
    private static bool Unsafe(string dir)
    {
        try
        {
            string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length <= 3 || Path.GetDirectoryName(full) == null) return true;
            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.DesktopDirectory,
                         Environment.SpecialFolder.MyDocuments,
                         Environment.SpecialFolder.UserProfile,
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.Windows,
                         Environment.SpecialFolder.System,
                         Environment.SpecialFolder.LocalApplicationData,
                         Environment.SpecialFolder.ApplicationData,
                     })
            {
                string special = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(special) &&
                    string.Equals(full, special.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
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

    private static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".写权限检测-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 延迟清理脚本（纯 ASCII，路径当参数传）：等我们退出 → 删剩下的文件 → **目录空了才删目录** → 自删。
    /// 只用不带 /s 的 rd，所以永远不可能连 数据/配置/导出/备份 一起删掉。
    /// </summary>
    private static string BuildScript()
    {
        var text = new StringBuilder();
        text.Append("@echo off\r\n");
        text.Append("setlocal\r\n");
        text.Append("cd /d \"%SystemRoot%\"\r\n");
        text.Append("set \"T=%~1\"\r\n");
        text.Append("set /a n=0\r\n");
        text.Append(":retry\r\n");
        text.Append("set /a n+=1\r\n");
        text.Append("del /f /q \"%T%\\*\" >nul 2>&1\r\n");
        text.Append("del /f /q /a \"%T%\\*\" >nul 2>&1\r\n");
        text.Append("if exist \"%T%\\*.exe\" if %n% LSS 20 (ping 127.0.0.1 -n 4 >nul & goto retry)\r\n");
        text.Append("rd \"%T%\" >nul 2>&1\r\n");
        text.Append("del /f /q \"%~f0\" >nul 2>&1\r\n");
        text.Append("exit /b 0\r\n");
        return text.ToString();
    }

    // ---------------------------------------------------------------- 安装后置

    /// <summary>写 安装信息.txt（安装包装完调用）；可选配置签名证书。</summary>
    private static int PostInstall(string target, string payload, bool trust)
    {
        if (!Directory.Exists(target)) return 1;
        string certNote = trust ? CertificateSetup(target) : "未配置（安装时未勾选）";
        string exe = Path.Combine(target, AppExeName);
        var text = new StringBuilder();
        text.Append(AppName + " " + AppVersion + Environment.NewLine);
        text.Append("安装位置：" + target + Environment.NewLine);
        text.Append("安装时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
        text.Append("负载：" + (string.IsNullOrEmpty(payload) ? "未知" : payload) + Environment.NewLine);
        text.Append("卸载：双击安装目录里的「卸载记账本.exe」（也可以在开始菜单里找）" + Environment.NewLine);
        text.Append("签名证书：" + certNote + Environment.NewLine);
        File.WriteAllText(Path.Combine(target, "安装信息.txt"), text.ToString(), new UTF8Encoding(true));
        return 0;
    }

    /// <summary>
    /// 就地把 exe 目录里的 记账本.exe 签上自签名证书（并信任它）。
    /// 返回一行给用户看的结果；**失败不抛异常**，而且只显示一行干净原因（不再是把 CLIXML 整坨贴出来）。
    /// </summary>
    private static string CertificateSetup(string appDir)
    {
        string appExe = Path.Combine(appDir, AppExeName);
        string cerPath = Path.Combine(appDir, "ajmn-证书.cer");
        try
        {
            string script =
                "$ErrorActionPreference = 'Stop'\r\n" +
                "try {\r\n" +
                "  $app = '" + appExe.Replace("'", "''") + "'\r\n" +
                "  $cer = '" + cerPath.Replace("'", "''") + "'\r\n" +
                "  $c = Get-ChildItem Cert:\\CurrentUser\\My -CodeSigningCert | Where-Object { $_.Subject -eq 'CN=ajmn' } | Select-Object -First 1\r\n" +
                "  if (-not $c) { $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ajmn' -CertStoreLocation 'Cert:\\CurrentUser\\My' -KeyUsage DigitalSignature -FriendlyName 'ajmn 记账本签名' -NotAfter (Get-Date).AddYears(10) }\r\n" +
                "  Export-Certificate -Cert $c -FilePath $cer -Force | Out-Null\r\n" +
                "  Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\\CurrentUser\\Root' | Out-Null\r\n" +
                "  Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\\CurrentUser\\TrustedPublisher' | Out-Null\r\n" +
                "  if (Test-Path $app) { Set-AuthenticodeSignature -FilePath $app -Certificate $c | Out-Null }\r\n" +
                "  Remove-Item -LiteralPath $cer -Force -ErrorAction SilentlyContinue\r\n" +
                "  Write-Output ('OK ' + $c.Thumbprint)\r\n" +
                "} catch { Write-Output ('FAIL ' + $_.Exception.Message) }\r\n";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = Process.Start(start))
            {
                if (process == null) return "失败（无法启动 PowerShell）";
                string output = process.StandardOutput.ReadToEnd().Trim();
                string error = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit(60000);
                try { if (File.Exists(cerPath)) File.Delete(cerPath); } catch (Exception) { }

                if (output.StartsWith("OK", StringComparison.Ordinal))
                {
                    return "已生成并信任（指纹 " + output.Substring(2).Trim() + "），并已给主程序签名";
                }

                string reason = output.StartsWith("FAIL", StringComparison.Ordinal)
                    ? output.Substring(4).Trim()
                    : Clean(error);
                if (reason.Length == 0) reason = "退出码 " + process.ExitCode;
                return "失败（" + reason + "）";
            }
        }
        catch (Exception ex)
        {
            return "失败（" + Clean(ex.Message) + "）";
        }
    }

    /// <summary>把 PowerShell 的 CLIXML/多行输出压成一行正常人话。</summary>
    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var lines = text.Replace("\r", "\n").Split('\n');
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("<", StringComparison.Ordinal)) continue;      // CLIXML 标签行
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;      // CLIXML 注释行
            int cut = line.IndexOf('<');
            if (cut > 0) line = line.Substring(0, cut);
            line = line.Trim();
            if (line.Length == 0) continue;
            return line.Length > 120 ? line.Substring(0, 120) + "…" : line;
        }

        return string.Empty;
    }

    // ---------------------------------------------------------------- 参数

    private static bool HasFlag(string[] args, string name)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string Value(string[] args, string name)
    {
        string value = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) value = args[i + 1];
        }

        return value;
    }
}
