using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>
/// 导入预览窗口（完整版）：
///   · 左边：冲突策略、保存为谁的收支、是否记设备来源、设备名列表（折叠截断/展开完整/可手动输入）
///   · 右边：按当前选项变换后的预览（改选项立即刷新，所见即所导）
///   · 底部：取消 / 开始导入；导入时用「加载中」转圈锁住操作，完成后给结果汇总
/// </summary>
public sealed class ImportWindow : Window
{
    private readonly Settings _settings;
    private readonly string _path;
    private ImportPreview _preview;
    private readonly ImportOptions _options = new();

    private readonly TextBlock _summary = Ui.Label("", Ui.FontBody, Ui.Text, bold: true);
    private readonly StackPanel _deviceList = new();
    private readonly Ui.DataTable _table;
    private readonly TextBlock _resultText = Ui.Label("", Ui.FontBody, Ui.Muted);
    private readonly TextBox _ownerBox;
    private readonly TextBox _sourceBox;
    private readonly Border _ownerShell;
    private readonly Border _sourceShell;
    private readonly List<(OwnerMode Mode, System.Windows.Controls.Primitives.ToggleButton Pill)> _ownerPills = new();
    private readonly List<(SourceMode Mode, System.Windows.Controls.Primitives.ToggleButton Pill)> _sourcePills = new();
    private readonly Grid _busyHost = new();
    private readonly HashSet<string> _expanded = new();
    private Button _importButton = null!;

    public int ImportedCount { get; private set; }

    public ImportWindow(Settings settings, string path)
    {
        _settings = settings;
        _path = path;
        _preview = Exchange.Preview(path);
        _options.LocalDevice = settings.DeviceName;
        _options.OwnerCode = Config.LoadAccount().UserCode;

        Title = "导入预览";
        Width = 1240;
        Height = 760;
        MinWidth = 900;
        MinHeight = 560;
        Background = Ui.Background;
        FontFamily = Ui.FontFamily;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // ---------- 顶部：文件与概览 ----------
        var head = new StackPanel { Margin = new Thickness(20, 16, 20, 6) };
        head.Children.Add(Ui.Label("导入预览", Ui.FontTitle, Ui.Text, bold: true));
        var fileHint = Ui.Hint($"文件：{path}");
        fileHint.TextWrapping = TextWrapping.Wrap;
        fileHint.Margin = new Thickness(0, 4, 0, 0);
        head.Children.Add(fileHint);
        _summary.Margin = new Thickness(0, 6, 0, 0);
        head.Children.Add(_summary);

        // ---------- 选项区 ----------
        var options = new StackPanel { Width = 375 };

        options.Children.Add(Ui.Label("遇到重复记录", Ui.FontBody, Ui.Text, bold: true));
        var (strategyShell, strategyBox) = Ui.Select(new[] { "跳过重复", "按编号覆盖", "全部追加" }, 160, _options.Strategy);
        strategyBox.SelectionChanged += (_, _) =>
        {
            _options.Strategy = strategyBox.SelectedItem?.ToString() ?? "跳过重复";
            _settings.ImportStrategy = _options.Strategy;
            RefreshPreview();
        };
        strategyShell.Margin = new Thickness(0, 4, 0, 14);
        options.Children.Add(strategyShell);

        options.Children.Add(Ui.Label("保存为谁的收支", Ui.FontBody, Ui.Text, bold: true));
        var ownerHint = Ui.Hint("不选就用文件里自带的归属（用户码）");
        ownerHint.Margin = new Thickness(0, 2, 0, 4);
        options.Children.Add(ownerHint);
        var ownerRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, text) in new[]
                 {
                     (OwnerMode.Keep, "用文件里的"), (OwnerMode.Replace, "统一改为"), (OwnerMode.Clear, "清空归属")
                 })
        {
            var captured = mode;
            var pill = Ui.Pill(text);
            pill.Click += (_, _) =>
            {
                _options.Owner = captured;
                SyncPills();
                RefreshPreview();
            };
            _ownerPills.Add((mode, pill));
            ownerRow.Children.Add(pill);
        }

        options.Children.Add(ownerRow);

        (_ownerShell, _ownerBox) = Ui.Input(_options.OwnerCode, 180, "用户码，例如 uXXXX");
        _ownerShell.Margin = new Thickness(0, 6, 0, 14);
        _ownerBox.TextChanged += (_, _) =>
        {
            _options.OwnerCode = _ownerBox.Text.Trim();
            if (_options.Owner == OwnerMode.Replace)
            {
                RefreshPreview();
            }
        };
        options.Children.Add(_ownerShell);

        options.Children.Add(Ui.Label("是否记录设备来源", Ui.FontBody, Ui.Text, bold: true));
        var sourceHint = Ui.Hint("本机记录默认不记设备；导出时可以自动补上当前设备名");
        sourceHint.Margin = new Thickness(0, 2, 0, 4);
        options.Children.Add(sourceHint);
        // 两行放四个胶囊，窄窗口也不挤出界
        var sourceRows = new[] { new StackPanel { Orientation = Orientation.Horizontal }, new StackPanel { Orientation = Orientation.Horizontal } };
        var sourceIndex = 0;
        foreach (var (mode, text) in new[]
                 {
                     (SourceMode.Keep, "保留文件里的"), (SourceMode.Clear, "不记（置空）"),
                     (SourceMode.FillLocal, "补当前设备"), (SourceMode.Replace, "手动输入")
                 })
        {
            var captured = mode;
            var pill = Ui.Pill(text);
            pill.Click += (_, _) =>
            {
                _options.Source = captured;
                SyncPills();
                RefreshPreview();
            };
            _sourcePills.Add((mode, pill));
            sourceRows[sourceIndex / 2].Children.Add(pill);
            sourceIndex++;
        }

        options.Children.Add(sourceRows[0]);
        sourceRows[1].Margin = new Thickness(0, 6, 0, 0);
        options.Children.Add(sourceRows[1]);

        (_sourceShell, _sourceBox) = Ui.Input(_options.SourceName, 200, "设备名，例如 我的笔记本");
        _sourceShell.Margin = new Thickness(0, 6, 0, 4);
        _sourceBox.TextChanged += (_, _) =>
        {
            _options.SourceName = _sourceBox.Text.Trim();
            if (_options.Source == SourceMode.Replace)
            {
                RefreshPreview();
            }
        };
        _sourceBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _options.Source = SourceMode.Replace;
                SyncPills();
                RefreshPreview();
            }
        };
        options.Children.Add(_sourceShell);
        var sourceButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        sourceButtons.Children.Add(Ui.GhostButton("确定用这个设备名", () =>
        {
            _options.Source = SourceMode.Replace;
            SyncPills();
            RefreshPreview();
        }, 30));
        var clearSource = Ui.GhostButton("清空输入", () =>
        {
            _sourceBox.Text = "";
            _options.SourceName = "";
            RefreshPreview();
        }, 30);
        clearSource.Margin = new Thickness(8, 0, 0, 0);
        sourceButtons.Children.Add(clearSource);
        options.Children.Add(sourceButtons);

        // ---------- 设备名列表（折叠：短名 + 完整条数；展开：完整名，固定宽度可换行） ----------
        options.Children.Add(Ui.Label($"导入名单里的设备名（{_preview.Devices.Count} 个）", Ui.FontBody, Ui.Text, bold: true));
        var deviceHint = Ui.Hint("点开某个设备名可以选它（会自动填进上面的「手动输入」）");
        deviceHint.Margin = new Thickness(0, 2, 0, 6);
        options.Children.Add(deviceHint);
        var deviceBox = new Border
        {
            Background = Ui.Card,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Ui.RadiusControl),
            Padding = new Thickness(10, 8, 10, 8),
            MaxHeight = 190,
        };
        deviceBox.Child = new ScrollViewer
        {
            Content = _deviceList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        options.Children.Add(deviceBox);

        // ---------- 预览表格 ----------
        _table = new Ui.DataTable(new (string, double, HorizontalAlignment)[]
        {
            ("日期", 1.15, HorizontalAlignment.Left),
            ("时间", 0.6, HorizontalAlignment.Left),
            ("类型", 0.55, HorizontalAlignment.Left),
            ("分类", 0.9, HorizontalAlignment.Left),
            ("金额", 1.0, HorizontalAlignment.Right),
            ("短备注", 1.25, HorizontalAlignment.Left),
            ("归属", 1.15, HorizontalAlignment.Left),
            ("来源", 1.0, HorizontalAlignment.Left),
        });
        _table.Root.Margin = new Thickness(12, 0, 0, 0);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var optionsScroll = new ScrollViewer
        {
            Content = options,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        optionsScroll.Margin = new Thickness(20, 0, 8, 0);
        Grid.SetColumn(optionsScroll, 0);
        body.Children.Add(optionsScroll);
        Grid.SetColumn(_table.Root, 1);
        body.Children.Add(_table.Root);

        // ---------- 底部 ----------
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 10, 20, 16),
        };
        _resultText.Margin = new Thickness(0, 0, 12, 0);
        _resultText.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_resultText);
        var cancel = Ui.GhostButton("取消", Close, 36);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        footer.Children.Add(cancel);
        _importButton = Ui.PrimaryButton("开始导入", DoImport, 36);
        footer.Children.Add(_importButton);

        var shell = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        shell.Children.Add(head);
        DockPanel.SetDock(footer, Dock.Bottom);
        shell.Children.Add(footer);
        shell.Children.Add(body);

        var root = new Grid();
        root.Children.Add(shell);
        root.Children.Add(_busyHost);
        Content = root;

        SyncPills();
        BuildDeviceList();
        RefreshPreview();
    }

    // ---------------------------------------------------------------- 选项与预览

    private void SyncPills()
    {
        foreach (var (mode, pill) in _ownerPills)
        {
            var on = mode == _options.Owner;
            pill.IsChecked = on;
            pill.Background = on ? Ui.Accent : Ui.Transparent;
            pill.Foreground = on ? Brushes.White : Ui.Muted;
        }

        foreach (var (mode, pill) in _sourcePills)
        {
            var on = mode == _options.Source;
            pill.IsChecked = on;
            pill.Background = on ? Ui.Accent : Ui.Transparent;
            pill.Foreground = on ? Brushes.White : Ui.Muted;
        }

        // 只有「统一改为」「手动输入」才需要输入框
        _ownerShell.Opacity = _options.Owner == OwnerMode.Replace ? 1 : 0.45;
        _sourceShell.Opacity = _options.Source == SourceMode.Replace ? 1 : 0.45;
    }

    private void BuildDeviceList()
    {
        _deviceList.Children.Clear();
        if (_preview.Devices.Count == 0)
        {
            _deviceList.Children.Add(Ui.Hint("这份文件里没有设备名（都是空的）"));
            return;
        }

        foreach (var entry in _preview.Devices)
        {
            var name = entry.Name;
            var expanded = _expanded.Contains(name);

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var arrow = Ui.Label(expanded ? "▾" : "▸", Ui.FontBody, Ui.Muted);
            arrow.Width = 18;
            arrow.Cursor = Cursors.Hand;
            arrow.MouseLeftButtonDown += (_, _) => Toggle(name);
            Grid.SetColumn(arrow, 0);
            row.Children.Add(arrow);

            // 折叠：短名（条数始终完整显示）；展开：完整名，固定宽度、可换行
            var label = Ui.Label(expanded ? name : entry.ShortName, Ui.FontBody);
            label.TextWrapping = TextWrapping.Wrap;
            label.MaxWidth = expanded ? 260 : 150;
            label.Cursor = Cursors.Hand;
            label.MouseLeftButtonDown += (_, _) => Toggle(name);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(Ui.Label($"{entry.Count} 条", Ui.FontSmall, Ui.Muted));
            var use = Ui.GhostButton("用这个", () => UseDeviceName(name), 26);
            use.FontSize = Ui.FontSmall;
            use.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(use);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);

            _deviceList.Children.Add(row);
        }
    }

    private void Toggle(string name)
    {
        if (!_expanded.Remove(name))
        {
            _expanded.Add(name);
        }

        BuildDeviceList();
    }

    private void UseDeviceName(string name)
    {
        _sourceBox.Text = name;
        _options.SourceName = name;
        _options.Source = SourceMode.Replace;
        SyncPills();
        RefreshPreview();
        _resultText.Text = $"已把「手动输入」设为：{name}";
    }

    private void RefreshPreview()
    {
        var transformed = Exchange.Transformed(_preview, _options);
        _summary.Text = $"{Exchange.Describe(_preview)}　|　将按「{_options.Strategy}」处理";
        _resultText.Text = "";

        _table.Clear();
        if (transformed.Count == 0)
        {
            _table.AddEmpty("这份文件里没有能导入的记录");
            return;
        }

        var symbol = _settings.CurrencySymbol;
        var shown = 0;
        foreach (var record in transformed)
        {
            if (shown++ >= 200)
            {
                break;
            }

            _table.AddRow(new[]
            {
                record.Date.ToString("yyyy-MM-dd"),
                record.Time,
                record.Kind,
                record.Category,
                Valid.Money(record.Amount, symbol),
                record.Note,
                record.Owner.Length > 0 ? record.Owner : "（空）",
                record.Source.Length > 0 ? record.Source : "（空）",
            });
        }

        if (transformed.Count > 200)
        {
            _table.AddEmpty($"…… 还有 {transformed.Count - 200} 条，导入时会全部处理");
        }
    }

    // ---------------------------------------------------------------- 导入

    private void DoImport()
    {
        if (_preview.Records.Count == 0)
        {
            _resultText.Text = "没有可导入的记录";
            return;
        }

        ShowBusy("正在导入，请不要关闭窗口……");

        // 让「加载中」先渲染出来，再干活
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _settings.Save("导入默认策略", _options.Strategy);
                var result = Exchange.Apply(_preview, _options);
                ImportedCount = result.Added + result.Updated;
                _resultText.Foreground = Ui.Text;
                _resultText.Text = $"完成：新增 {result.Added} 条，更新 {result.Updated} 条，"
                                   + $"跳过 {result.Skipped} 条，失败 {result.Failed} 条（涉及 {result.Days.Count} 天）";
            }
            catch (Exception exc)
            {
                _resultText.Foreground = Ui.Expense;
                _resultText.Text = "导入失败：" + exc.Message;
            }
            finally
            {
                HideBusy();
                _resultText.Visibility = Visibility.Visible;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ShowBusy(string message)
    {
        var card = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.Children.Add(Ui.Spinner());
        var label = Ui.Label(message, Ui.FontSubtitle, Ui.Text, bold: true);
        label.Margin = new Thickness(14, 0, 0, 0);
        card.Children.Add(label);

        _busyHost.Children.Clear();
        _busyHost.Background = new SolidColorBrush(Color.FromArgb(0x66, 0x1F, 0x23, 0x29));
        _busyHost.Children.Add(Ui.CardPanel(card, padding: 22));
        _busyHost.Visibility = Visibility.Visible;
        _importButton.IsEnabled = false;
        IsEnabled = true;   // 遮罩自己吃掉点击，下面的界面动不了
    }

    private void HideBusy()
    {
        _busyHost.Children.Clear();
        _busyHost.Visibility = Visibility.Collapsed;
        _importButton.IsEnabled = true;
    }
}
