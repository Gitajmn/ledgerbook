using System;
using System.IO;
using System.Text;

// 传统 namespace 块 + 无新语法：这个文件也要能被 .NET Framework 的 csc.exe 单独编译（见 假向导/自检）。
namespace LedgerBook.Setup
{
    /// <summary>
    /// 读单文件安装包尾部的负载（容器格式 v2）：
    ///
    ///   [stub.exe][wizard-slim.zip][wizard-full.zip][payload-slim.zip][payload-full.zip][footer]
    ///   footer = "LBSETUP2"(8B) + int64×4 = {full, slim, wizardFull, wizardSlim}
    ///
    /// 为什么要能"按需切"：装了 .NET 8 的机器只需要 0.4MB 的向导就能跑，
    /// 没必要先把 146MB 运行时解到 lb-setup（又慢又容易被占用锁住，用户就是这么踩坑的）——
    /// 这种情况下向导直接从安装包尾部把 payload 切出来解到安装目录即可。
    /// </summary>
    public static class PackageTail
    {
        public const string Magic = "LBSETUP2";
        public const int FooterSize = 40;

        /// <summary>物理顺序的倒序：Names[0] 在最末尾，Names[3] 紧跟 stub。</summary>
        public static readonly string[] Names =
        {
            "payload-full.zip",     // 自包含负载（没装 .NET 的机器）
            "payload-slim.zip",     // 精简负载（已装 .NET 的机器）
            "wizard-full.zip",      // 自包含向导本体（没装 .NET 时用；运行时由 payload-full 提供）
            "wizard-slim.zip",      // 框架依赖向导本体（已装 .NET 时用，0.4MB，秒解）
        };

        /// <summary>解析尾部：sizes 按 Names 顺序；stubSize = 文件长度 - footer - 四段之和。</summary>
        public static bool TryReadLayout(string packagePath, out long[] sizes, out long stubSize)
        {
            sizes = new long[Names.Length];
            stubSize = 0;
            try
            {
                var info = new FileInfo(packagePath);
                if (!info.Exists || info.Length < 64 + FooterSize)
                {
                    return false;
                }

                using (var fs = info.OpenRead())
                {
                    fs.Seek(-FooterSize, SeekOrigin.End);
                    var buf = new byte[FooterSize];
                    var read = 0;
                    while (read < FooterSize)
                    {
                        var got = fs.Read(buf, read, FooterSize - read);
                        if (got <= 0)
                        {
                            return false;
                        }

                        read += got;
                    }

                    if (Encoding.ASCII.GetString(buf, 0, 8) != Magic)
                    {
                        return false;
                    }

                    long sum = 0;
                    for (var i = 0; i < Names.Length; i++)
                    {
                        sizes[i] = BitConverter.ToInt64(buf, 8 + i * 8);
                        if (sizes[i] < 0)
                        {
                            return false;
                        }

                        sum += sizes[i];
                    }

                    stubSize = info.Length - FooterSize - sum;
                    return stubSize > 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>某段负载在包里的起始偏移。</summary>
        public static long OffsetOf(long[] sizes, long stubSize, string entryName)
        {
            var index = Array.IndexOf(Names, entryName);
            if (index < 0)
            {
                return -1;
            }

            long offset = stubSize;
            for (var i = Names.Length - 1; i > index; i--)
            {
                offset += sizes[i];
            }

            return offset;
        }

        /// <summary>把指定负载从安装包里切到 destPath。包里没有这段（长度 0）或格式不对时返回 false。</summary>
        public static bool Extract(string packagePath, string entryName, string destPath)
        {
            if (!TryReadLayout(packagePath, out var sizes, out var stubSize))
            {
                return false;
            }

            var index = Array.IndexOf(Names, entryName);
            if (index < 0 || sizes[index] <= 0)
            {
                return false;
            }

            var offset = OffsetOf(sizes, stubSize, entryName);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
            using (var input = File.OpenRead(packagePath))
            using (var output = File.Create(destPath))
            {
                input.Seek(offset, SeekOrigin.Begin);
                var buf = new byte[1 << 20];
                var left = sizes[index];
                while (left > 0)
                {
                    var want = (int)Math.Min(buf.Length, left);
                    var got = input.Read(buf, 0, want);
                    if (got <= 0)
                    {
                        break;
                    }

                    output.Write(buf, 0, got);
                    left -= got;
                }

                return left == 0;
            }
        }
    }
}
