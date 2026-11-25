using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LedgerBook.App;

/// <summary>
/// 「更新内容」窗口。
///
/// 关闭方式刻意做成"不必读完"：
///   · 点**文本区域以外**的任何地方（四周留白 / 标题行 / 底部行 / 整个遮罩）—— 立刻关闭
///   · 右下角「知道了」按钮
///   · Esc 键
/// 文本区域本身是只读可滚动的（内容长了可以滚，窗口高度也有上限）。
/// </summary>
public sealed class WhatsNewWindow : Window
{
    /// <summary>反馈问卷地址（公告末尾与窗口按钮共用）。</summary>
    public const string FeedbackUrl = "https://v.wjx.cn/vm/mOHXB2i.aspx";

    public WhatsNewWindow(string title, string body)
    {
        Title = "更新内容";
        Width = 820;
        Height = 640;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Ui.Background;
        FontFamily = Ui.FontFamily;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // ---------- 正文（只读、可滚、有最大高度）----------
        var text = new TextBox
        {
            Text = body,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            BorderThickness = new Thickness(0),
            Background = Ui.Field,
            Foreground = Ui.Text,
            FontSize = Ui.FontBody,
            Padding = new Thickness(14, 12, 14, 12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var textShell = new Border
        {
            Background = Ui.Field,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Ui.RadiusControl),
            Child = text,
            MinHeight = 160,
            // 高度由外层 Grid 的星号行决定 → 内容超出时 TextBox 自己出滚动条
        };

        // ---------- 标题行 ----------
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(Ui.Label(title, Ui.FontTitle, Ui.Text, bold: true));
        var headHint = Ui.Hint("点正文以外的任何地方都能直接关闭（不必读完）");
        headHint.VerticalAlignment = VerticalAlignment.Bottom;
        headHint.Margin = new Thickness(12, 0, 0, 3);
        head.Children.Add(headHint);

        // ---------- 底部行 ----------
        var foot = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var footHint = Ui.Hint("内容长了可以在框里滚动；想细看可以到「设置 → 账户 → 查看更新内容」再打开一次");
        footHint.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(footHint);
        var footButtons = new StackPanel { Orientation = Orientation.Horizontal };
        Button copyLink = null!;
        copyLink = Ui.GhostButton("复制反馈链接", () =>
        {
            if (Ui.CopyText(FeedbackUrl))
            {
                copyLink.Content = "已复制 ✓";
            }
        }, 36);
        copyLink.MinWidth = 130;
        footButtons.Children.Add(copyLink);
        var close = Ui.PrimaryButton("知道了（关闭）", Close, 36);
        close.MinWidth = 150;
        close.Margin = new Thickness(8, 0, 0, 0);
        footButtons.Children.Add(close);
        Grid.SetColumn(footButtons, 1);
        foot.Children.Add(footButtons);

        // 卡片用 Grid（Auto / * / Auto）：正文那一行拿"剩余高度" → 内容长了才会出现滚动条。
        // 原来用 StackPanel，会给子元素**无限高度**，TextBox 被撑到全文高度、滚动条永远不出现。
        var card = new Grid { Margin = new Thickness(22) };
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 标题行
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // 正文
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 底部行
        card.Children.Add(head);
        card.Children.Add(textShell);
        card.Children.Add(foot);
        Grid.SetRow(textShell, 1);
        Grid.SetRow(foot, 2);

        var cardShell = Ui.CardPanel(card, padding: 0);

        // ---------- 遮罩：点它 = 关闭 ----------
        var scrim = new Border
        {
            Background = new SolidColorBrush(Ui.ScrimColor),
            Child = cardShell,
        };
        scrim.MouseLeftButtonDown += (_, _) => Close();

        // 标题行 / 底部行也在正文之外 → 点它们同样关闭（卡片要吃掉事件，别让遮罩重复处理）
        head.MouseLeftButtonDown += (_, _) => Close();
        foot.MouseLeftButtonDown += (_, e) =>
        {
            // 点在「知道了」按钮上时由按钮自己处理
            if (!IsInsideButton(e.OriginalSource as DependencyObject))
            {
                Close();
            }
        };

        Content = scrim;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
            {
                return true;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : null;
        }

        return false;
    }

    /// <summary>弹给用户看（模态，关掉才回到主界面）。</summary>
    public static void Show(Window? owner)
        => Run(owner, "记账本 v" + Core.Paths.Version + " 更新内容");

    /// <summary>从「查看更新内容」按钮打开：也走同一条路径（owner 为空时用当前活动窗口）。</summary>
    public static void Run(Window? owner, string title)
    {
        var window = new WhatsNewWindow(title, WhatsNew.Text());
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
    }
}
