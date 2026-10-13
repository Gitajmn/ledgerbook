using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>月度汇总：按天列出笔数与收支，可导出 CSV。</summary>
public sealed class SummaryPanel
{
    private readonly Settings _settings;
    private readonly MainWindow _window;
    private readonly ComboBox _yearBox;
    private readonly ComboBox _monthBox;
    private readonly TextBlock _totals;
    private readonly Ui.DataTable _table;
    private Grid _statsHolder = new();
    private readonly Dictionary<string, Panel> _statsPanels = new();
    private string _statsViewsSignature = "";

    public Grid Root { get; }

    public SummaryPanel(Settings settings, MainWindow window)
    {
        _settings = settings;
        _window = window;

        Root = new Grid();
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 统计图

        var today = DateOnly.FromDateTime(DateTime.Now);
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

        bar.Children.Add(Ui.PrimaryButton("生成汇总", Reload));

        var exportButton = Ui.GhostButton("导出月度汇总 CSV", Export);
        exportButton.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(exportButton);

        var openButton = Ui.GhostButton("复制导出路径", () => CopyPath(Paths.ExportDir));
        openButton.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(openButton);

        var barCard = Ui.CardPanel(bar, padding: 14);
        Grid.SetRow(barCard, 0);
        Root.Children.Add(barCard);

        _totals = Ui.Label("", Ui.FontTitle, Ui.Text, bold: true);
        _totals.Margin = new Thickness(4, 12, 0, 8);
        Grid.SetRow(_totals, 1);
        Root.Children.Add(_totals);

        _table = new Ui.DataTable(new (string, double, HorizontalAlignment)[]
        {
            ("日期", 1.2, HorizontalAlignment.Left),
            ("星期", 0.7, HorizontalAlignment.Left),
            ("笔数", 0.6, HorizontalAlignment.Right),
            ("收入", 1.1, HorizontalAlignment.Right),
            ("支出", 1.1, HorizontalAlignment.Right),
            ("结余", 1.1, HorizontalAlignment.Right),
        });
        Grid.SetRow(_table.Root, 2);

        // 统计图卡片（视图由「设置 → 查询 → 在月度汇总里显示哪些视图」决定，和查询统计分开）
        _statsHolder = new Grid();
        var statsScroll = new ScrollViewer
        {
            Content = _statsHolder,
            MaxHeight = 400,   // 不钉死高度：卡片跟着内容走，留白自然就没了；内容超了才在卡片内滚
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,   // 不显示滚动条，但鼠标悬停时滚轮/触控板可以滚
            Focusable = false,   // 不吃焦点：免得出现虚线焦点框
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var statsCard = Ui.CardPanel(statsScroll, padding: 8);
        statsCard.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(statsCard, 3);
        Root.Children.Add(statsCard);
        Root.Children.Add(_table.Root);
    }


    /// <summary>设置里改了「月度汇总显示哪些视图」后调用：重建统计卡片并重画。</summary>
    public void RefreshStats()
    {
        BuildStatsCards();
        Reload();
    }
    public void Reload()
    {
        if (!int.TryParse(_yearBox.SelectedItem?.ToString(), out var year)
            || !int.TryParse(_monthBox.SelectedItem?.ToString(), out var month))
        {
            return;
        }

        var summary = Summary.Build(year, month);
        var symbol = _settings.CurrencySymbol;
        _totals.Text = $"{Paths.MonthName(year, month)}：{summary.Count} 笔"
                       + $"　|　收入 {Valid.Money(summary.Income.ToString(CultureInfo.InvariantCulture), symbol)}"
                       + $"　|　支出 {Valid.Money(summary.Expense.ToString(CultureInfo.InvariantCulture), symbol)}"
                       + $"　|　结余 {Valid.Money(summary.Balance.ToString(CultureInfo.InvariantCulture), symbol)}";

        _table.Clear();
        var hasAny = false;
        foreach (var day in summary.Days.Where(item => item.Count > 0))
        {
            hasAny = true;
            _table.AddRow(new[]
            {
                day.Day.ToString("yyyy-MM-dd"),
                day.Weekday,
                day.Count.ToString(CultureInfo.InvariantCulture),
                Valid.Money(day.Income.ToString(CultureInfo.InvariantCulture), symbol),
                Valid.Money(day.Expense.ToString(CultureInfo.InvariantCulture), symbol),
                Valid.Money(day.Balance.ToString(CultureInfo.InvariantCulture), symbol)
            });
        }

        if (!hasAny)
        {
            _table.AddEmpty("这个月还没有记录");
        }

        // 分类统计图（视图由设置决定；收入/支出各一张由 Charts.Draw 内部拆开）
        var category = Query.Run(new QueryFilter
        {
            Start = new DateOnly(year, month, 1),
            End = new DateOnly(year, month, DateTime.DaysInMonth(year, month)),
            Limit = 0,
        }).ByCategory;
        var viewsSignature = string.Join(",", _settings.SummaryStatsViews);
        if (viewsSignature != _statsViewsSignature)
        {
            _statsViewsSignature = viewsSignature;
            BuildStatsCards();
        }

        RenderStatsCards(category);
    }

    /// <summary>按「月度汇总显示哪些视图」铺统计卡片（和查询统计各自独立）。</summary>
    private void BuildStatsCards()
    {
        _statsHolder.Children.Clear();
        _statsHolder.ColumnDefinitions.Clear();
        _statsHolder.RowDefinitions.Clear();
        _statsPanels.Clear();

        var known = new[] { "表格", "饼状图", "柱状图", "方格图" };
        var views = _settings.SummaryStatsViews.Where(v => known.Contains(v)).ToList();
        if (views.Count == 0)
        {
            views.Add("饼状图");
        }

        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(views.Count)));
        var rows = Math.Max(1, (int)Math.Ceiling(views.Count / (double)columns));
        for (var i = 0; i < columns; i++)
        {
            _statsHolder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < rows; i++)
        {
            _statsHolder.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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

    private void RenderStatsCards(IReadOnlyList<CategoryTotal> data)
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
                    ("占比", 0.6, HorizontalAlignment.Right),
                }, compact: true);
                if (data.Count == 0)
                {
                    table.AddEmpty("没有分类数据");
                }

                foreach (var item in data)
                {
                    table.AddRow(new[]
                    {
                        item.Category,
                        item.Count.ToString(),
                        Valid.Money(item.Amount.ToString(CultureInfo.InvariantCulture), symbol),
                        $"{item.Share * 100:0.0}%"
                    });
                }

                body.Children.Add(table.Root);
                continue;
            }

            body.Children.Add(Ui.HoverScroll(Charts.Draw(view, data, compact: true)));
        }
    }

        /// <summary>复制路径（本程序不再打开目录）。</summary>
    private void CopyPath(string path)
    {
        _window.SetStatus(Ui.CopyText(path) ? "已复制路径：" + path : "剪贴板写不进去——路径：" + path);
    }

    private void Export()
    {
        if (!int.TryParse(_yearBox.SelectedItem?.ToString(), out var year)
            || !int.TryParse(_monthBox.SelectedItem?.ToString(), out var month))
        {
            return;
        }

        try
        {
            var summary = Summary.Build(year, month);
            if (summary.Count == 0)
            {
                _window.SetStatus("这个月还没有记录，没什么可导出的");
                return;
            }

            var path = Summary.ExportCsv(summary, _settings.CurrencySymbol);
            _window.SetStatus($"已导出：{path}");
        }
        catch (Exception exc)
        {
            _window.ShowError("导出失败：" + exc.Message);
        }
    }

}
