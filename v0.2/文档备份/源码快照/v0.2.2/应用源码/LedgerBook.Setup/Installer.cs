using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LedgerBook.Setup;

/// <summary>设计令牌：与主程序设置页同一套观感（圆角 6/8/12、同一组颜色与字体）。</summary>
public static class Theme
{
    public static readonly Color Accent = Color.FromRgb(0x4D, 0x6B, 0xFE);
    public static readonly Color AccentHover = Color.FromRgb(0x3F, 0x5C, 0xEA);
    public static readonly Color AccentPressed = Color.FromRgb(0x35, 0x4F, 0xD6);
    public static readonly Color Bg = Color.FromRgb(0xF5, 0xF6, 0xF8);
    public static readonly Color Card = Colors.White;
    public static readonly Color Line = Color.FromRgb(0xE2, 0xE5, 0xEA);
    public static readonly Color Text = Color.FromRgb(0x1F, 0x23, 0x29);
    public static readonly Color Muted = Color.FromRgb(0x8A, 0x90, 0x99);
    public static readonly Color Secondary = Color.FromRgb(0xF2, 0xF3, 0xF5);
    public static readonly Color SecondaryHover = Color.FromRgb(0xE9, 0xEB, 0xEF);

    public static readonly FontFamily Font = new("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

    public static Brush Brush(Color color) { var b = new SolidColorBrush(color); b.Freeze(); return b; }

    public static TextBlock Label(string text, double size, Color color, bool bold = false) => new()
    {
        Text = text,
        FontFamily = Font,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = Brush(color),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>药丸按钮（参考图那种圆角大按钮）。</summary>
    public static Button Pill(string text, Action action, bool primary = true, double height = 46, double fontSize = 16)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(height / 2));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var button = new Button
        {
            Content = text,
            Height = height,
            FontFamily = Font,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(height, 0, height, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(0),
            Template = template,
        };
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, primary ? Brush(Accent) : Brush(Secondary)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, primary ? Brushes.White : Brush(Text)));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, primary ? Brush(AccentHover) : Brush(SecondaryHover)));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, primary ? Brush(AccentPressed) : Brush(SecondaryHover)));
        style.Triggers.Add(pressed);
        button.Style = style;
        AddPressAnimation(button);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>按下缩放动画（0.96），点击手感更明显。</summary>
    public static void AddPressAnimation(Button button)
    {
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        button.RenderTransform = scale;
        button.PreviewMouseLeftButtonDown += (_, _) =>
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(70)));
        button.PreviewMouseLeftButtonUp += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
        };
        button.MouseLeave += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
        };
    }

    /// <summary>描边药丸按钮（取消安装用）：白底 + 描边，和主色的「安装」明显区分，悬停转柔和红。</summary>
    public static Button PillOutline(string text, Action action, double height = 38, double fontSize = 13)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(height / 2));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var button = new Button
        {
            Content = text,
            Height = height,
            FontFamily = Font,
            FontSize = fontSize,
            Padding = new Thickness(height, 0, height, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = template,
        };
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Card)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(Text)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(Line)));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(Color.FromRgb(0xFD, 0xEC, 0xEC))));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, Brush(Color.FromRgb(0xC0, 0x39, 0x35))));
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(Color.FromRgb(0xF0, 0xB9, 0xB7))));
        style.Triggers.Add(hover);
        button.Style = style;
        AddPressAnimation(button);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>圆角输入框（和设置页输入框同款）。</summary>
    public static (Border Shell, TextBox Box) Field(string text, double width, double height = 38)
    {
        var box = new TextBox
        {
            Text = text,
            FontFamily = Font,
            FontSize = 12,
            Foreground = Brush(Text),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Width = width,
        };
        var shell = new Border
        {
            Background = Brush(Card),
            BorderBrush = Brush(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Height = height,
            Child = box,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        box.GotFocus += (_, _) => shell.BorderBrush = Brush(Accent);
        box.LostFocus += (_, _) => shell.BorderBrush = Brush(Line);
        return (shell, box);
    }

    /// <summary>圆角进度条：用两列星号宽度表示百分比，天生圆角矢量，不做像素换算。</summary>
    public sealed class RoundedProgress
    {
        private readonly Grid _grid = new();
        private readonly ColumnDefinition _done = new();
        private readonly ColumnDefinition _rest = new();

        public FrameworkElement Root => _grid;

        public RoundedProgress()
        {
            _grid.ColumnDefinitions.Add(_done);
            _grid.ColumnDefinitions.Add(_rest);
            _grid.Height = 8;
            var track = new Border
            {
                Background = Brush(Color.FromRgb(0xE6, 0xE8, 0xEC)),
                CornerRadius = new CornerRadius(4),
            };
            var fill = new Border
            {
                Background = Brush(Accent),
                CornerRadius = new CornerRadius(4),
            };
            Grid.SetColumn(track, 0);
            Grid.SetColumnSpan(track, 2);
            Grid.SetColumn(fill, 0);
            _grid.Children.Add(track);
            _grid.Children.Add(fill);
            Value = 0;
        }

        public double Value
        {
            set
            {
                var pct = Math.Clamp(value, 0, 100);
                _done.Width = new GridLength(pct, GridUnitType.Star);
                _rest.Width = new GridLength(100 - pct, GridUnitType.Star);
            }
        }
    }

    public static FrameworkElement Spinner(double size = 30)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 16 2 A 14 14 0 0 1 30 16"),
            Stroke = Brush(Accent),
            StrokeThickness = 3.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = size,
            Height = size,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var rotate = new RotateTransform(0);
        path.RenderTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        return path;
    }

    /// <summary>应用图标（内嵌资源）。</summary>
    public static FrameworkElement AppIcon(double size)
    {
        try
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LedgerBook.Setup.appicon.ico");
            if (stream is not null)
            {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                return new Image { Source = frame, Width = size, Height = size, Stretch = Stretch.Uniform };
            }
        }
        catch (Exception)
        {
            // 取不到就留空
        }

        return new Border { Width = size, Height = size };
    }
}

public static class Program
{
    public const string AppName = "记账本";
    public const string Version = "2.0.1";
    public const string ExeName = "记账本.exe";

    [STAThread]
    public static int Main(string[] args)
    {
        // 从解包器的工作目录（lb-setup）里跑起来时，按住"我在运行"的锁文件：
        // 解包器留下的清理脚本靠它判断向导有没有退出（详见 WorkDirLock 的说明）。
        WorkDirLock.AcquireIfRunningFromWorkDir();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            if (args.Length > 1 && args[0].Equals("--shot", StringComparison.OrdinalIgnoreCase))
            {
                var win = new InstallerWindow();
                var state = args.Length > 2 ? int.Parse(args[2]) : 0;
                if (state == 1)
                {
                    win.ShowProgressForDemo();
                }
                else if (state == 2)
                {
                    win.ShowDoneForDemo();
                }

                win.Show();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    try
                    {
                        var bmp = new RenderTargetBitmap((int)win.ActualWidth, (int)win.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bmp.Render(win);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bmp));
                        using var fs = File.Create(args[1]);
                        encoder.Save(fs);
                        Console.WriteLine("SHOT OK " + args[1]);
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine("SHOT FAIL " + exc.Message);
                    }

                    app.Shutdown();
                };
                timer.Start();
                app.Run(win);
                return 0;
            }

            if (IsUninstallInvocation(args))
            {
                return Uninstaller.Run();
            }

            // 测试/自动化用：不走界面直接装到指定目录。
            // 安装包自检靠它验证"从安装包尾部切负载 → 解到目标目录"这条链（GUI 没法在沙箱里点）。
            var installTarget = ArgumentValue(args, "--install-to");
            if (!string.IsNullOrEmpty(installTarget))
            {
                var window = new InstallerWindow(PackagePathArgument(args));
                return window.RunHeadlessInstall(installTarget);
            }

            app.Run(new InstallerWindow(PackagePathArgument(args)));
            return 0;
        }
        catch (Exception exc)
        {
            MessageBox.Show(exc.Message, "安装程序出错", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    /// <summary>取 `--名字 值` 形式参数的值（取最后一个，没给返回 null）。</summary>
    private static string ArgumentValue(string[] args, string name)
    {
        string value = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = args[i + 1];
            }
        }

        return value;
    }

    /// <summary>
    /// 解包器会用 `--setup "&lt;安装包路径&gt;"` 把安装包自己的路径传进来：
    /// 装了 .NET 的机器上向导直接从安装包尾部切负载，不用先把 146MB 运行时解出来。
    /// </summary>
    private static string PackagePathArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--setup", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--package", StringComparison.OrdinalIgnoreCase))
            {
                var path = args[i + 1];
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 是不是在跑卸载：① 参数带 /uninstall、--uninstall、/U；② 或者这个 exe 的文件名以「卸载」开头
    /// （安装时会把向导本体复制成「卸载记账本.exe」，用户双击它就该卸载，而不是又开一遍安装向导）。
    /// </summary>
    private static bool IsUninstallInvocation(string[] args)
    {
        foreach (var arg in args)
        {
            var a = (arg ?? string.Empty).Trim();
            if (a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-uninstall", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/u", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        try
        {
            var self = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            var name = Path.GetFileNameWithoutExtension(self ?? string.Empty);
            return name.StartsWith("卸载", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string DefaultTarget() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static bool HasDotNetDesktop()
    {
        try
        {
            var shared = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared",
                "Microsoft.WindowsDesktop.App");
            return Directory.Exists(shared) &&
                   Directory.EnumerateDirectories(shared).Any(dir =>
                       Path.GetFileName(dir).StartsWith("8.", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// 安装向导（矩形紧凑窗口，560×312，观感与主程序设置页一致）：
///   上排：左应用名 + 中图标 + 右药丸「安装 ›」
///   下面：圆角路径框（+ 选择…）+ 路径提示 + 桌面快捷方式
///   安装中：转圈 + 圆角进度条 + **取消安装**（真的能中止并清理已写入的文件）
/// </summary>
public sealed class InstallerWindow : Window
{
    private readonly TextBox _pathBox;
    private readonly CheckBox _desktopBox;
    private readonly CheckBox _trustBox;
    private readonly StackPanel _ready = new();
    private readonly StackPanel _progress = new();
    private readonly StackPanel _done = new();
    private readonly TextBlock _status = Theme.Label("准备中…", 12, Theme.Muted);
    private readonly TextBlock _doneNote = Theme.Label(string.Empty, 12, Theme.Muted);
    private readonly Theme.RoundedProgress _bar = new();
    private readonly Grid _host = new();
    private readonly Button _installButton;
    private readonly string _packagePath;
    private volatile bool _cancelRequested;
    private volatile bool _installFailed;

    public InstallerWindow(string packagePath = null)
    {
        _packagePath = packagePath;
        Title = $"{Program.AppName} {Program.Version} 安装";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Theme.Font;
        Background = Theme.Brush(Theme.Bg);

        // ---------- 上排 ----------
        var header = new Grid { Margin = new Thickness(20, 14, 20, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(Theme.Label(Program.AppName, 32, Theme.Text, bold: true));
        var versionLabel = Theme.Label($"版本 {Program.Version}", 12, Theme.Muted);
        versionLabel.Margin = new Thickness(0, 2, 0, 0);
        nameStack.Children.Add(versionLabel);
        Grid.SetColumn(nameStack, 0);
        header.Children.Add(nameStack);

        var icon = Theme.AppIcon(92);
        icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        Grid.SetColumn(icon, 1);
        header.Children.Add(icon);

        _installButton = Theme.Pill("安装 ›", StartInstall);
        _installButton.HorizontalAlignment = HorizontalAlignment.Right;
        _installButton.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_installButton, 2);
        header.Children.Add(_installButton);

        // ---------- 路径 ----------
        var pathSection = new StackPanel { Margin = new Thickness(20, 14, 20, 0) };
        pathSection.Children.Add(Theme.Label("安装到", 12, Theme.Muted, bold: true));

        var pathRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
        var browse = Theme.Pill("选择…", PickFolder, primary: false, height: 38, fontSize: 12);
        browse.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        var (shell, box) = Theme.Field(Program.DefaultTarget(), 360, 38);
        _pathBox = box;
        pathRow.Children.Add(shell);
        pathSection.Children.Add(pathRow);

        var hint = Theme.Label(
            "建议装在 D 盘或用户目录；装在 C:\\Program Files 时数据会自动放到「文档\\记账本」。",
            11, Theme.Muted);
        hint.Margin = new Thickness(0, 6, 0, 0);
        pathSection.Children.Add(hint);

        _desktopBox = new CheckBox
        {
            Content = "创建桌面快捷方式",
            IsChecked = true,
            FontFamily = Theme.Font,
            FontSize = 12,
            Foreground = Theme.Brush(Theme.Text),
            Margin = new Thickness(0, 10, 0, 0),
        };
        pathSection.Children.Add(_desktopBox);

        _trustBox = new CheckBox
        {
            Content = "信任签名证书（不再提示“未知发布者”）",
            IsChecked = true,
            FontFamily = Theme.Font,
            FontSize = 12,
            Foreground = Theme.Brush(Theme.Text),
            Margin = new Thickness(0, 6, 0, 0),
        };
        pathSection.Children.Add(_trustBox);
        var trustHint = Theme.Label(
            "只在这台电脑上生成一张属于 ajmn 的签名证书并信任它，用来给装好的程序签名；不联网、不上传。",
            11, Theme.Muted);
        trustHint.Margin = new Thickness(0, 4, 0, 0);
        pathSection.Children.Add(trustHint);

        _ready.Children.Add(pathSection);

        // ---------- 安装中（含「取消安装」）----------
        var busy = new StackPanel { Margin = new Thickness(20, 22, 20, 18) };
        var busyRow = new StackPanel { Orientation = Orientation.Horizontal };
        busyRow.Children.Add(Theme.Spinner());
        var busyText = Theme.Label("正在安装…", 17, Theme.Text, bold: true);
        busyText.Margin = new Thickness(12, 0, 0, 0);
        busyRow.Children.Add(busyText);
        busy.Children.Add(busyRow);

        _bar.Root.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 18, 0, 0));
        busy.Children.Add(_bar.Root);

        _status.Margin = new Thickness(0, 10, 0, 0);
        busy.Children.Add(_status);

        var cancelRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        cancelRow.Children.Add(Theme.PillOutline("取消安装", CancelInstall));
        busy.Children.Add(cancelRow);

        _progress.Visibility = Visibility.Collapsed;
        _progress.Children.Add(busy);

        // ---------- 完成 ----------
        var donePanel = new StackPanel { Margin = new Thickness(20, 26, 20, 20) };
        donePanel.Children.Add(Theme.Label("安装完成", 26, Theme.Text, bold: true));
        var doneHint = Theme.Label("桌面和开始菜单里都能找到「记账本」。数据目录与备份位置可在设置里查看。", 13, Theme.Muted);
        doneHint.Margin = new Thickness(0, 8, 0, 0);
        donePanel.Children.Add(doneHint);
        var launchBox = new CheckBox
        {
            Content = "立刻启动记账本",
            IsChecked = true,
            FontFamily = Theme.Font,
            FontSize = 13,
            Foreground = Theme.Brush(Theme.Text),
            Margin = new Thickness(0, 14, 0, 0),
        };
        donePanel.Children.Add(launchBox);

        // 卸载登记 / 签名证书的结果如实写在这里（写不进去也要让用户知道，而不是靠猜）
        _doneNote.Margin = new Thickness(0, 12, 0, 0);
        _doneNote.TextWrapping = TextWrapping.Wrap;
        _doneNote.Visibility = Visibility.Collapsed;
        donePanel.Children.Add(_doneNote);
        var finishRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        finishRow.Children.Add(Theme.Pill("完成", () =>
        {
            if (launchBox.IsChecked == true)
            {
                Launch();
            }

            Close();
        }, height: 40, fontSize: 14));
        donePanel.Children.Add(finishRow);
        _done.Visibility = Visibility.Collapsed;
        _done.Children.Add(donePanel);

        _host.Children.Add(_ready);
        _host.Children.Add(_progress);
        _host.Children.Add(_done);
        var rootPanel = new StackPanel();
        rootPanel.Children.Add(header);
        rootPanel.Children.Add(_host);
        Content = rootPanel;
    }

    public void ShowProgressForDemo()
    {
        _ready.Visibility = Visibility.Collapsed;
        _done.Visibility = Visibility.Collapsed;
        _progress.Visibility = Visibility.Visible;
        SetInstallingState(true);
        _bar.Value = 62;
        _status.Text = "正在解压文件…（380/612）";
    }

    public void ShowDoneForDemo()
    {
        _ready.Visibility = Visibility.Collapsed;
        _progress.Visibility = Visibility.Collapsed;
        _done.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 不走界面，直接把程序装到 target（给安装包自检用：验证"从安装包尾部切负载 → 解到目标目录"
    /// 以及"注册表被拒也不让安装失败"）。返回 0 成功、1 失败。
    /// </summary>
    public int RunHeadlessInstall(string target)
    {
        DoInstall(target, Program.HasDotNetDesktop(), trustCertificate: false);
        return _installFailed ? 1 : 0;
    }

    private void PickFolder()
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择安装位置", InitialDirectory = _pathBox.Text.Trim() };
            if (dialog.ShowDialog(this) == true)
            {
                _pathBox.Text = Path.Combine(dialog.FolderName, Program.AppName);
            }
        }
        catch (Exception)
        {
            // 老系统没有这个对话框就手输路径
        }
    }

    private void Launch()
    {
        try
        {
            var dir = _pathBox.Text.Trim();
            Process.Start(new ProcessStartInfo { FileName = Path.Combine(dir, Program.ExeName), WorkingDirectory = dir, UseShellExecute = true });
        }
        catch (Exception)
        {
            // 启动失败不影响安装结果
        }
    }

    private void StartInstall()    {
        var target = _pathBox.Text.Trim();
        if (target.Length == 0)
        {
            MessageBox.Show(this, "请先选择安装位置。", "还差一步", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 目标路径预检：先试着建目录，不可写就给中文提示并推荐几个可写位置
        if (!CanWrite(target))
        {
            var good = new List<string>();
            foreach (var candidate in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "记账本"),
                         Program.DefaultTarget(),
                         Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "记账本"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "记账本"),
                     })
            {
                if (CanWrite(candidate) && !good.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    good.Add(candidate);
                }
            }

            var hint = good.Count > 0
                ? "\n\n可以改用这些位置（我已验证可写）：\n· " + string.Join("\n· ", good)
                : "\n\n这台电脑上常见位置都写不进去，可能是安全软件限制了本程序创建文件夹。";
            MessageBox.Show(this, $"无法写入安装位置：\n{target}{hint}\n\n提示：装在系统盘的程序目录（如 C:\\Program Files）需要管理员权限，建议装在 D 盘或用户目录。",
                "这个位置不能写入", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _cancelRequested = false;
        _ready.Visibility = Visibility.Collapsed;
        _progress.Visibility = Visibility.Visible;
        SetInstallingState(true);
        var useSlim = Program.HasDotNetDesktop();
        var trustCertificate = _trustBox.IsChecked == true;
        var worker = new System.Threading.Thread(() => DoInstall(target, useSlim, trustCertificate)) { IsBackground = true };
        worker.Start();
    }

    /// <summary>取消安装：置标志，工作线程会在下一步检查并中止、清理已写入的文件。</summary>
    private void CancelInstall()
    {
        var answer = MessageBox.Show(this, "是否取消安装？\\n\\n已写入的文件会被清理，不会留下半个程序。",
            "取消安装", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _cancelRequested = true;
        _status.Text = "正在取消，请稍候…";
    }

    private void DoInstall(string target, bool useSlim, bool trustCertificate)
    {
        var wroteTarget = !Directory.Exists(target);
        var extracted = new List<string>();
        try
        {
            Report("检查运行环境…", 5);
            Report(useSlim ? "这台电脑已装 .NET，只装记账本本体…" : "这台电脑没装 .NET，正在一起安装运行时…", 12);
            if (Cancelled())
            {
                FinishCancel(target, wroteTarget, extracted);
                return;
            }

            Directory.CreateDirectory(target);
            var payloadName = useSlim ? "payload-slim.zip" : "payload-full.zip";

            // 负载的取用顺序：
            //   ① 外壳解包器解到本程序旁边的负载（没装 .NET 时走这条：运行时本来就要解出来）
            //   ② 直接从**安装包尾部**切出来（装了 .NET 时走这条：向导自己就能跑，
            //      不必先把 146MB 运行时解到 lb-setup —— 又慢又容易被占用锁住）
            //   ③ 内嵌资源（备用，绝不写 %TEMP%）
            var besidePayload = Path.Combine(AppContext.BaseDirectory, payloadName);
            string zipPath;
            bool deleteAfterExtract;
            if (File.Exists(besidePayload))
            {
                zipPath = besidePayload;
                deleteAfterExtract = false;
            }
            else if (!string.IsNullOrEmpty(_packagePath) && PackageTail.Extract(_packagePath, payloadName, besidePayload + ".tmp"))
            {
                zipPath = besidePayload + ".tmp";
                deleteAfterExtract = true;
                Report("正在从安装包里取出文件…", 30);
            }
            else
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"LedgerBook.Setup.{payloadName}");
                if (stream is null)
                {
                    throw new InvalidOperationException($"安装包里缺少负载文件（{payloadName}）。请使用完整的安装包。");
                }

                zipPath = besidePayload + ".tmp";
                using (var file = File.Create(zipPath))
                {
                    stream.CopyTo(file);
                }

                deleteAfterExtract = true;
            }

            Report("正在解压文件…", 35);
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var total = archive.Entries.Count;
                var index = 0;
                foreach (var entry in archive.Entries)
                {
                    if (Cancelled())
                    {
                        File.Delete(zipPath);
                        FinishCancel(target, wroteTarget, extracted);
                        return;
                    }

                    var destination = Path.Combine(target, entry.FullName);
                    if (entry.Name.Length == 0)
                    {
                        Directory.CreateDirectory(destination);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        entry.ExtractToFile(destination, overwrite: true);
                        extracted.Add(destination);
                    }

                    index++;
                    if (index % 20 == 0)
                    {
                        Report($"正在解压文件…（{index}/{total}）", 35 + (int)(45.0 * index / total));
                    }
                }
            }

            if (deleteAfterExtract)
            {
                try
                {
                    File.Delete(zipPath);     // 从安装包切出来/内嵌写出来的临时 zip，用完就删
                }
                catch (Exception)
                {
                    // 删不掉不影响安装（工作目录里的这些文件由解包器留下的清理脚本收拾）
                }
            }

            if (Cancelled())
            {
                FinishCancel(target, wroteTarget, extracted);
                return;
            }

            // 先放好卸载器，再建快捷方式（桌面/开始菜单里同时也放一个"卸载记账本"，
            // 因为有些机器根本不让写注册表，"应用和功能"里看不到我们）
            Report("创建快捷方式…", 85);
            var exePath = Path.Combine(target, Program.ExeName);
            var uninstaller = Path.Combine(target, "卸载记账本.exe");
            File.Copy(Process.GetCurrentProcess().MainModule!.FileName, uninstaller, overwrite: true);
            extracted.Add(uninstaller);

            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "程序");
            Shortcut.Create(Path.Combine(startMenu, $"{Program.AppName}.lnk"), exePath, target);
            Shortcut.Create(Path.Combine(startMenu, "卸载记账本.lnk"), uninstaller, target);
            if (_desktopBox.Dispatcher.Invoke(() => _desktopBox.IsChecked == true))
            {
                Shortcut.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    $"{Program.AppName}.lnk"), exePath, target);
            }

            Report("登记卸载信息…", 92);
            var registryNote = Registry.Install(target, uninstaller, exePath);   // 写不进去也不让安装失败
            var trustNote = "未配置（安装时未勾选）";
            if (trustCertificate)
            {
                Report("配置签名证书…", 95);
                trustNote = CertificateSetup.Trust(exePath);
            }

            File.WriteAllText(Path.Combine(target, "安装信息.txt"),
                $"{Program.AppName} {Program.Version}{Environment.NewLine}安装位置：{target}{Environment.NewLine}"
                + $"安装时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}"
                + $"负载：{(useSlim ? "精简（使用系统 .NET）" : "内置运行时")}{Environment.NewLine}"
                + $"卸载：{registryNote}{Environment.NewLine}"
                + $"　　（卸载也可以直接双击：{uninstaller}）{Environment.NewLine}"
                + $"签名证书：{trustNote}{Environment.NewLine}");

            Dispatcher.Invoke(() =>
            {
                _doneNote.Text = $"卸载：{registryNote}\n签名证书：{trustNote}";
                _doneNote.Visibility = Visibility.Visible;
            });
            Report("完成", 100);
            Dispatcher.Invoke(() =>
            {
                _progress.Visibility = Visibility.Collapsed;
                _done.Visibility = Visibility.Visible;
            });
        }
        catch (Exception exc)
        {
            _installFailed = true;
            Dispatcher.Invoke(() =>
            {
                _progress.Visibility = Visibility.Collapsed;
                _ready.Visibility = Visibility.Visible;
                SetInstallingState(false);
                MessageBox.Show(this, exc.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
    }

    /// <summary>能不能在目标位置建目录（预检，避免抛英文异常）。</summary>
    private static bool CanWrite(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".写入测试-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool Cancelled() => _cancelRequested;

    /// <summary>安装中：右上角按钮变成浅色的「安装中」且不可点。</summary>
    private void SetInstallingState(bool installing)
    {
        _installButton.IsEnabled = !installing;
        _installButton.Content = installing ? "安装中" : "安装 ›";
        _installButton.Background = installing ? Theme.Brush(Theme.Secondary) : Theme.Brush(Theme.Accent);
        _installButton.Foreground = installing ? Theme.Brush(Theme.Muted) : Brushes.White;
    }

    /// <summary>取消后的清理：删掉刚写进去的文件；把我们新建的空目录也清掉，最后回到路径页。</summary>
    private void FinishCancel(string target, bool wroteTarget, List<string> extracted)
    {
        foreach (var file in extracted)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception)
            {
                // 个别文件占用就留着，下面会提示
            }
        }

        try
        {
            if (wroteTarget && Directory.Exists(target) &&
                !Directory.EnumerateFileSystemEntries(target).Any())
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception)
        {
            // 目录删不掉就留着
        }

        Dispatcher.Invoke(() =>
        {
            _progress.Visibility = Visibility.Collapsed;
            _ready.Visibility = Visibility.Visible;
            SetInstallingState(false);
            _bar.Value = 0;
            _status.Text = "准备中…";
            MessageBox.Show(this, "已取消安装，写入的文件已清理。", "已取消", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void Report(string text, int percent) => Dispatcher.Invoke(() =>
    {
        _status.Text = text;
        _bar.Value = percent;
    });
}

/// <summary>
/// 安装时配置签名证书（全部在当前用户范围内，不需要管理员）：
///   生成本机自签名证书 CN=ajmn → 导入受信任根 + 受信任的发布者 → 给装好的主程序签名。
/// 任何一步失败都不影响安装（只是会继续提示"未知发布者"）。
/// </summary>
public static class CertificateSetup
{
    /// <summary>
    /// 证书脚本（PowerShell 5.1 语法）。@APPEXE@ / @CER@ 由 C# 替换成真实路径。
    /// 注意：脚本和临时 .cer 都【不写 %TEMP%】——本机 %TEMP% 被安全策略锁住，写那儿必然失败
    ///（这正是之前"勾了信任证书却没生效"的原因）。.cer 放安装目录，导入完就删。
    /// 脚本正文通过 -EncodedCommand（UTF-16 base64）传，既不落临时文件，也不会被代码页坑到。
    /// </summary>
    private const string ScriptTemplate = @"
$ErrorActionPreference = 'Stop'
$AppExe = '@APPEXE@'
$cer = '@CER@'
$c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq 'CN=ajmn' } | Select-Object -First 1
if (-not $c) {
  $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ajmn' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyUsage DigitalSignature -FriendlyName 'ajmn 记账本签名' -NotAfter (Get-Date).AddYears(10)
}
Export-Certificate -Cert $c -FilePath $cer -Force | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\CurrentUser\TrustedPublisher' | Out-Null
if (Test-Path $AppExe) { Set-AuthenticodeSignature -FilePath $AppExe -Certificate $c | Out-Null }
Remove-Item -LiteralPath $cer -Force -ErrorAction SilentlyContinue
Write-Output ('OK ' + $c.Thumbprint)
";

    public static string Trust(string appExe)
    {
        var appDir = Path.GetDirectoryName(appExe);
        var cerPath = Path.Combine(string.IsNullOrEmpty(appDir) ? AppContext.BaseDirectory : appDir, "ajmn-证书.cer");
        try
        {
            var script = ScriptTemplate
                .Replace("@APPEXE@", appExe.Replace("'", "''"))
                .Replace("@CER@", cerPath.Replace("'", "''"));
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return "失败（无法启动 PowerShell）";
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit(60000);

            if (process.ExitCode == 0 && output.StartsWith("OK", StringComparison.Ordinal))
            {
                return $"已生成并信任（指纹 {output[3..].Trim()}），并已给主程序签名";
            }

            return $"失败（{(error.Length > 0 ? error.Split('\n')[0] : "退出码 " + process.ExitCode)}）";
        }
        catch (Exception exc)
        {
            return "失败（" + exc.Message + "）";
        }
        finally
        {
            try
            {
                if (File.Exists(cerPath))
                {
                    File.Delete(cerPath);       // 脚本异常退出时兜底清掉证书文件
                }
            }
            catch (Exception)
            {
                // 删不掉无所谓
            }
        }
    }
}

public static class Shortcut
{
    public static void Create(string linkPath, string targetPath, string workingDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = targetPath;
            link.WorkingDirectory = workingDir;
            link.IconLocation = targetPath;
            link.Description = "记账本";
            link.Save();
        }
        catch (Exception)
        {
            // 建不了快捷方式不影响安装
        }
    }
}

public static class Registry
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\记账本";

    /// <summary>
    /// 登记"应用和功能"里的卸载项。**失败不算安装失败**（返回一句给用户看的说明）：
    ///   实测有些机器（本机就是）安全软件/策略**禁止在 HKCU\...\Uninstall 下写子键**，
    ///   连新建一个测试键都被拒；而注册表项只影响"能不能在应用和功能里看到记账本"，
    ///   绝不该让整个安装失败（旧版就因为这个把一次已经拷好文件的安装判成失败，还留下半成品）。
    /// </summary>
    public static string Install(string target, string uninstaller, string exePath)
    {
        try
        {
            Write(target, uninstaller, exePath);
            return "已登记（可以在「应用和功能」里卸载）";
        }
        catch (Exception first)
        {
            // 常见情况：老键权限坏了或删了一半 → 整个删掉重来一次
            try
            {
                Remove();
                Write(target, uninstaller, exePath);
                return "已登记（重建注册表项后成功）";
            }
            catch (Exception second)
            {
                return $"未登记（{Short(second.Message)}）→ 卸载请直接双击安装目录里的「卸载记账本.exe」";
            }
        }
    }

    private static void Write(string target, string uninstaller, string exePath)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("无法创建注册表项");
        }

        key.SetValue("DisplayName", $"{Program.AppName} {Program.Version}");
        key.SetValue("DisplayVersion", Program.Version);
        key.SetValue("Publisher", "记账本");
        key.SetValue("InstallLocation", target);
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static string Short(string message)
    {
        var text = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 80 ? text[..80] + "…" : text;
    }

    public static void Remove()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // 删不掉就留着
        }
    }
}

public static class Uninstaller
{
    public static int Run()
    {
        var exePath = Process.GetCurrentProcess().MainModule!.FileName;
        var exeDir = Path.GetDirectoryName(exePath)!;
        var kept = UninstallPlan.ExistingKeepDirs(exeDir);
        var dataHint = kept.Length > 0
            ? $"账本数据不会删除，会留在安装目录里（{string.Join(" / ", kept)}）。"
            : "这个目录里没有账本数据，卸载后整个目录会被删除。";

        var answer = MessageBox.Show(
            $"确定卸载「{Program.AppName}」吗？\n\n安装位置：{exeDir}\n{dataHint}",
            "卸载记账本", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return 0;
        }

        try
        {
            foreach (var folder in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "程序"),
                     })
            {
                foreach (var name in new[] { $"{Program.AppName}.lnk", "卸载记账本.lnk" })
                {
                    var link = Path.Combine(folder, name);
                    if (File.Exists(link))
                    {
                        File.Delete(link);
                    }
                }
            }

            Registry.Remove();
            KillApp();                       // 先结束可能残留的后台进程（否则目录删不掉）

            // 删程序文件：数据目录（数据/配置/导出/备份）和正在运行的自己都留着，
            // 自己那个 exe 由下面的延迟脚本等我们退出后再删。
            var unsafeTarget = UninstallPlan.IsUnsafeToDelete(exeDir);
            var failed = unsafeTarget
                ? new List<string>()
                : UninstallPlan.RemoveProgramFiles(exeDir, new[] { exePath });

            if (unsafeTarget)
            {
                MessageBox.Show(
                    $"卸载信息已清理，但为安全起见没有自动删除这个目录（它看起来是盘根或系统/用户目录）：\n{exeDir}\n\n请手动确认后删除。",
                    "卸载完成", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }

            // 顺序很关键：先让用户看到结果（这个框关掉后进程才会退出），再起清理脚本。
            // 脚本靠"删得掉卸载器自己的 exe 没有"来判断我们退出了没有，重试 20 次（约 1 分钟）。
            var retryHint = failed.Count > 0 ? $"\n\n（有 {failed.Count} 项正在使用，稍后自动重试清理）" : string.Empty;
            MessageBox.Show(
                (kept.Length > 0
                    ? $"卸载完成：程序文件已清理。\n\n你的账本数据保留在：\n{exeDir}\n（{string.Join(" / ", kept)} 不会被删除；确认不再需要时可以手动删掉这个文件夹）"
                    : "卸载完成：程序目录已清理。") + retryHint,
                "卸载完成", MessageBoxButton.OK, MessageBoxImage.Information);

            // 清理脚本放在安装目录【外面】：脚本自己也在被删的目录里的话，目录永远删不掉
            var scriptDir = Path.GetDirectoryName(exeDir);
            if (string.IsNullOrEmpty(scriptDir))
            {
                scriptDir = exeDir;
            }

            try
            {
                var script = UninstallPlan.WriteScript(scriptDir);
                UninstallPlan.StartScript(script, exeDir);
            }
            catch (Exception)
            {
                // 脚本起不来只会剩下"卸载记账本.exe"这一个文件，不影响用户手动删掉
            }

            return 0;
        }
        catch (Exception exc)
        {
            MessageBox.Show(exc.Message, "卸载出错", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    /// <summary>结束还在跑的主程序，并等它真的退出（不然安装目录里的文件被占用，删不干净）。</summary>
    private static void KillApp()
    {
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/f /im {Program.ExeName}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            killer?.WaitForExit(8000);
        }
        catch (Exception)
        {
            // 没有残留进程时 taskkill 会返回非 0，无所谓
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var still = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Program.ExeName));
            if (still.Length == 0)
            {
                return;
            }

            foreach (var process in still)
            {
                process.Dispose();
            }

            System.Threading.Thread.Sleep(300);
        }
    }
}
