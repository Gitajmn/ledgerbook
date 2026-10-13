using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// 记账本安装器（甲方案：自研，.NET Framework 4.8，Windows 10/11 自带，不需要任何运行时下载）
//
// 设计目标（2026-10-09 与用户商定）：
//   · 单文件：安装器本体 + 负载都在这一个 exe 里，不往 %TEMP% 解压任何东西
//   · 界面与记账本本体同一套设计令牌（GDI+ 全自绘：药丸按钮/圆角输入框/自绘勾选框/圆角进度条）
//   · 不依赖注册表（这台机器不让写 HKCU\...\Uninstall）；安装位置记在 %LOCALAPPDATA%\记账本\安装位置.txt
//   · 更新友好：负载分成"程序"(0.4MB) 与"运行时"(61MB) 两个 zip；
//     程序负载两种机器通用（有 .NET 就用系统 .NET，没 .NET 就用包内私有运行时），
//     运行时带版本标记，已存在且版本一致时**整段跳过** → 以后每次更新只写 0.4MB
//   · 卸载/证书交给自带的小工具（卸载记账本.exe，同样不需要管理员）
//
// 负载布局（私有运行时，实测 trace 会用包内运行时）：
//   程序：记账本.exe / 记账本.dll / deps.json / runtimeconfig.json / LedgerBook.Core.dll(+pdb)
//   运行时：hostfxr.dll + shared\Microsoft.NETCore.App\<版本>\ + shared\Microsoft.WindowsDesktop.App\<版本>\
//
// 命令行：
//   （无参数）                        图形界面安装
//   --silent --dir <路径>             静默安装（自检/批量用）
//        [--desktop] [--trust] [--no-launch] [--test-icons] [--force-runtime]
//   --shot <png> --page 0|1|2         渲染界面截图（文档用）
//   退出码 0 成功、1 失败、2 取消
internal static class Program
{
    public const string AppName = "记账本";
    // 版本号只有一个源头：v0.2\Directory.Build.props 的 <Version>；
    // 打包脚本 工具\打包-自研安装包.ps1 会据此生成 临时\生成\版本信息.cs（BuildInfo）
    public const string AppVersion = BuildInfo.AppVersion;
    public const string AppExe = "记账本.exe";
    public const string UninstallerName = "卸载记账本.exe";
    public const string RuntimeVersion = BuildInfo.RuntimeVersion;   // 与 LB-构建工具链 里的运行时一致
    public const string RuntimeMarker = "运行时版本.txt";

    public static string PackagePath { get { return Process.GetCurrentProcess().MainModule.FileName; } }

    [STAThread]
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.Shot != null)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            return RenderShot(options);
        }

        if (options.Silent)
        {
            try
            {
                var installer = new InstallEngine(options);
                return installer.Run(null);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new InstallerForm(options));
        return InstallerForm.LastExitCode;
    }

    private static int RenderShot(Options options)
    {
        var form = new InstallerForm(options);
        form.Show();
        Application.DoEvents();
        System.Threading.Thread.Sleep(400);
        Application.DoEvents();
        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(options.Shot, ImageFormat.Png);
        }

        Console.WriteLine("SHOT OK " + options.Shot);
        form.Close();
        return 0;
    }
}

// ------------------------------------------------------------------ 命令行参数

internal sealed class Options
{
    public bool Silent;
    public string Dir;
    public bool Desktop;
    public bool Trust;
    public bool NoLaunch = true;
    public bool TestIcons;
    public bool ForceRuntime;
    public string Shot;
    public int Page;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--silent": o.Silent = true; break;
                case "--dir": if (i + 1 < args.Length) o.Dir = args[++i]; break;
                case "--desktop": o.Desktop = true; break;
                case "--trust": o.Trust = true; break;
                case "--launch": o.NoLaunch = false; break;
                case "--no-launch": o.NoLaunch = true; break;
                case "--test-icons": o.TestIcons = true; break;
                case "--force-runtime": o.ForceRuntime = true; break;
                case "--shot": if (i + 1 < args.Length) o.Shot = args[++i]; break;
                case "--page": if (i + 1 < args.Length) int.TryParse(args[++i], out o.Page); break;
            }
        }

        return o;
    }
}

// ------------------------------------------------------------------ 安装逻辑

internal sealed class InstallEngine
{
    private readonly Options _options;
    public Action<int, string> Progress;     // 0-100 + 状态文字

    public InstallEngine(Options options)
    {
        _options = options;
    }

    /// <summary>执行安装。返回 0 成功 / 1 失败 / 2 取消。</summary>
    public int Run(Func<bool> cancelled)
    {
        string target = ResolveTarget(_options.Dir);
        Report(2, "准备中…");

        // 1) 目标目录可写吗（先试建 + 写探针，给中文提示）
        EnsureWritable(target);

        // 2) 现在有没有装过？装过就按"更新"处理（不碰数据目录）
        bool updating = File.Exists(Path.Combine(target, Program.AppExe));

        // 3) 要不要装运行时：系统没 .NET 8 且目标里没有同版本运行时
        bool needRuntime = false;
        bool runtimeSkipped = false;
        if (!HasResource("payload-runtime.zip"))
        {
            if (!HasSystemDotNet8())
            {
                throw new InvalidOperationException(
                    "这个安装包是「小包」（不含内置运行时），而这台电脑没装 .NET 8 桌面运行时。\n" +
                    "请改用完整安装包（约 62MB）。");
            }
        }
        else if (_options.ForceRuntime || !RuntimePresent(target))
        {
            needRuntime = !HasSystemDotNet8() || _options.ForceRuntime;
        }
        else
        {
            runtimeSkipped = true;
        }

        Report(4, "正在结束正在运行的记账本…");
        KillRunningApp();

        // 4) 解程序负载（总是覆盖，0.4MB 很便宜）
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Report(8, "正在安装程序文件…");
        if (!ExtractZip("payload-app.zip", target, false, 8, needRuntime ? 30 : 88, cancelled, allowed))
        {
            Report(100, "已取消");
            return 2;
        }

        // 4.5) 不管这次装不装运行时，都要先把**运行时文件名**收进白名单：
        //      否则"跳过运行时"的那次更新会把运行时文件当成旧版本残留删掉（实测踩过这个坑）。
        //      小包里没有运行时负载，靠随包附带的"运行时文件清单.txt" + 兜底规则保护。
        if (HasResource("payload-runtime.zip"))
        {
            CollectPayloadNames("payload-runtime.zip", allowed);
        }

        CollectNameList("runtime-files.txt", allowed);

        // 5) 解运行时（只在需要时；已存在同版本就整段跳过 —— 这是"更新只写 0.4MB"的关键）
        if (needRuntime)
        {
            Report(32, "正在安装内置运行时（约 145MB，只装这一次）…");
            if (!ExtractZip("payload-runtime.zip", target, true, 32, 88, cancelled, allowed))
            {
                Report(100, "已取消");
                return 2;
            }

            if (cancelled != null && cancelled())
            {
                Report(100, "已取消");
                return 2;
            }

            File.WriteAllText(Path.Combine(target, Program.RuntimeMarker),
                Program.RuntimeVersion + Environment.NewLine + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine,
                new UTF8Encoding(true));
        }
        else if (runtimeSkipped)
        {
            Report(32, "运行时已经是 " + Program.RuntimeVersion + "，跳过（更新这么快就是这个原因）");
        }

        // 6) 清掉旧版本残留（清单外的文件），保留 卸载器/安装信息/数据目录
        Report(88, "正在清理旧版本残留…");
        int removed = CleanupStale(target, allowed);

        // 7) 放卸载器
        Report(90, "正在放置卸载程序…");
        ExtractResourceToFile("helper.exe", Path.Combine(target, Program.UninstallerName), true);

        // 8) 快捷方式
        Report(92, "正在创建快捷方式…");
        CreateShortcuts(target);

        // 9) 安装信息（顺带配置证书，交给小工具做）
        Report(95, "正在写入安装信息…");
        WriteInstallInfo(target, needRuntime ? "内置运行时（自带 .NET 8）" : "精简（使用系统 .NET）", runtimeSkipped);

        // 10) 记住安装位置（不依赖注册表）
        RememberTarget(target);

        Report(100, "完成");
        if (!_options.NoLaunch && !_options.Silent)
        {
            TryLaunch(target);
        }

        return 0;
    }

    // ---------------------------------------------------------- 目标目录

    public static string DefaultTarget
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Program.AppName);
        }
    }

    /// <summary>安装位置：命令行 &gt; 上次记的 &gt; 默认（都不依赖注册表）。</summary>
    public static string ResolveTarget(string fromArgs)
    {
        if (!string.IsNullOrEmpty(fromArgs))
        {
            return Path.GetFullPath(fromArgs);
        }

        try
        {
            string memory = MemoryFile;
            if (File.Exists(memory))
            {
                string line = File.ReadAllText(memory, Encoding.UTF8).Trim();
                if (line.Length > 0 && Directory.Exists(line))
                {
                    return Path.GetFullPath(line);
                }
            }
        }
        catch (Exception)
        {
        }

        return DefaultTarget;
    }

    private static string MemoryFile
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Program.AppName, "安装位置.txt"); }
    }

    private static void RememberTarget(string target)
    {
        try
        {
            string file = MemoryFile;
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, target + Environment.NewLine, new UTF8Encoding(true));
        }
        catch (Exception)
        {
            // 记不住不影响使用（下次会回到默认值）
        }
    }

    private static void EnsureWritable(string target)
    {
        try
        {
            Directory.CreateDirectory(target);
            string probe = Path.Combine(target, ".写入测试-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "这个位置不能写入：\n" + target + "\n\n建议装到用户目录（默认位置）或 D 盘。\n\n" + ex.Message, ex);
        }
    }

    // ---------------------------------------------------------- 运行时判断

    public static bool HasSystemDotNet8()
    {
        try
        {
            string shared = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared",
                "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(shared))
            {
                return false;
            }

            foreach (var dir in Directory.GetDirectories(shared))
            {
                if (Path.GetFileName(dir).StartsWith("8.", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>目标目录里有没有可用的私有运行时（标记版本一致 + hostfxr + shared 两个框架都在）。</summary>
    public static bool RuntimePresent(string target)
    {
        try
        {
            string marker = Path.Combine(target, Program.RuntimeMarker);
            if (!File.Exists(marker))
            {
                return false;
            }

            string first = File.ReadAllText(marker, Encoding.UTF8).Split('\n')[0].Trim();
            if (!string.Equals(first, Program.RuntimeVersion, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!File.Exists(Path.Combine(target, "hostfxr.dll")))
            {
                return false;
            }

            string netCore = Path.Combine(target, "shared", "Microsoft.NETCore.App", Program.RuntimeVersion);
            string desktop = Path.Combine(target, "shared", "Microsoft.WindowsDesktop.App", Program.RuntimeVersion);
            return File.Exists(Path.Combine(netCore, "System.Private.CoreLib.dll"))
                   && File.Exists(Path.Combine(desktop, "PresentationFramework.dll"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------- 解压与清理

    private static bool HasResource(string name)
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(name) != null;
    }

    private static void ExtractResourceToFile(string resource, string path, bool overwrite)
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        {
            if (stream == null)
            {
                throw new InvalidOperationException("安装包缺少内置文件：" + resource);
            }

            if (File.Exists(path) && !overwrite)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var file = File.Create(path))
            {
                stream.CopyTo(file);
            }
        }
    }

    /// <summary>
    /// 把某个负载 zip 解到 target。skipSameSize=true 时"文件已存在且大小一致"就跳过
    /// （运行时 145MB 全靠这个跳过，更新才能只写 0.4MB）。返回 false = 用户取消。
    /// </summary>
    private bool ExtractZip(string resource, string target, bool skipSameSize, int from, int to,
        Func<bool> cancelled, HashSet<string> allowed)
    {
        using (var raw = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        {
            if (raw == null)
            {
                throw new InvalidOperationException("安装包缺少内置文件：" + resource);
            }

            Stream seekable = raw;
            string tempCopy = null;
            if (!raw.CanSeek)
            {
                // 极少数情况下资源流不可定位（zip 需要从尾部读目录）→ 先落到安装目录旁边
                tempCopy = Path.Combine(target, "." + resource + ".tmp");
                using (var file = File.Create(tempCopy))
                {
                    raw.CopyTo(file);
                }

                seekable = File.OpenRead(tempCopy);
            }

            try
            {
                using (var zip = new ZipArchive(seekable, ZipArchiveMode.Read))
                {
                    int total = zip.Entries.Count;
                    int index = 0;
                    long written = 0;
                    foreach (var entry in zip.Entries)
                    {
                        index++;
                        if (entry.Name.Length == 0)
                        {
                            Directory.CreateDirectory(Path.Combine(target, entry.FullName));
                            continue;
                        }

                        string dest = Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        allowed.Add(Path.GetFullPath(dest).Substring(Path.GetFullPath(target).Length).TrimStart(Path.DirectorySeparatorChar));
                        var info = new FileInfo(dest);
                        bool same = info.Exists && info.Length == entry.Length;
                        if (!(skipSameSize && same))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            entry.ExtractToFile(dest, true);
                            written++;
                        }

                        if (index % 20 == 0 || index == total)
                        {
                            int percent = from + (int)((to - from) * (double)index / total);
                            Report(percent, "正在安装文件…（" + index + "/" + total + "）");
                            Application.DoEvents();
                            if (cancelled != null && cancelled())
                            {
                                return false;
                            }
                        }
                    }

                    Debug.WriteLine(resource + "：写入 " + written + " 个文件");
                }

                return true;
            }
            finally
            {
                if (tempCopy != null)
                {
                    seekable.Dispose();
                    try { File.Delete(tempCopy); } catch (Exception) { }
                }
            }
        }
    }

    /// <summary>只把负载 zip 里的文件名收进白名单（不解压）——用于"跳过运行时"时保护它不被当残留删掉。</summary>
    private static void CollectPayloadNames(string resource, HashSet<string> allowed)
    {
        try
        {
            using (var raw = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (raw == null)
                {
                    return;
                }

                Stream seekable = raw;
                string tempCopy = null;
                if (!raw.CanSeek)
                {
                    tempCopy = Path.Combine(Path.GetTempPath(), resource + "." + Guid.NewGuid().ToString("N") + ".tmp");
                    using (var file = File.Create(tempCopy))
                    {
                        raw.CopyTo(file);
                    }

                    seekable = File.OpenRead(tempCopy);
                }

                try
                {
                    using (var zip = new ZipArchive(seekable, ZipArchiveMode.Read))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            if (entry.Name.Length > 0)
                            {
                                allowed.Add(entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                            }
                        }
                    }
                }
                finally
                {
                    if (tempCopy != null)
                    {
                        seekable.Dispose();
                        try { File.Delete(tempCopy); } catch (Exception) { }
                    }
                }
            }
        }
        catch (Exception)
        {
            // 收不齐就少保护几个文件，不影响安装主流程（最坏情况是运行时被重装一次）
        }
    }

    /// <summary>读一个"一行一个相对路径"的清单资源，收进白名单（小包靠它保护已装的运行时）。</summary>
    private static void CollectNameList(string resource, HashSet<string> allowed)
    {
        try
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null)
                {
                    return;
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length > 0)
                        {
                            allowed.Add(line.Replace('/', Path.DirectorySeparatorChar));
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>删掉"本版清单之外"的程序文件，保留 卸载器/安装信息/证书/数据目录。</summary>
    private static int CleanupStale(string target, HashSet<string> allowed)
    {
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Program.UninstallerName, "安装信息.txt", "ajmn-证书.cer", Program.RuntimeMarker,
        };
        var removed = 0;
        var targetFull = Path.GetFullPath(target);
        foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetFullPath(file).Substring(targetFull.Length).TrimStart(Path.DirectorySeparatorChar);
            string first = rel.Split(Path.DirectorySeparatorChar)[0];
            if (IsKeepDir(first))
            {
                continue;
            }

            if (allowed.Contains(rel) || pinned.Contains(Path.GetFileName(file)) || IsRuntimePath(rel))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception)
            {
                // 删不掉就留着，不影响安装
            }
        }

        return removed;
    }

    /// <summary>
    /// 兜底规则：当前版本的运行时文件绝不删（小包里没有运行时清单时靠它）。
    /// 只保护**本版标记的那个版本目录**，所以换运行时版本时旧版本目录还是会被清掉。
    /// </summary>
    private static bool IsRuntimePath(string rel)
    {
        string name = Path.GetFileName(rel);
        if (string.Equals(name, "hostfxr.dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "hostpolicy.dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Program.RuntimeMarker, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] prefixes =
        {
            Path.Combine("shared", "Microsoft.NETCore.App", Program.RuntimeVersion),
            Path.Combine("shared", "Microsoft.WindowsDesktop.App", Program.RuntimeVersion),
        };
        foreach (var prefix in prefixes)
        {
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsKeepDir(string name)
    {
        return string.Equals(name, "数据", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "配置", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "导出", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "备份", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------- 快捷方式 / 信息 / 进程

    private void CreateShortcuts(string target)
    {
        string exe = Path.Combine(target, Program.AppExe);
        string uninstaller = Path.Combine(target, Program.UninstallerName);
        string root = _options.TestIcons
            ? Path.Combine(target, "_测试快捷方式")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "程序");
        Shortcut.Create(Path.Combine(root, Program.AppName + ".lnk"), exe, target);
        Shortcut.Create(Path.Combine(root, Program.UninstallerName.Replace(".exe", string.Empty) + ".lnk"), uninstaller, target);
        if (_options.Desktop)
        {
            Shortcut.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Program.AppName + ".lnk"), exe, target);
        }
    }

    private void WriteInstallInfo(string target, string payloadKind, bool runtimeSkipped)
    {
        try
        {
            string helper = Path.Combine(target, Program.UninstallerName);
            if (File.Exists(helper))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = helper,
                    Arguments = "--post-install --target \"" + target + "\" --payload \"" + payloadKind + "\""
                                + (_options.Trust ? " --trust" : string.Empty),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        p.WaitForExit(120000);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 写不了安装信息不影响安装
        }
    }

    private static void KillRunningApp()
    {
        try
        {
            using (var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = "/f /im " + Program.AppExe,
                UseShellExecute = false,
                CreateNoWindow = true,
            }))
            {
                if (killer != null)
                {
                    killer.WaitForExit(8000);
                }
            }
        }
        catch (Exception)
        {
        }

        for (int i = 0; i < 20; i++)
        {
            var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Program.AppExe));
            if (running.Length == 0)
            {
                return;
            }

            foreach (var p in running)
            {
                p.Dispose();
            }

            System.Threading.Thread.Sleep(300);
        }
    }

    private static void TryLaunch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(target, Program.AppExe),
                WorkingDirectory = target,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
        }
    }

    private void Report(int percent, string text)
    {
        if (Progress != null)
        {
            Progress(percent, text);
        }
    }
}

// ------------------------------------------------------------------ 快捷方式

internal static class Shortcut
{
    public static void Create(string linkPath, string targetPath, string workingDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(type);
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = targetPath;
            link.WorkingDirectory = workingDir;
            link.IconLocation = targetPath;
            link.Description = Program.AppName;
            link.Save();
        }
        catch (Exception)
        {
            // 建不了快捷方式不影响安装
        }
    }
}

// ------------------------------------------------------------------ 界面（GDI+ 自绘，与本体同一套设计令牌）

internal sealed class InstallerForm : Form
{
    private static readonly Color AccentColor = Color.FromArgb(0x4D, 0x6B, 0xFE);
    private static readonly Color BgColor = Color.FromArgb(0xF5, 0xF6, 0xF8);
    private static readonly Color CardColor = Color.White;
    private static readonly Color LineColor = Color.FromArgb(0xE2, 0xE5, 0xEA);
    private static readonly Color InkColor = Color.FromArgb(0x1F, 0x23, 0x29);
    private static readonly Color MutedColor = Color.FromArgb(0x8A, 0x90, 0x99);
    private static readonly Color SecondaryColor = Color.FromArgb(0xF2, 0xF3, 0xF5);
    private static readonly Color WarnColor = Color.FromArgb(0xC0, 0x39, 0x35);

    public static int LastExitCode;

    private readonly Options _options;
    private readonly string _path;
    private int _page;                     // 0 路径 1 安装中 2 完成
    private int _progress;
    private string _status = "准备中…";
    private bool _cancelRequested;
    private bool _failed;
    private bool _cancelHover;
    private Rectangle _cancelRect;
    private Rectangle _browseRect;
    private Rectangle _primaryRect;
    private bool _desktop = true;
    private bool _trust = true;

    public InstallerForm(Options options)
    {
        _options = options;
        _path = InstallEngine.ResolveTarget(options.Dir);
        if (options.Desktop)
        {
            _desktop = true;
        }

        if (options.Trust)
        {
            _trust = true;
        }

        base.Text = Program.AppName + " " + Program.AppVersion + " 安装";
        ClientSize = new Size(560, 336);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgColor;
        base.Font = new Font("Microsoft YaHei UI", 9f);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        if (options.Page != 0)
        {
            _page = options.Page;
            _progress = 62;
            _status = "正在安装文件…（380/612）";
            _target = _path;          // 预览模式也要有内容可画（完成页会显示"装到哪"）
        }

        MouseMove += OnMouseMove;
        MouseClick += OnMouseClick;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BgColor);
        DrawHeader(g);
        if (_page == 0)
        {
            DrawPathPage(g);
        }
        else if (_page == 1)
        {
            DrawProgressPage(g);
        }
        else
        {
            DrawDonePage(g);
        }
    }

    private void DrawHeader(Graphics g)
    {
        TextRenderer.DrawText(g, Program.AppName, UiFontBold(30), new Point(24, 18), InkColor, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "版本 " + Program.AppVersion, UiFont(11), new Point(26, 60), MutedColor, TextFormatFlags.NoPadding);

        var iconRect = new Rectangle((Width - 92) / 2, 14, 92, 92);
        bool drew = false;
        // 1) 包内嵌的图标资源（最清晰）
        try
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("appicon.ico"))
            {
                if (stream != null)
                {
                    using (var ico = new Icon(stream, 92, 92))
                    {
                        g.DrawIcon(ico, iconRect);
                        drew = true;
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        // 2) 安装器同目录的 记账本.ico（调试时为方便替换）
        string iconFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Program.AppName + ".ico");
        if (!drew && File.Exists(iconFile))
        {
            try
            {
                using (var ico = new Icon(iconFile, 92, 92))
                {
                    g.DrawIcon(ico, iconRect);
                    drew = true;
                }
            }
            catch (Exception)
            {
            }
        }

        // 3) exe 自带的图标（/win32icon 那个，尺寸小一点）
        if (!drew)
        {
            try
            {
                using (var ico = Icon.ExtractAssociatedIcon(Program.PackagePath))
                {
                    if (ico != null)
                    {
                        g.DrawIcon(ico, iconRect);
                        drew = true;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        if (!drew)
        {
            using (var path = Round(iconRect, 12))
            using (var brush = new SolidBrush(CardColor))
            using (var pen = new Pen(LineColor))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, Program.AppName, UiFont(14), iconRect, MutedColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        string label = _page == 0 ? "安装 ›" : (_page == 1 ? "安装中" : "完成");
        bool primary = _page != 1;
        _primaryRect = new Rectangle(Width - 24 - 132, 34, 132, 46);
        using (var path = Round(_primaryRect, _primaryRect.Height / 2))
        using (var brush = new SolidBrush(primary ? AccentColor : SecondaryColor))
        {
            g.FillPath(brush, path);
        }

        TextRenderer.DrawText(g, label, UiFont(15), _primaryRect, primary ? Color.White : MutedColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawPathPage(Graphics g)
    {
        int top = 130;
        TextRenderer.DrawText(g, "安装到", UiFont(11), new Point(24, top), MutedColor, TextFormatFlags.NoPadding);

        var box = new Rectangle(24, top + 22, Width - 48, 40);
        using (var path = Round(box, 8))
        using (var brush = new SolidBrush(CardColor))
        using (var pen = new Pen(LineColor))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, EffectivePath, UiFont(10), new Rectangle(box.X + 12, box.Y, box.Width - 118, box.Height),
            InkColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        _browseRect = new Rectangle(box.Right - 94, box.Y + 3, 86, box.Height - 6);
        using (var path = Round(_browseRect, 6))
        using (var brush = new SolidBrush(SecondaryColor))
        {
            g.FillPath(brush, path);
        }

        TextRenderer.DrawText(g, "选择…", UiFont(10.5f), _browseRect, InkColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        string hint = InstallEngine.HasSystemDotNet8()
            ? "这台电脑已装 .NET 8 → 只装记账本本体（约 0.7MB）。"
            : "这台电脑没装 .NET 8 → 会一起装内置运行时（约 145MB，只装这一次）。";
        TextRenderer.DrawText(g, hint, UiFont(9), new Point(24, box.Bottom + 9), MutedColor, TextFormatFlags.NoPadding);

        DrawCheck(g, new Point(24, box.Bottom + 40), _desktop, "创建桌面快捷方式");
        DrawCheck(g, new Point(24, box.Bottom + 70), _trust, "信任签名证书（不再提示“未知发布者”）");
    }

    private void DrawProgressPage(Graphics g)
    {
        int top = 146;
        var spin = new Rectangle((Width - 30) / 2, top - 4, 30, 30);
        using (var pen = new Pen(AccentColor, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(pen, spin, 30 + _progress * 6, 250);
        }

        TextRenderer.DrawText(g, "正在安装…", UiFontBold(16), new Rectangle(0, top + 38, Width, 26), InkColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

        var bar = new Rectangle(24, top + 80, Width - 48, 10);
        using (var path = Round(bar, 5))
        using (var brush = new SolidBrush(LineColor))
        {
            g.FillPath(brush, path);
        }

        int fillWidth = (int)(bar.Width * (_progress / 100.0));
        if (fillWidth > 12)
        {
            using (var path = Round(new Rectangle(bar.X, bar.Y, fillWidth, bar.Height), 5))
            using (var brush = new SolidBrush(AccentColor))
            {
                g.FillPath(brush, path);
            }
        }

        TextRenderer.DrawText(g, _status, UiFont(9.5f), new Rectangle(24, bar.Bottom + 10, Width - 48, 20),
            MutedColor, TextFormatFlags.NoPadding);

        _cancelRect = new Rectangle(Width - 24 - 110, bar.Bottom + 38, 110, 38);
        using (var path = Round(_cancelRect, 8))
        using (var brush = new SolidBrush(_cancelHover ? Color.FromArgb(0xFD, 0xEC, 0xEC) : CardColor))
        using (var pen = new Pen(LineColor))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, _cancelRequested ? "正在取消…" : "取消安装", UiFont(10.5f), _cancelRect,
            _cancelHover ? WarnColor : InkColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawDonePage(Graphics g)
    {
        int top = 138;
        string title = _failed ? "安装没成功" : (_cancelRequested ? "已取消安装" : "安装完成");
        TextRenderer.DrawText(g, title, UiFontBold(24), new Point(24, top), _failed ? WarnColor : InkColor,
            TextFormatFlags.NoPadding);
        if (_failed)
        {
            TextRenderer.DrawText(g, "出错了，详情见弹窗。原有数据没有被删除。", UiFont(10),
                new Point(24, top + 44), MutedColor, TextFormatFlags.NoPadding);
        }
        else if (_cancelRequested)
        {
            TextRenderer.DrawText(g, "已经写入的文件已停止，账本数据没有被删除。", UiFont(10),
                new Point(24, top + 44), MutedColor, TextFormatFlags.NoPadding);
        }
        else
        {
            TextRenderer.DrawText(g, "「" + Program.AppName + " " + Program.AppVersion + "」已装到：", UiFont(10),
                new Point(24, top + 40), MutedColor, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _target ?? string.Empty, UiFont(10),
                new Rectangle(24, top + 58, Width - 48, 18), InkColor,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "桌面和开始菜单里都能找到它；账本数据就在这个目录旁边。", UiFont(10),
                new Point(24, top + 78), MutedColor, TextFormatFlags.NoPadding);
        }

        if (!_failed && !_cancelRequested)
        {
            DrawCheck(g, new Point(24, top + 104), true, "立刻启动记账本");
        }
    }

    private void DrawCheck(Graphics g, Point at, bool on, string label)
    {
        var box = new Rectangle(at.X, at.Y, 16, 16);
        using (var path = Round(box, 4))
        {
            if (on)
            {
                using (var brush = new SolidBrush(AccentColor))
                {
                    g.FillPath(brush, path);
                }

                using (var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLines(pen, new[]
                    {
                        new Point(box.X + 4, box.Y + 8),
                        new Point(box.X + 7, box.Y + 11),
                        new Point(box.X + 12, box.Y + 5),
                    });
                }
            }
            else
            {
                using (var brush = new SolidBrush(CardColor))
                {
                    g.FillPath(brush, path);
                }

                using (var pen = new Pen(LineColor))
                {
                    g.DrawPath(pen, path);
                }
            }
        }

        TextRenderer.DrawText(g, label, UiFont(10), new Point(at.X + 24, at.Y), InkColor, TextFormatFlags.NoPadding);
    }

    // ---------------------------------------------------------- 交互

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_page != 1)
        {
            return;
        }

        bool hover = _cancelRect.Contains(e.Location);
        if (hover != _cancelHover)
        {
            _cancelHover = hover;
            Invalidate();
        }
    }

    private void OnMouseClick(object sender, MouseEventArgs e)
    {
        if (_page == 0)
        {
            if (_browseRect.Contains(e.Location))
            {
                PickFolder();
                return;
            }

            if (_primaryRect.Contains(e.Location))
            {
                StartInstall();
            }

            return;
        }

        if (_page == 1)
        {
            if (_cancelRect.Contains(e.Location) && !_cancelRequested)
            {
                var answer = MessageBox.Show(this, "确定要取消安装吗？\n\n已经写入的文件会停下，账本数据不会被删除。",
                    "取消安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer == DialogResult.Yes)
                {
                    _cancelRequested = true;
                    _status = "正在取消…";
                    Invalidate();
                }
            }

            return;
        }

        if (_primaryRect.Contains(e.Location))
        {
            if (_failed || _cancelRequested)
            {
                Close();
                return;
            }

            // 完成页：勾选框默认勾着 → 点「完成」就启动记账本
            if (_installed && _launchOnFinish)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Path.Combine(_target, Program.AppExe),
                        WorkingDirectory = _target,
                        UseShellExecute = true,
                    });
                }
                catch (Exception)
                {
                }
            }

            Close();
        }
    }

    private readonly bool _launchOnFinish = true;

    private void PickFolder()
    {
        try
        {
            var dialog = new FolderBrowserDialog { Description = "选择安装位置", SelectedPath = _path };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                SetPath(Path.Combine(dialog.SelectedPath, Program.AppName));
            }
        }
        catch (Exception)
        {
        }
    }

    private string _path2;

    private string EffectivePath { get { return _path2 ?? _path; } }

    private void SetPath(string value)
    {
        _path2 = value;
        Invalidate();
    }

    private void StartInstall()
    {
        string target = EffectivePath;
        _target = target;
        _page = 1;
        _progress = 2;
        _status = "准备中…";
        Invalidate();
        Application.DoEvents();

        var options = new Options();
        options.Dir = target;
        options.Desktop = _desktop;
        options.Trust = _trust;
        options.NoLaunch = true;
        var engine = new InstallEngine(options);
        engine.Progress = delegate(int percent, string text)
        {
            _progress = percent;
            _status = text;
            Invalidate();
            Application.DoEvents();
        };

        int code;
        try
        {
            code = engine.Run(delegate { Application.DoEvents(); return _cancelRequested; });
        }
        catch (Exception ex)
        {
            _failed = true;
            _page = 2;
            Invalidate();
            Application.DoEvents();
            MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            LastExitCode = 1;
            return;
        }

        _page = 2;
        if (code == 2)
        {
            _cancelRequested = true;
        }

        _installed = code == 0;
        Invalidate();
        Application.DoEvents();
        LastExitCode = code == 0 ? 0 : code;
    }

    private string _target;
    private bool _installed;

    private static Font UiFont(float size)
    {
        return new Font("Microsoft YaHei UI", size);
    }

    private static Font UiFontBold(float size)
    {
        return new Font("Microsoft YaHei UI", size, FontStyle.Bold);
    }

    internal static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
