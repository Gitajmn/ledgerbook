using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>
/// 设置页（按参考图重做）：
///   左侧 = 分类导航（图标 + 名称，选中是浅色圆角高亮）
///   右侧 = 只显示选中那一项的内容；内容按「组」排布：组标题 + 说明 + 控件，组之间用分隔线（不再套卡片框）
/// </summary>
public sealed class SettingsPanel
{
    private readonly Settings _settings;
    private readonly Account _account;
    private readonly MainWindow _window;
    private readonly StackPanel _nav = new();
    private readonly StackPanel _content = new();
    private readonly List<(Button Button, Func<UIElement> Build)> _sections = new();
    private DateField _exportStartField = null!;
    private DateField _exportEndField = null!;

    public Grid Root { get; }

    /// <summary>右侧内容的滚动器（自动化截图需要滚到指定位置）。</summary>
    public ScrollViewer ContentScroll { get; private set; } = null!;

    public SettingsPanel(Settings settings, Account account, MainWindow window)
    {
        _settings = settings;
        _account = account;
        _window = window;

        _nav.Width = 186;
        _nav.Margin = new Thickness(0, 0, 16, 0);

        // 分区顺序（2026-10-09 按用户要求定，**账户挪到最下面**）：
        //   通用 → 种类与支付方式 → 记一笔 → 查询 → 导入导出 → 数据与备份 → 账户
        // 原来的「存储格式」分区已撤掉：里面的"当前格式说明"和"统一升级全部"都并进了「数据与备份」，
        // 而且升级按钮只在真的有老格式数据时才显示（没有老数据时那个分区整页都是空话，用户说"什么都没用"）。
        AddSection("通用", "\uE713", BuildGeneral);
        AddSection("种类与支付方式", "\uE8EC", BuildCategories);
        AddSection("记一笔", "\uE70F", BuildRecord);
        AddSection("查询", "\uE721", BuildQuery);
        AddSection("导入导出", "\uE8AB", BuildExchange);
        AddSection("数据与备份", "\uE8B7", BuildBackup);
        AddSection("账户", "\uE77B", BuildAccount);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navScroll = new ScrollViewer
        {
            Content = _nav,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(navScroll, 0);
        root.Children.Add(navScroll);

        var contentScroll = new ScrollViewer
        {
            Content = _content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(contentScroll, 1);
        root.Children.Add(contentScroll);
        ContentScroll = contentScroll;

        Root = root;
        // 记住上次看的分区：界面重建（改字号/主题/昵称…）后不会再"跳回第一个分区"
        Show(_window.SettingsSection);
    }

    // ---------------------------------------------------------------- 导航

    private void AddSection(string title, string glyph, Func<UIElement> build)
    {
        var button = BuildNavButton(title, glyph);
        var index = _sections.Count;
        button.Click += (_, _) => Show(index);
        _sections.Add((button, build));
        _nav.Children.Add(button);
    }

    private static Button BuildNavButton(string title, string glyph)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Ui.RadiusControl));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 9, 10, 9));
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.TextProperty, glyph);
        icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        icon.SetValue(TextBlock.FontSizeProperty, Ui.FontSubtitle);
        icon.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        icon.SetValue(FrameworkElement.WidthProperty, 26.0);
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(icon);

        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(text);
        border.AppendChild(row);
        template.VisualTree = border;

        var button = new Button
        {
            Content = title,
            FontFamily = Ui.FontFamily,
            FontSize = Ui.FontBody,
            Foreground = Ui.Text,
            Background = Ui.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 3),
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = template,
        };
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Transparent));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Hover));
        style.Triggers.Add(hover);
        button.Style = style;
        return button;
    }

    private void Show(int index)
    {
        if (index < 0 || index >= _sections.Count)
        {
            return;
        }

        _window.SettingsSection = index;   // 记在窗口上，界面重建后仍停在同一分区

        for (var i = 0; i < _sections.Count; i++)
        {
            var selected = i == index;
            var button = _sections[i].Button;
            button.Background = selected ? Ui.Hover : Ui.Transparent;
            button.Foreground = selected ? Ui.Text : Ui.Muted;
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }

        _content.Children.Clear();
        _content.Children.Add(_sections[index].Build());
    }

    /// <summary>切换分类（自动化截图用）。</summary>
    public void ShowSection(int index) => Show(index);

    /// <summary>自动化截图用：切到「导入导出」并把日历弹出来。</summary>
    public void OpenExportCalendar()
    {
        Show(4);   // 导入导出（新顺序：通用0 种类1 记一笔2 查询3 导入导出4 数据与备份5 账户6）
        _exportStartField?.OpenForAutomation();
        Diag.Trace($"日历：弹窗已打开={_exportStartField?.IsOpen}，当前选中={_exportStartField?.Value:yyyy-MM-dd}");
    }

    /// <summary>一组设置：标题 + 说明 + 控件，组之间用分隔线隔开。</summary>
    private static StackPanel Group(string title, string hint, params UIElement[] controls)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Label(title, Ui.FontSubtitle + 3, Ui.Text, bold: true));
        if (hint.Length > 0)
        {
            var hintLabel = Ui.Hint(hint);
            hintLabel.TextWrapping = TextWrapping.Wrap;
            hintLabel.Margin = new Thickness(0, 5, 0, 0);
            panel.Children.Add(hintLabel);
        }

        foreach (var control in controls)
        {
            // 一律左对齐、按内容宽度：不然按钮会被拉成一整条横条，看着很怪
            var holder = new StackPanel
            {
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            holder.Children.Add(control);
            panel.Children.Add(holder);
        }

        panel.Children.Add(Ui.Divider(18, 18));
        return panel;
    }

    // ---------------------------------------------------------------- 通用

    private UIElement BuildGeneral()
    {
        var page = new StackPanel();
        page.Children.Add(Group("外观",
            "浅色与深色两套配色都为彩色单独调过（降饱和、提亮），深色下不刺眼；「跟随系统」跟随 Windows 的应用主题。",
            ThemeCards()));
        page.Children.Add(Group("界面字号", "改完立即生效，所有页面一起变", FontSizeSelect()));
        page.Children.Add(Group("货币符号", "显示在金额前面，例如 ¥ / $ / €", CurrencyInput()));
        page.Children.Add(Group("数据目录", "账本就存在这里；装在受保护目录时会自动换到用户目录",
            Ui.InfoRow("当前数据目录", Paths.Root, "打开", () => OpenPath(Paths.Root))));
        return page;
    }

    /// <summary>外观主题：大圆角卡片（☀ 浅色 / ☾ 深色 / 跟随系统）。</summary>
    private UIElement ThemeCards()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var cards = new List<(Ui.ThemeMode Mode, Border Card)>();
        foreach (var (mode, glyph, label) in new[]
                 {
                     (Ui.ThemeMode.Light, "\uE706", "浅色"),
                     (Ui.ThemeMode.Dark, "\uE708", "深色"),
                     (Ui.ThemeMode.System, "\uE7F4", "跟随系统"),
                 })
        {
            var captured = mode;
            var body = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var icon = Ui.Label(glyph, 22, Ui.Text);
            icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            body.Children.Add(icon);
            var text = Ui.Label(label, Ui.FontBody, Ui.Text);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(text);

            var card = new Border
            {
                Width = 130,
                Height = 90,
                Background = Ui.Card,
                BorderBrush = Ui.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Ui.RadiusCard),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = body,
            };
            card.MouseLeftButtonDown += (_, _) =>
            {
                _window.ApplyTheme(Ui.ThemeName(captured));
                _window.SetStatus($"外观已切换为「{Ui.ThemeName(captured)}」");
            };
            cards.Add((mode, card));
            row.Children.Add(card);
        }

        foreach (var (mode, card) in cards)
        {
            var selected = mode == Ui.Current;
            card.BorderBrush = selected ? Ui.Accent : Ui.Line;
            card.BorderThickness = new Thickness(selected ? 2 : 1);
            card.Background = selected ? Ui.Hover : Ui.Card;
        }

        return row;
    }

    private static readonly (string Label, int Size)[] FontChoices =
    {
        ("小（12）", 12), ("标准（13）", 13), ("大（14）", 14), ("很大（15）", 15), ("特大（16）", 16)
    };

    private FrameworkElement FontSizeSelect()
    {
        var labels = FontChoices.Select(item => item.Label).ToList();
        var current = FontChoices.FirstOrDefault(item => item.Size == (int)_settings.FontSize).Label ?? labels[1];
        var (shell, combo) = Ui.Select(labels, 130, current);
        combo.SelectionChanged += (_, _) =>
        {
            var picked = FontChoices.FirstOrDefault(item => item.Label == combo.SelectedItem?.ToString());
            if (picked.Label is null)
            {
                return;
            }

            _settings.FontSize = picked.Size;
            _settings.Save("字号", picked.Label);
            Ui.SetBaseFontSize(picked.Size);
            _window.RebuildUi();
        };
        return shell;
    }

    private FrameworkElement CurrencyInput()
    {
        var (shell, box) = Ui.Input(_settings.CurrencySymbol, 70);
        void Commit()
        {
            var text = box.Text.Trim();
            if (text.Length == 0 || text.Length > 4)
            {
                box.Text = _settings.CurrencySymbol;
                shell.BorderBrush = Ui.Expense;
                return;
            }

            shell.BorderBrush = Ui.Line;
            _settings.CurrencySymbol = text;
            _settings.Save("货币符号", text);
            _window.RebuildUi();
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Commit();
            }
        };
        return shell;
    }

    // ---------------------------------------------------------------- 记一笔

    private UIElement BuildRecord()
    {
        var page = new StackPanel();
        page.Children.Add(Group("默认类型", "打开「记一笔」时预选的类型", KindSelect()));
        page.Children.Add(Group("默认支付方式", "记一笔时预选；可选项来自「种类与支付方式」", DefaultPaySelect()));
        page.Children.Add(Group("保存后自动清空", "关掉后可以连续记同样金额，但要自己点「清空」",
            Ui.Checkbox("保存后连时间一起清空（金额与备注总是会清）", _settings.ClearAfterSave, value =>
            {
                _settings.ClearAfterSave = value;
                _settings.Save("保存后清空", value);
                _window.SetStatus(value ? "已开启：保存后清空金额与备注" : "已关闭：保存后保留金额与备注");
            })));
        // 用户要求（2026-10-09）：这个列表**保留**，只是给个开关（之前我误解成"删掉"，已恢复）
        // 开关必须**立刻生效**：直接切记录页那个列表的显隐，不重建界面（重建那条路试过，开关不生效）
        page.Children.Add(Group("显示「本次打开后已记」", "关掉可以让录入区更简洁；列表本身还在，随时可以再打开",
            Ui.Checkbox("显示这个列表", _settings.ShowSessionList, value =>
            {
                _settings.ShowSessionList = value;
                _settings.Save("显示本次已记", value);
                _window.RecordPage.ApplySessionListVisibility();
                _window.SetStatus(value ? "已显示「本次打开后已记」列表" : "已隐藏「本次打开后已记」列表");
            })));
        return page;
    }

    private FrameworkElement KindSelect()
    {
        var (shell, combo) = Ui.Select(Kinds.All, 100, _settings.DefaultKind);
        combo.SelectionChanged += (_, _) =>
        {
            var kind = combo.SelectedItem?.ToString();
            if (kind is null)
            {
                return;
            }

            _settings.DefaultKind = kind;
            _settings.Save("默认记账类型", kind);
        };
        return shell;
    }

    private FrameworkElement DefaultPaySelect()
    {
        var pays = _settings.PayMethods.Count > 0 ? _settings.PayMethods : Settings.DefaultPayMethods().Keys.ToList();
        var (shell, combo) = Ui.Select(pays, 130, pays.Contains(_settings.DefaultPay) ? _settings.DefaultPay : pays.FirstOrDefault());
        combo.SelectionChanged += (_, _) =>
        {
            var pay = combo.SelectedItem?.ToString();
            if (pay is null)
            {
                return;
            }

            _settings.DefaultPay = pay;
            _settings.Save("默认支付方式", pay);
        };
        return shell;
    }

    // ---------------------------------------------------------------- 查询

    private UIElement BuildQuery()
    {
        var page = new StackPanel();
        page.Children.Add(Group("打开查询页时的默认范围", "保存后立即生效", RangeSelect()));
        page.Children.Add(Group("明细最多显示行数", "只影响明细显示多少行；合计与统计永远按全部记录算", DetailLimitSelect()));
        page.Children.Add(Group("在查询统计里显示哪些视图",
            "可多选，勾完立即生效（多个视图会自动分配到左右/上下区域）：表格、饼状图（3D）、柱状图（竖着的 3D 方柱）、"
            + "方格图（10×10，每格 1%，用最大余数法把 100 格填满）。占比过小的分类不画进图里，下面会用一行小字说明。",
            StatsViewsPicker(forSummary: false)));
        page.Children.Add(Group("在月度汇总里显示哪些视图",
            "同上，但只管月度汇总页（两个页面各自独立，互不影响）。月度汇总页原来的「每天一笔一行」表格不受这里影响。",
            StatsViewsPicker(forSummary: true)));
        return page;
    }

    /// <summary>「分类统计显示哪些视图」多选（0.1 版设计里就有，之前漏了）。</summary>
    private FrameworkElement StatsViewsPicker(bool forSummary)
    {
        // 月度汇总页本来就固定有一张「每天一笔一行」的表格，所以月度那边不再提供"表格"这个视图
        var known = new[] { "表格", "饼状图", "柱状图", "方格图" };
        var choices = forSummary ? new[] { "饼状图", "柱状图", "方格图" } : known;
        var current = forSummary ? _settings.SummaryStatsViews : _settings.StatsViews;   // 仅用于"初始勾选状态"
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var view in choices)
        {
            var box = Ui.Checkbox(view, current.Contains(view), value =>
            {
                // 每次都重新读当前设置：外面那个 current 是"建面板时的快照"，而下面会把设置换成**新列表**，
                // 再用快照算就会丢掉前几次的勾选（用户遇到的"连点几次，只有最后一次生效"就是这个）
                var live = forSummary ? _settings.SummaryStatsViews : _settings.StatsViews;
                var picked = choices.Where(v => live.Contains(v)).ToList();
                if (value)
                {
                    if (!picked.Contains(view))
                    {
                        picked.Add(view);
                    }
                }
                else
                {
                    picked.Remove(view);
                }

                if (picked.Count == 0)
                {
                    picked.Add(forSummary ? "饼状图" : "表格");   // 一个都不选会空着，给个兜底
                }

                // 按固定顺序存：画面分区稳定，不会因为勾选先后而跳来跳去
                var ordered = choices.Where(v => picked.Contains(v)).ToList();
                if (forSummary)
                {
                    _settings.SummaryStatsViews = ordered;
                    _settings.Save("月度汇总统计显示", ordered);
                    _window.SummaryPage.RefreshStats();
                    _window.SetStatus("月度汇总视图：" + string.Join("、", ordered));
                }
                else
                {
                    _settings.StatsViews = ordered;
                    _settings.Save("分类统计显示", ordered);
                    _window.QueryPage.Reload();
                    _window.SetStatus("查询统计视图：" + string.Join("、", ordered));
                }
            });
            box.Margin = new Thickness(0, 0, 14, 0);
            row.Children.Add(box);
        }

        return row;
    }

    private FrameworkElement RangeSelect()
    {
        var (shell, combo) = Ui.Select(Query.Presets, 120, _settings.QueryDefaultRange);
        combo.SelectionChanged += (_, _) =>
        {
            var range = combo.SelectedItem?.ToString();
            if (range is null)
            {
                return;
            }

            _settings.QueryDefaultRange = range;
            _settings.Save("查询默认范围", range);
            _window.QueryPage.ApplyDefaultRange();
        };
        return shell;
    }

    private FrameworkElement DetailLimitSelect()
    {
        var choices = new[] { "5", "10", "20", "50", "100", "200", "500", "不限" };
        var current = _settings.DetailLimit <= 0 ? "不限" : _settings.DetailLimit.ToString(CultureInfo.InvariantCulture);
        var (shell, combo) = Ui.Select(choices, 100, choices.Contains(current) ? current : "500");
        combo.SelectionChanged += (_, _) =>
        {
            var text = combo.SelectedItem?.ToString();
            var limit = text == "不限" ? 0 : int.TryParse(text, out var parsed) ? parsed : 500;
            _settings.DetailLimit = limit;
            _settings.Save("明细最多显示", limit);
            _window.QueryPage.Reload();
        };
        return shell;
    }

    // ---------------------------------------------------------------- 导入导出

    private UIElement BuildExchange()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        _exportStartField = new DateField(monthStart);
        _exportEndField = new DateField(monthEnd);
        var presets = new Dictionary<string, (DateOnly, DateOnly)>
        {
            ["本月"] = (monthStart, monthEnd),
            ["上月"] = (Valid.MonthBefore(monthStart), monthStart.AddDays(-1)),
            ["今年"] = (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            ["全部"] = (new DateOnly(2000, 1, 1), today),
        };

        var page = new StackPanel();
        page.Children.Add(Group("导出范围", "点日期框会弹出日历选择（不用手打，避免格式出错）；「全部」会导出所有年份",
            DateField.Range(presets, _exportStartField, _exportEndField)));
        page.Children.Add(Group("导出为 CSV",
            "带日期、编号、详细备注列，可以再导入回来；导出时会顺手给「来源」为空的记录补上当前设备名（只改导出文件，不动账本）",
            Ui.PrimaryButton("导出 CSV", ExportCsv, 34)));
        page.Children.Add(Group("导出目录", "导出文件都放在这里",
            Ui.InfoRow("当前导出目录", Paths.ExportDir, "打开", () => OpenPath(Paths.ExportDir))));
        page.Children.Add(Group("导入别人的账",
            "先看清要导入什么、可以改「保存为谁」「是否记设备来源」，确认后才写入账本",
            Ui.GhostButton("选择文件并预览…", PickAndImport, 34)));
        return page;
    }

    private void ExportCsv()
    {
        try
        {
            var start = _exportStartField.Value;
            var end = _exportEndField.Value;
            if (end < start)
            {
                _window.SetStatus("结束日期不能早于开始日期，请重新选");
                return;
            }

            var (path, count, filled) = Exchange.ExportWithDevice(
                new QueryFilter { Start = start, End = end, Limit = 0 },
                $"{start:yyyyMMdd}-{end:yyyyMMdd}",
                SourceMode.FillLocal,
                _settings.DeviceName);
            _window.SetStatus(count == 0
                ? "这个范围里没有记录，导出了只有表头的空文件"
                : $"已导出 {count} 条到 {path}" + (filled > 0 ? $"（其中 {filled} 条补上了设备名）" : ""));
        }
        catch (Exception exc)
        {
            _window.ShowError("导出失败：" + exc.Message);
        }
    }

    private void PickAndImport()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要导入的交换文件",
            Filter = "交换文件 (*.csv;*.tsv)|*.csv;*.tsv|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(Paths.ExportDir) ? Paths.ExportDir : Paths.Root,
        };
        if (dialog.ShowDialog(_window) != true)
        {
            return;
        }

        try
        {
            var window = new ImportWindow(_settings, dialog.FileName) { Owner = _window };
            window.ShowDialog();
            if (window.ImportedCount > 0)
            {
                _window.SetStatus($"导入完成：处理了 {window.ImportedCount} 条");
                _window.RebuildUi();
                _window.NotifyDataChanged();
            }
        }
        catch (Exception exc)
        {
            _window.ShowError("打不开这个文件：" + exc.Message);
        }
    }

    // ---------------------------------------------------------------- 数据与备份

    private UIElement BuildBackup()
    {
        var page = new StackPanel();
        page.Children.Add(Group("自动备份", "每次改动账本前，先备份当天文件到备份目录",
            Ui.Checkbox("改动前自动备份", _settings.AutoBackup, value =>
            {
                _settings.AutoBackup = value;
                _settings.Save("自动备份", value);
                _window.SetStatus(value ? "已开启自动备份" : "已关闭自动备份");
            })));
        page.Children.Add(Group("自动备份占用空间",
            "超过上限会自动删最旧的备份（至少保留最新一份）。参考：一条记录约 150 字节、一天 20 笔约 3KB、"
            + "一年原始数据约 1~3MB；备份是「每次改动留一份」，所以 10MB 都够普通用好几年。"
            + $"当前备份占用 {FormatSize(Storage.BackupUsage())}。",
            BackupLimitSelect()));
        page.Children.Add(Group("备份目录", "升级老格式、导入、改删记录前都会往这里放一份",
            Ui.InfoRow("当前备份目录", _settings.BackupDir, "立即备份一次", () =>
            {
                var path = BackupNow();
                _window.SetStatus(path is null ? "没有可备份的文件" : $"已备份到 {path}");
            })));
        page.Children.Add(Group("打开备份位置", "出问题时可以从这里找回旧文件",
            Ui.GhostButton("打开备份目录", () => OpenPath(_settings.BackupDir), 34)));

        // ---------- 存储格式（原来单独一个分区，2026-10-09 并到这里，并且只在真有老数据时才给升级按钮）----------
        page.Children.Add(Group("存储格式",
            $"每天一个 JSONL 文件，一行一条记录、字段带名字（格式版本 {Paths.DataFormatVersion}）；"
            + "老格式（每天一个 TSV）仍然能读，改到哪一天就自动把那一天升级成新格式。",
            Ui.GhostButton("看格式说明", () => OpenPath(Paths.FormatFile), 34)));

        var legacyDays = Storage.LegacyDays();
        if (legacyDays.Count > 0)
        {
            page.Children.Add(Group("老格式的历史数据",
                $"还有 {legacyDays.Count} 天是老格式（{legacyDays.First():yyyy-MM-dd} ~ {legacyDays.Last():yyyy-MM-dd}）。"
                + "不升级也完全能正常用，升级只影响存储方式。",
                Ui.GhostButton("统一升级全部", UpgradeAllLegacy, 34),
                Ui.Hint("升级一天的顺序是：先备份 → 再写新格式 → 写后校验条数 → 最后才删老文件；"
                        + $"任何一步不对就保留老文件，绝不会因为升级丢账（日志：{Path.Combine(Paths.BackupDir, "格式升级日志.txt")}）")));
        }

        return page;
    }

    // ---------------------------------------------------------------- 存储格式（分区已撤，方法留着给自动化/文档引用）

    private UIElement BuildFormat()
    {
        var legacyDays = Storage.LegacyDays();
        var page = new StackPanel();
        page.Children.Add(Group("当前格式",
            $"每天一个 JSONL 文件，一行一条记录、字段带名字（格式版本 {Paths.DataFormatVersion}）。"
            + "老格式（每天一个 TSV）仍然能读，改到哪一天就自动把那一天升级成新格式。",
            Ui.GhostButton("看格式说明", () => OpenPath(Paths.FormatFile), 34)));
        page.Children.Add(Group("老格式的历史数据",
            legacyDays.Count == 0
                ? "没有老格式数据了，全部是新格式"
                : $"还有 {legacyDays.Count} 天是老格式（{legacyDays.First():yyyy-MM-dd} ~ {legacyDays.Last():yyyy-MM-dd}）。"
                  + "不升级也完全能正常用，升级只影响存储方式。",
            legacyDays.Count == 0
                ? Ui.Hint("无需升级")
                : Ui.GhostButton("统一升级全部", UpgradeAllLegacy, 34)));
        page.Children.Add(Group("升级安全性",
            "升级一天的顺序是：先备份 → 再写新格式 → 写后校验条数 → 最后才删老文件；任何一步不对就保留老文件，绝不会因为升级丢账。",
            Ui.Hint($"升级日志：{Path.Combine(Paths.BackupDir, "格式升级日志.txt")}")));
        return page;
    }

    private void UpgradeAllLegacy()
    {
        var days = Storage.LegacyDays();
        if (days.Count == 0)
        {
            _window.SetStatus("没有老格式数据，无需升级");
            return;
        }

            var confirmed = _window.Confirm(
                "统一升级格式？",
                $"要把 {days.Count} 天的老格式文件升级成新格式吗？\n\n"
                + "· 升级前会把老文件备份到「备份」目录（老格式 子文件夹）；\n"
                + "· 每天都会校验一遍，校验不过就放弃那一天、保留老文件；\n"
                + "· 升级期间请不要用老版本（v0.1.0）打开账本。",
                "开始升级",
                "先不升级");
            if (!confirmed)
            {
                return;
            }

        _window.ShowBusy("正在升级格式，请不要关闭窗口……");
        _window.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var (total, upgraded, problems) = Store.UpgradeAll();
                if (problems.Count == 0)
                {
                    _window.SetStatus($"已升级 {upgraded} / {total} 天，全部成功；老文件已备份");
                }
                else
                {
                    _window.SetStatus($"已升级 {upgraded} / {total} 天，有 {problems.Count} 天没成功（老文件保留）");
                    try
                    {
                        File.AppendAllText(Path.Combine(Paths.BackupDir, "格式升级日志.txt"),
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 升级 {upgraded}/{total}{Environment.NewLine}"
                            + string.Join(Environment.NewLine, problems) + Environment.NewLine + Environment.NewLine);
                    }
                    catch (Exception)
                    {
                        // 日志写不了就算了，状态栏已经提示了
                    }
                }
            }
            finally
            {
                _window.HideBusy();
                _window.RebuildUi();
                _window.NotifyDataChanged();
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- 账户

    private UIElement BuildAccount()
    {
        var page = new StackPanel();
        page.Children.Add(Group("用户码",
            "导出/导入时用它区分「这是谁的账」；建议不要改",
            Ui.InfoRow("你的用户码", _account.UserCode, "复制", () =>
            {
                try
                {
                    Clipboard.SetText(_account.UserCode);
                    _window.SetStatus("用户码已复制到剪贴板");
                }
                catch (Exception)
                {
                    _window.SetStatus("复制失败，可以手动抄下来：" + _account.UserCode);
                }
            })));
        page.Children.Add(Group("昵称", "只用来显示，例如「ajmn」",
            AccountField(_account.Nickname, "比如 ajmn", value =>
            {
                _account.Nickname = value;
                _account.Customized = true;
                Config.SaveAccount(_account);
                _window.RebuildUi();
            })));
        page.Children.Add(Group("设备名", "本机记录默认不写设备名；导出时可以自动补上这个名字",
            AccountField(_account.Device, "比如 我的笔记本", value =>
            {
                _account.Device = value;
                Config.SaveAccount(_account);
            })));
        // 当前版本（2026-10-09 用户要求：版本号显示在「账户」里）
        page.Children.Add(Group("当前版本", "报问题或对账时带上这个版本号",
            Ui.InfoRow("当前版本", "v" + Paths.Version, "复制", () =>
            {
                try
                {
                    Clipboard.SetText("记账本 v" + Paths.Version);
                    _window.SetStatus("版本号已复制到剪贴板");
                }
                catch (Exception)
                {
                    _window.SetStatus("版本号：v" + Paths.Version);
                }
            })));
        return page;
    }

    private FrameworkElement AccountField(string initial, string placeholder, Action<string> onCommit)
    {
        var (shell, box) = Ui.Input(initial, 220, placeholder);
        void Commit()
        {
            var text = box.Text.Trim();
            if (text == initial)
            {
                return;
            }

            onCommit(text);
            _window.SetStatus("账户信息已保存");
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Commit();
            }
        };
        return shell;
    }

    // ---------------------------------------------------------------- 种类与支付方式

    private UIElement BuildCategories()
    {
        var categories = Config.LoadCategories();
        var page = new StackPanel();
        page.Children.Add(new ListEditor(
            "收入种类",
            "拖动左边的手柄可以调整顺序；双击名字也能改名。删掉的种类，统计时会并入「其他」，记录本身不变。",
            categories[Kinds.Income],
            items => SaveCategories(Kinds.Income, items)).Root);
        page.Children.Add(new Border { Height = 12 });
        page.Children.Add(new ListEditor(
            "支出种类",
            "同上。删除的种类在统计里会并入「其他」。",
            categories[Kinds.Expense],
            items => SaveCategories(Kinds.Expense, items)).Root);
        page.Children.Add(new Border { Height = 12 });
        page.Children.Add(new ListEditor(
            "支付方式",
            "拖动调整顺序，顺序会影响「记一笔」里的预选顺序。",
            _settings.PayMethods,
            items =>
            {
                _settings.PayMethods = items;
                _settings.Save("支付方式", items);
            }).Root);
        return page;
    }

    private void SaveCategories(string kind, List<string> items)
    {
        var categories = Config.LoadCategories();
        categories[kind] = items;
        Config.SaveCategories(categories);
        _window.SetStatus($"「{kind}种类」已更新，共 {items.Count} 个");
        _window.NotifyDataChanged();
    }

    // ---------------------------------------------------------------- 公共动作

    // ---------------------------------------------------------------- 自动备份占用空间

    private FrameworkElement BackupLimitSelect()
    {
        // 4 档 + 不限：10 / 50 / 100 / 200 MB（依据见 Settings.BackupLimitMb 注释：一条记录约 150 字节）
        var options = new List<(string Label, int Mb)>
        {
            ("10 MB", 10),
            ("50 MB", 50),
            ("100 MB", 100),
            ("200 MB", 200),
            ("不限", 0),
        };

        var current = _settings.BackupLimitMb;
        var currentLabel = options.FirstOrDefault(o => o.Mb == current).Label;
        if (currentLabel is null)
        {
            // 配置文件里是自定义值（比如手改成 1MB）：保留它，不偷偷改回默认
            currentLabel = FormatSize((long)current * 1024 * 1024) + "（当前）";
            options.Insert(0, (currentLabel, current));
        }

        var (shell, combo) = Ui.Select(options.Select(o => o.Label), 132, currentLabel);
        combo.SelectionChanged += (_, _) =>
        {
            var picked = combo.SelectedItem?.ToString();
            if (picked is null)
            {
                return;
            }

            var index = options.FindIndex(o => o.Label == picked);
            if (index < 0)
            {
                return;
            }

            var mb = options[index].Mb;
            _settings.BackupLimitMb = mb;
            _settings.Save("备份上限MB", mb);
            var removed = Storage.PruneBackups();   // 上限调小了要立刻生效
            _window.SetStatus(removed > 0
                ? $"备份上限已设为 {picked}，顺手清掉了 {removed} 个最旧的备份"
                : $"备份上限已设为 {picked}");
        };
        return shell;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024 / 1024:0.#} GB";
        }

        return bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.#} MB" : $"{bytes / 1024.0:0.#} KB";
    }

    private string? BackupNow()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var folder = Path.Combine(_settings.BackupDir, stamp);
            Directory.CreateDirectory(folder);
            var copied = 0;
            foreach (var file in Directory.EnumerateFiles(Paths.DataDir, "*.jsonl", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Paths.DataDir, file);
                var target = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                copied++;
            }

            foreach (var file in new[] { Paths.SettingsFile, Paths.CategoriesFile, Paths.AccountFile })
            {
                if (File.Exists(file))
                {
                    File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
                }
            }

            return copied == 0 ? null : folder;
        }
        catch (Exception exc)
        {
            _window.SetStatus("备份失败：" + exc.Message);
            return null;
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            // 目录：用 explorer 打开（把目录直接当进程启动会报
            // "An error occurred trying to start process ..." —— 用户截图里就是这个）
            if (Directory.Exists(path) || !File.Exists(path))
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                Process.Start("explorer.exe", "\"" + path + "\"");
                return;
            }

            // 文件：交给系统默认程序打开
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception exc)
        {
            _window.SetStatus("打不开：" + path + " —— " + exc.Message);
        }
    }
}
