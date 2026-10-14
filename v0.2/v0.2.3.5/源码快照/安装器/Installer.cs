using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// 记账本安装器（.NET Framework 4.8，Windows 预装，不需要额外运行时）
// 特点：完全不使用 %TEMP%（本机 %TEMP% 被安全策略锁住会导致所有标准安装器失败）
//
// 容器格式 v2（见 工具\组装安装包.py / LedgerBook.Setup\PackageTail.cs，两处必须一致）：
//   [stub.exe][wizard-slim.zip][wizard-full.zip][payload-slim.zip][payload-full.zip]
//   [footer 40B: "LBSETUP2"(8B) + int64×4 = {full, slim, wizardFull, wizardSlim}]
//
// 流程：
//   ① 已经有向导在跑？把它叫到前面来就退出（不再"删目录→重新解压"撞上被占用的 DLL）
//   ② 有 .NET 8 桌面运行时 → 只解 0.4MB 的向导本体（wizard-slim），负载让向导直接从安装包尾部切；
//      没装 .NET → 解 payload-full（向导的运行时就靠它）+ wizard-full（自包含向导）
//   ③ 工作目录已就绪（标记文件匹配）→ 跳过解压，直接启动向导（第二次双击安装包秒开）
//   ④ 启动向导（`--setup <安装包路径>`）后**立刻退出**；另起独立延迟清理脚本收拾工作目录
//
// 注意：这个文件用系统自带 csc.exe（C# 5）编译 —— 别用 => 表达式体、$"" 插值、?.、out var。
static class Program
{
    const string WorkName = "lb-setup";
    const string MarkerName = "lb-setup-ready.txt";
    const string LockName = "lb-setup-running.lock";
    const int FooterSize = 40;

    /// <summary>物理顺序的倒序：Names[0] 在最末尾，Names[3] 紧跟 stub。</summary>
    static readonly string[] Names = { "payload-full.zip", "payload-slim.zip", "wizard-full.zip", "wizard-slim.zip" };
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("LBSETUP2");

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var form = new Form
        {
            Text = "记账本 安装程序",
            ClientSize = new Size(460, 132),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(0xF5, 0xF6, 0xF8),
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        var title = new Label { Text = "正在准备安装文件…", Left = 22, Top = 20, Width = 420, Height = 24, Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold) };
        var hint = new Label { Text = "已装 .NET 的机器只需一秒；没装的要先解压约 146MB（十秒左右）", Left = 22, Top = 48, Width = 420, Height = 20, ForeColor = Color.FromArgb(0x8A, 0x90, 0x99) };
        var bar = new ProgressBar { Left = 22, Top = 78, Width = 416, Height = 18, Maximum = 100 };
        form.Controls.Add(title);
        form.Controls.Add(hint);
        form.Controls.Add(bar);
        form.Shown += delegate
        {
            Application.DoEvents();
            try
            {
                Prepare(form, bar);
            }
            catch (Exception ex)
            {
                MessageBox.Show(form, ex.Message, "安装准备失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                form.Close();
            }
        };
        Application.Run(form);
    }

    static void Prepare(Form form, ProgressBar bar)
    {
        string exe = Application.ExecutablePath;
        string setupDir = Path.GetDirectoryName(exe);
        string dir = Path.Combine(setupDir, WorkName);
        string lockFile = Path.Combine(setupDir, LockName);

        // ① 上一个向导还开着？那就别再解一份（它的 DLL 被占用，硬解只会报"正由另一进程使用"）
        if (WizardRunning(lockFile))
        {
            if (!ActivateExistingWizard())
            {
                MessageBox.Show(form, "安装向导已经在运行了（看一下任务栏），请不要重复打开安装包。",
                    "记账本 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            form.Close();
            return;
        }

        long[] sizes;
        long stubSize;
        if (!ReadFooter(exe, out sizes, out stubSize))
        {
            throw new InvalidOperationException("安装包校验失败（尾部标记不对），请重新下载。");
        }

        var hasDotNet = HasDotNetDesktop();
        Process wizardProcess;
        if (hasDotNet)
        {
            // ② 快路：只解 0.4MB 向导本体，负载由向导从安装包尾部自己切
            wizardProcess = PrepareAndStart(form, bar, exe, dir, sizes, stubSize, "slim", true);
            if (ExitedWithError(wizardProcess, 3000))
            {
                // 万一 .NET 检测不准（向导起不来）→ 老实按"没装 .NET"再来一次
                TryDeleteDirectory(dir);
                wizardProcess = PrepareAndStart(form, bar, exe, dir, sizes, stubSize, "full", false);
            }
        }
        else
        {
            wizardProcess = PrepareAndStart(form, bar, exe, dir, sizes, stubSize, "full", false);
        }

        // ④ 独立延迟清理脚本：放在安装包【旁边】（不在工作目录里，脚本才删得掉那个目录）
        //    等向导退出（靠它按住的锁文件判断）→ rd /s /q 工作目录（12 次重试）→ 自删
        try
        {
            string script = WriteCleanupScript(setupDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"\"" + script + "\" \"" + dir + "\" \"" + lockFile + "\" \"" + LogPath(setupDir) + "\"\"",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),   // 千万别把工作目录当自己的 cwd
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception)
        {
            // 清理脚本起不来不影响安装；下次运行安装包时会先清旧目录
        }

        form.Close();   // 立即退出：不再等向导（旧写法会一直挂到用户关掉向导为止）
    }

    /// <summary>需要时解出工作目录，然后启动向导（返回向导进程，供"起不来就退回完整解压"用）。</summary>
    static Process PrepareAndStart(Form form, ProgressBar bar, string exe, string dir, long[] sizes, long stubSize, string mode, bool fast)
    {
        if (!WorkDirReady(dir, mode, sizes))
        {
            TryDeleteDirectory(dir);
            Directory.CreateDirectory(dir);
            try
            {
                if (fast)
                {
                    ExtractSegment(exe, sizes, stubSize, "wizard-slim.zip", dir, bar, 30, 80, true);
                }
                else
                {
                    // payload-full 解压后把 zip 留在旁边：向导直接用它解到安装目录，比再从安装包切一遍快
                    ExtractSegment(exe, sizes, stubSize, "payload-full.zip", dir, bar, 25, 70, false);
                    ExtractSegment(exe, sizes, stubSize, "wizard-full.zip", dir, bar, 70, 85, true);
                }

                WriteMarker(dir, mode, sizes);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    "安装临时文件被占用：上一次的安装向导可能还开着。\n请先关掉它（或重启电脑），再重新双击安装包。\n\n" + ex.Message, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    "没权限写这个位置：" + dir + "\n把安装包放到桌面或 D 盘等可写位置再试一次。\n\n" + ex.Message, ex);
            }
        }

        string wizard = Path.Combine(dir, "lbsetup.exe");
        if (!File.Exists(wizard))
        {
            throw new InvalidOperationException("安装文件不完整：缺少 lbsetup.exe。请重新下载安装包。");
        }

        bar.Value = 100;
        Application.DoEvents();
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = wizard,
                WorkingDirectory = dir,
                UseShellExecute = true,
                Arguments = "--setup \"" + exe + "\"",   // 向导据此直接从安装包尾部取负载
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法启动安装向导：" + ex.Message, ex);
        }
    }

    /// <summary>把安装包里某一段负载切到工作目录并解开（keepZip=false 时立刻删掉 zip）。</summary>
    static void ExtractSegment(string exe, long[] sizes, long stubSize, string name, string dir, ProgressBar bar, int from, int to, bool keepZip)
    {
        string zip = Path.Combine(dir, name);
        CopySegment(exe, sizes, stubSize, name, zip);
        Extract(zip, dir, bar, from, to);
        if (!keepZip)
        {
            try { File.Delete(zip); } catch (Exception) { }
        }
    }

    static bool ReadFooter(string exe, out long[] sizes, out long stubSize)
    {
        sizes = new long[Names.Length];
        stubSize = 0;
        using (var fs = new FileStream(exe, FileMode.Open, FileAccess.Read))
        {
            if (fs.Length < 64 + FooterSize) return false;
            fs.Seek(-FooterSize, SeekOrigin.End);
            var buf = new byte[FooterSize];
            var read = 0;
            while (read < FooterSize)
            {
                var got = fs.Read(buf, read, FooterSize - read);
                if (got <= 0) return false;
                read += got;
            }

            for (var i = 0; i < 8; i++)
            {
                if (buf[i] != Magic[i]) return false;
            }

            long sum = 0;
            for (var i = 0; i < Names.Length; i++)
            {
                sizes[i] = BitConverter.ToInt64(buf, 8 + i * 8);
                if (sizes[i] < 0) return false;
                sum += sizes[i];
            }

            stubSize = fs.Length - FooterSize - sum;
            return stubSize > 0;
        }
    }

    static long OffsetOf(long[] sizes, long stubSize, string name)
    {
        var index = Array.IndexOf(Names, name);
        if (index < 0) return -1;
        long offset = stubSize;
        for (var i = Names.Length - 1; i > index; i--) offset += sizes[i];
        return offset;
    }

    static void CopySegment(string exe, long[] sizes, long stubSize, string name, string dest)
    {
        var index = Array.IndexOf(Names, name);
        if (index < 0 || sizes[index] <= 0) throw new InvalidOperationException("安装包里缺少 " + name + "，请重新下载。");
        long start = OffsetOf(sizes, stubSize, name);
        using (var input = new FileStream(exe, FileMode.Open, FileAccess.Read))
        using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write))
        {
            input.Seek(start, SeekOrigin.Begin);
            var buf = new byte[1 << 20];
            var left = sizes[index];
            while (left > 0)
            {
                var want = (int)Math.Min(buf.Length, left);
                var got = input.Read(buf, 0, want);
                if (got <= 0) break;
                output.Write(buf, 0, got);
                left -= got;
            }

            if (left > 0) throw new InvalidOperationException("安装包不完整（" + name + " 没读全），请重新下载。");
        }
    }

    /// <summary>有没有 .NET 8 桌面运行时（有的话向导不用自带运行时，负载也能待在安装包里）。</summary>
    static bool HasDotNetDesktop()
    {
        try
        {
            var shared = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared",
                "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(shared)) return false;
            foreach (var dir in Directory.GetDirectories(shared))
            {
                if (Path.GetFileName(dir).StartsWith("8.", StringComparison.Ordinal)) return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>工作目录是不是已经按当前模式解好了（第二遍安装就不必再解 146MB）。</summary>
    static bool WorkDirReady(string dir, string mode, long[] sizes)
    {
        try
        {
            if (!File.Exists(Path.Combine(dir, "lbsetup.exe"))) return false;
            if (mode == "full" &&
                (!File.Exists(Path.Combine(dir, "hostfxr.dll")) || !File.Exists(Path.Combine(dir, "coreclr.dll"))))
            {
                return false;
            }

            var marker = Path.Combine(dir, MarkerName);
            if (!File.Exists(marker)) return false;
            var sawMode = false;
            var sawSizes = false;
            foreach (var line in File.ReadAllLines(marker))
            {
                if (line.StartsWith("mode=", StringComparison.Ordinal))
                {
                    sawMode = true;
                    if (line.Substring(5).Trim() != mode) return false;
                }
                else if (line.StartsWith("sizes=", StringComparison.Ordinal))
                {
                    sawSizes = true;
                    if (line.Substring(6).Trim() != Sizes(sizes)) return false;
                }
            }

            return sawMode && sawSizes;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static void WriteMarker(string dir, string mode, long[] sizes)
    {
        var text = new StringBuilder();
        text.Append("mode=" + mode + "\r\n");
        text.Append("sizes=" + Sizes(sizes) + "\r\n");
        text.Append("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n");
        File.WriteAllText(Path.Combine(dir, MarkerName), text.ToString());
    }

    static string Sizes(long[] sizes)
    {
        var parts = new string[sizes.Length];
        for (var i = 0; i < sizes.Length; i++) parts[i] = sizes[i].ToString();
        return string.Join(",", parts);
    }

    /// <summary>向导在不在跑：它用 FileShare.None 按住锁文件，我们能独占打开就说明它已经退了。</summary>
    static bool WizardRunning(string lockFile)
    {
        try
        {
            if (!File.Exists(lockFile)) return false;
            using (var probe = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                probe.Close();
            }

            try { File.Delete(lockFile); } catch (Exception) { }
            return false;
        }
        catch (Exception)
        {
            return File.Exists(lockFile);
        }
    }

    /// <summary>把已经在跑的向导窗口叫到前面来。</summary>
    static bool ActivateExistingWizard()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("lbsetup"))
            {
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, 9);            // SW_RESTORE
                    SetForegroundWindow(handle);
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>
    /// 向导是不是刚起来就报错退出了：只给它 3 秒宽限，**不阻塞等它跑完**
    /// （向导正常会开着好几分钟，用户自己关掉时退出码是 0）。
    /// </summary>
    static bool ExitedWithError(Process process, int graceMs)
    {
        try
        {
            if (process == null) return false;
            var deadline = DateTime.UtcNow.AddMilliseconds(graceMs);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited) return process.ExitCode != 0;
                System.Threading.Thread.Sleep(150);
            }

            return false;      // 3 秒后还活着 → 向导正常起来了
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>删不掉就留着（旧目录被上一轮的向导占着时删不掉）。</summary>
    static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception)
        {
        }
    }

    // 注意：这个文件是用系统自带的 csc.exe（C# 5）编译的，别用 => 表达式体、$"" 插值、?. 这些新语法
    static string LockPath(string setupDir)
    {
        return Path.Combine(setupDir, LockName);
    }

    static string LogPath(string setupDir)
    {
        return Path.Combine(setupDir, "lb-setup-清理失败.txt");
    }

    /// <summary>
    /// 生成清理脚本。脚本正文是【纯 ASCII】，中文路径全部当参数传进去
    /// —— cmd 是按系统代码页读 .cmd 文件字节的，中文写进文件容易变乱码（设计文档 3.5 的坑），
    /// 而命令行参数是 Unicode 转过来的，不会乱。
    ///
    /// 逻辑：① 等向导的锁文件出现（最多 60 秒，没出现说明向导根本没起来）；
    ///       ② 等它消失（向导退出会自动释放，最多约 10 分钟）；
    ///       ③ rd /s /q 工作目录，最多 12 次重试；④ 自删。
    /// 向导一直不退出（真卡住了）本次就不删，留给"下次运行安装包先清旧目录"兜底。
    /// </summary>
    static string WriteCleanupScript(string setupDir)
    {
        string script = Path.Combine(setupDir, "lb-setup-清理.cmd");
        var text = new StringBuilder();
        text.Append("@echo off\r\n");
        text.Append("setlocal\r\n");
        text.Append("cd /d \"%SystemRoot%\"\r\n");
        text.Append("set \"T=%~1\"\r\n");
        text.Append("set \"LOCK=%~2\"\r\n");
        text.Append("set \"LOG=%~3\"\r\n");
        text.Append("set /a s=0\r\n");
        text.Append(":waitstart\r\n");                                   // 等向导起来（锁文件出现）
        text.Append("set /a s+=1\r\n");
        text.Append("if exist \"%LOCK%\" goto waitgone\r\n");
        text.Append("if %s% LSS 60 (ping 127.0.0.1 -n 2 >nul & goto waitstart)\r\n");
        text.Append("goto delete\r\n");
        text.Append(":waitgone\r\n");                                    // 等向导退出（锁文件能删掉）
        text.Append("set /a w=0\r\n");
        text.Append(":waitloop\r\n");
        text.Append("set /a w+=1\r\n");
        text.Append("del /f /q \"%LOCK%\" >nul 2>&1\r\n");
        text.Append("if not exist \"%LOCK%\" goto delete\r\n");
        text.Append("if %w% LSS 600 (ping 127.0.0.1 -n 2 >nul & goto waitloop)\r\n");
        text.Append("if not exist \"%LOG%\" (echo lb-setup cleanup skipped: the wizard is still running. > \"%LOG%\")\r\n");
        text.Append("del /f /q \"%~f0\" >nul 2>&1\r\n");
        text.Append("exit /b 0\r\n");
        text.Append(":delete\r\n");
        text.Append("set /a n=0\r\n");
        text.Append(":retry\r\n");
        text.Append("set /a n+=1\r\n");
        text.Append("rd /s /q \"%T%\" >nul 2>&1\r\n");
        text.Append("if exist \"%T%\" if %n% LSS 12 (ping 127.0.0.1 -n 4 >nul & goto retry)\r\n");
        text.Append("if exist \"%T%\" if not exist \"%LOG%\" (echo lb-setup cleanup failed: some files are still in use. > \"%LOG%\")\r\n");
        text.Append("del /f /q \"%~f0\" >nul 2>&1\r\n");
        text.Append("exit /b 0\r\n");
        File.WriteAllText(script, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return script;
    }

    /// <summary>
    /// 把运行时目录打成存储式 zip（向导按 payload-full.zip 安装），并排除向导自身与负载文件。
    /// 保留给 LZMA 变体（Installer-lzma.cs）参考用；当前流程用的是真自包含发布 + 工具\打包zip.py。
    /// </summary>
    static void MakeFullZip(string dir)
    {
        string zip = Path.Combine(dir, "payload-full.zip");
        if (File.Exists(zip)) File.Delete(zip);
        var skip = new[] { "payload-full.zip", "payload-slim.zip", "payload-full.tar.xz", "wizard.zip", "lb-setup-log.txt" };
        using (var fs = new FileStream(zip, FileMode.Create))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(dir.Length).TrimStart('\\');
                string name = Path.GetFileName(file);
                if (Array.IndexOf(skip, name) >= 0) continue;
                if (name.StartsWith("lbsetup.", StringComparison.OrdinalIgnoreCase)) continue;
                archive.CreateEntryFromFile(file, rel.Replace('\\', '/'), CompressionLevel.NoCompression);
            }
        }
    }

    static void RunTar(string archive, string dir)
    {
        string tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        var psi = new ProcessStartInfo
        {
            FileName = File.Exists(tar) ? tar : "tar.exe",
            Arguments = "-xf \"" + archive + "\" -C \"" + dir + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using (var p = Process.Start(psi))
        {
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("解压内置运行时失败（tar 返回 " + p.ExitCode + "）。");
        }
    }

    static void Extract(string zipPath, string dir, ProgressBar bar, int from, int to)
    {
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            int total = zip.Entries.Count, index = 0;
            foreach (var entry in zip.Entries)
            {
                string dest = Path.Combine(dir, entry.FullName);
                if (entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(dest);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                }
                index++;
                if (index % 25 == 0)
                {
                    bar.Value = from + (int)((to - from) * (double)index / total);
                    Application.DoEvents();
                }
            }
        }
    }
}
