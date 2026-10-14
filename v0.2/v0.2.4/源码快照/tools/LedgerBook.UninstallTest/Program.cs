using System.Diagnostics;
using System.Text;
using LedgerBook.Setup;

// 卸载逻辑离线自检：在临时目录里造"假安装目录"（带中文名 + 空格，专门压引号与代码页），
// 然后调用产品代码 UninstallPlan 走完整流程，最后真的把生成的清理脚本跑起来，验证四件事：
//   ① 程序文件被删干净；② 账本数据（数据/配置/导出/备份）一个都没少；
//   ③ 卸载器自己的 exe 被占用时脚本会耐心重试，不会提前把目录删掉；
//   ④ 目录空了才删目录、脚本自己会删掉自己。
//
// 用法：卸载逻辑自检.exe <工作根目录>
internal static class Program
{
    private static int _pass;
    private static int _fail;

    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "卸载逻辑自检");
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("工作根目录：" + root);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }

        Directory.CreateDirectory(root);

        Check("盘根不许自动删", UninstallPlan.IsUnsafeToDelete(@"D:\"));
        Check("用户目录不许自动删", UninstallPlan.IsUnsafeToDelete(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Check("普通安装目录允许删", !UninstallPlan.IsUnsafeToDelete(Path.Combine(root, "记账本")));

        // ---------- 场景 A：安装目录里有账本数据（数据必须一个都不少）----------
        var dirA = Path.Combine(root, "记账本 测试目录");
        MakeInstallDir(dirA, withData: true);
        Check("能认出 4 个数据目录", UninstallPlan.ExistingKeepDirs(dirA).Length == 4);
        var selfExe = Path.Combine(dirA, "卸载记账本.exe");
        Check("删程序文件没有失败项", UninstallPlan.RemoveProgramFiles(dirA, new[] { selfExe }).Count == 0);
        Check("程序文件已删（记账本.exe）", !File.Exists(Path.Combine(dirA, "记账本.exe")));
        Check("程序文件已删（_internal 子目录）", !Directory.Exists(Path.Combine(dirA, "_internal")));
        Check("正在运行的卸载器自己留着（等脚本删）", File.Exists(selfExe));

        var scriptA = UninstallPlan.WriteScript(root);
        var textA = File.ReadAllText(scriptA);
        Check("清理脚本写在安装目录外面", !scriptA.StartsWith(dirA, StringComparison.OrdinalIgnoreCase));
        Check("脚本是纯 ASCII（不踩代码页）", textA.All(c => c < 128));
        Check("脚本先 cd 到系统目录", textA.Contains("cd /d \"%SystemRoot%\""));
        Check("脚本会重试（不是一删了之）", textA.Contains("%n% LSS 20"));
        Check("只删空目录（不带 /s），永不误删数据", !textA.Contains("rd /s"));

        UninstallPlan.StartScript(scriptA, dirA);
        Check("脚本跑完了（自己删掉了）", WaitUntil(() => !File.Exists(scriptA), 60));
        Check("场景 A：安装目录还在（因为里面有账本数据）", Directory.Exists(dirA));
        Check("场景 A：数据文件完好", File.Exists(Path.Combine(dirA, "数据", "2026", "10", "2026-10-09.jsonl")));
        Check("场景 A：配置目录完好", File.Exists(Path.Combine(dirA, "配置", "分类.json")));
        Check("场景 A：导出/备份目录完好", Directory.Exists(Path.Combine(dirA, "导出")) && Directory.Exists(Path.Combine(dirA, "备份")));
        Check("场景 A：卸载器自己的 exe 已被脚本删掉", !File.Exists(selfExe));
        Check("场景 A：一个程序文件都没剩", Directory.GetFiles(dirA).Length == 0);

        // ---------- 场景 B：安装目录里没有数据 → 目录该整个消失 ----------
        var dirB = Path.Combine(root, "记账本无数据目录");
        MakeInstallDir(dirB, withData: false);
        Check("场景 B：没有数据目录时保留清单为空", UninstallPlan.ExistingKeepDirs(dirB).Length == 0);
        Check("场景 B 删除无失败项", UninstallPlan.RemoveProgramFiles(dirB, new[] { Path.Combine(dirB, "卸载记账本.exe") }).Count == 0);
        var scriptB = UninstallPlan.WriteScript(root);
        UninstallPlan.StartScript(scriptB, dirB);
        Check("场景 B：脚本跑完了", WaitUntil(() => !File.Exists(scriptB), 60));
        Check("场景 B：安装目录整个消失", !Directory.Exists(dirB));

        // ---------- 场景 C：卸载器还在运行（自己的 exe 被独占锁住）时不许提前删 ----------
        var dirC = Path.Combine(root, "记账本等待验证");
        MakeInstallDir(dirC, withData: false);
        var lockedExe = Path.Combine(dirC, "卸载记账本.exe");
        UninstallPlan.RemoveProgramFiles(dirC, new[] { lockedExe });
        var hold = new FileStream(lockedExe, FileMode.Open, FileAccess.Read, FileShare.None);   // 假装"卸载器正在运行"
        var scriptC = UninstallPlan.WriteScript(root);
        UninstallPlan.StartScript(scriptC, dirC);
        System.Threading.Thread.Sleep(5000);
        Check("场景 C：卸载器还在跑时目录不许删", Directory.Exists(dirC));
        Check("场景 C：卸载器还在跑时 exe 还在", File.Exists(lockedExe));
        Check("场景 C：卸载器还在跑时脚本不许自删", File.Exists(scriptC));
        hold.Dispose();                                    // "卸载器退出了"
        Check("场景 C：卸载器退出后目录被删掉", WaitUntil(() => !Directory.Exists(dirC), 90));
        Check("场景 C：脚本自删", WaitUntil(() => !File.Exists(scriptC), 30));

        // ---------- 场景 D：安装包尾部切片（向导快路要用：直接从安装包里取负载）----------
        // 自己造一个容器 v2：[stub][wizard-slim][wizard-full][payload-slim][payload-full][footer]
        var pkg = Path.Combine(root, "假安装包.exe");
        var order = new[] { "payload-full.zip", "payload-slim.zip", "wizard-full.zip", "wizard-slim.zip" };
        var parts = new Dictionary<string, byte[]>
        {
            ["wizard-slim.zip"] = Encoding.UTF8.GetBytes("wizard-slim"),
            ["wizard-full.zip"] = Encoding.UTF8.GetBytes("wizard-full-" + new string('b', 300)),
            ["payload-slim.zip"] = Encoding.UTF8.GetBytes("payload-slim"),
            ["payload-full.zip"] = Encoding.UTF8.GetBytes("payload-full-" + new string('c', 5000)),
        };
        var stubBytes = Encoding.UTF8.GetBytes("stub-" + new string('a', 1000));
        using (var fs = File.Create(pkg))
        {
            fs.Write(stubBytes, 0, stubBytes.Length);
            foreach (var name in new[] { "wizard-slim.zip", "wizard-full.zip", "payload-slim.zip", "payload-full.zip" })
            {
                fs.Write(parts[name], 0, parts[name].Length);
            }

            fs.Write(Encoding.ASCII.GetBytes(PackageTail.Magic), 0, 8);
            foreach (var name in order)
            {
                fs.Write(BitConverter.GetBytes((long)parts[name].Length), 0, 8);
            }
        }

        var layoutOk = PackageTail.TryReadLayout(pkg, out var sizes, out var stubSize);
        Check("场景 D：能解析容器 v2 的尾部", layoutOk);
        Check("场景 D：stub 长度算得对", stubSize == stubBytes.Length, stubSize.ToString());
        var sizesOk = true;
        for (var i = 0; i < order.Length; i++)
        {
            if (sizes[i] != parts[order[i]].Length)
            {
                sizesOk = false;
            }
        }

        Check("场景 D：四段长度都对得上", sizesOk);

        var sliceOk = true;
        foreach (var name in order)
        {
            var dest = Path.Combine(root, "切片-" + name);
            if (!PackageTail.Extract(pkg, name, dest))
            {
                sliceOk = false;
                continue;
            }

            var got = File.ReadAllBytes(dest);
            if (got.Length != parts[name].Length || !got.SequenceEqual(parts[name]))
            {
                sliceOk = false;
            }
        }

        Check("场景 D：按需切出的每一段都和原字节一致", sliceOk);
        Check("场景 D：切不存在的段要老实失败", !PackageTail.Extract(pkg, "不存在.zip", Path.Combine(root, "x")));

        var bad = Path.Combine(root, "坏安装包.exe");
        var bytes = File.ReadAllBytes(pkg);
        bytes[bytes.Length - 40] = (byte)'X';
        File.WriteAllBytes(bad, bytes);
        Check("场景 D：尾部标记坏了要拒绝", !PackageTail.TryReadLayout(bad, out _, out _));

        Console.WriteLine("RESULT " + (_fail == 0 ? "PASS" : "FAIL") + $" 通过 {_pass} / 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static bool WaitUntil(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            System.Threading.Thread.Sleep(200);
        }

        return condition();
    }

    private static void MakeInstallDir(string dir, bool withData)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "记账本.exe"), "fake app");
        File.WriteAllText(Path.Combine(dir, "LedgerBook.Core.dll"), "fake dll");
        File.WriteAllText(Path.Combine(dir, "使用说明.md"), "fake readme");
        File.WriteAllText(Path.Combine(dir, "卸载记账本.exe"), "fake uninstaller");
        File.WriteAllText(Path.Combine(dir, "安装信息.txt"), "fake info");
        var internalDir = Path.Combine(dir, "_internal");
        Directory.CreateDirectory(internalDir);
        File.WriteAllText(Path.Combine(internalDir, "System.Private.CoreLib.dll"), "fake runtime");

        if (!withData)
        {
            return;
        }

        var data = Path.Combine(dir, "数据", "2026", "10");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "2026-10-09.jsonl"), "{\"金额\":\"18.50\"}");
        Directory.CreateDirectory(Path.Combine(dir, "配置"));
        File.WriteAllText(Path.Combine(dir, "配置", "分类.json"), "{}");
        Directory.CreateDirectory(Path.Combine(dir, "导出"));
        Directory.CreateDirectory(Path.Combine(dir, "备份"));
    }

    private static void Check(string what, bool ok, string extra = "")
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine("  [通过] " + what + (string.IsNullOrEmpty(extra) ? string.Empty : "  " + extra));
        }
        else
        {
            _fail++;
            Console.WriteLine("  [失败] " + what + (string.IsNullOrEmpty(extra) ? string.Empty : "  " + extra));
        }
    }
}
