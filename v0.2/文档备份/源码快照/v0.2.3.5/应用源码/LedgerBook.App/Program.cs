using System.IO;
using System.Windows;

namespace LedgerBook.App;

/// <summary>
/// 程序入口。
/// 正常双击运行；也支持两个给自动化验证用的参数：
///   记账本.exe --root &lt;数据目录&gt;        指定数据目录（测试隔离）
///   记账本.exe --screenshot &lt;png 路径&gt;   渲染完成后截图并自动退出
/// </summary>
public static class Program
{
    private static long _startTick;
    private static System.Threading.Mutex? _singleInstance;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>把已经在运行的那个窗口调到前台；调不动就提示一句。</summary>
    private static void FocusRunningWindow()
    {
        try
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var other in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                if (other.Id == me.Id || other.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                ShowWindow(other.MainWindowHandle, 9);   // SW_RESTORE
                SetForegroundWindow(other.MainWindowHandle);
                return;
            }

            MessageBox.Show("记账本已经在运行了，请查看任务栏上的窗口。", "记账本", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception)
        {
            // 调不动就静默退出，不影响已经开着的那个
        }
    }

    [STAThread]
    public static int Main(string[] args)

    {
        // 全局兜底：任何没人接的异常都写进运行日志（用户发日志就能定位），界面异常不让它把程序打死
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Core.Log.Error("未处理异常（进程即将退出）", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Core.Log.Error("后台任务未观察异常", e.Exception);
            e.SetObserved();
        };

        string? root = null;
        string? screenshot = null;
        var page = 0;
        var height = 800;
        var width = 0;
        var wholePage = false;
        var scroll = 0.0;
        string? dialog = null;
        string? chartPreview = null;
        var showDetail = false;
        var detailPreview = false;
        string? importPreview = null;
        var settingsSection = -1;
        string? toastText = null;   // --toast "文字"：启动就弹一个悬浮提示（给截图/演示用）
        var toastIsError = false;   // --toast-error：弹的是报错样式（红图标、不自动消失）
        var confirmPreview = false; // --confirm-preview：把"应用内确认弹窗"画出来（给截图用）
        var openCalendar = false;
        var pageCycle = 0;
        string? shootSections = null;
        var trace = false;
        var whatsNew = false;   // --whats-new：打开「更新内容」窗口（配合 --screenshot 截图）
        var pasteImport = false;   // --paste-import：打开"粘贴文本导入"窗口（配合 --screenshot 截图验证界面）
        string? pasteText = null;   // --paste-text <文本>：填进粘贴框并直接走"继续"（真正跑一次写入）
        var selectFirst = false;   // --select-first：启动后选中查询页明细第一行（验证选中配色）
        string? statsKind = null;         // --stats-kind 收入|支出：查询页只画这一类
        string? statsHighlight = null;    // --stats-highlight 分类名：图表里该类保持原色、其余变暗
        string? themeTest = null;   // --theme-test <浅色|深色>：启动后真的调用一次切换（含 RebuildUi），用于自动验证
        // 注意：不带值的开关**不要**去动 i —— 之前写成 i-- 导致同一参数被反复读到，
        // 参数解析直接死循环（界面还没出来就卡住了）。这里一律只是置标志位。
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root" when i + 1 < args.Length:
                    root = args[i + 1];
                    i++;
                    break;
                case "--theme-test" when i + 1 < args.Length:
                    themeTest = args[i + 1];
                    i++;
                    break;
                case "--stats-kind" when i + 1 < args.Length:
                    statsKind = args[i + 1];
                    i++;
                    break;
                case "--stats-highlight" when i + 1 < args.Length:
                    statsHighlight = args[i + 1];
                    i++;
                    break;
                case "--paste-text" when i + 1 < args.Length:
                    pasteText = args[i + 1];
                    i++;
                    break;
                case "--whats-new":
                    whatsNew = true;
                    break;
                case "--paste-import":
                    pasteImport = true;
                    break;
                case "--select-first":
                    selectFirst = true;
                    break;
                case "--screenshot" when i + 1 < args.Length:
                    screenshot = args[i + 1];
                    i++;
                    break;
                case "--page" when i + 1 < args.Length:
                    _ = int.TryParse(args[i + 1], out page);
                    i++;
                    break;
                case "--height" when i + 1 < args.Length:
                    _ = int.TryParse(args[i + 1], out height);
                    i++;
                    break;
                case "--width" when i + 1 < args.Length:
                    _ = int.TryParse(args[i + 1], out width);
                    i++;
                    break;
                case "--scroll" when i + 1 < args.Length:
                    _ = double.TryParse(args[i + 1], out scroll);
                    i++;
                    break;
                case "--dialog" when i + 1 < args.Length:
                    dialog = args[i + 1];
                    i++;
                    break;
                case "--chart-preview" when i + 1 < args.Length:
                    chartPreview = args[i + 1];
                    i++;
                    break;
                case "--whole-page":
                    wholePage = true;
                    break;
case "--trace":
                    trace = true;
                    break;
                case "--show-detail":
                    showDetail = true;
                    break;
                case "--detail-preview":
                    detailPreview = true;
                    break;
                case "--toast" when i + 1 < args.Length:
                    toastText = args[i + 1];
                    break;
                case "--confirm-preview":
                    confirmPreview = true;
                    break;
                case "--toast-error" when i + 1 < args.Length:
                    toastText = args[i + 1];
                    toastIsError = true;
                    break;
                case "--section" when i + 1 < args.Length:
                    _ = int.TryParse(args[i + 1], out settingsSection);
                    i++;
                    break;
                case "--shoot-sections" when i + 1 < args.Length:
                    shootSections = args[i + 1];
                    i++;
                    break;
                case "--page-cycle" when i + 1 < args.Length:
                    _ = int.TryParse(args[i + 1], out pageCycle);
                    i++;
                    break;
                case "--open-calendar":
                    openCalendar = true;
                    break;
                case "--import-preview" when i + 1 < args.Length:
                    importPreview = args[i + 1];
                    i++;
                    break;
            }
        }

        if (root is not null)
        {
            Core.Paths.OverrideRoot(root);
        }

        Diag.Enabled = trace;
        Diag.StartTick = Environment.TickCount64;
        Diag.Trace("Main 开始");

        // 单实例：已经在运行就把已有窗口调到前台，不再开第二个（避免两个窗口抢同一份账本）
        _singleInstance = new System.Threading.Mutex(true, @"Local\LedgerBook-记账本", out var isFirst);
        if (!isFirst)
        {
            FocusRunningWindow();
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };   // 关闭主窗口即退出（否则会残留后台进程）
        try
        {
            var startupSettings = Core.Settings.Load();
            Core.Paths.ExportDirOverride = startupSettings.ExportDir;   // 设置里的自定义导出目录（空 = 默认）
            Core.Log.Enabled = startupSettings.LogEnabled;
            Core.Log.Info($"启动 记账本 v{Core.Paths.Version}　根目录={Core.Paths.Root}　主题={startupSettings.Theme}　系统={Environment.OSVersion.VersionString}　运行时={Environment.Version}");
            Core.Log.Info($"导出目录={Core.Paths.ExportDir}" + (Core.Paths.ExportDirOverride.Length > 0 ? "（设置里的自定义目录）" : "（默认）"));
            Ui.ApplyTheme(Ui.ParseTheme(Core.Settings.Load().Theme));   // 主题：浅色 / 深色 / 跟随系统
            Ui.InstallGlobalStyles();   // 圆角滚动条 / 圆角提示气泡（要按当前主题取色，所以放在后面）
            // 开发用：单独渲染某个图形，方便检查画得对不对
            if (chartPreview is not null && screenshot is not null)
            {                var sample = new List<Core.CategoryTotal>
                {
                    new() { Kind = "收入", Category = "工资", Amount = 8500m, Share = 0.83 },
                    new() { Kind = "收入", Category = "兼职", Amount = 800m, Share = 0.078 },
                    new() { Kind = "支出", Category = "医疗", Amount = 420m, Share = 0.041 },
                    new() { Kind = "支出", Category = "购物", Amount = 268m, Share = 0.026 },
                    new() { Kind = "支出", Category = "餐饮", Amount = 105.5m, Share = 0.010 },
                    new() { Kind = "支出", Category = "娱乐", Amount = 96m, Share = 0.009 },
                };
                var surface = new Window
                {
                    Title = "图表预览",
                    Width = 520,
                    Height = 380,
                    Background = Ui.Card,
                    FontFamily = Ui.FontFamily,
                };
                surface.Content = Ui.CardPanel(Charts.Draw(chartPreview, sample), padding: 14);
                var main = new MainWindow();
                surface.Show();
                main.CaptureWindow(surface, screenshot, () => app.Shutdown());
                app.Run(surface);
                return 0;
            }

            // 开发用：单独渲染「详情」卡片（浮窗内容），确认排版正确
            if (detailPreview && screenshot is not null)
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                var start = new DateOnly(today.Year, today.Month, 1);
                var end = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
                Diag.Trace("开始查询当月记录");
                var records = Core.Query.Run(new Core.QueryFilter { Start = start, End = end, Limit = 3 }).Records;
                Diag.Trace($"查询完成，{records.Count} 条");
                var sample = records.FirstOrDefault();
                if (sample is not null)
                {
                    sample.Detail = sample.Detail.Length > 0 ? sample.Detail : "和同事一起吃的，用了公司餐补，等月底一起报销。";
                    var settings = Core.Settings.Load();
                    Diag.Trace("开始构建详情卡片");
                    var card = DetailCard.Build(sample, settings, () => { });
                    Diag.Trace("详情卡片构建完成");
                    var surface = new Window
                    {
                        Title = "详情预览",
                        Width = 540,
                        Height = 620,
                        Background = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0xF5, 0xF6, 0xF8)),
                        FontFamily = Ui.FontFamily,
                        Content = card,
                    };
                    Diag.Trace("预览窗口已创建");
                    var main = new MainWindow();
                    Diag.Trace("MainWindow 已创建（只为借用截图工具）");
                    surface.Show();
                    Diag.Trace("预览窗口已显示");
                    main.CaptureWindow(surface, screenshot, () => app.Shutdown());
                    Diag.Trace("截图计时器已启动");
                    app.Run(surface);
                    Diag.Trace("app.Run 返回");
                    return 0;
                }
            }

            // 自动化验证：直接打开导入预览窗口
            if (importPreview is not null && screenshot is not null)
            {
                var helper = new MainWindow();
                if (themeTest is not null)
                {
                    // 必须在构造 ImportWindow **之前**应用主题：窗口构造时就把当时的画刷拷进控件了
                    helper.ApplyTheme(themeTest);
                }

                var importWindow = new ImportWindow(Core.Settings.Load(), importPreview);
                importWindow.Show();
                helper.CaptureWindow(importWindow, screenshot, () => app.Shutdown());
                app.Run(importWindow);
                return 0;
            }

            // 自动化：打开"粘贴文本导入"窗口并截图
            if (pasteImport && screenshot is not null)
            {
                var pasteWindow = new PasteImportWindow(Core.Settings.Load());
                if (pasteText is not null)
                {
                    pasteWindow.FillAndContinueForAutomation(pasteText);
                    Core.Log.Info("粘贴导入自动化：写入结果 " + (pasteWindow.TempPath ?? "失败"));
                }
                var helper2 = new MainWindow();
                pasteWindow.Show();
                helper2.CaptureWindow(pasteWindow, screenshot, () => app.Shutdown());
                app.Run(pasteWindow);
                return 0;
            }

            // 自动化：打开「更新内容」窗口并截图
            if (whatsNew && screenshot is not null)
            {
                var helper3 = new MainWindow();
                var whatsWindow = new WhatsNewWindow("记账本 v" + Core.Paths.Version + " 更新内容", WhatsNew.Text());
                whatsWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                whatsWindow.Show();
                helper3.CaptureWindow(whatsWindow, screenshot, () => app.Shutdown());
                app.Run(whatsWindow);
                return 0;
            }

            var window = new MainWindow { InitialPage = page, AutomationSelectFirst = selectFirst, AutomationStatsKind = statsKind, AutomationStatsCategory = statsHighlight, SuppressWhatsNew = screenshot is not null };
        if (themeTest is not null)
        {
            window.ApplyTheme(themeTest);   // 与用户在设置里点"深色/浅色"完全同一条路径（含 RebuildUi）
        }
        if (toastText is not null)
        {
            window.ShowToast(toastText, toastIsError);   // 预览：让截图/演示里能看到悬浮提示的样子
        }

        if (confirmPreview)
        {
            window.PreviewConfirm(
                "统一升级格式？",
                "要把 12 天的老格式文件升级成新格式吗？\n\n"
                + "· 升级前会把老文件备份到「备份」目录（老格式 子文件夹）；\n"
                + "· 每天都会校验一遍，校验不过就放弃那一天、保留老文件；\n"
                + "· 升级期间请不要用老版本（v0.1.0）打开账本。",
                "开始升级",
                "先不升级");
        }
            if (shootSections is not null)
            {
                window.InitialPage = 3;
                window.AfterShown = () =>
                {
                    Directory.CreateDirectory(shootSections);
                    for (var i = 0; i < 8; i++)
                    {
                        window.SettingsPage.ShowSection(i);
                        window.UpdateLayout();
                        window.CaptureNow(Path.Combine(shootSections, $"分区-{i}.png"));
                    }

                    Console.WriteLine("SHOOT SECTIONS DONE");
                    app.Shutdown();
                };
            }

            if (pageCycle > 0)
            {
                window.AfterShown = () =>
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    for (var round = 0; round < pageCycle; round++)
                    {
                        for (var i = 0; i < 4; i++)
                        {
                            window.SelectPage(i);
                        }
                    }

                    var total = watch.ElapsedMilliseconds;
                    Diag.Trace($"切页：{pageCycle * 4} 次共 {total} ms，平均 {total / (double)(pageCycle * 4):0.0} ms/次");
                };
            }
            if (settingsSection >= 0 || openCalendar)
            {
                window.InitialPage = 3;
                window.AfterShown = () =>
                {
                    if (openCalendar)
                    {
                        window.SettingsPage.OpenExportCalendar();
                    }
                    else
                    {
                        window.SettingsPage.ShowSection(settingsSection);
                    }
                };
            }

            // 自动化验证：在真实主窗口里弹一次「详情」浮窗，看看实际效果
            if (showDetail && screenshot is not null)
            {
                var today0 = DateOnly.FromDateTime(DateTime.Now);
                var start0 = new DateOnly(today0.Year, today0.Month, 1);
                var end0 = new DateOnly(today0.Year, today0.Month, DateTime.DaysInMonth(today0.Year, today0.Month));
                var newest = Core.Query.Run(new Core.QueryFilter { Start = start0, End = end0, Limit = 1 }).Records.FirstOrDefault();
                if (newest is not null)
                {
                    var st = Core.Settings.Load();
                    Diag.Trace("准备弹详情浮窗");
                    window.OverlayAfterLoad = DetailCard.Build(newest, st, window.HideOverlay);
                    Diag.Trace("详情浮窗已排队");
                }
            }
            if (height > 0 && screenshot is not null)
            {
                window.Height = height;   // 自动化截图时可以把窗口拉高，一屏看全
            }

            if (width > 0 && screenshot is not null)
            {
                window.Width = width;     // 用来检查「最小窗口有没有裁掉内容」
            }

            if (scroll > 0)
            {
                window.ScrollSettingsTo(scroll);
            }

            // 自动化验证：把「改一笔」弹窗也截下来
            if (dialog == "edit" && screenshot is not null)
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                var start = new DateOnly(today.Year, today.Month, 1);
                var end = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
                var newest = Core.Query.Run(new Core.QueryFilter { Start = start, End = end, Limit = 1 }).Records.FirstOrDefault();
                if (newest is not null)
                {
                    // 自动化路径里不给弹窗设 Owner（主窗口还没显示，WPF 不允许）
                    var editor = new RecordDialog(newest, Core.Settings.Load());
                    editor.Show();
                    window.CaptureWindow(editor, screenshot, () => app.Shutdown());
                    app.Run(window);
                    return 0;
                }
            }


            if (screenshot is not null)
            {
                window.CaptureAfterRender(screenshot, () => app.Shutdown(), wholePage);
                // 自动化兜底：万一截图流程卡住，20 秒后强制退出，避免挂在那里
                var guard = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(20)
                };
                guard.Tick += (_, _) =>
                {
                    Console.WriteLine("SCREENSHOT TIMEOUT");
                    Environment.Exit(3);
                };
                guard.Start();
            }

            app.Run(window);
            return 0;
        }
        catch (Exception exc)
        {
            // 启动失败一定要留下证据：写日志（正式环境也一样），自动化运行时再打印一份
            var log = Path.Combine(Core.Paths.AppDir, "运行错误.log");
            try
            {
                File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 启动失败：{exc}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // 日志都写不了就算了，别在这里再抛
            }

            Console.WriteLine("启动失败：" + exc);
            if (screenshot is null)
            {
                MessageBox.Show(exc.Message, "记账本启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return 1;
        }
    }
}
