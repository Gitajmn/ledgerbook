using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LedgerBook.App;

/// <summary>
/// 界面设计系统：颜色、字号、圆角尺度，以及各种控件的「圆角版」工厂方法。
///
/// 圆角只分三档（和 v0.1.0 的尺度一致，见设计文档 9.x）：
///   小控件 6 / 输入框与按钮 8 / 卡片与列表 12
/// WPF 的圆角是矢量绘制的，天然抗锯齿；高 DPI 由系统处理，不用手算缩放。
/// </summary>
public static class Ui
{
    // ---------- 主题与颜色 ----------
    /// <summary>主题模式：浅色 / 深色 / 跟随系统。</summary>
    public enum ThemeMode { Light, Dark, System }

    public static ThemeMode Current { get; private set; } = ThemeMode.Light;

    // 这些画刷会随主题切换（所以不是 readonly）
    public static Brush Background = Brushes.White;
    public static Brush Card = Brushes.White;
    public static Brush Line = Brushes.Gray;
    public static Brush Text = Brushes.Black;
    public static Brush Muted = Brushes.Gray;
    public static Brush Accent = Brushes.Blue;
    public static Brush AccentHover = Brushes.Blue;
    public static Brush Hover = Brushes.LightGray;
    public static Brush Income = Brushes.Green;
    public static Brush Expense = Brushes.Red;
    public static Brush Transparent = Brushes.Transparent;
    /// <summary>输入框/下拉框的填充色（深色下比卡片略深一点，层次才清楚）。</summary>
    public static Brush Field = Brushes.White;
    /// <summary>次要按钮（浅底无边框那种）与分隔线、滚动条滑块，都要随主题走。</summary>
    public static Brush Secondary = Brushes.WhiteSmoke;
    public static Brush SecondaryHover = Brushes.LightGray;
    public static Brush SecondaryPressed = Brushes.Gray;
    public static Brush PrimaryPressed = Brushes.Blue;
    public static Brush Separator = Brushes.WhiteSmoke;
    public static Color ThumbColor = Color.FromRgb(0xC8, 0xCE, 0xD6);

    /// <summary>「加载中」遮罩色。</summary>
    public static Color ScrimColor = Color.FromArgb(0x55, 0x1F, 0x23, 0x29);

    /// <summary>
    /// 深色模式不是把浅色反过来那么简单：彩色要**降饱和 + 提亮**，否则在黑底上会「发光刺眼」。
    /// 这里参照 Windows 11 深色主题与常见深色设计系统的做法。
    /// </summary>
    public static void ApplyTheme(ThemeMode mode)
    {
        Current = mode;
        var dark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());

        if (!dark)
        {
            Background = Freeze(Color.FromRgb(0xF5, 0xF6, 0xF8));
            Card = Freeze(Colors.White);
            Field = Freeze(Colors.White);
            Line = Freeze(Color.FromRgb(0xE2, 0xE5, 0xEA));
            Text = Freeze(Color.FromRgb(0x1F, 0x23, 0x29));
            Muted = Freeze(Color.FromRgb(0x8A, 0x90, 0x99));
            Accent = Freeze(Color.FromRgb(0x4D, 0x6B, 0xFE));
            AccentHover = Freeze(Color.FromRgb(0x3F, 0x5C, 0xEA));
            Hover = SecondaryHover;
            Income = Freeze(Color.FromRgb(0x1F, 0x9E, 0x62));
            Expense = Freeze(Color.FromRgb(0xD9, 0x53, 0x4F));
            ScrimColor = Color.FromArgb(0x55, 0x1F, 0x23, 0x29);
            Secondary = Secondary;
            SecondaryHover = SecondaryHover;
            SecondaryPressed = SecondaryPressed;
            PrimaryPressed = Freeze(Color.FromRgb(0x35, 0x4F, 0xD6));
            Separator = Freeze(Color.FromRgb(0xF0, 0xF2, 0xF5));
            ThumbColor = Color.FromRgb(0xC8, 0xCE, 0xD6);
            Charts.UsePalette(false);
        }
        else
        {
            Background = Freeze(Color.FromRgb(0x17, 0x19, 0x1C));
            Card = Freeze(Color.FromRgb(0x22, 0x25, 0x2A));
            Field = Freeze(Color.FromRgb(0x2A, 0x2E, 0x34));
            Line = Freeze(Color.FromRgb(0x36, 0x3A, 0x41));
            Text = Freeze(Color.FromRgb(0xE6, 0xE8, 0xEB));
            Muted = Freeze(Color.FromRgb(0x98, 0x9E, 0xA6));
            Accent = Freeze(Color.FromRgb(0x86, 0x9B, 0xFF));      // 提亮的蓝，黑底上不刺眼
            AccentHover = Freeze(Color.FromRgb(0x9D, 0xAE, 0xFF));
            Hover = Freeze(Color.FromRgb(0x2F, 0x34, 0x3A));
            Income = Freeze(Color.FromRgb(0x5A, 0xC8, 0x8F));      // 降饱和的绿
            Expense = Freeze(Color.FromRgb(0xE8, 0x82, 0x7E));     // 降饱和的红
            ScrimColor = Color.FromArgb(0x88, 0x0B, 0x0D, 0x10);
            Secondary = Freeze(Color.FromRgb(0x2F, 0x34, 0x3A));       // 深色下的次要按钮底色
            SecondaryHover = Freeze(Color.FromRgb(0x3A, 0x40, 0x47));
            SecondaryPressed = Freeze(Color.FromRgb(0x25, 0x29, 0x2E));
            PrimaryPressed = Freeze(Color.FromRgb(0x6E, 0x86, 0xF5));
            Separator = Freeze(Color.FromRgb(0x2C, 0x31, 0x37));
            ThumbColor = Color.FromRgb(0x4A, 0x50, 0x58);
            Charts.UsePalette(true);
        }

        CardColor = dark ? Color.FromRgb(0x22, 0x25, 0x2A) : Colors.White;
        BackgroundColor = dark ? Color.FromRgb(0x17, 0x19, 0x1C) : Color.FromRgb(0xF5, 0xF6, 0xF8);
        LineColor = dark ? Color.FromRgb(0x36, 0x3A, 0x41) : Color.FromRgb(0xE2, 0xE5, 0xEA);
        TextColor = dark ? Color.FromRgb(0xE6, 0xE8, 0xEB) : Color.FromRgb(0x1F, 0x23, 0x29);
        MutedColor = dark ? Color.FromRgb(0x98, 0x9E, 0xA6) : Color.FromRgb(0x8A, 0x90, 0x99);
        AccentColor = dark ? Color.FromRgb(0x86, 0x9B, 0xFF) : Color.FromRgb(0x4D, 0x6B, 0xFE);
        IncomeColor = dark ? Color.FromRgb(0x5A, 0xC8, 0x8F) : Color.FromRgb(0x1F, 0x9E, 0x62);
        ExpenseColor = dark ? Color.FromRgb(0xE8, 0x82, 0x7E) : Color.FromRgb(0xD9, 0x53, 0x4F);
        IsDark = dark;
    }

    public static bool IsDark { get; private set; }

    public static Color CardColor { get; private set; } = Colors.White;
    public static Color BackgroundColor { get; private set; } = Color.FromRgb(0xF5, 0xF6, 0xF8);
    public static Color LineColor { get; private set; } = Color.FromRgb(0xE2, 0xE5, 0xEA);
    public static Color TextColor { get; private set; } = Color.FromRgb(0x1F, 0x23, 0x29);
    public static Color MutedColor { get; private set; } = Color.FromRgb(0x8A, 0x90, 0x99);
    public static Color AccentColor { get; private set; } = Color.FromRgb(0x4D, 0x6B, 0xFE);
    public static Color IncomeColor { get; private set; } = Color.FromRgb(0x1F, 0x9E, 0x62);
    public static Color ExpenseColor { get; private set; } = Color.FromRgb(0xD9, 0x53, 0x4F);

    /// <summary>读系统「应用使用浅色/深色主题」（跟随系统用）。</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static ThemeMode ParseTheme(string? text) => text switch
    {
        "深色" => ThemeMode.Dark,
        "跟随系统" => ThemeMode.System,
        _ => ThemeMode.Light
    };

    public static string ThemeName(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => "深色",
        ThemeMode.System => "跟随系统",
        _ => "浅色"
    };

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    // ---------- 字号与圆角 ----------
    public static readonly FontFamily FontFamily = new("Microsoft YaHei UI, Microsoft YaHei, Segoe UI, SimSun");

    private static double _baseFontSize = 13;

    /// <summary>设置里的「界面字号」改的就是它；全局字号只允许 ±1~4 的层次差。</summary>
    public static void SetBaseFontSize(double size) => _baseFontSize = Math.Clamp(size, 10, 22);

    public static double BaseFontSize => _baseFontSize;
    public static double FontTitle => _baseFontSize + 4;
    public static double FontSubtitle => _baseFontSize + 1;
    public static double FontBody => _baseFontSize;
    public static double FontSmall => Math.Max(9, _baseFontSize - 1);

    public const double RadiusSmall = 6;
    public const double RadiusControl = 8;
    public const double RadiusCard = 12;

    public static TextBlock Label(string text, double? size = null, Brush? brush = null, bool bold = false)
        => new()
        {
            Text = text,
            FontFamily = FontFamily,
            FontSize = size ?? FontBody,
            Foreground = brush ?? Text,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };

    public static TextBlock Hint(string text) => Label(text, FontSmall, Muted);

    /// <summary>
    /// 设置页的一行：左边「标题 + 说明」，右边控件（可带单位后缀）。
    /// 这是照着设计稿那种「一行一个设置」的风格做的，窄了也不会把控件挤没。
    /// </summary>
    public static Grid SettingRow(string title, string hint, UIElement control, string unit = "")
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 16, 8) };
        text.Children.Add(Label(title, FontBody, Text, bold: true));
        if (hint.Length > 0)
        {
            var hintLabel = Hint(hint);
            hintLabel.Margin = new Thickness(0, 2, 0, 0);
            hintLabel.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(hintLabel);
        }

        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        right.Children.Add(control);
        if (unit.Length > 0)
        {
            var unitLabel = Hint(unit);
            unitLabel.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(unitLabel);
        }

        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    /// <summary>分组标题（设置页里的小标题）。</summary>
    public static TextBlock SectionTitle(string text)
    {
        var label = Label(text, FontSubtitle, Text, bold: true);
        label.Margin = new Thickness(2, 16, 0, 6);
        return label;
    }

    /// <summary>圆角复选框（默认的方框勾选太丑，这里自己画）。</summary>
    public static CheckBox Checkbox(string text, bool initial, Action<bool> onChanged)
    {
        var box = new CheckBox
        {
              Content = text,          // 原来漏了这行：参数收了却没用，全应用的勾选框都没有文字
            IsChecked = initial,
            FontFamily = FontFamily,
            FontSize = FontBody,
            Foreground = Text,
            Cursor = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            Template = CheckboxTemplate(),
        };
        var style = new Style(typeof(CheckBox));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.ForegroundProperty, Accent));
        style.Triggers.Add(hover);
        box.Style = style;
        box.Click += (_, _) => onChanged(box.IsChecked == true);
        return box;
    }

    private static ControlTemplate CheckboxTemplate()
    {
        var mark = new FrameworkElementFactory(typeof(Path));
        mark.Name = "mark";
        mark.SetValue(Path.DataProperty, Geometry.Parse("M 1 4 L 3.6 6.6 L 8 1.6"));
        mark.SetValue(Shape.StrokeProperty, Brushes.White);
        mark.SetValue(Shape.StrokeThicknessProperty, 2.0);
        mark.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        mark.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);

        var box = new FrameworkElementFactory(typeof(Border));
        box.Name = "box";
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusSmall));
        box.SetValue(Border.WidthProperty, 18.0);
        box.SetValue(Border.HeightProperty, 18.0);
        box.SetValue(Border.BackgroundProperty, Card);
        box.SetValue(Border.BorderBrushProperty, Line);
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1.4));
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.AppendChild(mark);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 0, 0));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.AppendChild(box);
        panel.AppendChild(content);

        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = panel };
        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Accent, "box"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "box"));
        checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "mark"));
        template.Triggers.Add(checkedTrigger);
        return template;
    }

    /// <summary>只读信息行：左边说明、中间值、右边可选按钮（例如「打开目录」「复制」）。</summary>
    public static Grid InfoRow(string label, string value, string? buttonText = null, Action? onButton = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 16, 8) };
        text.Children.Add(Label(label, FontBody, Text, bold: true));
        var valueLabel = Hint(value);
        valueLabel.Margin = new Thickness(0, 2, 0, 0);
        valueLabel.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(valueLabel);
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        if (buttonText is not null && onButton is not null)
        {
            var button = GhostButton(buttonText, onButton, 30);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
        }

        return row;
    }

    // ---------- 卡片 ----------
    /// <summary>圆角卡片（列表、面板都放里面）。</summary>
    public static Border CardPanel(UIElement child, double padding = 16, double radius = RadiusCard, Thickness? margin = null)
        => new()
        {
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(padding),
            Margin = margin ?? new Thickness(0),
            Child = child,
        };

    // ---------- 按钮 ----------
    /// <summary>圆角按钮模板：Border + CornerRadius，背景/前景绑定到控件属性，带悬停与按下状态。</summary>
    private static ControlTemplate ButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusControl));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        return template;
    }

    public static Button PrimaryButton(string text, Action onClick, double height = 34)
    {
        var button = new Button
        {
            Content = text,
            Height = height,
            Padding = new Thickness(18, 0, 18, 0),
            FontFamily = FontFamily,
            FontSize = FontBody,
            Foreground = Brushes.White,
            Background = Accent,
            BorderBrush = Accent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Template = ButtonTemplate(),
        };
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Accent));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, AccentHover));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, PrimaryPressed));
        style.Triggers.Add(pressed);
        button.Style = style;
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>次要按钮：浅底无边框，悬停变深（对应设计稿里的「同色按钮悬浮效果」）。</summary>
    public static Button GhostButton(string text, Action onClick, double height = 34)
    {
        var button = new Button
        {
            Content = text,
            Height = height,
            Padding = new Thickness(16, 0, 16, 0),
            FontFamily = FontFamily,
            FontSize = FontBody,
            Foreground = Text,
            Background = Secondary,
            // 加一圈描边：用户反馈"光看文字看不出这是按钮"（2026-10-10）
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Template = ButtonTemplate(),
        };
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Secondary));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Hover));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, SecondaryPressed));
        style.Triggers.Add(pressed);
        button.Style = style;
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---------- 输入框 ----------
    /// <summary>
    /// 圆角输入框：原生 TextBox 去掉边框，外面套一个圆角 Border（得到焦点时描边变主色）。
    /// 用原生控件承载输入，输入法/光标/选中/快捷键全都正常。
    /// </summary>
    public static (Border Shell, TextBox Box) Input(string text = "", double width = 160, string placeholder = "")
    {
        var box = new TextBox
        {
            Text = text,
            FontFamily = FontFamily,
            FontSize = FontBody,
            Foreground = Text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Width = width,
            Height = 22,
            CaretBrush = Text,
            ToolTip = placeholder.Length > 0 ? placeholder : null,
        };

        var shell = new Border
        {
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusControl),
            Padding = new Thickness(10, 6, 10, 6),
            Child = box,
            HorizontalAlignment = HorizontalAlignment.Left,
            Height = 34,
        };

        box.GotFocus += (_, _) => shell.BorderBrush = Accent;
        box.LostFocus += (_, _) => shell.BorderBrush = Line;
        return (shell, box);
    }

    /// <summary>圆角下拉框的自绘模板：去掉系统那层灰色方壳，箭头和弹出列表都自己画。</summary>
    private static ControlTemplate ComboTemplate()
    {
        var textPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        textPresenter.Name = "textPresenter";
        textPresenter.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 28, 0));
        textPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        textPresenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
        textPresenter.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));

        var arrow = new FrameworkElementFactory(typeof(Path));
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
        arrow.SetValue(Shape.StrokeProperty, Muted);
        arrow.SetValue(Shape.StrokeThicknessProperty, 1.5);
        arrow.SetValue(Path.DataProperty, Geometry.Parse("M 0 0 L 4.5 4.5 L 9 0"));

        var inner = new FrameworkElementFactory(typeof(Grid));
        inner.AppendChild(textPresenter);

        // 可编辑模式需要的部件：WPF 靠这个名字自动接线（没有它，可编辑下拉框会显示空白）
        var editable = new FrameworkElementFactory(typeof(TextBox));
        editable.Name = "PART_EditableTextBox";
        editable.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 28, 0));
        editable.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        editable.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        editable.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        editable.SetValue(Control.FontFamilyProperty, FontFamily);
        editable.SetValue(Control.FontSizeProperty, FontBody);
        editable.SetValue(Control.ForegroundProperty, Text);
        editable.SetValue(Control.PaddingProperty, new Thickness(0));
        editable.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        inner.AppendChild(editable);

        inner.AppendChild(arrow);

        var shell = new FrameworkElementFactory(typeof(Border));
        shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusControl));
        shell.SetValue(Border.BackgroundProperty, Card);
        shell.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        shell.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        shell.AppendChild(inner);

        var toggleTemplate = new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton));
        var toggleFill = new FrameworkElementFactory(typeof(Border));
        toggleFill.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        toggleTemplate.VisualTree = toggleFill;

        var toggle = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton));
        toggle.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        toggle.SetValue(UIElement.FocusableProperty, false);
        toggle.SetValue(System.Windows.Controls.Primitives.ToggleButton.ClickModeProperty,
            System.Windows.Controls.ClickMode.Press);
        toggle.SetValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(ComboBox.IsDropDownOpen))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                Mode = BindingMode.TwoWay
            });
        toggle.SetValue(Control.TemplateProperty, toggleTemplate);

        var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        var scroller = new FrameworkElementFactory(typeof(ScrollViewer));
        scroller.SetValue(ScrollViewer.MaxHeightProperty, 280.0);
        scroller.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        scroller.AppendChild(itemsPresenter);

        var popupBorder = new FrameworkElementFactory(typeof(Border));
        popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusControl));
        popupBorder.SetValue(Border.BackgroundProperty, Card);
        popupBorder.SetValue(Border.BorderBrushProperty, Line);
        popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popupBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        popupBorder.SetValue(FrameworkElement.MinWidthProperty, new TemplateBindingExtension(FrameworkElement.ActualWidthProperty));
        popupBorder.AppendChild(scroller);

        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.PlacementProperty, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        popup.SetValue(Popup.PopupAnimationProperty, System.Windows.Controls.Primitives.PopupAnimation.Fade);
        popup.SetValue(Popup.IsOpenProperty, new TemplateBindingExtension(ComboBox.IsDropDownOpenProperty));
        popup.AppendChild(popupBorder);

        var root = new FrameworkElementFactory(typeof(Grid));
        root.AppendChild(shell);
        root.AppendChild(toggle);
        root.AppendChild(popup);

        var template = new ControlTemplate(typeof(ComboBox)) { VisualTree = root };
        var editableTrigger = new Trigger { Property = ComboBox.IsEditableProperty, Value = true };
        editableTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "PART_EditableTextBox"));
        editableTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "textPresenter"));
        template.Triggers.Add(editableTrigger);
        return template;
    }

    private static ControlTemplate ComboItemTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusSmall));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = border };
    }

    /// <summary>下拉项：悬停浅灰、去掉默认方框。</summary>
    private static Style ComboItemStyle()
    {
        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Setters.Add(new Setter(Control.TemplateProperty, ComboItemTemplate()));
        var mouseOver = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        mouseOver.Setters.Add(new Setter(Control.BackgroundProperty, Secondary));
        style.Triggers.Add(mouseOver);
        return style;
    }

    /// <summary>圆角下拉框：用自绘模板，所以不存在「框里还有框」。</summary>
    /// <summary>
    /// 可滚动但**不显示滚动条**的容器：鼠标悬停在上面就能用滚轮 / 触控板双指 / 数位板拖动滚动。
    /// WPF 的 ScrollViewer 在 Hidden 模式下有时不接滚轮事件，所以这里显式处理 PreviewMouseWheel。
    /// </summary>
    public static ScrollViewer HoverScroll(UIElement content, double? height = null)
    {
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            PanningMode = PanningMode.VerticalOnly,
        };
        if (height is not null)
        {
            scroll.Height = height.Value;
        }

        return scroll;
    }

    public static (Border Shell, ComboBox Combo) Select(IEnumerable<string> items, double width = 140, string? selected = null, bool editable = false)
    {
        var combo = new ComboBox
        {
            ItemsSource = items.ToList(),
            ItemContainerStyle = ComboItemStyle(),
            FontFamily = FontFamily,
            FontSize = FontBody,
            Foreground = Text,
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            Width = width + 26,
            Height = 34,
            IsEditable = editable,
            VerticalContentAlignment = VerticalAlignment.Center,
            Template = ComboTemplate(),
        };
        if (selected is not null)
        {
            combo.SelectedItem = selected;
        }
        else if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }

        // 外壳只负责占位：圆角与描边由 ComboBox 自己的模板画
        var shell = new Border
        {
            Background = Brushes.Transparent,
            Child = combo,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0),
        };

        combo.DropDownOpened += (_, _) => combo.BorderBrush = Accent;
        combo.DropDownClosed += (_, _) => combo.BorderBrush = Line;
        return (shell, combo);
    }

    // ---------- 页签胶囊 ----------
    /// <summary>顶部导航胶囊：选中是主色，未选中悬停变浅灰。</summary>
    public static ToggleButton Pill(string text)
    {
        var template = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(RadiusControl));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new Thickness(16, 7, 16, 7));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var pill = new ToggleButton
        {
            Content = text,
            FontFamily = FontFamily,
            FontSize = FontBody,
            Cursor = Cursors.Hand,
            Template = template,
            Background = Transparent,
            Foreground = Muted,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var style = new Style(typeof(ToggleButton));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Muted));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Hover));
        style.Triggers.Add(hover);
        var isChecked = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        isChecked.Setters.Add(new Setter(Control.BackgroundProperty, Accent));
        isChecked.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Triggers.Add(isChecked);
        pill.Style = style;
        return pill;
    }

    // ---------- 数据表 ----------
    /// <summary>列表：圆角卡片 + 表头 + 行（行是 Grid，列宽按比例分配）。</summary>
    public sealed class DataTable
    {
        private readonly Grid _header;
        private readonly StackPanel _rows = new();
        private readonly ScrollViewer _scroll;
        private readonly List<(Border Element, object? Payload)> _entries = new();
        private readonly bool _compact;
        private Border? _selected;
        public Border Root { get; }

        /// <summary>双击某一行时回调（参数是这一行携带的数据）。</summary>
        public Action<object?>? OnActivate { get; set; }

        /// <summary>当前选中那一行携带的数据（没选就是 null）。</summary>
        public object? SelectedPayload => _entries.FirstOrDefault(entry => entry.Element == _selected).Payload;

        public DataTable(IReadOnlyList<(string Title, double Weight, HorizontalAlignment Align)> columns, bool compact = false)
        {
            _compact = compact;
            _header = new Grid { Background = Card };
            for (var i = 0; i < columns.Count; i++)
            {
                _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columns[i].Weight, GridUnitType.Star) });
                var label = Label(columns[i].Title, FontSmall, Muted, bold: true);
                label.HorizontalAlignment = columns[i].Align;
                label.Margin = new Thickness(compact ? 6 : 10, 0, compact ? 6 : 10, 0);
                Grid.SetColumn(label, i);
                _header.Children.Add(label);
            }

            _header.Height = compact ? 30 : 34;

            _rows.Margin = new Thickness(0);

            var stack = new StackPanel();
            stack.Children.Add(_header);
            stack.Children.Add(_rows);

            _scroll = new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0),
            };

            Root = CardPanel(_scroll, padding: 2);
        }

        public void Clear()
        {
            _rows.Children.Clear();
            _entries.Clear();
            _selected = null;
        }

        public UIElement AddRow(IEnumerable<string> cells, bool highlight = false, object? payload = null,
            Action<object?>? onAction = null)
        {
            var grid = new Grid { Height = _compact ? 26 : 32, Background = Brushes.Transparent };
            var values = cells.ToList();
            var columnCount = _header.ColumnDefinitions.Count;
            for (var i = 0; i < columnCount; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = _header.ColumnDefinitions[i].Width });

                // 标题为「详情」的列是操作列：放一个小按钮，而不是文字
                if (!_compact && _header.Children[i] is TextBlock headerCell && headerCell.Text == "详情")
                {
                    if (onAction is not null)
                    {
                        var button = GhostButton("详情", () => onAction(payload), 24);
                        button.FontSize = FontSmall;
                        button.Padding = new Thickness(10, 0, 10, 0);
                        button.HorizontalAlignment = HorizontalAlignment.Left;
                        button.Margin = new Thickness(6, 0, 6, 0);
                        Grid.SetColumn(button, i);
                        grid.Children.Add(button);
                    }

                    continue;
                }

                var text = i < values.Count ? values[i] : "";
                var label = Label(text, _compact ? FontSmall : FontBody, highlight ? Accent : Text);
                label.Margin = new Thickness(_compact ? 6 : 10, 0, _compact ? 6 : 10, 0);
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                var align = _header.Children.Count > i && _header.Children[i] is TextBlock headerLabel
                    ? headerLabel.HorizontalAlignment
                    : HorizontalAlignment.Left;
                label.HorizontalAlignment = align;
                Grid.SetColumn(label, i);
                grid.Children.Add(label);
            }

            var separator = new Border
            {
                BorderBrush = Separator,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = grid,
                Background = Brushes.Transparent,
                Cursor = payload is null ? Cursors.Arrow : Cursors.Hand,
            };

            if (payload is not null)
            {
                separator.MouseLeftButtonDown += (_, e) =>
                {
                    _selected = separator;
                    foreach (var entry in _entries)
                    {
                        entry.Element.Background = entry.Element == separator
                            ? Freeze(Color.FromRgb(0xEE, 0xF2, 0xFF))
                            : Brushes.Transparent;
                    }

                    if (e.ClickCount >= 2)
                    {
                        OnActivate?.Invoke(payload);
                    }
                };
            }

            _rows.Children.Add(separator);
            _entries.Add((separator, payload));
            return separator;
        }

        public void AddEmpty(string message)
        {
            var label = Hint(message);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 24, 0, 24);
            _rows.Children.Add(label);
        }
    }

    public static Grid TwoColumn(string left, string right, double leftWidth = 92)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = Label(left, FontBody, Muted);
        label.VerticalAlignment = VerticalAlignment.Center;
        var content = Label(right, FontBody);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(label);
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>转圈（加载中）：矢量弧线 + 旋转动画，圆角端点，不会有锯齿。</summary>
    public static FrameworkElement Spinner(double size = 32)
    {
        var spinner = new Path
        {
            Data = Geometry.Parse("M 16 2 A 14 14 0 0 1 30 16"),
            Stroke = Accent,
            StrokeThickness = 3.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = size,
            Height = size,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var spin = new RotateTransform(0);
        spinner.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            });
        return spinner;
    }

    /// <summary>
    /// 全局样式：把系统默认的「方角滚动条」换成圆角细滚动条，ToolTip 也统一成圆角卡片。
    /// 在创建任何窗口之前调用一次。
    /// </summary>
    public static void InstallGlobalStyles()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        // 美化样式绝不能挡住启动：任何一项出错都只记日志，程序照常跑
        Try(() => app.Resources[typeof(ScrollBar)] = ScrollBarStyle(), "圆角滚动条");
        Try(() => app.Resources[typeof(ToolTip)] = ToolTipStyle(), "圆角提示气泡");
    }

    private static void Try(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception exc)
        {
            Diag.Trace($"全局样式「{what}」安装失败（不影响使用）：{exc.Message}");
        }
    }

    private const string VerticalBarXaml = @"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>
  <Grid Background='Transparent'>
    <Track x:Name='PART_Track' IsDirectionReversed='True'>
      <Track.DecreaseRepeatButton>
        <RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/>
      </Track.DecreaseRepeatButton>
      <Track.Thumb>
        <Thumb>
          <Thumb.Template>
            <ControlTemplate TargetType='Thumb'>
              <Border CornerRadius='6' Background='__THUMB__' Margin='1'/>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
      <Track.IncreaseRepeatButton>
        <RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/>
      </Track.IncreaseRepeatButton>
    </Track>
  </Grid>
</ControlTemplate>";

    private const string HorizontalBarXaml = @"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>
  <Grid Background='Transparent'>
    <Track x:Name='PART_Track' IsDirectionReversed='False'>
      <Track.DecreaseRepeatButton>
        <RepeatButton Command='ScrollBar.PageLeftCommand' Opacity='0' Focusable='False'/>
      </Track.DecreaseRepeatButton>
      <Track.Thumb>
        <Thumb>
          <Thumb.Template>
            <ControlTemplate TargetType='Thumb'>
              <Border CornerRadius='6' Background='__THUMB__' Margin='1'/>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
      <Track.IncreaseRepeatButton>
        <RepeatButton Command='ScrollBar.PageRightCommand' Opacity='0' Focusable='False'/>
      </Track.IncreaseRepeatButton>
    </Track>
  </Grid>
</ControlTemplate>";

    private const string ToolTipXaml = @"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ToolTip'>
  <Border CornerRadius='8' Background='#2B3036' Padding='10,6,10,6'>
    <ContentPresenter TextBlock.Foreground='White' TextBlock.FontSize='12'/>
  </Border>
</ControlTemplate>";

    /// <summary>圆角滚动条（滚轮/拖拽/点轨道都能用，只是没有箭头按钮，视觉更干净）。</summary>
    public static Style ScrollBarStyle()
    {
        var style = new Style(typeof(ScrollBar));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.WidthProperty, 10.0));
        style.Setters.Add(new Setter(Control.TemplateProperty, ParseTemplate(VerticalBarXaml.Replace("__THUMB__", ThumbHex()))));
        var horizontal = new Trigger { Property = ScrollBar.OrientationProperty, Value = Orientation.Horizontal };
        horizontal.Setters.Add(new Setter(Control.TemplateProperty, ParseTemplate(HorizontalBarXaml.Replace("__THUMB__", ThumbHex()))));
        horizontal.Setters.Add(new Setter(Control.WidthProperty, double.NaN));
        horizontal.Setters.Add(new Setter(Control.HeightProperty, 10.0));
        style.Triggers.Add(horizontal);
        return style;
    }

    /// <summary>ToolTip：圆角深色卡片，不用系统那层方框。</summary>
    public static Style ToolTipStyle()
    {
        var style = new Style(typeof(ToolTip));
        style.Setters.Add(new Setter(Control.TemplateProperty, ParseTemplate(ToolTipXaml)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, FontFamily));
        return style;
    }

    private static string ThumbHex() => $"#{ThumbColor.R:X2}{ThumbColor.G:X2}{ThumbColor.B:X2}";

    private static ControlTemplate ParseTemplate(string xaml) => (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    /// <summary>分隔线。</summary>
    public static Border Divider(double marginTop = 10, double marginBottom = 10) => new()
    {
        Height = 1,
        Background = Line,
        Margin = new Thickness(0, marginTop, 0, marginBottom),
    };
}
