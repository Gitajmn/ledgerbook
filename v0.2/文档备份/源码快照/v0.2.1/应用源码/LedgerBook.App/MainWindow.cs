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

        BuildUi();

        Loaded += (_, _) =>
        {
            SelectPage(Math.Clamp(InitialPage, 0, _pages.Count - 1));
            RefreshStatus();
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

        _userText = Ui.Label("", Ui.FontSmall, Ui.Muted);
        _userText.Text = string.IsNullOrWhiteSpace(_account.Nickname)
            ? $"用户 {_account.UserCode}"
            : $"用户 {_account.Nickname}（{_account.UserCode}）";
        _userText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_userText, 2);
        header.Children.Add(_userText);
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

    public void HideOverlay()
    {
        _overlayHost.Children.Clear();
        _overlayHost.Visibility = Visibility.Collapsed;
        _overlayHost.Tag = null;
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

    /// <summary>切换外观主题（浅色/深色/跟随系统）：保存设置 → 换调色板 → 重装全局样式 → 重建界面。</summary>
    public void ApplyTheme(string themeName)
    {
        _settings.Theme = themeName;
        _settings.Save("主题", themeName);
        Ui.ApplyTheme(Ui.ParseTheme(themeName));
        Ui.InstallGlobalStyles();
        RebuildUi();
        SelectPage(3);   // 停在设置页，方便继续调
    }

    /// <summary>改字号这类影响所有控件的设置后重建界面（不用重启）。</summary>
    public void RebuildUi()
    {
        var keep = _activePage;
        BuildUi();
        SelectPage(Math.Clamp(keep, 0, _pages.Count - 1));
        RefreshStatus();
    }

    public void SelectPage(int index)
    {
        _activePage = index;
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

    public void SetStatus(string message) => _statusText.Text = message;

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
