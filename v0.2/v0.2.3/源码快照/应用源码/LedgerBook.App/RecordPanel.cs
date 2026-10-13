using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>记一笔：表单 + 「本次打开已记」列表。</summary>
public sealed class RecordPanel
{
    private readonly Settings _settings;
    private readonly Account _account;
    private readonly MainWindow _window;
    private readonly List<Record> _session = new();

    private readonly TextBox _dateBox;
    private readonly TextBox _timeBox;
    private readonly TextBox _amountBox;
    private readonly TextBox _noteBox;
    private readonly TextBox _detailBox;
    private readonly ComboBox _categoryBox;
    private readonly ComboBox _payBox;
    private readonly Border _categoryShell;
    private readonly Border _payShell;
    private readonly TextBlock _amountUnit;
    private readonly Ui.DataTable _sessionTable;
    private readonly Border _sessionCard;
    private readonly StackPanel _sessionBox;
    private readonly RowDefinition _formRow;
    private readonly RowDefinition _sessionRow;
    private readonly Border _formCard;
    private readonly Dictionary<string, bool> _kindFlags = new() { [Kinds.Expense] = true, [Kinds.Income] = false };
    private readonly List<(string Kind, System.Windows.Controls.Primitives.ToggleButton Button)> _kindPills = new();

    public Grid Root { get; }

    public RecordPanel(Settings settings, Account account, MainWindow window)
    {
        _settings = settings;
        _account = account;
        _window = window;

        Root = new Grid();
        _formRow = new RowDefinition { Height = GridLength.Auto };
        Root.RowDefinitions.Add(_formRow);
        _sessionRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
        Root.RowDefinitions.Add(_sessionRow);

        // ---------- 表单 ----------
        var fields = new StackPanel { Orientation = Orientation.Vertical };

        var today = DateOnly.FromDateTime(DateTime.Now);
        var (dateShell, dateBox) = Ui.Input(today.ToString("yyyy-MM-dd"), 120);
        _dateBox = dateBox;
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        dateRow.Children.Add(dateShell);
        var todayButton = Ui.GhostButton("今天", () => _dateBox.Text = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"), 34);
        todayButton.Margin = new Thickness(8, 0, 0, 0);
        dateRow.Children.Add(todayButton);
        var dateHint = Ui.Hint("可改日期补记以前的账");
        dateHint.Margin = new Thickness(10, 0, 0, 0);
        dateRow.Children.Add(dateHint);
        fields.Children.Add(LabeledRow("日期", dateRow));

        var (timeShell, timeBox) = Ui.Input("", 120);
        _timeBox = timeBox;
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
        timeRow.Children.Add(timeShell);
        var timeHint = Ui.Hint("留空就用此刻时间");
        timeHint.Margin = new Thickness(10, 0, 0, 0);
        timeRow.Children.Add(timeHint);
        fields.Children.Add(LabeledRow("时间", timeRow));

        var kindRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var kind in Kinds.All)
        {
            var captured = kind;
            var pill = Ui.Pill(kind);
            var selected = _kindFlags[kind];
            pill.IsChecked = selected;
            pill.Background = selected ? Ui.Accent : Ui.Transparent;
            pill.Foreground = selected ? System.Windows.Media.Brushes.White : Ui.Muted;
            pill.Click += (_, _) => SetKind(captured);
            _kindPills.Add((kind, pill));
            kindRow.Children.Add(pill);
        }

        fields.Children.Add(LabeledRow("类型", kindRow));

        var (amountShell, amountBox) = Ui.Input("", 140);
        _amountBox = amountBox;
        var amountRow = new StackPanel { Orientation = Orientation.Horizontal };
        amountRow.Children.Add(amountShell);
        _amountUnit = Ui.Hint($"元（{_settings.CurrencySymbol}）");
        _amountUnit.Margin = new Thickness(10, 0, 0, 0);
        amountRow.Children.Add(_amountUnit);
        fields.Children.Add(LabeledRow("金额", amountRow));

        var categories = Config.LoadCategories();
        var kindKey = _kindFlags[Kinds.Income] ? Kinds.Income : Kinds.Expense;
        (_categoryShell, _categoryBox) = Ui.Select(categories[kindKey], 180);
        var categoryRow = new StackPanel { Orientation = Orientation.Horizontal };
        categoryRow.Children.Add(_categoryShell);
        var categoryHint = Ui.Hint("可在「设置」里增删分类");
        categoryHint.Margin = new Thickness(10, 0, 0, 0);
        categoryRow.Children.Add(categoryHint);
        fields.Children.Add(LabeledRow("分类", categoryRow));

        var pays = _settings.PayMethods.Count > 0 ? _settings.PayMethods : Settings.DefaultPayMethods().Keys.ToList();
        (_payShell, _payBox) = Ui.Select(pays, 180, string.IsNullOrWhiteSpace(_settings.DefaultPay) ? pays.FirstOrDefault() : _settings.DefaultPay);
        fields.Children.Add(LabeledRow("支付方式", _payShell));

        var (noteShell, noteBox) = Ui.Input("", 420);
        _noteBox = noteBox;
        var noteRow = new StackPanel { Orientation = Orientation.Horizontal };
        noteRow.Children.Add(noteShell);
        var noteHint = Ui.Hint("建议 10~20 字以内，长的写在下面「详细备注」里");
        noteHint.Margin = new Thickness(10, 0, 0, 0);
        noteRow.Children.Add(noteHint);
        fields.Children.Add(LabeledRow("短备注", noteRow));

        // 详细备注：多行，不占列表位置，只在「详情」里看
        _detailBox = new TextBox
        {
            FontFamily = Ui.FontFamily,
            FontSize = Ui.FontBody,
            Foreground = Ui.Text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 58,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        var detailShell = new Border
        {
            Background = Ui.Card,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Ui.RadiusControl),
            Padding = new Thickness(10, 6, 10, 6),
            Width = 420,
            Child = _detailBox,
        };
        _detailBox.GotFocus += (_, _) => detailShell.BorderBrush = Ui.Accent;
        _detailBox.LostFocus += (_, _) => detailShell.BorderBrush = Ui.Line;
        var detailRow = new StackPanel { Orientation = Orientation.Horizontal };
        detailRow.Children.Add(detailShell);
        var detailHint = Ui.Hint("比如「和谁、买了什么、要不要报销」");
        detailHint.Margin = new Thickness(10, 0, 0, 0);
        detailRow.Children.Add(detailHint);
        fields.Children.Add(LabeledRow("详细备注", detailRow));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var saveButton = Ui.PrimaryButton("保存并继续（Ctrl+Enter）", Save, 36);
        buttons.Children.Add(saveButton);
        var clearButton = Ui.GhostButton("清空", ClearInputs, 36);
        clearButton.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(clearButton);
        fields.Children.Add(buttons);

        _formCard = Ui.CardPanel(fields, padding: 18);
        _formCard.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(_formCard, 0);
        Root.Children.Add(_formCard);

        // ---------- 本次打开已记 ----------
        _sessionTable = new Ui.DataTable(new (string, double, HorizontalAlignment)[]
        {
            ("时间", 0.6, HorizontalAlignment.Left),
            ("类型", 0.5, HorizontalAlignment.Left),
            ("分类", 0.9, HorizontalAlignment.Left),
            ("金额", 0.9, HorizontalAlignment.Right),
            ("支付方式", 0.8, HorizontalAlignment.Left),
            ("备注", 1.6, HorizontalAlignment.Left),
            ("详情", 0.5, HorizontalAlignment.Center),
        });
        _sessionCard = _sessionTable.Root;
        _sessionCard.Margin = new Thickness(0, 8, 0, 0);
        _sessionBox = new StackPanel();
        var sessionTitle = Ui.Label("本次打开后已记", Ui.FontSubtitle, Ui.Text, bold: true);
        sessionTitle.Margin = new Thickness(4, 0, 0, 2);
        _sessionBox.Children.Add(sessionTitle);
        _sessionBox.Children.Add(Ui.Hint("记错了不用慌：到「查询统计」里选中那一行双击改掉，或选中后删除。"));
        _sessionBox.Children.Add(_sessionCard);
        // 用户要求（2026-10-09）：这个列表**保留**，设置里可以关；开关就地切显隐，不重建界面
        ApplySessionListVisibility();
        Grid.SetRow(_sessionBox, 1);
        Root.Children.Add(_sessionBox);

        // 快捷键：Ctrl+Enter 保存、Enter 在金额框里也保存
        _amountBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Save();
            }
        };
        _noteBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Save();
            }
        };

        RefreshSessionList();
    }

    private StackPanel LabeledRow(string title, UIElement content)
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
        foreach (var (name, pill) in _kindPills)
        {
            var selected = name == kind;
            _kindFlags[name] = selected;
            pill.IsChecked = selected;
            // 选中态直接给颜色：不依赖样式触发器，任何外观设置下都稳定
            pill.Background = selected ? Ui.Accent : Ui.Transparent;
            pill.Foreground = selected ? System.Windows.Media.Brushes.White : Ui.Muted;
        }

        var categories = Config.LoadCategories();
        _categoryBox.ItemsSource = categories[kind];
        _categoryBox.SelectedIndex = 0;
    }

    private string CurrentKind => _kindFlags[Kinds.Income] ? Kinds.Income : Kinds.Expense;

    private void ClearInputs()
    {
        _amountBox.Text = "";
        _noteBox.Text = "";
        _detailBox.Text = "";
        _timeBox.Text = "";
        _amountBox.Focus();
    }

    private void Save()
    {
        try
        {
            var day = Valid.ParseDate(_dateBox.Text);
            var record = Storage.Append(
                day,
                _timeBox.Text,
                CurrentKind,
                _categoryBox.Text,
                _amountBox.Text,
                _payBox.Text,
                _noteBox.Text,
                _account.UserCode,
                _account.Device,
                _detailBox.Text);

            _session.Insert(0, record);
            var symbol = _settings.CurrencySymbol;
            Core.Log.Info($"记账：{record.Kind}/{record.Category}/{record.Amount}");
            _window.SetStatus($"已记入 {record.Date:yyyy-MM-dd} {record.Time}：{record.Kind} {record.Category} "
                              + $"{Valid.Money(record.Amount, symbol)}");
            // 保存后一定清掉金额与备注（否则看不出"到底存进去没有"），时间按设置决定清不清
            _amountBox.Text = "";
            _noteBox.Text = "";
            _detailBox.Text = "";
            if (_settings.ClearAfterSave)
            {
                _timeBox.Text = "";
            }

            RefreshSessionList();
            _window.NotifyDataChanged();
            _amountBox.Focus();
        }
        catch (Exception exc)
        {
            _window.ShowError("没能保存：" + exc.Message);
            _amountBox.Focus();
        }
    }

    /// <summary>
    /// 「设置 → 记一笔 → 显示本次打开后已记」切换时调用：**就地切显隐，不重建界面**。
    /// （之前那版走 RebuildUi 重建整个界面，结果开关不生效：打开也没列表、关掉也不消失。）
    ///
    /// 布局也要跟着变（用户提醒"关掉后布局跟打开要有区别"）：
    ///   · 显示列表：表单按内容高度 + 列表占剩余高度（列表长了它自己滚）；
    ///   · 隐藏列表：**表单卡片吃掉整块高度**，下半屏不会剩一大片空白。
    /// </summary>
    public void ApplySessionListVisibility()
    {
        // 用户要求（2026-10-10）：隐藏时**不要**把表单卡片拉长成一大块白的，
        // 就让「本次打开后已记」那块位置留灰底（页面底色），比白块好看。
        _sessionBox.Visibility = _settings.ShowSessionList ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDetail(object? payload)    {
        if (payload is Record record)
        {
            _window.ShowOverlay(DetailCard.Build(record, _settings, _window.HideOverlay));
        }
    }

    private void RefreshSessionList()
    {
        _sessionTable.Clear();
        if (_session.Count == 0)
        {
            _sessionTable.AddEmpty("这次打开还没有记录");
            return;
        }

        var symbol = _settings.CurrencySymbol;
        foreach (var record in _session)
        {
            _sessionTable.AddRow(new[]
            {
                record.Time,
                record.Kind,
                record.Category,
                Valid.Money(record.Amount, symbol),
                record.Pay,
                record.Note
            }, payload: record, onAction: ShowDetail);
        }
    }
}
