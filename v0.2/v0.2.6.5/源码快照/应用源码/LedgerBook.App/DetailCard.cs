using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>
/// 一条记录的「详情」浮窗内容：短备注、详细备注、归属、来源（记录设备）都放这里，
/// 列表里只显示短备注，保持清爽。
/// </summary>
public static class DetailCard
{
    public static UIElement Build(Record record, Settings settings, Action onClose)
    {
        Diag.Trace("DetailCard: 开始");
        var symbol = settings.CurrencySymbol;
        var body = new StackPanel { Width = 460 };

        // 标题：支出 餐饮　¥62.00
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        title.Children.Add(Ui.Label($"{record.Kind} · {record.Category}", Ui.FontTitle, Ui.Text, bold: true));
        var amount = Ui.Label(Valid.Money(record.Amount, symbol), Ui.FontTitle,
            record.Kind == Kinds.Income ? Ui.Income : Ui.Expense, bold: true);
        amount.Margin = new Thickness(12, 0, 0, 0);
        title.Children.Add(amount);
        body.Children.Add(title);

        Diag.Trace("DetailCard: 标题好了");
        body.Children.Add(Ui.Divider(6, 10));

        body.Children.Add(InfoLine("日期", record.Date.ToString("yyyy-MM-dd")));
        body.Children.Add(InfoLine("时间", record.Time));
        body.Children.Add(InfoLine("支付方式", record.Pay.Length > 0 ? record.Pay : "（未填）"));
        body.Children.Add(InfoLine("编号", record.Id.Length > 0 ? record.Id : "（无）"));
        body.Children.Add(InfoLine("短备注", record.Note.Length > 0 ? record.Note : "（空）"));

        // 详细备注：多行，自动换行
        var detailText = record.Detail.Length > 0 ? record.Detail : "（没有详细备注）";
        var detailLabel = Ui.Label(detailText, Ui.FontBody, record.Detail.Length > 0 ? Ui.Text : Ui.Muted);
        detailLabel.TextWrapping = TextWrapping.Wrap;
        detailLabel.MaxWidth = 320;
        detailLabel.VerticalAlignment = VerticalAlignment.Top;
        body.Children.Add(InfoLine("详细备注", detailLabel, top: true));

        // 归属 / 来源：老数据这两项是空的，这里就说清楚「老数据没有这一项」
        body.Children.Add(InfoLine("归属（用户）",
            record.Owner.Length > 0 ? OwnerText(record.Owner) : "（空 —— 老数据或未标注）"));
        body.Children.Add(InfoLine("来源（设备）",
            record.Source.Length > 0 ? record.Source : "（空 —— 本机记录，默认不记设备）"));

        Diag.Trace("DetailCard: 信息行好了");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(Ui.PrimaryButton("知道了", onClose, 34));
        body.Children.Add(buttons);

        var hint = Ui.Hint("提示：点卡片外面或按 Esc 也能关掉");
        hint.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(hint);

        // 卡片本身不该被遮罩的点击关掉：点卡片时把事件吃掉
        Diag.Trace("DetailCard: 按钮好了，开始做卡片");
        var card = Ui.CardPanel(body, padding: 20, radius: Ui.RadiusCard);
        Diag.Trace("DetailCard: 卡片完成");
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return card;
    }

    private static string OwnerText(string owner) =>
        owner == Config.LoadAccount().UserCode ? $"{owner}（本机账户）" : owner;

    private static Grid InfoLine(string label, string value, bool top = false) => InfoLine(label, Ui.Label(value, Ui.FontBody), top);

    private static Grid InfoLine(string label, UIElement value, bool top = false)
    {
        var grid = new Grid { Margin = new Thickness(0, top ? 4 : 2, 0, top ? 4 : 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = Ui.Label(label, Ui.FontBody, Ui.Muted);
        name.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center;
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        value.SetValue(FrameworkElement.VerticalAlignmentProperty, top ? VerticalAlignment.Top : VerticalAlignment.Center);
        value.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }
}
