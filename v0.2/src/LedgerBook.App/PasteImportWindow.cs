using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>
/// 粘贴文本导入。
///
/// 用途（用户要求 2026-10-15）：把**账单截图 + 模板文字**发给任意免费对话软件，让它按模板回一段文本，
/// 直接粘进来就能导入 —— **全程不用生成文件**。
///
/// 界面：左边粘贴区，右边模板区（可一键复制）。
/// 确认后：把文本写成临时 CSV，再打开与"选择文件导入"**完全相同**的二次确认界面（ImportWindow）。
/// 模板刻意精简：只保留 日期/时间/类型/分类/金额/支付方式/备注 七列 ——
/// 去掉 编号/详细备注/归属/来源（设备、用户这些用户不需要、也容易让对话软件编错）。
/// 导入器按列名读、只要求 日期/分类/金额，所以七列完全够用。
/// </summary>
public sealed class PasteImportWindow : Window
{
    private readonly TextBox _input;
    private readonly TextBlock _status = Ui.Hint("把对话软件回的文本整段粘进来（带 ``` 代码块围栏也没关系，会自动去掉）");

    /// <summary>确认后写出的临时 CSV 路径（取消则为 null）。</summary>
    public string? TempPath { get; private set; }

    public PasteImportWindow(Settings settings)
    {
        Title = "粘贴文本导入";
        Width = 1040;
        Height = 680;
        MinWidth = 860;
        MinHeight = 520;
        Background = Ui.Background;
        FontFamily = Ui.FontFamily;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var root = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                 // 标题
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 两栏
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                 // 状态 + 按钮

        var head = new StackPanel();
        head.Children.Add(Ui.Label("粘贴文本导入", Ui.FontTitle, Ui.Text, bold: true));
        var headHint = Ui.Hint("把账单截图 + 右边的模板一起发给免费对话软件，拿回文本粘到左边即可；不需要生成文件。");
        headHint.Margin = new Thickness(0, 4, 0, 10);
        head.Children.Add(headHint);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);

        // ---------- 左：粘贴区 ----------
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.Children.Add(Ui.Label("① 粘贴对话软件回的文本", Ui.FontBody, Ui.Text, bold: true));
        _input = MakeBox(editable: true);
        var inputShell = Shell(_input);
        Grid.SetRow(inputShell, 1);
        left.Children.Add(inputShell);
        Grid.SetColumn(left, 0);
        columns.Children.Add(left);

        // ---------- 右：模板区 ----------
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var rightHead = new StackPanel { Orientation = Orientation.Horizontal };
        rightHead.Children.Add(Ui.Label("② 模板（连同截图一起发给对话软件）", Ui.FontBody, Ui.Text, bold: true));
        // 先声明再赋值：按钮的处理里要用到 copy 自己（改文字做"已复制"提醒）
        Button copy = null!;
        copy = Ui.GhostButton("复制模板", () =>
        {
            var text = Template(settings);
            if (Ui.CopyText(text))
            {
                _status.Text = "模板已复制 —— 粘到对话软件里，连同账单截图一起发过去";
                copy.Content = "已复制 ✓";
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    copy.Content = "复制模板";
                };
                timer.Start();
            }
            else
            {
                _status.Text = "剪贴板写不进去，请手动选中右边的模板复制";
            }
        }, 30);
        copy.Margin = new Thickness(10, 0, 0, 0);
        rightHead.Children.Add(copy);
        right.Children.Add(rightHead);

        var templateBox = MakeBox(editable: false);
        templateBox.Text = Template(settings);
        var templateShell = Shell(templateBox);
        Grid.SetRow(templateShell, 1);
        right.Children.Add(templateShell);

        var templateHint = Ui.Hint("模板只有 7 列：日期/时间/类型/分类/金额/支付方式/备注（不含设备、用户等字段）");
        templateHint.Margin = new Thickness(0, 6, 0, 0);
        templateHint.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(templateHint, 2);
        right.Children.Add(templateHint);
        Grid.SetColumn(right, 2);
        columns.Children.Add(right);

        // ---------- 底部：状态 + 按钮 ----------
        var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextWrapping = TextWrapping.Wrap;
        bottom.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = Ui.GhostButton("取消", () => { DialogResult = false; }, 34);
        buttons.Children.Add(cancel);
        var next = Ui.PrimaryButton("继续（进入确认界面）", Continue, 34);
        next.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(next);
        Grid.SetColumn(buttons, 1);
        bottom.Children.Add(buttons);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);

        Content = root;
    }

    /// <summary>确认：把粘进来的文本整理成临时 CSV，交给调用方去打开二次确认界面。</summary>
    private void Continue()
    {
        var text = Clean(_input.Text);
        if (text.Length == 0)
        {
            _status.Text = "左边还是空的 —— 先把对话软件回的文本粘进来";
            return;
        }

        try
        {
            if (!TryWriteTemp(text, out var path, out var error))
            {
                _status.Text = "写临时文件失败：" + error;
                return;
            }

            TempPath = path;
            Core.Log.Info($"粘贴导入：文本 {text.Length} 字 → {path}");
            try
            {
                DialogResult = true;   // 自动化（非 ShowDialog）路径下会抛异常，忽略即可
            }
            catch (InvalidOperationException)
            {
            }
        }
        catch (Exception exc)
        {
            _status.Text = "写临时文件失败：" + exc.Message;
        }
    }

    /// <summary>自动化验证用：填入文本并直接走"继续"，用来真正跑一次临时文件写入。</summary>
    public void FillAndContinueForAutomation(string text)
    {
        _input.Text = text;
        Continue();
    }

    /// <summary>清理粘贴内容：去掉 ``` 代码块围栏、去掉首尾空行（对话软件常带围栏）。</summary>
    /// <summary>
    /// 把文本写成一个临时 CSV。
    /// 为什么要这么绕：用户反馈过 "Access to the path ... 记账本-粘贴导入.csv is denied" ——
    /// 固定文件名 + 固定目录，一旦那个文件已存在/还被上一次的导入窗口占着，就会直接失败。
    /// 所以这里：① 每次用**唯一文件名** ② 逐个候选目录**真实试写**，第一个成功的就是它。
    /// </summary>
    public static bool TryWriteTemp(string text, out string path, out string error)
    {
        path = "";
        error = "";
        var name = $"记账本-粘贴导入-{DateTime.Now:yyyyMMdd-HHmmss-fff}.csv";
        var candidates = new List<string>
        {
            Path.GetTempPath(),
            Paths.ExportDir,
            Paths.Root,
        };

        var body = text + (text.EndsWith("\n") ? "" : "\n");
        foreach (var dir in candidates)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, name);
                File.WriteAllText(target, body, new UTF8Encoding(true));
                path = target;
                return true;
            }
            catch (Exception exc)
            {
                error = exc.Message;
            }
        }

        return false;
    }

    public static string Clean(string raw)
    {
        var lines = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                continue;   // 代码块围栏
            }

            kept.Add(line);
        }

        // 去掉首尾空行
        while (kept.Count > 0 && kept[0].Trim().Length == 0)
        {
            kept.RemoveAt(0);
        }

        while (kept.Count > 0 && kept[^1].Trim().Length == 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return string.Join("\r\n", kept);
    }

    /// <summary>给对话软件的模板：七列 + 用户自己的分类与支付方式（让它直接对得上）。</summary>
    public static string Template(Settings settings)
    {
        var categories = Core.Config.LoadCategories();
        var expense = categories.TryGetValue(Kinds.Expense, out var e) ? string.Join(" / ", e) : "餐饮 / 交通 / 购物 / 其他";
        var income = categories.TryGetValue(Kinds.Income, out var i) ? string.Join(" / ", i) : "工资 / 红包 / 其他";
        var pays = settings.PayMethods.Count > 0 ? string.Join(" / ", settings.PayMethods) : "微信 / 支付宝 / 现金 / 银行卡";

        var builder = new StringBuilder();
        builder.AppendLine("请把账单截图整理成下面的 CSV 文本，只回 CSV 文本本身：");
        builder.AppendLine("（只保留真实的收入与支出；理财赎回、充值/提现/冲正、自己账户之间的转账等内部资金往来请忽略，不要写进 CSV）");
        builder.AppendLine();
        builder.AppendLine("日期,时间,类型,分类,金额,支付方式,备注");
        builder.AppendLine("2026-10-13,13:45,支出,借出,10.00,微信,转账-转给牢尖");
        builder.AppendLine("2026-10-15,02:17,支出,娱乐,629.98,银行卡,某易游戏充值助手");
        builder.AppendLine();
        builder.AppendLine("要求：");
        builder.AppendLine("1. 列不要增减，表头照抄；一行一条记录。");
        builder.AppendLine("2. 日期写成 YYYY-MM-DD；时间写成 HH:mm（账单里没有时间就留空）。");
        builder.AppendLine("3. 类型只能填「支出」或「收入」。");
        builder.AppendLine("4. 金额只写数字、保留两位小数，不要 ￥、不要正负号（收支由「类型」区分）。");
        builder.AppendLine($"5. 支付方式优先用：{pays}。");
        builder.AppendLine($"6. 支出分类优先用：{expense}。");
        builder.AppendLine($"7. 收入分类优先用：{income}。");
        builder.AppendLine("8. 备注保留账单里的原始摘要（例如「金拱门」「转账-转给某某」）。");
        builder.AppendLine("9. 理财赎回（如「朝朝宝赎回」）、数字人民币/零钱充值或提现、冲正、以及自己账户之间的转账，都不是真实收支，不要写进 CSV。");
        builder.AppendLine("10. 不要在 CSV 之外输出解释文字；不要输出 Markdown 表格。");
        return builder.ToString().TrimEnd();
    }

    private static TextBox MakeBox(bool editable) => new()
    {
        // 粘贴区不换行（CSV 列对齐更好认，横向滚动即可）；模板区换行（分类清单较长，不换行会被截断）
        TextWrapping = editable ? TextWrapping.NoWrap : TextWrapping.Wrap,
        AcceptsReturn = true,
        AcceptsTab = editable,
        IsReadOnly = !editable,
        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
        FontSize = Ui.FontSmall,
        Foreground = Ui.Text,
        Background = Ui.Field,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(10),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private static Border Shell(TextBox box) => new()
    {
        Background = Ui.Field,
        BorderBrush = Ui.Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Ui.RadiusControl),
        Child = box,
    };
}
