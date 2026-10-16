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
    private readonly ComboBox _yearBox;
    private readonly ComboBox _monthBox;
    private readonly TextBlock _totals;
    private readonly Ui.DataTable _detailTable;
    private readonly Ui.DataTable _categoryTable;
    private readonly Grid _statsHolder = new();
    private readonly Dictionary<string, Panel> _statsPanels = new();
    private string _statsSignature = "";
    private string _statsViewsSignature = "";

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

        for (var i = 0; i < rows; i++)
        {
            _statsHolder.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
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
            Grid.SetRow(card, index / columns);
            Grid.SetColumn(card, index % columns);
            _statsHolder.Children.Add(card);
            _statsPanels[view] = body;
        }
    }

    /// <summary>把分类小计铺到各个面板（表格填行、图形重画）。</summary>
    private void RenderStats(IReadOnlyList<CategoryTotal> data)
    {
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
                // 图形放在可滚动容器里：面板再小也不会被裁掉
                body.Children.Add(Ui.HoverScroll(Charts.Draw(view, data)));
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
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(Ui.Label("年份", Ui.FontBody, Ui.Muted));
        var years = Enumerable.Range(today.Year - 6, 8).Select(year => year.ToString()).ToList();
        var (yearShell, yearBox) = Ui.Select(years, 92, today.Year.ToString());
        _yearBox = yearBox;
        yearShell.Margin = new Thickness(8, 0, 14, 0);
        bar.Children.Add(yearShell);

        bar.Children.Add(Ui.Label("月份", Ui.FontBody, Ui.Muted));
        var months = Enumerable.Range(1, 12).Select(month => month.ToString("D2")).ToList();
        var (monthShell, monthBox) = Ui.Select(months, 76, today.Month.ToString("D2"));
        _monthBox = monthBox;
        monthShell.Margin = new Thickness(8, 0, 14, 0);
        bar.Children.Add(monthShell);

        var thisMonth = Ui.GhostButton("本月", () =>
        {
            var now = DateOnly.FromDateTime(DateTime.Now);
            _yearBox.SelectedItem = now.Year.ToString();
            _monthBox.SelectedItem = now.Month.ToString("D2");
            Reload();
        });
        thisMonth.Margin = new Thickness(0, 0, 8, 0);
        bar.Children.Add(thisMonth);

        var lastMonth = Ui.GhostButton("上月", () =>
        {
            var previous = Valid.MonthBefore(new DateOnly(DateOnly.FromDateTime(DateTime.Now).Year, DateOnly.FromDateTime(DateTime.Now).Month, 1));
            _yearBox.SelectedItem = previous.Year.ToString();
            _monthBox.SelectedItem = previous.Month.ToString("D2");
            Reload();
        });
        lastMonth.Margin = new Thickness(0, 0, 8, 0);
        bar.Children.Add(lastMonth);

        bar.Children.Add(Ui.PrimaryButton("查询", Reload));

        var editButton = Ui.GhostButton("编辑选中", EditSelected);
        editButton.Margin = new Thickness(12, 0, 0, 0);
        bar.Children.Add(editButton);
        var deleteButton = Ui.GhostButton("删除选中", DeleteSelected);
        deleteButton.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(deleteButton);

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
        var (start, _) = Query.ResolvePreset(_settings.QueryDefaultRange);
        var years = _yearBox.Items.Cast<object>().Select(item => item.ToString()).ToList();
        if (!years.Contains(start.Year.ToString()))
        {
            years.Add(start.Year.ToString());
            years.Sort(StringComparer.Ordinal);
            _yearBox.ItemsSource = years;
        }

        _yearBox.SelectedItem = start.Year.ToString();
        _monthBox.SelectedItem = start.Month.ToString("D2");
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
            _window.SetStatus($"已改：{record.Date:yyyy-MM-dd} {record.Category} {record.Amount}");
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
            if (Storage.Delete(record.Id))
            {
                _window.SetStatus($"已删除 {record.Date:yyyy-MM-dd} {record.Category} {record.Amount}");
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
        if (!int.TryParse(_yearBox.SelectedItem?.ToString(), out var year)
            || !int.TryParse(_monthBox.SelectedItem?.ToString(), out var month))
        {
            return;
        }

        var start = new DateOnly(year, month, 1);
        var end = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var result = Query.Run(new QueryFilter
        {
            Start = start,
            End = end,
            Limit = _settings.DetailLimit <= 0 ? 0 : _settings.DetailLimit,
        });

        var symbol = _settings.CurrencySymbol;
        _totals.Text = $"共 {result.TotalCount} 笔　|　收入 {Valid.Money(result.Income.ToString(CultureInfo.InvariantCulture), symbol)}"
                       + $"　|　支出 {Valid.Money(result.Expense.ToString(CultureInfo.InvariantCulture), symbol)}"
                       + $"　|　结余 {Valid.Money(result.Balance.ToString(CultureInfo.InvariantCulture), symbol)}"
                       + (result.Truncated ? $"　（只显示前 {_settings.DetailLimit} 行）" : "");

        _detailTable.Clear();
        if (result.Records.Count == 0)
        {
            _detailTable.AddEmpty("这个月还没有记录");
        }
        else
        {
            foreach (var record in result.Records)
            {
                _detailTable.AddRow(new[]
                {
                    record.Date.ToString("yyyy-MM-dd"),
                    record.Time,
                    record.Kind,
                    record.Category,
                    Valid.Money(record.Amount, symbol),
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
        
        var viewsSignature = string.Join(",", _settings.StatsViews);
        
        if (viewsSignature != _statsViewsSignature)
        {
            _statsViewsSignature = viewsSignature;
            BuildStatsPanels();
            _statsSignature = "";      // 分区换了，数据必须重画
        }

        // 统计结果没变就不重画图表（切页/刷新时省下重建开销）
        var signature = string.Join("|", result.ByCategory.Select(item => $"{item.Kind}{item.Category}{item.Count}{item.Amount}"));
        if (signature != _statsSignature)
        {
            _statsSignature = signature;
            RenderStats(result.ByCategory);
        }
    }
}
