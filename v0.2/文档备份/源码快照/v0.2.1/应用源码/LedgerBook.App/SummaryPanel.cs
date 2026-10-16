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

    public Grid Root { get; }

    public SummaryPanel(Settings settings, MainWindow window)
    {
        _settings = settings;
        _window = window;

        Root = new Grid();
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

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

        var openButton = Ui.GhostButton("打开导出目录", () => OpenPath(Paths.ExportDir));
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
        Root.Children.Add(_table.Root);
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
            MessageBox.Show(_window, exc.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception exc)
        {
            _window.SetStatus("打不开目录：" + exc.Message);
        }
    }
}
