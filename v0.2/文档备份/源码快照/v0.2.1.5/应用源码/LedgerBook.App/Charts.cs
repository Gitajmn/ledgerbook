using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>
/// 分类统计的三种图形：饼状图、横向柱状图、10×10 方格图。
///
/// 全部用 WPF 的矢量图形（Path / Border）画：圆角与斜线都是矢量渲染，**不会有锯齿**，
/// 也不需要任何第三方绘图库。
/// </summary>
public static class Charts
{
    /// <summary>浅色调色板。</summary>
    private static readonly Color[] LightPalette =
    {
        Color.FromRgb(0x4E, 0x79, 0xA7), Color.FromRgb(0xF2, 0x8E, 0x2B), Color.FromRgb(0xE1, 0x57, 0x59),
        Color.FromRgb(0x76, 0xB7, 0xB2), Color.FromRgb(0x59, 0xA1, 0x4F), Color.FromRgb(0xED, 0xC9, 0x48),
        Color.FromRgb(0xB0, 0x7A, 0xA1), Color.FromRgb(0xFF, 0x9D, 0xA7), Color.FromRgb(0x9C, 0x75, 0x5F),
        Color.FromRgb(0xBA, 0xB0, 0xAC), Color.FromRgb(0x1F, 0x77, 0xB4), Color.FromRgb(0xD6, 0x27, 0x28)
    };

    /// <summary>
    /// 深色调色板：同样的色系，但**降饱和、提亮**，在黑底上不会「发光」。
    /// </summary>
    private static readonly Color[] DarkPalette =
    {
        Color.FromRgb(0x7C, 0x9E, 0xC8), Color.FromRgb(0xE0, 0xA5, 0x63), Color.FromRgb(0xE0, 0x84, 0x86),
        Color.FromRgb(0x8F, 0xC4, 0xC0), Color.FromRgb(0x86, 0xBC, 0x7E), Color.FromRgb(0xD9, 0xC4, 0x7A),
        Color.FromRgb(0xC1, 0x9C, 0xB6), Color.FromRgb(0xE8, 0xAE, 0xB4), Color.FromRgb(0xB5, 0x99, 0x89),
        Color.FromRgb(0xB4, 0xAC, 0xA8), Color.FromRgb(0x6E, 0xA8, 0xD8), Color.FromRgb(0xDF, 0x7A, 0x7B)
    };

    private static Color[] Palette = LightPalette;

    /// <summary>主题切换时换调色板。</summary>
    public static void UsePalette(bool dark) => Palette = dark ? DarkPalette : LightPalette;

    private static Brush BrushAt(int index) => Freeze(Palette[index % Palette.Length]);
    private static Brush Freeze(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Color ColorAt(int index) => Palette[index % Palette.Length];
    private static Color Darken(Color color, double factor) => Color.FromRgb(
        (byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));

    /// <summary>把分类小计整理成画图要用的序列（只保留有权重的分类）。</summary>
    public static List<(string Label, decimal Amount, int Count, double Share, int Index)> Series(
        IReadOnlyList<CategoryTotal> data, double minShare = 0.0005)
    {
        var total = data.Sum(item => item.Amount);
        var seen = data.GroupBy(item => item.Category).ToDictionary(group => group.Key, group => group.Count());
        var series = new List<(string, decimal, int, double, int)>();
        var index = 0;
        foreach (var item in data)
        {
            var share = total > 0 ? (double)(item.Amount / total) : 0;
            if (share < minShare)
            {
                continue;   // 占比小到显示成 0.0% 的，不画进图里
            }

            var label = seen[item.Category] > 1 ? $"{item.Kind}·{item.Category}" : item.Category;
            series.Add((label, item.Amount, item.Count, share, index++));
        }

        return series;
    }

    /// <summary>被「只画有权重分类」挡掉的那几项，用一行小字说明。</summary>
    public static string HiddenNote(IReadOnlyList<CategoryTotal> data, double minShare = 0.0005)
    {
        var total = data.Sum(item => item.Amount);
        if (total <= 0)
        {
            return "";
        }

        var hidden = data.Count(item => (double)(item.Amount / total) < minShare);
        return hidden == 0 ? "" : $"另有 {hidden} 项占比不足 {minShare * 100:0.##}%，未在图中显示";
    }

    // ---------------------------------------------------------------- 图例

    private static UIElement Legend(IReadOnlyList<(string Label, decimal Amount, int Count, double Share, int Index)> series,
        int maxLines = 12)
    {
        var panel = new StackPanel { Margin = new Thickness(8, 6, 0, 0) };
        var symbol = Settings.Load().CurrencySymbol;
        var shown = 0;
        foreach (var item in series)
        {
            if (shown++ >= maxLines)
            {
                panel.Children.Add(Ui.Hint($"…… 还有 {series.Count - maxLines} 项"));
                break;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(2),
                Background = BrushAt(item.Index),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            row.Children.Add(Ui.Label($"{item.Label}　{Valid.Money(item.Amount.ToString(CultureInfo.InvariantCulture), symbol)}　{item.Share * 100:0.0}%",
                Ui.FontSmall, Ui.Text));
            panel.Children.Add(row);
        }

        return panel;
    }

    // ---------------------------------------------------------------- 饼状图

    public static UIElement Pie(IReadOnlyList<CategoryTotal> data, double size = 190)
    {
        var series = Series(data);
        var host = new Grid();
        if (series.Count == 0)
        {
            host.Children.Add(Ui.Hint("这个范围内还没有数据"));
            return host;
        }

        var canvas = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left };
        var center = new Point(size / 2, size / 2);
        var radius = size / 2 - 4;
        var start = -90.0;   // 从 12 点方向开始
        foreach (var item in series)
        {
            var sweep = item.Share * 360.0;
            // 只有一项时画整圆（ArcSegment 画不出 360 度）
            if (series.Count == 1)
            {
                canvas.Children.Add(new Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    Fill = BrushAt(item.Index),
                    Margin = new Thickness(4, 4, 0, 0)
                });
                break;
            }

            var path = new Path { Fill = BrushAt(item.Index), Stroke = Brushes.White, StrokeThickness = 1 };
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = center, IsClosed = true, IsFilled = true };
            var endAngle = start + sweep;
            figure.Segments.Add(new LineSegment(PointOn(center, radius, start), true));
            figure.Segments.Add(new ArcSegment(
                PointOn(center, radius, endAngle),
                new Size(radius, radius),
                0,
                sweep > 180,
                SweepDirection.Clockwise,
                true));
            geometry.Figures.Add(figure);
            path.Data = geometry;
            canvas.Children.Add(path);
            start = endAngle;
        }

        // 布局：饼在上、图例在下（面板窄的时候图例也不会被挤没，可滚动）
        var layout = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        canvas.HorizontalAlignment = HorizontalAlignment.Center;
        layout.Children.Add(canvas);
        var legendScroll = new ScrollViewer
        {
            Content = Legend(series, 8),
            MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        layout.Children.Add(legendScroll);
        var note = HiddenNote(data);
        if (note.Length > 0)
        {
            var hint = Ui.Hint(note);
            hint.Margin = new Thickness(0, 4, 0, 0);
            hint.TextWrapping = TextWrapping.Wrap;
            layout.Children.Add(hint);
        }

        host.Children.Add(layout);
        return host;
    }

    private static Point PointOn(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    // ---------------------------------------------------------------- 横向柱状图

    public static UIElement Bar(IReadOnlyList<CategoryTotal> data)
    {
        var series = Series(data);
        var panel = new StackPanel();
        if (series.Count == 0)
        {
            panel.Children.Add(Ui.Hint("这个范围内还没有数据"));
            return panel;
        }

        var symbol = Settings.Load().CurrencySymbol;
        var max = series.Max(item => item.Amount);
        // 可用宽度按面板估计，柱子据此换算（不做精确测量，够用且不会因为布局未完成而算出 0）
        const double plotWidth = 150.0;
        foreach (var item in series)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };

            var value = Ui.Label($"{Valid.Money(item.Amount.ToString(CultureInfo.InvariantCulture), symbol)}　{item.Share * 100:0.0}%", Ui.FontSmall);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.TextAlignment = TextAlignment.Right;
            value.Margin = new Thickness(6, 0, 0, 0);
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);

            var name = Ui.Label(item.Label, Ui.FontSmall);
            name.Width = 58;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);

            // 柱子：浅色轨道 + 彩色主体 + 略深的底边（一点立体感；矢量绘制不会有锯齿）
            var ratio = max > 0 ? (double)(item.Amount / max) : 0;
            var track = new Grid { Height = 18, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border
            {
                Background = Freeze(Color.FromRgb(0xEE, 0xF0, 0xF3)),
                CornerRadius = new CornerRadius(Ui.RadiusSmall),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
            var bar = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(8, plotWidth * ratio)
            };
            bar.Children.Add(new Border
            {
                Background = BrushAt(item.Index),
                CornerRadius = new CornerRadius(Ui.RadiusSmall)
            });
            bar.Children.Add(new Border
            {
                Background = Freeze(Darken(ColorAt(item.Index), 0.72)),
                CornerRadius = new CornerRadius(Ui.RadiusSmall),
                Height = 4,
                VerticalAlignment = VerticalAlignment.Bottom
            });
            track.Children.Add(bar);
            row.Children.Add(track);

            panel.Children.Add(row);
        }

        var note = HiddenNote(data);
        if (note.Length > 0)
        {
            var hint = Ui.Hint(note);
            hint.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(hint);
        }

        return panel;
    }

    // ---------------------------------------------------------------- 10×10 方格图

    public static UIElement Waffle(IReadOnlyList<CategoryTotal> data, double cell = 15)
    {
        var series = Series(data);
        var panel = new StackPanel();
        if (series.Count == 0)
        {
            panel.Children.Add(Ui.Hint("这个范围内还没有数据"));
            return panel;
        }

        const int cells = 100;
        var counts = WaffleCounts(series.Select(item => item.Share).ToList(), cells);
        var colors = new List<int>();
        for (var i = 0; i < series.Count; i++)
        {
            for (var j = 0; j < counts[i]; j++)
            {
                colors.Add(series[i].Index);
            }
        }

        while (colors.Count < cells)
        {
            colors.Add(-1);   // 空格子
        }

        var grid = new UniformGrid { Rows = 10, Columns = 10, Width = cell * 10, Height = cell * 10, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var index in colors)
        {
            grid.Children.Add(new Border
            {
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Background = index < 0 ? Freeze(Color.FromRgb(0xE8, 0xE8, 0xE8)) : BrushAt(index)
            });
        }

        panel.Children.Add(grid);
        var filled = colors.Count(index => index >= 0);
        var caption = Ui.Hint($"每格 1%　有色 {filled} 格");
        caption.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(caption);
        panel.Children.Add(Legend(series, 5));
        return panel;
    }

    /// <summary>把占比换算成方格数（最大余数法，保证总和正好 100）。</summary>
    public static List<int> WaffleCounts(IReadOnlyList<double> shares, int total)
    {
        if (shares.Count == 0)
        {
            return new List<int>();
        }

        var exact = shares.Select(share => share * total).ToList();
        var counts = exact.Select(value => (int)Math.Floor(value)).ToList();
        var remainder = total - counts.Sum();
        if (remainder > 0)
        {
            var order = Enumerable.Range(0, shares.Count)
                .OrderByDescending(index => exact[index] - counts[index])
                .ToList();
            for (var i = 0; i < remainder; i++)
            {
                counts[order[i % order.Count]]++;
            }
        }

        return counts;
    }

    /// <summary>把某一项的分类小计画成对应的图形。</summary>
    public static UIElement Draw(string view, IReadOnlyList<CategoryTotal> data) => view switch
    {
        "饼状图" => Pie(data),
        "柱状图" => Bar(data),
        "方格图" => Waffle(data),
        _ => Ui.Hint("未知的视图")
    };
}
