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
    private readonly Grid _planGrid = new();
    private readonly TextBlock _planCounts = Ui.Label("", Ui.FontSmall, Ui.Muted);
    private List<RowPlan> _plans = new();
    private readonly TextBlock _resultText = Ui.Label("", Ui.FontBody, Ui.Muted);
    private readonly TextBox _ownerBox;
    private readonly Border _ownerShell;
    private Border _sourceShell = null!;
    private ComboBox _sourceCombo = null!;
    private readonly List<(OwnerMode Mode, System.Windows.Controls.Primitives.ToggleButton Pill)> _ownerPills = new();
    private readonly Grid _busyHost = new();
    private readonly HashSet<string> _expanded = new();
    private Button _importButton = null!;

    public int ImportedCount { get; private set; }

    /// <summary>本次真正新增的记录（调用方拿去放进「本次已记」列表）。</summary>
    public List<Record> AddedRecords { get; } = new();

    public ImportWindow(Settings settings, string path)
    {
        _settings = settings;
        _path = path;
        _preview = Exchange.Preview(path);
        _options.LocalDevice = settings.DeviceName;
        _options.OwnerCode = Config.LoadAccount().UserCode;
        _options.MainDevice = _preview.MainDevice;   // 文件里「导出设备」列（老文件则按出现最多的来源兜底）

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

        options.Children.Add(Ui.Label("设备来源怎么记", Ui.FontBody, Ui.Text, bold: true));
        var sourceHint = Ui.Hint(_preview.MainDevice.Length > 0
            ? $"主设备＝导出这份文件的设备：{_preview.MainDevice}"
            : "这份文件没写主设备（老文件），选「统一记为主设备」会退回本机设备名");
        sourceHint.TextWrapping = TextWrapping.Wrap;
        sourceHint.Margin = new Thickness(0, 2, 0, 4);
        options.Children.Add(sourceHint);

        var sourceItems = new[] { "不记录设备", "统一记为主设备", "保留各自设备" };
        var sourceModes = new[] { SourceMode.Clear, SourceMode.FillMain, SourceMode.Keep };
        var startIndex = Array.IndexOf(sourceModes, _options.Source);
        (_sourceShell, _sourceCombo) = Ui.Select(sourceItems, 190, sourceItems[startIndex < 0 ? 1 : startIndex]);
        _sourceShell.Margin = new Thickness(0, 0, 0, 14);
        _sourceCombo.SelectionChanged += (_, _) =>
        {
            if (_sourceCombo.SelectedIndex >= 0 && _sourceCombo.SelectedIndex < sourceModes.Length)
            {
                _options.Source = sourceModes[_sourceCombo.SelectedIndex];
                RefreshPreview();
            }
        };
        options.Children.Add(_sourceShell);

        // ---------- 设备名列表（折叠：短名 + 完整条数；展开：完整名，固定宽度可换行） ----------
        options.Children.Add(Ui.Label($"导入名单里的设备名（{_preview.Devices.Count} 个）", Ui.FontBody, Ui.Text, bold: true));
        var deviceHint = Ui.Hint("点开看完整设备名；也可把某个设备「定为主设备」（老文件没写主设备时用得上）");
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
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        options.Children.Add(deviceBox);

        // ---------- 预览表格 ----------
        // ---------- 预览列表：逐行（左侧三态按钮；预判不会导入的整行显示为浅色，适配深色/自定义配色）----------
        _planCounts.Margin = new Thickness(12, 0, 0, 6);
        _planCounts.Margin = new Thickness(12, 6, 0, 6);
        var listScroll = new ScrollViewer
        {
            Content = _planGrid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
        };

        // 外观与旧版数据表一致：圆角卡片 + 表头 + 行（列宽按比例分配）
        var listCard = Ui.CardPanel(listScroll, padding: 2);
        var listColumn = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        listColumn.Children.Add(_planCounts);
        listColumn.Children.Add(listCard);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var optionsScroll = new ScrollViewer
        {
            Content = options,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        optionsScroll.Margin = new Thickness(20, 0, 8, 0);
        Grid.SetColumn(optionsScroll, 0);
        body.Children.Add(optionsScroll);
        Grid.SetColumn(listColumn, 1);
        body.Children.Add(listColumn);

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

        // 设备来源已是下拉三选一 → 把选项同步回下拉框（如从别处改了 _options.Source）
        var modes = new[] { SourceMode.Clear, SourceMode.FillMain, SourceMode.Keep };
        var index = Array.IndexOf(modes, _options.Source);
        if (index >= 0 && _sourceCombo.SelectedIndex != index)
        {
            _sourceCombo.SelectedIndex = index;
        }

        // 只有「统一改为」才需要输入框
        _ownerShell.Opacity = _options.Owner == OwnerMode.Replace ? 1 : 0.45;
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
            var use = Ui.GhostButton("定为主设备", () => UseDeviceName(name), 26);
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
        _options.MainDevice = name;
        _options.Source = SourceMode.FillMain;
        SyncPills();
        RefreshPreview();
        _resultText.Text = $"已把「统一记为主设备」设为主设备：{name}";
    }

    private void RefreshPreview()
    {
        var transformed = Exchange.Transformed(_preview, _options);
        _summary.Text = $"{Exchange.Describe(_preview)}　|　将按「{_options.Strategy}」处理";
        _resultText.Text = "";

        // ---------- 先一步检查：与已录入数据快速对比 ----------
        // 判重范围：把候选日期所在的**整月**都读进来。只读候选那几天的话，
        // "同一笔账落在别的日期"就看不出重复（跨天重复）。
        var existing = ReadExistingAround(transformed);

        _plans = Exchange.Plan(transformed, existing);
        BuildPlanRows();
    }

    /// <summary>
    /// 判重用的「已录入数据」：取候选日期所在的**整月**（跨月也算进来）。
    /// 只读候选那几天的话，同一笔账落在别的日期时就认不出重复（跨天重复）——
    /// 用户 2026-10-16 遇到的正是这类问题。
    /// </summary>
    private static List<Record> ReadExistingAround(List<Record> transformed)
    {
        if (transformed.Count == 0)
        {
            return new List<Record>();
        }

        var first = transformed.Min(item => item.Date);
        var last = transformed.Max(item => item.Date);
        var start = new DateOnly(first.Year, first.Month, 1);
        var end = new DateOnly(last.Year, last.Month, 1).AddMonths(1).AddDays(-1);
        return Query.Run(new QueryFilter { Start = start, End = end, Limit = 0 }).Records;
    }

    /// <summary>
    /// 导入前**重新比一遍**：不关窗口连点两次「开始导入」时，
    /// 第一次写入的账已经进账本了，旧的判定还说"新增"→ 会重复写入。
    /// 这里只更新**判定**（Verdict/Exact/Matched），**保留**每行的人工三态（Action），
    /// 所以用户锁定的行不会被改动。
    /// </summary>
    private void RecheckPlans()
    {
        try
        {
            var transformed = Exchange.Transformed(_preview, _options);
            var existing = ReadExistingAround(transformed);

            var fresh = Exchange.Plan(transformed, existing);
            for (var i = 0; i < _plans.Count && i < fresh.Count; i++)
            {
                _plans[i].Verdict = fresh[i].Verdict;
                _plans[i].Exact = fresh[i].Exact;
                _plans[i].Matched = fresh[i].Matched;
            }

            BuildPlanRows();   // 刷新显示：变灰的行与顶部计数都会更新
        }
        catch (Exception exc)
        {
            Core.Log.Info("导入前重新比对失败（不影响本次导入）：" + exc.Message);
        }
    }

    /// <summary>把三态与预判渲染成一行行；点左侧小按钮可改成 自动 / 导入锁定 / 不导入锁定。</summary>
    /// <summary>列宽权重：与旧版数据表同款比例（第一列「状态」固定宽度）。</summary>
    private static readonly double[] PlanWeights = new[] { 1.15, 0.6, 0.55, 0.9, 1.0, 1.25, 1.15, 1.0 };

    /// <summary>把三态与预判渲染成整张表（表头 + 所有行都在同一个 Grid 里 → 列宽天然对齐、互不遮挡）。</summary>
    private void BuildPlanRows()
    {
        _planGrid.Children.Clear();
        _planGrid.ColumnDefinitions.Clear();
        _planGrid.RowDefinitions.Clear();

        const int stateWidth = 92;
        _planGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(stateWidth, GridUnitType.Pixel) });
        foreach (var weight in PlanWeights)
        {
            _planGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
        }

        var header = new[] { "状态", "日期", "时间", "类型", "分类", "金额", "短备注", "归属", "来源" };
        _planGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34, GridUnitType.Pixel) });
        for (var column = 0; column < header.Length; column++)
        {
            var cell = Ui.Label(header[column], Ui.FontSmall, Ui.Muted, bold: true);
            cell.Margin = new Thickness(10, 0, 10, 0);
            cell.VerticalAlignment = VerticalAlignment.Center;
            if (column == 5)
            {
                cell.HorizontalAlignment = HorizontalAlignment.Right;   // 金额列表头右对齐（与数据行一致）
            }

            Grid.SetRow(cell, 0);
            Grid.SetColumn(cell, column);
            _planGrid.Children.Add(cell);
        }

        if (_plans.Count == 0)
        {
            _planCounts.Text = "这份文件里没有能导入的记录";
            return;
        }

        var fresh = _plans.Count(item => item.WillImport);
        var skip = _plans.Count(item => !item.WillImport && item.Verdict != RowVerdict.Similar);
        var similar = _plans.Count(item => item.Verdict == RowVerdict.Similar);
        _planCounts.Text = $"会导入 {fresh} 条　｜　跳过 {skip} 条（账本里已有）　｜　疑似重复 {similar} 条"
                           + "　（点左侧「状态」按钮可改成 自动 / 导入锁定 / 不导入锁定）";

        var shown = 0;
        foreach (var plan in _plans)
        {
            if (shown >= 300)
            {
                break;
            }

            AddPlanRow(plan, shown + 1);
            shown++;
        }

        if (_plans.Count > shown)
        {
            _planGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30, GridUnitType.Pixel) });
            var more = Ui.Hint($"…… 还有 {_plans.Count - shown} 条未显示，导入时会全部按各自状态处理");
            more.Margin = new Thickness(10, 0, 10, 0);
            more.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(more, shown + 1);
            Grid.SetColumn(more, 1);
            Grid.SetColumnSpan(more, 8);
            _planGrid.Children.Add(more);
        }
    }

    private void AddPlanRow(RowPlan plan, int row)
    {
        var record = plan.Record;
        var willImport = plan.WillImport;
        _planGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32, GridUnitType.Pixel) });

        // 预判不会导入：整行浅色（前景次要色 + 背景朝背景色混合）—— 深色/自定义配色下同样成立
        var brush = new SolidColorBrush(willImport ? Colors.Transparent : Ui.Blend(Ui.CardColor, Ui.BackgroundColor, 0.55));
        brush.Freeze();
        var background = new Border { Background = brush, CornerRadius = new CornerRadius(4) };
        Grid.SetRow(background, row);
        Grid.SetColumn(background, 0);
        Grid.SetColumnSpan(background, 9);
        _planGrid.Children.Add(background);   // 先放背景，再放内容 → 不会被遮住

        var state = Ui.GhostButton(PlanStateText(plan), () => { }, 24);
        state.FontSize = Ui.FontSmall;
        state.Padding = new Thickness(8, 0, 8, 0);
        state.Margin = new Thickness(8, 0, 8, 0);
        state.HorizontalAlignment = HorizontalAlignment.Left;

        var menu = new ContextMenu();
        foreach (var (action, label) in new[]
                 {
                     (RowAction.Auto, "自动（按预判）"),
                     (RowAction.ForceImport, "导入锁定（一定要导入）"),
                     (RowAction.ForceSkip, "不导入锁定（一定跳过）"),
                 })
        {
            var captured = action;
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = plan.Action == action };
            item.Click += (_, _) =>
            {
                plan.Action = captured;
                BuildPlanRows();   // 改完状态立刻重画：颜色、计数、按钮文字一起刷新
            };
            menu.Items.Add(item);
        }

        state.ContextMenu = menu;
        state.Click += (_, _) =>
        {
            menu.PlacementTarget = state;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        Grid.SetRow(state, row);
        Grid.SetColumn(state, 0);
        _planGrid.Children.Add(state);

        var symbol = _settings.CurrencySymbol;
        var cells = new[]
        {
            record.Date.ToString("yyyy-MM-dd"),
            record.Time.Length > 0 ? record.Time : "（空）",
            record.Kind,
            record.Category,
            Valid.Money(record.Amount, symbol),
            record.Note.Length > 0 ? record.Note : "（空）",
            record.Owner.Length > 0 ? record.Owner : "（空）",
            record.Source.Length > 0 ? record.Source : "（空）",
        };

        for (var index = 0; index < cells.Length; index++)
        {
            var cell = Ui.Label(cells[index], Ui.FontBody, willImport ? Ui.Text : Ui.Muted);
            cell.Margin = new Thickness(10, 0, 10, 0);
            cell.VerticalAlignment = VerticalAlignment.Center;
            cell.TextTrimming = TextTrimming.CharacterEllipsis;
            if (index == 5)
            {
                cell.HorizontalAlignment = HorizontalAlignment.Right;
            }

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, index + 1);
            _planGrid.Children.Add(cell);
        }
    }

    private static string PlanStateText(RowPlan plan) => plan.Action switch
    {
        RowAction.ForceImport => "导入锁定",
        RowAction.ForceSkip => "不导入锁定",
        _ => "自动",
    };


    private static readonly string[] PlanHeaders = new[]
    {
        "状态", "日期", "时间", "类型", "分类", "金额", "短备注", "归属", "来源"
    };

    /// <summary>表头（与数据行共用列宽组，保证对齐）。</summary>

    /// <summary>9 列：状态(小按钮) + 8 个字段；除「短备注」拉伸外，其余按内容共用宽度。</summary>




    // ---------------------------------------------------------------- 导入

    private void DoImport()
    {
        if (_preview.Records.Count == 0)
        {
            _resultText.Text = "没有可导入的记录";
            return;
        }

        RecheckPlans();   // 关键：导入前重比一次，防止连点造成重复写入

        ShowBusy("正在导入，请不要关闭窗口……");

        // 让「加载中」先渲染出来，再干活
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _settings.Save("导入默认策略", _options.Strategy);
                var result = Exchange.Apply(_preview, _options, _plans);
                RecheckPlans();   // 导入后再比一次：这些行现在都该显示为"跳过"
                ImportedCount = result.Added + result.Updated;
                AddedRecords.AddRange(result.AddedRecords);   // 交给调用方放进「本次已记」
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
