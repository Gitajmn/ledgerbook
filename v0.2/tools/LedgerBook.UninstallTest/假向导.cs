using System;
using System.IO;
using System.Threading;

// 测试用的"假向导"：模仿真向导会做的两件事 ——
//   ① 在工作目录（lb-setup）外面按住锁文件（向导靠它告诉清理脚本"我还在跑"）；
//   ② 忽略命令行参数（真向导收的是 --setup <安装包路径>），活几秒再正常退出。
// 用来验证解包器留下的清理脚本确实会等向导退出、并且不会提前删目录。
internal static class FakeWizard
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            LedgerBook.Setup.WorkDirLock.AcquireIfRunningFromWorkDir();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "假向导已启动.txt"), DateTime.Now.ToString("HH:mm:ss"));

            // 参数形如 --setup "<安装包路径>"：只认纯数字那个（测试里用来控制存活时长），别的一律忽略
            int seconds = 8;
            foreach (var arg in args)
            {
                int parsed;
                if (int.TryParse(arg, out parsed) && parsed > 0 && parsed < 600)
                {
                    seconds = parsed;
                }
            }

            Thread.Sleep(seconds * 1000);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "假向导正常退出.txt"), DateTime.Now.ToString("HH:mm:ss"));
        }
        catch (Exception ex)
        {
            // 别让未处理异常弹"程序已停止工作"，否则进程会一直挂着
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "假向导出错.txt"), ex.ToString()); } catch (Exception) { }
        }
    }
}
