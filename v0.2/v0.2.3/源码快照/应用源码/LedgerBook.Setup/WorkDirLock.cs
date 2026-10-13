using System;
using System.IO;

// 注意：这个文件要能被"假向导"测试用 .NET Framework 的 csc.exe 单独编译，
// 所以用传统 namespace 块（file-scoped namespace 是 C# 10 的，老编译器不认）。
namespace LedgerBook.Setup
{
    /// <summary>
    /// 解包器工作目录（lb-setup）的"我在运行"标志：
    ///   向导如果是从解包器的工作目录里跑起来的，就在工作目录【外面】按住一个锁文件（独占打开）。
    ///   解包器留下的清理脚本只要删不掉这个文件，就知道向导还在跑，不会去动工作目录；
    ///   向导一退出，进程结束会自动释放句柄，脚本就能把锁文件删掉，然后安全地清空工作目录。
    ///
    /// 为什么不用 tasklist 判断向导在不在：tasklist 在某些环境下枚举进程会被拒（实测本机就报
    /// "ERROR: Access denied"），命令行里一旦匹配不到就会被误判成"向导已退出"→ 提前删目录 →
    /// 向导正在用的 DLL 被删掉，直接崩。用文件锁不存在这个误判。
    /// </summary>
    public static class WorkDirLock
    {
        /// <summary>解包器的工作目录名（两处必须一致）。</summary>
        public const string WorkDirName = "lb-setup";

        /// <summary>锁文件名（ASCII，脚本里要直接引用）。</summary>
        public const string LockFileName = "lb-setup-running.lock";

        private static FileStream _hold;

        /// <summary>从工作目录里跑起来时才上锁；锁文件放工作目录外面。</summary>
        public static void AcquireIfRunningFromWorkDir()
        {
            try
            {
                var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(Path.GetFileName(dir), WorkDirName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent))
                {
                    return;
                }

                _hold = new FileStream(
                    Path.Combine(parent, LockFileName),
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None);        // 独占：别人删它、改它都会失败
                _hold.WriteByte(0x31);
                _hold.Flush();
            }
            catch (Exception)
            {
                _hold = null;               // 上不了锁也不影响使用，清理脚本会按"向导没起来"处理
            }
        }
    }
}

