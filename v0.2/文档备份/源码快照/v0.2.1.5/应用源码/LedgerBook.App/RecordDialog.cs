using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>改一条记录的弹窗（和「记一笔」同一套字段，预填原值）。</summary>
public sealed class RecordDialog : Window
{
    private readonly Record _record;
    private readonly TextBox _dateBox;
    private readonly TextBox _timeBox;
    private readonly TextBox _amountBox;
    private readonly TextBox _noteBox;
    private readonly TextBox _detailBox;
    private readonly ComboBox _categoryBox;
    private readonly ComboBox _payBox;
    private readonly List<(string Kind, System.Windows.Controls.Primitives.ToggleButton Pill)> _kindPills = new();
    private string _kind;

    public bool Saved { get; private set; }

    public RecordDialog(Record record, Settings settings)
    {
        _record = record;
        _kind = record.Kind;

        Title = "改一笔";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Background;
        FontFamily = Ui.FontFamily;
        ShowInTaskbar = false;

        var fields = new StackPanel { Margin = new Thickness(20) };
        var categories = Config.LoadCategories();

        var (dateShell, dateBox) = Ui.Input(record.Date.ToString("yyyy-MM-dd"), 140);
        _dateBox = dateBox;
        fields.Children.Add(Row("日期", dateShell));

        var (timeShell, timeBox) = Ui.Input(record.Time, 140);
        _timeBox = timeBox;
        fields.Children.Add(Row("时间", timeShell));

        var kindRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var kind in Kinds.All)
        {
            var captured = kind;
            var pill = Ui.Pill(kind);
            var selected = kind == record.Kind;
            pill.IsChecked = selected;
            pill.Background = selected ? Ui.Accent : Ui.Transparent;
            pill.Foreground = selected ? System.Windows.Media.Brushes.White : Ui.Muted;
            pill.Click += (_, _) => SetKind(captured);
            _kindPills.Add((kind, pill));
            kindRow.Children.Add(pill);
        }

        fields.Children.Add(Row("类型", kindRow));

        var (amountShell, amountBox) = Ui.Input(record.Amount, 160);
        _amountBox = amountBox;
        fields.Children.Add(Row("金额", amountShell));

        var kindCategories = categories.TryGetValue(_kind, out var list) ? list : categories[Kinds.Expense];
        var (categoryShell, categoryBox) = Ui.Select(kindCategories, 200, record.Category);
        _categoryBox = categoryBox;
        fields.Children.Add(Row("分类", categoryShell));

        var pays = settings.PayMethods.Count > 0 ? settings.PayMethods : Settings.DefaultPayMethods().Keys.ToList();
        var (payShell, payBox) = Ui.Select(pays, 200, record.Pay, editable: true);
        _payBox = payBox;
        fields.Children.Add(Row("支付方式", payShell));

        var (noteShell, noteBox) = Ui.Input(record.Note, 380);
        _noteBox = noteBox;
        fields.Children.Add(Row("短备注", noteShell));

        // 详细备注：多行输入框
        _detailBox = new TextBox
        {
            Text = record.Detail,
            FontFamily = Ui.FontFamily,
            FontSize = Ui.FontBody,
            Foreground = Ui.Text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 60,
            Width = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        var detailShell = new Border
        {
            Background = Ui.Card,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Ui.RadiusControl),
            Padding = new Thickness(10, 6, 10, 6),
            Child = _detailBox,
        };
        _detailBox.GotFocus += (_, _) => detailShell.BorderBrush = Ui.Accent;
        _detailBox.LostFocus += (_, _) => detailShell.BorderBrush = Ui.Line;
        fields.Children.Add(Row("详细备注", detailShell));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 20)
        };
        var cancel = Ui.GhostButton("取消", Close, 36);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(Ui.PrimaryButton("保存", Save, 36));

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(Ui.CardPanel(fields, padding: 4, margin: new Thickness(20, 20, 20, 0)));
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        _amountBox.Focus();
    }

    private static StackPanel Row(string title, UIElement content)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var label = Ui.Label(title, Ui.FontBody, Ui.Muted);
        label.Width = 70;
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        row.Children.Add(content);
        return row;
    }

    private void SetKind(string kind)
    {
        _kind = kind;
        foreach (var (name, pill) in _kindPills)
        {
            var selected = name == kind;
            pill.IsChecked = selected;
            pill.Background = selected ? Ui.Accent : Ui.Transparent;
            pill.Foreground = selected ? System.Windows.Media.Brushes.White : Ui.Muted;
        }

        var categories = Config.LoadCategories();
        _categoryBox.ItemsSource = categories.TryGetValue(kind, out var list) ? list : categories[Kinds.Expense];
        _categoryBox.SelectedIndex = 0;
    }

    private void Save()
    {
        try
        {
            var day = Valid.ParseDate(_dateBox.Text);
            var time = Valid.NormalizeTime(_timeBox.Text);
            var amount = Valid.NormalizeAmount(_amountBox.Text);
            var category = _categoryBox.Text.Trim();
            if (category.Length == 0)
            {
                throw new LedgerException("请选择分类");
            }

            Storage.Update(_record.Id, target =>
            {
                target.Date = day;
                target.Time = time;
                target.Kind = Valid.ParseKind(_kind);
                target.Category = category;
                target.Amount = amount;
                target.Pay = _payBox.Text.Trim();
                target.Note = _noteBox.Text.Trim();
                target.Detail = _detailBox.Text.Trim();
            });

            // 改了日期就等于换了文件：把记录从原文件挪到新文件
            if (day != _record.Date)
            {
                Storage.Delete(_record.Id);
                Storage.Append(day, time, _kind, category, amount, _payBox.Text.Trim(), _noteBox.Text.Trim(),
                    _record.Owner, _record.Source);
            }

            Saved = true;
            Close();
        }
        catch (Exception exc)
        {
            MessageBox.Show(this, exc.Message, "改不了", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
