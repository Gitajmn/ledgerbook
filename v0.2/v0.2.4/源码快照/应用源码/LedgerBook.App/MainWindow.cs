using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>主窗口：顶部导航 + 内容区 + 状态栏。</summary>
public sealed class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly Account _account;
    private Grid _content = new();
    private TextBlock _statusText = Ui.Label("", Ui.FontSmall, Ui.Muted);
    private TextBlock _userText = Ui.Label("", Ui.FontSmall, Ui.Muted);
    private readonly List<(string Title, System.Windows.Controls.Primitives.ToggleButton Pill, UIElement Panel)> _pages = new();
    private int _activePage;

    /// <summary>启动后默认打开的页签（自动化截图用 --page 设置）。</summary>
    public int InitialPage { get; set; }

    /// <summary>自动化截图用：窗口加载完成后要弹出的浮层内容。</summary>
    public UIElement? OverlayAfterLoad { get; set; }

    /// <summary>自动化截图用：窗口加载完成后再执行一下（例如切到某个设置分区、弹出日历）。</summary>
    public Action? AfterShown { get; set; }

    public RecordPanel RecordPage { get; private set; } = null!;
    public QueryPanel QueryPage { get; private set; } = null!;
    public SummaryPanel SummaryPage { get; private set; } = null!;
    public SettingsPanel SettingsPage { get; private set; } = null!;

    /// <summary>设置页当前停在哪个分区（界面重建后要停回原处，不然会"跳回第一个分区"）。</summary>
    public int SettingsSection { get; set; }


    /// <summary>
    /// 全局滚轮：任何 ScrollViewer（含隐藏了滚动条的那些）鼠标悬停其中时，滚轮/触控板都能滚。
    /// 放在静态构造里注册一次即可，不用到处写。
    /// </summary>
    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler((sender, e) =>
            {
                if (sender is ScrollViewer scroll && scroll.ScrollableHeight > 0)
                {
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 3.0);
                    e.Handled = true;
                }
            }));
    }
    /// <summary>自绘窗口按钮（最小化/最大化/关闭）：扁平、悬停变色、关闭悬停变红。</summary>
    private static Border WindowButton(string glyph, string tip, Action onClick, bool isClose = false)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = Ui.Muted,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hit = new Border
        {
            Width = 44,
            Height = 30,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(Ui.RadiusSmall),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = text,
            ToolTip = tip,
            Tag = "chrome",   // 标记：拖标题栏时要跳过它
        };
        hit.MouseEnter += (_, _) =>
        {
            hit.Background = isClose ? Ui.Expense : Ui.Hover;
            text.Foreground = isClose ? Brushes.White : Ui.Text;
        };
        hit.MouseLeave += (_, _) =>
        {
            hit.Background = Brushes.Transparent;
            text.Foreground = Ui.Muted;
        };
        hit.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return hit;
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>点在交互控件（导航胶囊 / 窗口按钮）上时，不要当成拖标题栏。</summary>
    private static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            if (source is FrameworkElement { Tag: "chrome" })
            {
                return true;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    /// <summary>自动化用：启动后自动选中查询页明细的第一行（靠它截图验证选中配色）。</summary>
    public bool AutomationSelectFirst { get; set; }

    /// <summary>自动化截图时置 true：跳过"更新后首次打开"自动弹窗，免得干扰截图。</summary>
    public bool SuppressWhatsNew { get; set; }

    /// <summary>自动化用：启动后直接设定分类统计钻取（只看哪一类 / 高亮哪个分类）。</summary>
    public string? AutomationStatsKind { get; set; }
    public string? AutomationStatsCategory { get; set; }

    public MainWindow()
    {
        Paths.EnsureDirs();
        _settings = Settings.Load();
        _account = Config.LoadAccount();
        Ui.SetBaseFontSize(_settings.FontSize);

        Title = $"{Paths.AppName} v{Paths.Version}";
        Width = 1180;
        Height = 800;
        MinWidth = 960;
        MinHeight = 640;
        Background = Ui.Background;
        FontFamily = Ui.FontFamily;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // 清晰度（配合 app.manifest 的 PerMonitorV2）：布局对齐像素 + 文字走 Display 模式/ClearType，
        // 否则 125%/150% 缩放下 1px 细线和文字都会发虚（用户反馈"边缘有点糊"）。
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        // 去掉系统标题栏（用户要求）：最小化/最大化/关闭改到应用自己的头部。
        // 用 WindowChrome 保留"边缘拖动缩放 / 双击最大化 / 贴边"这些系统行为。
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;   // 无边框窗口最大化时别盖住任务栏
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        BuildUi();

        Loaded += (_, _) =>
        {
            SelectPage(Math.Clamp(InitialPage, 0, _pages.Count - 1));
            RefreshStatus();
            if (AutomationSelectFirst)
            {
                QueryPage.SelectFirstRow();
            }

            if (!SuppressWhatsNew && WhatsNew.ConsumeUpdatedVersion(Paths.Version))
            {
                WhatsNewWindow.Show(this);   // 更新后首次打开：弹更新内容（首次安装不弹）
            }

            if (AutomationStatsCategory is not null || AutomationStatsKind is not null)
            {
                QueryPage.ApplyStatsSelection(AutomationStatsKind ?? "全部", AutomationStatsCategory);
            }
            if (OverlayAfterLoad is not null)
            {
                ShowOverlay(OverlayAfterLoad);
                OverlayAfterLoad = null;
            }

            AfterShown?.Invoke();
            if (Diag.Enabled)
            {
                var me = System.Diagnostics.Process.GetCurrentProcess();
                me.Refresh();
                Diag.Trace($"性能：启动到界面就绪 {Environment.TickCount64 - Diag.StartTick} ms，" +
                           $"工作集 {me.WorkingSet64 / 1024 / 1024} MB，私有内存 {me.PrivateMemorySize64 / 1024 / 1024} MB，" +
                           $"托管堆 {GC.GetTotalMemory(false) / 1024 / 1024} MB，线程 {me.Threads.Count} 个");
            }
        };
    }

    private void BuildUi()
    {
        // 主题相关的窗口级属性每次重建都要重设：
        // 否则切深色时窗口背景还是浅的、字却已是浅色 —— 用户报的"背景明亮、字体看不清"就是这个。
        Background = Ui.Background;
        Foreground = Ui.Text;
        FontFamily = Ui.FontFamily;
        RecordPage = new RecordPanel(_settings, _account, this);
        QueryPage = new QueryPanel(_settings, this);
        var summaryPage = new SummaryPanel(_settings, this);
        var settingsPage = new SettingsPanel(_settings, _account, this);
        SummaryPage = summaryPage;
        SettingsPage = settingsPage;

        var root = new DockPanel { LastChildFill = true };

        // ---------- 顶部 ----------
        var header = new Grid { Margin = new Thickness(20, 16, 20, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Ui.Label(Paths.AppName, Ui.FontTitle, Ui.Text, bold: true));
        var version = Ui.Hint($"v{Paths.Version}");
        version.Margin = new Thickness(8, 4, 14, 0);
        title.Children.Add(version);

        _pages.Clear();
        var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var pills = new List<System.Windows.Controls.Primitives.ToggleButton>();
        foreach (var name in new[] { "记一笔", "查询统计", "月度汇总", "设置" })
        {
            var pill = Ui.Pill(name);
            var index = pills.Count;
            pill.Click += (_, _) => SelectPage(index);
            pills.Add(pill);
            nav.Children.Add(pill);
        }

        title.Children.Add(nav);
        Grid.SetColumn(title, 0);
        header.Children.Add(title);

        // ---------- 右侧：用户/设备信息 + 自绘窗口按钮（系统标题栏已去掉） ----------
        var rightSide = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _userText = Ui.Label("", Ui.FontSmall, Ui.Muted);
        _userText.Text = string.IsNullOrWhiteSpace(_account.Nickname)
            ? $"用户 {_account.UserCode}"
            : $"用户 {_account.Nickname}（{_account.UserCode}）";
        _userText.VerticalAlignment = VerticalAlignment.Center;
        _userText.Margin = new Thickness(0, 0, 12, 0);
        rightSide.Children.Add(_userText);

        var deviceText = Ui.Hint(string.IsNullOrWhiteSpace(_account.Device) ? "设备未命名" : $"设备 {_account.Device}");
        deviceText.VerticalAlignment = VerticalAlignment.Center;
        deviceText.Margin = new Thickness(0, 0, 14, 0);
        // 「精简顶端信息」：设备名不占一行，挂在提示里
        _userText.ToolTip = string.IsNullOrWhiteSpace(_account.Device)
            ? "设备名还没设置（账户页可改）"
            : $"设备名：{_account.Device}";
        _ = deviceText;

        rightSide.Children.Add(WindowButton("\uE921", "最小化", () => WindowState = WindowState.Minimized));
        var maximize = WindowButton("\uE922", "最大化", ToggleMaximize);
        rightSide.Children.Add(maximize);
        rightSide.Children.Add(WindowButton("\uE8BB", "关闭", Close, isClose: true));
        Grid.SetColumn(rightSide, 2);
        header.Children.Add(rightSide);

        // 标题栏区域可拖动 / 双击最大化（点在按钮、导航胶囊上时不拖）
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (IsInteractive(e.OriginalSource as DependencyObject))
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            try
            {
                DragMove();
            }
            catch (Exception)
            {
            }
        };
        header.Background = Brushes.Transparent;   // 透明也能收到点击（null 会穿透）

        // 最大化按钮的字形跟着窗口状态变（还原 ↕）
        StateChanged += (_, _) =>
        {
            if (maximize.Child is TextBlock glyph)
            {
                glyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            }
        };

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ---------- 状态栏 ----------
        _statusText = Ui.Label("", Ui.FontSmall, Ui.Muted);
        var status = new Border
        {
            Background = Ui.Card,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 8, 20, 8),
        };
        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_statusText, 0);
        var dirText = Ui.Hint($"数据目录：{Paths.Root}");
        Grid.SetColumn(dirText, 1);
        statusGrid.Children.Add(_statusText);
        statusGrid.Children.Add(dirText);
        status.Child = statusGrid;
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        // ---------- 内容区 ----------
        _content = new Grid { Margin = new Thickness(20, 0, 20, 16) };
        _pages.Add(("记一笔", pills[0], RecordPage.Root));
        _pages.Add(("查询统计", pills[1], QueryPage.Root));
        _pages.Add(("月度汇总", pills[2], summaryPage.Root));
        _pages.Add(("设置", pills[3], settingsPage.Root));
        foreach (var page in _pages)
        {
            page.Panel.Visibility = Visibility.Collapsed;
            _content.Children.Add(page.Panel);
        }

        root.Children.Add(_content);

        // 浮层：详情卡片、加载中转圈都放在这里（点遮罩/Esc 关闭）
        _overlayHost = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
        _overlayHost.MouseLeftButtonDown += (_, _) =>
        {
            if (_overlayHost.Tag is true)
            {
                HideOverlay();
            }
        };
        var shell = new Grid();
        shell.Children.Add(root);
        shell.Children.Add(_overlayHost);
        shell.Children.Add(BuildToastHost());   // 悬浮提示在最上层
        Content = shell;
        PreviewKeyDown -= OnWindowKey;
        PreviewKeyDown += OnWindowKey;
    }

    private void OnWindowKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _overlayHost.Visibility == Visibility.Visible
            && _overlayHost.Tag is true)
        {
            HideOverlay();
            e.Handled = true;
        }
    }

    /// <summary>窗口内的浮层容器（详情卡片、加载中都用它，不会弹到窗口外面去）。</summary>
    private Grid _overlayHost = new();

    /// <summary>弹一个浮层（半透明遮罩 + 居中卡片）。closable=点遮罩或按 Esc 关闭。</summary>
    public void ShowOverlay(UIElement content, bool closable = true)
    {
        _overlayHost.Children.Clear();
        _overlayHost.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x1F, 0x23, 0x29));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        _overlayHost.Children.Add(content);
        _overlayHost.Visibility = Visibility.Visible;
        _overlayHost.Tag = closable;
        _overlayHost.Cursor = closable ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Wait;
    }

    private Action? _overlayClosed;

    public void HideOverlay()
    {
        _overlayHost.Children.Clear();
        _overlayHost.Visibility = Visibility.Collapsed;
        _overlayHost.Tag = null;
        var callback = _overlayClosed;
        _overlayClosed = null;
        callback?.Invoke();      // 点遮罩 / 按 Esc 关闭时，也要让"确认弹窗"知道结果（= 取消）
    }

    /// <summary>「加载中」：转圈 + 文字，并锁住后面的操作（点不动底下的界面）。</summary>
    public void ShowBusy(string message)
    {
        var spinner = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 16 2 A 14 14 0 0 1 30 16"),
            Stroke = Ui.Accent,
            StrokeThickness = 3.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 32,
            Height = 32,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var spin = new RotateTransform(0);
        spinner.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            });

        var card = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        card.Children.Add(spinner);
        var label = Ui.Label(message, Ui.FontSubtitle, Ui.Text, bold: true);
        label.Margin = new Thickness(14, 0, 0, 0);
        card.Children.Add(label);

        ShowOverlay(Ui.CardPanel(card, padding: 22), closable: false);
    }

    public void HideBusy() => HideOverlay();

    /// <summary>
    /// 应用内确认弹窗（**不用系统 MessageBox**）：椭圆角卡片 + 背景变暗 + 点遮罩/按 Esc = 取消。
    /// 用嵌套消息循环实现"同步等待"，所以调用处写法不变（if (!Confirm(...)) return;）。
    /// </summary>
    public bool Confirm(string title, string message, string yesText = "确认", string noText = "取消")
    {
        var result = false;
        var frame = new System.Windows.Threading.DispatcherFrame();
        void Close(bool ok)
        {
            result = ok;
            _overlayClosed = null;
            HideOverlay();
            frame.Continue = false;
        }

        _overlayClosed = () =>
        {
            result = false;
            frame.Continue = false;
        };

        ShowOverlay(BuildConfirmCard(title, message, yesText, noText, Close), closable: true);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        return result;
    }

    /// <summary>预览用：把确认卡片画出来但不进入等待（给自动化截图）。</summary>
    public void PreviewConfirm(string title, string message, string yesText = "确认", string noText = "取消")
        => ShowOverlay(BuildConfirmCard(title, message, yesText, noText, _ => HideOverlay()), closable: true);

    private FrameworkElement BuildConfirmCard(string title, string message, string yesText, string noText, Action<bool> close)
    {
        var stack = new StackPanel { Width = 400 };

        var head = Ui.Label(title, Ui.FontSubtitle + 4, Ui.Text, bold: true);
        stack.Children.Add(head);

        var body = new TextBlock
        {
            Text = message,
            FontFamily = Ui.FontFamily,
            FontSize = Ui.FontBody,
            Foreground = Ui.Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 18),
            LineHeight = Ui.FontBody * 1.6,
        };
        stack.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = Ui.GhostButton(noText, () => close(false), 34);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        var ok = Ui.PrimaryButton(yesText, () => close(true), 34);
        ok.IsDefault = true;      // 回车 = 确认
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        var card = new Border
        {
            Background = Ui.Card,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22, 20, 22, 18),
            Child = stack,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 3,
                Opacity = 0.26,
                Color = Colors.Black,
            },
        };

        // 点卡片本身不要穿透到遮罩（否则一点正文就当"取消"）
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return card;
    }

    /// <summary>切换外观主题（浅色/深色/跟随系统）：保存设置 → 换调色板 → 重装全局样式 → 重建界面。</summary>
    public void ApplyTheme(string themeName)
    {
        _settings.Theme = themeName;
        _settings.Save("主题", themeName);
        Ui.ApplyTheme(Ui.ParseTheme(themeName));
        Ui.InstallGlobalStyles();
        ApplyWindowFrame();   // 圆角边框颜色跟着主题走
        RebuildUi();
        SelectPage(3);   // 停在设置页，方便继续调
    }

    // ---------------- 无边框窗口的圆角（Windows 11 的 DWM） ----------------

    private const int DwmWindowCornerPreference = 33;   // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int DwmBorderColor = 34;              // DWMWA_BORDER_COLOR
    private const int DwmRound = 2;                     // DWMWCP_ROUND

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 给**最外层窗口**加圆角（用户要求）：用 DWM 的原生圆角，
    /// 所以不需要 AllowsTransparency（不会牺牲硬件加速、也不影响 WindowChrome 的缩放/贴边）。
    /// 顺手把 1px 外框调成跟主题一致；Windows 10 不支持这个属性 → 静默忽略（窗口仍是直角）。
    /// </summary>
    private void ApplyWindowFrame()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var round = DwmRound;
            _ = DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref round, sizeof(int));

            // 外框颜色**交回系统**（DWMWA_COLOR_DEFAULT = 0xFFFFFFFF）。
            // 教训（2026-10-13 用户反馈"只有外框糊"）：无边框窗口上 DWM 画的这条 1px 边框本身是抗锯齿的，
            // 再给它指定低对比度的浅灰（原来的 Ui.LineColor）→ 视觉上就是"糊"。用系统默认最锐利。
            var defaultColor = unchecked((int)0xFFFFFFFF);
            _ = DwmSetWindowAttribute(hwnd, DwmBorderColor, ref defaultColor, sizeof(int));
        }
        catch (Exception)
        {
            // 老系统没有这些属性：不影响使用
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowFrame();
    }

    /// <summary>改字号这类影响所有控件的设置后重建界面（不用重启）。</summary>
    public void RebuildUi()
    {
        var keep = _activePage;
        BuildUi();
        SelectPage(Math.Clamp(keep, 0, _pages.Count - 1));
        RefreshStatus();
    }

    private static string PageName(int index) => index switch
    {
        0 => "记一笔",
        1 => "查询统计",
        2 => "月度汇总",
        3 => "设置",
        _ => "第 " + (index + 1) + " 页",
    };

    public void SelectPage(int index)
    {
        // 用户要求：从别的页面（如月度汇总）回到设置时，设置页回到**第一个分区**；
        // 但已经在设置页里改外观/字号导致的界面重建不算"重新进入"，仍停在原分区。
        if (index == 3 && _activePage != 3 && SettingsPage is not null)
        {
            SettingsPage.ShowSection(0);
        }

        _activePage = index;
        Core.Log.Info("切到「" + PageName(index) + "」");
        for (var i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            page.Pill.IsChecked = i == index;
            page.Pill.Background = i == index ? Ui.Accent : Ui.Transparent;
            page.Pill.Foreground = i == index ? Brushes.White : Ui.Muted;
            page.Panel.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        if (index == 1)
        {
            QueryPage.Reload();
        }
        else if (index == 2)
        {
            SummaryPage.Reload();
        }

        RefreshStatus();
    }

    /// <summary>状态栏：本月概况。任何一处改了数据后调用。</summary>
    public void RefreshStatus()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = new DateOnly(today.Year, today.Month, 1);
        var end = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        var month = Query.Run(new QueryFilter { Start = start, End = end, Limit = 0 });
        var symbol = _settings.CurrencySymbol;
        _statusText.Text = $"{Paths.MonthName(today.Year, today.Month)}：{month.TotalCount} 笔"
                           + $"　|　收入 {Valid.Money(month.Income.ToString(CultureInfo.InvariantCulture), symbol)}"
                           + $"　|　支出 {Valid.Money(month.Expense.ToString(CultureInfo.InvariantCulture), symbol)}"
                           + $"　|　结余 {Valid.Money(month.Balance.ToString(CultureInfo.InvariantCulture), symbol)}";
        _statusText.Foreground = Ui.Text;
    }

    public void SetStatus(string message)
    {
        _statusText.Text = message;
        // 左下角那行字用户说"根本看不到"（2026-10-10）→ 同时弹一个圆角浮层，点「确认」或 5 秒后收起
        ShowToast(message);
    }

    // ---------------------------------------------------------------- 悬浮提示（Toast）

    private Border? _toastCard;
    private TextBlock? _toastText;
    private TextBlock? _toastIcon;
    private bool _toastIsError;
    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    /// <summary>
    /// 圆角悬浮提示：底部居中的一张卡片 + 「确认」按钮。
    /// 刻意**不铺全屏遮罩**（Grid 没有 Background 就不参与命中测试）：所以它不挡鼠标、不吃事件，
    /// 不会像模态弹窗那样把界面卡住；点「确认」立即关闭，没人点就 5 秒后自己收起。
    /// </summary>
    private FrameworkElement BuildToastHost()
    {
        _toastText = new TextBlock
        {
            FontFamily = Ui.FontFamily,
            FontSize = Ui.FontBody,
            Foreground = Ui.Text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var icon = Ui.Label("✓", Ui.FontSubtitle, Ui.Accent, bold: true);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        _toastIcon = icon;

        var confirm = Ui.PrimaryButton("确认", HideToast, 28);
        confirm.Margin = new Thickness(14, 0, 0, 0);
        confirm.VerticalAlignment = VerticalAlignment.Center;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(_toastText);
        row.Children.Add(confirm);

        _toastCard = new Border
        {
            Background = Ui.Card,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Child = row,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 56),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.20,
                Color = Colors.Black,
            },
        };

        var host = new Grid();
        host.Children.Add(_toastCard);
        return host;
    }

    /// <summary>普通提示（✓、5 秒自动收起）。</summary>
    public void ShowToast(string message) => ShowToast(message, isError: false);

    /// <summary>报错提示（红色、**不自动消失**，必须点「确认」才关；别人发普通提示也不会把它顶掉）。</summary>
    public void ShowError(string message)
    {
        Core.Log.Error(message);          // 所有应用内报错都进运行日志（用户发日志就能定位）
        ShowToast(message, isError: true);
    }

    public void ShowToast(string message, bool isError)
    {
        try
        {
            if (_toastCard is null || _toastText is null || _toastIcon is null)
            {
                return;
            }

            // 正在显示报错时，普通提示不许把它顶掉（报错得让人看见；状态栏那行仍然会更新）
            if (_toastIsError && !isError)
            {
                return;
            }

            _toastIsError = isError;
            _toastText.Text = message;
            _toastIcon.Text = isError ? "!" : "✓";
            _toastIcon.Foreground = isError ? Ui.Expense : Ui.Accent;
            _toastCard.BorderBrush = isError ? Ui.Expense : Ui.Line;
            _toastCard.Visibility = Visibility.Visible;

            _toastTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            _toastTimer.Stop();
            if (isError)
            {
                return;   // 报错不自动收起
            }

            _toastTimer.Tick -= OnToastTick;
            _toastTimer.Tick += OnToastTick;
            _toastTimer.Start();
        }
        catch (Exception)
        {
            // 提示本身绝不能把界面搞崩
        }
    }

    private void OnToastTick(object? sender, EventArgs e) => HideToast();

    public void HideToast()
    {
        try
        {
            _toastTimer?.Stop();
            _toastIsError = false;
            if (_toastCard is not null)
            {
                _toastCard.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>数据变了：刷新状态栏和查询页。</summary>
    public void NotifyDataChanged()
    {
        RefreshStatus();
        QueryPage.Reload();
    }

    /// <summary>自动化截图时：把设置页滚到指定位置（看长页面的下半部分）。</summary>
    public void ScrollSettingsTo(double offset)
    {
        SettingsPage.ContentScroll.ScrollToVerticalOffset(offset);
        SettingsPage.ContentScroll.UpdateLayout();
    }

    /// <summary>立刻把窗口渲染成 PNG（进程内连拍用，不需要计时器）。</summary>
    public void CaptureNow(string path)
    {
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            UpdateLayout();
            var source = PresentationSource.FromVisual(this);
            var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(ActualWidth * dpiX), (int)Math.Ceiling(ActualHeight * dpiY),
                96 * dpiX, 96 * dpiY, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            encoder.Save(stream);
            Console.WriteLine($"SHOT OK {path}");
        }
        catch (Exception exc)
        {
            Console.WriteLine("SHOT FAIL " + exc.Message);
        }
    }

    /// <summary>自动化验证：渲染任意窗口到 PNG（例如弹窗）。</summary>
    public void CaptureWindow(Window target, string path, Action done)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                target.UpdateLayout();
                var source = PresentationSource.FromVisual(target);
                var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(Math.Max(1, target.ActualWidth) * dpiX),
                    (int)Math.Ceiling(Math.Max(1, target.ActualHeight) * dpiY),
                    96 * dpiX, 96 * dpiY, PixelFormats.Pbgra32);
                bitmap.Render(target);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var stream = File.Create(path))
                {
                    encoder.Save(stream);
                }

                Console.WriteLine($"SCREENSHOT OK {path}");
            }
            catch (Exception exc)
            {
                Console.WriteLine("SCREENSHOT FAIL " + exc);
            }

            done();
        };
        timer.Start();
    }

    /// <summary>给自动化验证用：渲染完成后截图并回调（正常使用时不会走到这里）。</summary>
    public void CaptureAfterRender(string path, Action done, bool wholePage = false)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var source = PresentationSource.FromVisual(this);
                var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(ActualWidth * dpiX),
                    (int)Math.Ceiling(ActualHeight * dpiY),
                    96 * dpiX, 96 * dpiY, PixelFormats.Pbgra32);
                bitmap.Render(this);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var stream = File.Create(path))
                {
                    encoder.Save(stream);
                }

                Console.WriteLine($"SCREENSHOT OK {path}");
            }
            catch (Exception exc)
            {
                Console.WriteLine("SCREENSHOT FAIL " + exc);
            }

            done();
        };
        timer.Start();
    }
}
