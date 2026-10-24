using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>查询统计：按月看明细 + 分类小计 + 合计。</summary>
public sealed class QueryPanel
{
    private readonly Settings _settings;
    private readonly MainWindow _window;
    private readonly DateField _startField;
    private readonly DateField _endField;
    private readonly TextBlock _totals;
    private readonly Ui.DataTable _detailTable;
    private readonly Ui.DataTable _categoryTable;

    /// <summary>选中明细表第一行（自动化截图验证选中行配色用）。</summary>
    public bool SelectFirstRow() => _detailTable.SelectFirstRow();

    /// <summary>自动化用：设定分类统计的钻取状态（类型 + 要高亮的分类）。</summary>
    public void ApplyStatsSelection(string kind, string? category)
    {
        _statsKind = string.IsNullOrEmpty(kind) ? "全部" : kind;
        _statsCategory = string.IsNullOrEmpty(category) || category == "全部" ? null : category;
        _suppressStatsReload = true;
        try
        {
            _kindBox.SelectedItem = _statsKind;
            _categoryBox.SelectedItem = _statsCategory ?? "全部";
        }
        finally
        {
            _suppressStatsReload = false;
        }

        Reload();
    }
    private readonly Grid _statsHolder = new();
    private readonly Dictionary<string, Panel> _statsPanels = new();
    private string _statsSignature = "";
    private string _statsViewsSignature = "";

    /// <summary>分类统计：只看哪一类（全部 / 收入 / 支出）。</summary>
    private string _statsKind = "全部";
    /// <summary>分类统计：要高亮的分类（null = 不高亮，其余不正常变暗）。</summary>
    private string? _statsCategory;
    /// <summary>图表高亮用的标签：同名分类分属收支时是「收入·其他」这种，避免"其他"撞车。</summary>
    private string? _statsCategoryLabel;
    private ComboBox _kindBox = null!;
    private ComboBox _categoryBox = null!;
    /// <summary>防止"程序改下拉项"时反过来触发 Reload 造成递归。</summary>
    private bool _suppressStatsReload;

    /// <summary>单位下拉里的「全部单位」这一项。</summary>
    private const string AllUnits = "全部单位";
    /// <summary>排序下拉的三个选项（单选）。</summary>
    private static readonly string[] SortItems = { "消费时间", "录入时间", "金额" };
    /// <summary>当前排序方式（默认按消费时间）。</summary>
    private string _sortMode = SortItems[0];
    private ComboBox _sortBox = null!;
    private bool _suppressSortReload;
    /// <summary>只看哪个单位的账（null = 全部单位）。**统计与绘图**只算这个单位；**明细列表**仍列出全部单位。</summary>
    private string? _unitFilter;
    private ComboBox _unitBox = null!;
    /// <summary>同上，防止程序改单位下拉项时反过来触发 Reload。</summary>
    private bool _suppressUnitReload;

    public Grid Root { get; }

    /// <summary>按设置里的「分类统计显示」建面板：多个视图按接近正方形自动分配区域。</summary>
    private void BuildStatsPanels()
    {
        _statsHolder.Children.Clear();
        _statsHolder.ColumnDefinitions.Clear();
        _statsHolder.RowDefinitions.Clear();
        _statsPanels.Clear();

        var known = new[] { "表格", "饼状图", "柱状图", "方格图" };
        var views = _settings.StatsViews.Where(view => known.Contains(view)).ToList();
        if (views.Count == 0)
        {
            views.Add("表格");
        }

        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(views.Count)));
        var rows = Math.Max(1, (int)Math.Ceiling(views.Count / (double)columns));
        for (var i = 0; i < columns; i++)
        {
            _statsHolder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // 行高：**含「表格」的那一行按内容自动定高**，其余行分掉剩下的高度。
        // 以前四格一律平分高度 → 纯支出只有 8 个分类，表格也只能显示 5 行（用户 2026-10-24 反馈）。
        // 现在分类少 → 表格那行按行数自然变高；分类多（收支都有）→ Auto 行有上限（见下面的 62%），表格内部滚动。
        for (var i = 0; i < rows; i++)
        {
            var rowIndex = i;
            var hasTable = views.Where((view, index) => index / columns == rowIndex).Contains("表格");
            _statsHolder.RowDefinitions.Add(new RowDefinition
            {
                Height = hasTable ? GridLength.Auto : new GridLength(1, GridUnitType.Star),
            });
        }

        for (var index = 0; index < views.Count; index++)
        {
            var view = views[index];
            var body = new Grid();   // 必须是 Grid：StackPanel 会给子元素无限高度，里面的 ScrollViewer 就滚不动了
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // 图表区（有确定高度才能滚）
            content.Children.Add(Ui.Label(view, Ui.FontSmall, Ui.Muted, bold: true));
            content.Children.Add(body);
            Grid.SetRow(body, 1);
            var card = Ui.CardPanel(content, padding: 10, margin: new Thickness(3));
            if (_statsHolder.RowDefinitions[index / columns].Height.IsAuto)
            {
                // 再长的表格 / 再多的图例，最多吃掉可用高度的 62%：剩下 38% 留给另一行的图形，
                // 免得「分类特别多」时把柱状图、方格图压成一条缝。
                card.SetBinding(FrameworkElement.MaxHeightProperty, new System.Windows.Data.Binding("ActualHeight")
                {
                    Source = _statsHolder,
                    Converter = new HeightCap(0.62),
                });
            }

            Grid.SetRow(card, index / columns);
            Grid.SetColumn(card, index % columns);
            _statsHolder.Children.Add(card);
            _statsPanels[view] = body;
        }
    }

    /// <summary>把分类小计铺到各个面板（表格填行、图形重画）。</summary>
    private void RenderStats(IReadOnlyList<CategoryTotal> data)
    {
        // 只勾了收入或支出时，只画该类（其余分类不参与统计）
        data = _statsKind == "全部" ? data : data.Where(item => item.Kind == _statsKind).ToList();
        var symbol = _settings.CurrencySymbol;
        foreach (var (view, body) in _statsPanels)
        {
            body.Children.Clear();
            if (view == "表格")
            {
                var table = new Ui.DataTable(new (string, double, HorizontalAlignment)[]
                {
                    ("分类", 1.6, HorizontalAlignment.Left),
                    ("笔数", 0.5, HorizontalAlignment.Right),
                    ("金额", 1.2, HorizontalAlignment.Right),
                }, compact: true);
                if (data.Count == 0)
                {
                    table.AddEmpty("没有分类数据");
                }
                else
                {
                    foreach (var item in data)
                    {
                        table.AddRow(new[]
                        {
                            $"{item.Kind}·{item.Category}",
                            item.Count.ToString(),
                            Valid.Money(item.Amount.ToString(CultureInfo.InvariantCulture), symbol)
                        });
                    }
                }

                body.Children.Add(table.Root);
            }
            else
            {
                // 图形放在可滚动容器里：面板再小也不会被裁掉；给一条细滚动条，让人看得出下面还有内容
                body.Children.Add(Ui.HoverScroll(Charts.Draw(view, data, compact: false, _statsCategoryLabel), showBar: true));
            }
        }
    }

    public QueryPanel(Settings settings, MainWindow window)
    {
        _settings = settings;
        _window = window;

        Root = new Grid();
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var today = DateOnly.FromDateTime(DateTime.Now);

        // ---------- 工具条 ----------
        var bar = new StackPanel { Orientation = Orientation.Vertical };
        // 第一行：快捷范围（Core.Query.Presets 里已备好 10 个；v0.1 / v0.2.1 当时就是这些）
        var presetsRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var name in Query.Presets)
        {
            var captured = name;
            var presetButton = Ui.GhostButton(name, () =>
            {
                var (presetStart, presetEnd) = Query.ResolvePreset(captured);
                _startField.Set(presetStart);
                _endField.Set(presetEnd);
                Reload();
            }, 30);
            presetButton.FontSize = Ui.FontSmall;
            presetButton.Margin = new Thickness(0, 0, 6, 0);
            presetsRow.Children.Add(presetButton);
        }

        bar.Children.Add(presetsRow);

        // 第二行：起止日期（点日期框弹日历，不用手打）+ 查询/编辑/删除
        var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        _startField = new DateField(monthStart);
        _endField = new DateField(monthEnd);
        rangeRow.Children.Add(DateField.Range(new Dictionary<string, (DateOnly Start, DateOnly End)>(), _startField, _endField));

        var queryButton = Ui.PrimaryButton("查询", Reload);
        queryButton.Margin = new Thickness(12, 0, 0, 0);
        rangeRow.Children.Add(queryButton);

        var editButton = Ui.GhostButton("编辑选中", EditSelected);
        editButton.Margin = new Thickness(8, 0, 0, 0);
        rangeRow.Children.Add(editButton);
        var deleteButton = Ui.GhostButton("删除选中", DeleteSelected);
        deleteButton.Margin = new Thickness(8, 0, 0, 0);
        rangeRow.Children.Add(deleteButton);

        bar.Children.Add(rangeRow);

        // 第三行：分类统计（与 v0.1 一致 —— 类型 + 分类 两个独立下拉；分类只在图上高亮，其余变暗）
        var statsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        statsRow.Children.Add(Ui.Label("分类统计", Ui.FontBody, Ui.Muted));
        var (kindShell, kindBox) = Ui.Select(new[] { "全部", Kinds.Income, Kinds.Expense }, 96, _statsKind);
        _kindBox = kindBox;
        kindShell.Margin = new Thickness(8, 0, 14, 0);
        statsRow.Children.Add(kindShell);
        statsRow.Children.Add(Ui.Label("分类", Ui.FontBody, Ui.Muted));
        var categoryItems = new List<string> { "全部" };
        try
        {
            var configured = Core.Config.LoadCategories();
            foreach (var pair in configured)
            {
                foreach (var name in pair.Value)
                {
                    if (!categoryItems.Contains(name))
                    {
                        categoryItems.Add(name);
                    }
                }
            }

            // 同名分类分属收入/支出时（经典例子：「其他」），下拉里必须区分开，否则两者会撞在一起
            var expenseNames = configured.TryGetValue(Kinds.Expense, out var expenseList) ? expenseList : new List<string>();
            var incomeNames = configured.TryGetValue(Kinds.Income, out var incomeList) ? incomeList : new List<string>();
            foreach (var name in expenseNames.Where(item => incomeNames.Contains(item)))
            {
                categoryItems.Remove(name);
                categoryItems.Add(name + "（支出）");
                categoryItems.Add(name + "（收入）");
            }
        }
        catch (Exception)
        {
        }

        var (catShell, catBox) = Ui.Select(categoryItems, 140, _statsCategory ?? "全部");
        _categoryBox = catBox;
        catShell.Margin = new Thickness(8, 0, 14, 0);
        statsRow.Children.Add(catShell);

        // 单位：统计与绘图只算选中的单位；明细列表仍列出全部单位（用户 2026-10-15 的要求）
        statsRow.Children.Add(Ui.Label("单位", Ui.FontBody, Ui.Muted));
        var (unitShell, unitBox) = Ui.Select(new[] { AllUnits }, 110, AllUnits);
        _unitBox = unitBox;
        unitShell.Margin = new Thickness(8, 0, 0, 0);
        statsRow.Children.Add(unitShell);
        unitBox.SelectionChanged += (_, _) =>
        {
            if (_suppressUnitReload)
            {
                return;
            }

            var picked = unitBox.SelectedItem?.ToString() ?? AllUnits;
            _unitFilter = picked == AllUnits ? null : picked;
            Reload();
        };
        bar.Children.Add(statsRow);

        // 排序：只影响下方明细列表的顺序（统计与图表与顺序无关）
        // 消费时间 = 账单上的日期+时间；录入时间 = 按编号推算的录入次序（只是排序键，不写进数据）；
        // 金额 = 金额大小。都是单选，默认「消费时间」。
        var sortRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        sortRow.Children.Add(Ui.Label("排序", Ui.FontBody, Ui.Muted));
        var (sortShell, sortBox) = Ui.Select(SortItems, 130, SortItems[0]);
        _sortBox = sortBox;
        sortShell.Margin = new Thickness(8, 0, 0, 0);
        sortRow.Children.Add(sortShell);
        sortRow.Children.Add(Ui.Hint("明细列表按这个排；消费时间=账单时间，录入时间=按编号推算（不写进数据），金额=金额大小"));
        sortBox.SelectionChanged += (_, _) =>
        {
            if (_suppressSortReload)
            {
                return;
            }

            _sortMode = sortBox.SelectedItem?.ToString() ?? SortItems[0];
            Reload();
        };
        bar.Children.Add(sortRow);

        kindBox.SelectionChanged += (_, _) =>
        {
            if (_suppressStatsReload)
            {
                return;
            }

            // 只换类型；**不动分类下拉**（v0.2 的 bug 就是这里把小分类跳回了「全部」）
            _statsKind = kindBox.SelectedItem?.ToString() ?? "全部";
            Reload();
        };

        catBox.SelectionChanged += (_, _) =>
        {
            if (_suppressStatsReload)
            {
                return;
            }

            // 分类只影响"图上高亮哪个分类"，不改类型、也不会把选择跳回「全部」（修 v0.2 的 bug）
            var picked = catBox.SelectedItem?.ToString() ?? "全部";
            // 「其他（收入）」这种：明细仍按裸分类名过滤；图表高亮用「收入·其他」
            var bare = picked;
            string? kindOfPicked = null;
            if (picked.EndsWith("（收入）", StringComparison.Ordinal)) { bare = picked[..^4]; kindOfPicked = Kinds.Income; }
            else if (picked.EndsWith("（支出）", StringComparison.Ordinal)) { bare = picked[..^4]; kindOfPicked = Kinds.Expense; }
            _statsCategory = bare == "全部" ? null : bare;
            _statsCategoryLabel = _statsCategory is null ? null : (kindOfPicked is null ? _statsCategory : kindOfPicked + "·" + _statsCategory);
            Reload();
        };

        var barCard = Ui.CardPanel(bar, padding: 14);
        Grid.SetRow(barCard, 0);
        Root.Children.Add(barCard);

        // ---------- 合计 ----------
        _totals = Ui.Label("", Ui.FontTitle, Ui.Text, bold: true);
        _totals.Margin = new Thickness(4, 12, 0, 8);
        Grid.SetRow(_totals, 1);
        Root.Children.Add(_totals);

        // ---------- 明细 + 分类小计 ----------
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _detailTable = new Ui.DataTable(new (string, double, HorizontalAlignment)[]
        {
            ("日期", 1.0, HorizontalAlignment.Left),
            ("时间", 0.6, HorizontalAlignment.Left),
            ("类型", 0.5, HorizontalAlignment.Left),
            ("分类", 0.9, HorizontalAlignment.Left),
            ("金额", 0.9, HorizontalAlignment.Right),
            ("支付方式", 0.8, HorizontalAlignment.Left),
            ("备注", 1.3, HorizontalAlignment.Left),
            ("详情", 0.55, HorizontalAlignment.Center),
        });
        var detailCard = Ui.CardPanel(_detailTable.Root, padding: 6);
        detailCard.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(detailCard, 0);
        split.Children.Add(detailCard);
        _detailTable.OnActivate = payload =>
        {
            if (payload is Record record)
            {
                OpenEditor(record);
            }
        };

        _categoryTable = new Ui.DataTable(new (string, double, HorizontalAlignment)[]
        {
            ("类型", 0.6, HorizontalAlignment.Left),
            ("分类", 1.1, HorizontalAlignment.Left),
            ("笔数", 0.5, HorizontalAlignment.Right),
            ("金额", 1.0, HorizontalAlignment.Right),
            ("占比", 0.6, HorizontalAlignment.Right),
        });
        var statsCard = Ui.CardPanel(_statsHolder, padding: 8);
        Grid.SetColumn(statsCard, 1);
        split.Children.Add(statsCard);
        BuildStatsPanels();

        Grid.SetRow(split, 2);
        Root.Children.Add(split);
    }

    /// <summary>按设置里的「查询默认范围」重设年月（设置页改完立即调用）。</summary>
    public void ApplyDefaultRange()
    {
        var (start, end) = Query.ResolvePreset(_settings.QueryDefaultRange);
        _startField.Set(start);
        _endField.Set(end);
        Reload();
    }

    /// <summary>点「详情」：在窗口内浮一个卡片，显示完整信息（含详细备注与记录设备）。</summary>
    private void ShowDetail(object? payload)
    {
        if (payload is Record record)
        {
            _window.ShowOverlay(DetailCard.Build(record, _settings, _window.HideOverlay));
        }
    }

    /// <summary>双击明细行 / 点「编辑选中」。</summary>
    private void EditSelected()
    {
        if (_detailTable.SelectedPayload is Record record)
        {
            OpenEditor(record);
        }
        else
        {
            _window.SetStatus("先在明细里点选一行，再点「编辑选中」（也可以直接双击那一行）");
        }
    }

    private void OpenEditor(Record record)
    {
        var dialog = new RecordDialog(record, _settings) { Owner = _window };
        dialog.ShowDialog();
        if (dialog.Saved)
        {
        Core.Log.Info($"改记录：{record.Date:yyyy-MM-dd} {record.Kind}/{record.Category}/{record.Amount}");
            _window.NotifyDataChanged();
        }
    }

    /// <summary>删除选中行（会先确认）。</summary>
    private void DeleteSelected()
    {
        if (_detailTable.SelectedPayload is not Record record)
        {
            _window.SetStatus("先在明细里点选一行，再点「删除选中」");
            return;
        }

            var confirmed = _window.Confirm(
                "删除这一笔？",
                $"确定删除这一笔吗？\n\n{record.Date:yyyy-MM-dd} {record.Time}　{record.Kind} {record.Category}　{record.Amount}\n\n"
                + "删除后编号不会回收；开启自动备份时，改动前会先备份当天文件。",
                "删除",
                "算了");
            if (!confirmed)
            {
                return;
            }

        try
        {
            if (Storage.Delete(record))
            {
        Core.Log.Info($"删记录：{record.Date:yyyy-MM-dd} {record.Kind}/{record.Category}/{record.Amount}");
                _window.NotifyDataChanged();
            }
        }
        catch (Exception exc)
        {
            _window.ShowError("删不掉：" + exc.Message);
        }
    }

    public void Reload()
    {
        var start = _startField.Value;
        var end = _endField.Value;
        if (end < start)
        {
            (start, end) = (end, start);   // 手选了反的也能用
        }
        // ① 先"不过滤单位"跑一次：既用来收集这个范围里出现过哪些单位，也作为**明细列表**的数据源
        //    （用户要求：明细列表要显示其他单位的账单；而统计与绘图只算当前单位）
        var all = Query.Run(new QueryFilter
        {
            Start = start,
            End = end,
            Limit = _settings.DetailLimit <= 0 ? 0 : _settings.DetailLimit,
        });

        var fallbackUnit = _settings.CurrencySymbol;
        var unitItems = new List<string> { AllUnits };
        foreach (var item in all.Records)
        {
            var unit = item.Unit.Length > 0 ? item.Unit : fallbackUnit;
            if (!unitItems.Contains(unit))
            {
                unitItems.Add(unit);
            }
        }

        _suppressUnitReload = true;
        try
        {
            _unitBox.ItemsSource = unitItems;
            if (_unitFilter is not null && unitItems.Contains(_unitFilter))
            {
                _unitBox.SelectedItem = _unitFilter;
            }
            else
            {
                _unitBox.SelectedItem = AllUnits;
                _unitFilter = null;
            }
        }
        finally
        {
            _suppressUnitReload = false;
        }

        // ② 统计与绘图：只看当前单位的账（老记录没有单位 → 算默认单位）
        var result = _unitFilter is null
            ? all
            : Query.Run(new QueryFilter
            {
                Start = start,
                End = end,
                Limit = _settings.DetailLimit <= 0 ? 0 : _settings.DetailLimit,
                Unit = _unitFilter,
            });

        var symbol = _unitFilter ?? _settings.CurrencySymbol;   // 统计口径的符号 = 当前单位
        _totals.Text = $"共 {result.TotalCount} 笔　|　收入 {Valid.Money(result.Income.ToString(CultureInfo.InvariantCulture), symbol)}"
                       + $"　|　支出 {Valid.Money(result.Expense.ToString(CultureInfo.InvariantCulture), symbol)}"
                       + $"　|　结余 {Valid.Money(result.Balance.ToString(CultureInfo.InvariantCulture), symbol)}"
                       + (result.Truncated ? $"　（只显示前 {_settings.DetailLimit} 行）" : "") + (_statsCategory is null ? "" : $"　（明细只显示「{_statsCategory}」）")
                        + (_unitFilter is null ? "　（统计含全部单位）" : "　（统计只算单位：" + _unitFilter + "）");

        _detailTable.Clear();

        // 选了某个分类：下方明细只显示该分类（图表仍画全部该类，只做高亮）
        var detailRecords = _statsCategory is null
            ? all.Records
            : all.Records.Where(record => record.Category == _statsCategory).ToList();

        // 排序（用户 2026-10-16 要求，三选一）：只影响明细列表顺序，统计与图表不受影响。
        //   消费时间 = 账单上的日期 + 时间；
        //   录入时间 = 按「编号」推算的录入次序（编号里的流水号越大越后录）——**只是排序键，不写进数据**；
        //   金额     = 金额大小。
        // 一律"新的/大的在前"，同键再用编号兜底，保证顺序稳定。
        detailRecords = _sortMode switch
        {
            "录入时间" => detailRecords
                .OrderByDescending(record => Valid.IdSequence(record.Id))
                .ThenByDescending(record => record.Date)
                .ToList(),
            "金额" => detailRecords
                .OrderByDescending(record => record.AmountValue)
                .ThenByDescending(record => record.Date)
                .ThenByDescending(record => record.Time, StringComparer.Ordinal)
                .ToList(),
            _ => detailRecords
                .OrderByDescending(record => record.Date)
                .ThenByDescending(record => record.Time, StringComparer.Ordinal)
                .ThenByDescending(record => Valid.IdSequence(record.Id))
                .ToList(),
        };

        if (detailRecords.Count == 0)
        {
            _detailTable.AddEmpty(_statsCategory is null
                ? "这个范围还没有记录"
                : $"「{_statsCategory}」在这个范围里没有记录");
        }
        else
        {
            foreach (var record in detailRecords)
            {
                _detailTable.AddRow(new[]
                {
                    record.Date.ToString("yyyy-MM-dd"),
                    record.Time,
                    record.Kind,
                    record.Category,
                    Valid.Money(record.Amount, record.Unit.Length > 0 ? record.Unit : fallbackUnit),   // 明细逐行显示它自己的单位
                    record.Pay,
                    record.Note
                }, payload: record, onAction: ShowDetail);
            }
        }

        _categoryTable.Clear();
        if (result.ByCategory.Count == 0)
        {
            _categoryTable.AddEmpty("没有分类数据");
        }
        else
        {
            foreach (var item in result.ByCategory)
            {
                _categoryTable.AddRow(new[]
                {
                    item.Kind,
                    item.Category,
                    item.Count.ToString(),
                    Valid.Money(item.Amount.ToString(CultureInfo.InvariantCulture), symbol),
                    $"{item.Share * 100:0.0}%"
                });
            }
        }

        // 设置里勾选的视图变了：必须**重建分区**（原来只重画旧分区 → 表现成「设置没生效」）
        
        // 分类统计的下拉：按"只看哪一类"过滤后重建分类清单（改下拉时用 _suppressStatsReload 防递归）

        var viewsSignature = string.Join(",", _settings.StatsViews);
        
        if (viewsSignature != _statsViewsSignature)
        {
            _statsViewsSignature = viewsSignature;
            BuildStatsPanels();
            _statsSignature = "";      // 分区换了，数据必须重画
        }

        // 统计结果没变就不重画图表（切页/刷新时省下重建开销）
        var signature = string.Join("|", result.ByCategory.Select(item => $"{item.Kind}{item.Category}{item.Count}{item.Amount}")) + $"|{_statsKind}|{_statsCategory}";
        if (signature != _statsSignature)
        {
            _statsSignature = signature;
            RenderStats(result.ByCategory);
        }
    }

    /// <summary>把「可用高度的比例」换算成 MaxHeight（0.62 = 最多占 62%）。</summary>
    private sealed class HeightCap : System.Windows.Data.IValueConverter
    {
        public HeightCap(double ratio)
        {
            _ratio = ratio;
        }

        private readonly double _ratio;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double height && height > 0 ? height * _ratio : double.PositiveInfinity;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
