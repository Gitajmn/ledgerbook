using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;

// 记账本安装器（.NET Framework 4.8，Windows 预装，不需要额外运行时）
// 特点：完全不使用 %TEMP%（本机 %TEMP% 被安全策略锁住会导致所有标准安装器失败）
// 流程：把尾部附带的负载解到自己旁边 → 解开内置 .NET 8 运行时 → 启动漂亮的 WPF 安装向导
static class Program
{
    const string WorkName = "lb-setup";
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("LBSETUP1");

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
        var hint = new Label { Text = "首次运行需要解压约 160MB（十秒左右），请稍候", Left = 22, Top = 48, Width = 420, Height = 20, ForeColor = Color.FromArgb(0x8A, 0x90, 0x99) };
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
        string dir = Path.Combine(Path.GetDirectoryName(exe), WorkName);
        Directory.CreateDirectory(dir);

        // 1) 从自身尾部取出三个负载
        long[] sizes = ReadFooter(exe);
        long end = new FileInfo(exe).Length - 32;
        string[] names = { "payload-full.tar.xz", "payload-slim.zip", "wizard.zip" };
        var paths = new string[3];
        for (int i = 0; i < 3; i++)
        {
            long start = end - sizes[i];
            paths[i] = Path.Combine(dir, names[i]);
            CopyRange(exe, start, sizes[i], paths[i]);
            end = start;
            bar.Value = 10 + (i + 1) * 5;
            Application.DoEvents();
        }

        // 2) 解开 LZMA 压缩的内置运行时（Windows 自带 tar 支持 .tar.xz，包体因此小很多）
        bar.Style = ProgressBarStyle.Marquee;
        Application.DoEvents();
        RunTar(paths[0], dir);
        bar.Style = ProgressBarStyle.Continuous;
        bar.Value = 68;
        Application.DoEvents();
        // 3) 按向导的要求，就地生成 payload-full.zip（存储式，不重复压缩）
        MakeFullZip(dir);
        bar.Value = 80;
        Application.DoEvents();
        try { File.Delete(paths[0]); } catch { }
        // 3) 放好向导本体
        Extract(paths[2], dir, bar, 70, 85);

        bar.Value = 92;
        Application.DoEvents();
        string wizard = Path.Combine(dir, "lbsetup.exe");
        if (!File.Exists(wizard))
        {
            throw new InvalidOperationException("安装文件不完整：缺少 lbsetup.exe。请重新下载安装包。");
        }

        bar.Value = 100;
        Application.DoEvents();
        Process.Start(new ProcessStartInfo { FileName = wizard, WorkingDirectory = dir, UseShellExecute = true });
        form.Close();
    }

    static long[] ReadFooter(string exe)
    {
        using (var fs = new FileStream(exe, FileMode.Open, FileAccess.Read))
        {
            if (fs.Length < 64) throw new InvalidOperationException("安装包文件不完整。");
            fs.Seek(-32, SeekOrigin.End);
            var buf = new byte[32];
            fs.Read(buf, 0, 32);
            for (int i = 0; i < 8; i++)
            {
                if (buf[i] != Magic[i]) throw new InvalidOperationException("安装包校验失败（尾部标记不对），请重新下载。");
            }
            var sizes = new long[3];
            for (int i = 0; i < 3; i++) sizes[i] = BitConverter.ToInt64(buf, 8 + i * 8);
            return sizes;
        }
    }

    static void CopyRange(string src, long start, long length, string dest)
    {
        using (var input = new FileStream(src, FileMode.Open, FileAccess.Read))
        using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write))
        {
            input.Seek(start, SeekOrigin.Begin);
            var buf = new byte[1 << 20];
            long left = length;
            while (left > 0)
            {
                int want = (int)Math.Min(buf.Length, left);
                int got = input.Read(buf, 0, want);
                if (got <= 0) break;
                output.Write(buf, 0, got);
                left -= got;
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

    // 把运行时目录打成存储式 zip（向导按 payload-full.zip 安装），并排除向导自身与负载文件
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