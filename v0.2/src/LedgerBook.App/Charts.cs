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
    /// <summary>浅色调色板（Office / Excel 默认色系，观感最接近办公软件图表）。</summary>
    private static readonly Color[] LightPalette =
    {
        Color.FromRgb(0x44, 0x72, 0xC4), Color.FromRgb(0xED, 0x7D, 0x31), Color.FromRgb(0xA5, 0xA5, 0xA5),
        Color.FromRgb(0xFF, 0xC0, 0x00), Color.FromRgb(0x5B, 0x9B, 0xD5), Color.FromRgb(0x70, 0xAD, 0x47),
        Color.FromRgb(0x26, 0x44, 0x78), Color.FromRgb(0x9E, 0x48, 0x0E), Color.FromRgb(0x63, 0x63, 0x63),
        Color.FromRgb(0x99, 0x73, 0x00), Color.FromRgb(0x25, 0x5E, 0x91), Color.FromRgb(0x43, 0x68, 0x2B)
    };

    /// <summary>
    /// 深色调色板：同样的 Office 色系，但**降饱和、提亮**，在黑底上不会「发光」。
    /// </summary>
    private static readonly Color[] DarkPalette =
    {
        Color.FromRgb(0x6E, 0x9B, 0xE0), Color.FromRgb(0xF2, 0x9B, 0x5C), Color.FromRgb(0xB8, 0xB8, 0xB8),
        Color.FromRgb(0xFF, 0xD1, 0x4D), Color.FromRgb(0x82, 0xB6, 0xE3), Color.FromRgb(0x93, 0xC1, 0x72),
        Color.FromRgb(0x4A, 0x6E, 0xA8), Color.FromRgb(0xBE, 0x6C, 0x35), Color.FromRgb(0x8A, 0x8A, 0x8A),
        Color.FromRgb(0xC2, 0x9B, 0x2E), Color.FromRgb(0x4B, 0x82, 0xB4), Color.FromRgb(0x6A, 0x91, 0x51)
    };

    private static Color[] Palette = LightPalette;

    /// <summary>主题切换时换调色板。</summary>
    public static void UsePalette(bool dark) => Palette = dark ? DarkPalette : LightPalette;
    private static Brush BrushAt(int index) => Freeze(Palette[index % Palette.Length]);
    private static Brush Freeze(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Color ColorAt(int index) => Palette[index % Palette.Length];
    /// <summary>本次绘制要高亮的分类（空 = 都不变暗）。由 Draw 设置。</summary>
    private static string? _highlight;
    /// <summary>把颜色朝背景混合 = 变暗退到后面（浅色/深色/自定义配色下都成立）。</summary>
    private static Color Dim(Color color) => Ui.Blend(color, Ui.CardColor, 0.62);
    /// <summary>这个标签是否属于高亮分类（支持"收入·工资"这种带前缀的标签）。</summary>
    private static bool IsHighlighted(string label) => _highlight is not null && (label == _highlight || label.EndsWith("·" + _highlight, StringComparison.Ordinal));
    /// <summary>取色：细选了某个分类时，其它分类变暗。</summary>
    private static Color Shade(string label, int index) => _highlight is null || IsHighlighted(label) ? ColorAt(index) : Dim(ColorAt(index));
    /// <summary>往白色靠（提亮）。不能拿 Darken 传 &gt;1：byte 转换会溢出回绕。</summary>
    private static Brush VerticalGradient(Color from, Color to) => Gradient(from, to, new Point(0, 0), new Point(0, 1));

    private static Brush HorizontalGradient(Color from, Color to) => Gradient(from, to, new Point(0, 0), new Point(1, 0));

    private static Brush Gradient(Color from, Color to, Point start, Point end)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        brush.GradientStops.Add(new GradientStop(from, 0));
        brush.GradientStops.Add(new GradientStop(to, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>多边形（画斜顶面/斜侧面用）。</summary>
    private static Geometry Quad(params Point[] points)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
        for (var i = 1; i < points.Length; i++)
        {
            figure.Segments.Add(new LineSegment(points[i], true));
        }

        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)(color.R + (255 - color.R) * amount),
        (byte)(color.G + (255 - color.G) * amount),
        (byte)(color.B + (255 - color.B) * amount));

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
                Background = Freeze(Shade(item.Label, item.Index)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            row.Children.Add(Ui.Label($"{item.Label}　{item.Share * 100:0.0}%", Ui.FontSmall, Ui.Text));
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

        // 照 plotrix::pie3D（explode=0 的普通立体饼）重写，结构就两步：
        //   ① 先把**所有**外壁画完  ② 再把**所有**顶面盖上
        // 这样不存在"谁盖谁"的层次问题；左右半边也完全对称。
        // 几何要点：顶面是压扁的椭圆（2θ/π = 0.333），外壁是"朝前的下半圈"往下平移 depth 形成的带，
        //          外壁两端落在椭圆左右极点 → 边缘竖直；画布高度按几何算足，绝不溢出。
        const double flatten = 0.333;
        const double depth = 14;
        var width = size + 12;
        var halfHeight = size / 2 * flatten;
        var height = halfHeight * 2 + depth + 12;
        var center = new Point(width / 2, 6 + halfHeight);
        var radius = size / 2 - 4;

        var canvas = new Canvas { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Center };

        // 各牙的角度（从 12 点开始顺时针，和参考图一致）
        var wedges = new List<(double A0, double A1, Color Color, int Index)>();
        var acc = 0.0;
        foreach (var item in series)
        {
            var sweep = item.Share * 360.0;
            wedges.Add((-90.0 + acc, -90.0 + acc + sweep, Shade(item.Label, item.Index), item.Index));
            acc += sweep;
        }

        // ① 所有外壁（只画朝前的下半圈，即屏幕角度 0°~180° 与各牙角度的交集）
        foreach (var wedge in wedges)
        {
            var from = Math.Max(wedge.A0, 0.0);
            var to = Math.Min(wedge.A1, 180.0);
            if (to <= from)
            {
                continue;
            }

            var wall = Freeze(Darken(wedge.Color, 0.75));   // plotrix 的 shade=0.75：纯色平涂
            canvas.Children.Add(new Path
            {
                Data = OuterWall(center, radius, from, to, flatten, depth),
                Fill = wall,
            });
        }

        // ② 所有顶面
        foreach (var wedge in wedges)
        {
            canvas.Children.Add(new Path
            {
                Data = TopFace(center, radius, wedge.A0, wedge.A1, flatten, series.Count == 1),
                Fill = Freeze(wedge.Color),
            });
        }


        // ③ 白色分隔线：改成**径向直线**（每个分界画一次）——描边勾勒会在椭圆左右极点留下"竖刺"
        if (wedges.Count > 1)   // 只有一个扇形时没有分界，画了会在正上方多一条白竖线
        {
            foreach (var wedge in wedges)
            {
                var edge = EllipsePoint(center, radius, wedge.A0, flatten);
                canvas.Children.Add(new Line
                {
                    X1 = center.X,
                    Y1 = center.Y,
                    X2 = edge.X,
                    Y2 = edge.Y,
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                });
            }
        }

        // ④ 外轮廓：**一整条闭合椭圆**（一条曲线，两端不会有描边端点）
        canvas.Children.Add(new Path
        {
            Data = new EllipseGeometry(center, radius, radius * flatten),
            Stroke = Brushes.White,
            StrokeThickness = 1,
            StrokeLineJoin = PenLineJoin.Bevel,
            Fill = null,
        });

        // ⑤ 立体轮廓的另一半：**底边 + 前端两条竖切边**
        //（直接用外壁自己的几何描白边：前弧 + 两端竖直落下的边 + 底弧）
        canvas.Children.Add(new Path
        {
            Data = OuterWall(center, radius, 0, 180, flatten, depth),
            Stroke = Brushes.White,
            StrokeThickness = 1,
            StrokeLineJoin = PenLineJoin.Bevel,   // 关键：默认 Miter 尖角会在竖直边上拉出白色小刺
            Fill = null,
        });
        var layout = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        layout.Children.Add(new Grid
        {
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipToBounds = true,          // 双保险：几何再算错也不会漏到卡片外面
            Children = { canvas },
        });
        var legendScroll = Ui.HoverScroll(Legend(series, 8));   // 不钉高度：图例有几项就多高，超出才滚
        legendScroll.MaxHeight = 120;
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

    /// <summary>压扁椭圆上的一点（angle 用度，-90 = 12 点方向）。</summary>
    private static Point EllipsePoint(Point center, double radius, double angle, double flatten)
    {
        var radians = angle * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians) * flatten);
    }

    /// <summary>顶面：一块扇形（只有一类时是整圆）。</summary>
    private static Geometry TopFace(Point center, double radius, double a0, double a1, double flatten, bool whole)
    {
        if (whole)
        {
            return new EllipseGeometry(center, radius, radius * flatten);
        }

        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = center, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(EllipsePoint(center, radius, a0, flatten), true));
        figure.Segments.Add(new ArcSegment(
            EllipsePoint(center, radius, a1, flatten),
            new Size(radius, radius * flatten),
            0,
            Math.Abs(a1 - a0) > 180,
            SweepDirection.Clockwise,
            true));
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>外弧侧壁：外弧 + 往下 depth 的那条外弧围成的"墙"。</summary>
    private static Geometry OuterWall(Point center, double radius, double a0, double a1, double flatten, double depth)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = EllipsePoint(center, radius, a0, flatten), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(
            EllipsePoint(center, radius, a1, flatten),
            new Size(radius, radius * flatten),
            0,
            Math.Abs(a1 - a0) > 180,
            SweepDirection.Clockwise,
            true));
        figure.Segments.Add(new LineSegment(
            new Point(EllipsePoint(center, radius, a1, flatten).X, EllipsePoint(center, radius, a1, flatten).Y + depth), true));
        figure.Segments.Add(new ArcSegment(
            new Point(EllipsePoint(center, radius, a0, flatten).X, EllipsePoint(center, radius, a0, flatten).Y + depth),
            new Size(radius, radius * flatten),
            0,
            Math.Abs(a1 - a0) > 180,
            SweepDirection.Counterclockwise,
            true));
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>径向面：圆心—外沿这条直边往下 depth 形成的四边形（朝前才画）。</summary>

    /// <summary>一块扇形（只有一类时是整圆，ArcSegment 画不出 360 度）。</summary>

    private static Point PointOn(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    // ---------------------------------------------------------------- 横向柱状图

    public static UIElement Bar(IReadOnlyList<CategoryTotal> data, double plotHeight = 150)
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
        // 用 UniformGrid 让所有柱子平分宽度：分类多的时候柱子变窄，但一定全部看得见（不用滚动）
        var strip = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        const double boxWidth = 26;
        const double depthX = 6;      // 右侧面的宽度
        const double depthY = 6;      // 顶面的高度

        foreach (var item in series)
        {
            var ratio = max > 0 ? (double)(item.Amount / max) : 0;
            var barHeight = Math.Max(6, plotHeight * ratio);
            var color = Shade(item.Label, item.Index);

            var column = new StackPanel
            {
                Margin = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };


            var share = Ui.Label($"{item.Share * 100:0.0}%", Ui.FontSmall, Ui.Muted);
            share.HorizontalAlignment = HorizontalAlignment.Center;
            share.TextAlignment = TextAlignment.Center;
            share.Margin = new Thickness(0, 0, 0, 2);
            column.Children.Add(share);

            // 竖着立起来的 3D 方柱：正面（横向渐变）+ 斜顶面 + 斜右侧面，像真的挤出来的方块
            var box = new Canvas
            {
                Width = boxWidth + depthX,
                Height = barHeight + depthY,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            box.Children.Add(new Path
            {
                Data = Quad(new Point(0, depthY), new Point(depthX, 0), new Point(boxWidth + depthX, 0), new Point(boxWidth, depthY)),
                Fill = HorizontalGradient(Lighten(color, 0.62), Lighten(color, 0.34)),
            });
            box.Children.Add(new Path
            {
                Data = Quad(new Point(boxWidth, depthY), new Point(boxWidth + depthX, 0), new Point(boxWidth + depthX, barHeight), new Point(boxWidth, barHeight + depthY)),
                Fill = VerticalGradient(Darken(color, 0.38), Darken(color, 0.68)),
            });
            box.Children.Add(new Path
            {
                Data = Quad(new Point(0, depthY), new Point(boxWidth, depthY), new Point(boxWidth, barHeight + depthY), new Point(0, barHeight + depthY)),
                Fill = HorizontalGradient(Lighten(color, 0.18), Darken(color, 0.10)),
            });
            column.Children.Add(box);

            var name = Ui.Label(item.Label, Ui.FontSmall, Ui.Muted);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.TextAlignment = TextAlignment.Center;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 72;
            name.Margin = new Thickness(0, 3, 0, 0);
            column.Children.Add(name);

            strip.Children.Add(column);
        }

        panel.Children.Add(strip);

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
        // 细选了某个分类时：其余分类的格子也变暗（只按调色板下标判断，不动循环结构）
        var dimmed = _highlight is null
            ? new System.Collections.Generic.HashSet<int>()
            : series.Where(item => !IsHighlighted(item.Label)).Select(item => item.Index).ToHashSet();
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
                Background = index < 0
                    ? Freeze(Color.FromRgb(0xE8, 0xE8, 0xE8))
                    : Freeze(dimmed.Contains(index) ? Dim(ColorAt(index)) : ColorAt(index)),
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
    /// <summary>
    /// 把分类小计画成对应的图形：**收入、支出分成两张图**（各自按本类的合计算占比）。
    /// 每张图头上写"合计金额"，图里只标各项百分比（具体金额看左边表格）。
    /// </summary>
    public static UIElement Draw(string view, IReadOnlyList<CategoryTotal> data, bool compact = false, string? highlight = null)
    {
        _highlight = highlight;   // 本次要高亮的分类；其余在 Shade/Dim 里变暗
        // 饼状图 / 方格图：**收入、支出两张图左右平列**（省一半高度，月度汇总里留白也少了）
        // 柱状图：仍上下排（柱子需要横向宽度），但整体更紧凑
        // 只有"紧凑模式"（月度汇总）才把饼图/方格图的收支两张图左右平列；查询统计里上下排（左右会挤掉下面的图例）
        var sideBySide = compact && view is "饼状图" or "方格图";
        var host = sideBySide
            ? (Panel)new Grid()
            : new StackPanel();

        if (sideBySide)
        {
            ((Grid)host).ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ((Grid)host).ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var index = 0;
        foreach (var kind in new[] { Kinds.Expense, Kinds.Income })
        {
            var list = data.Where(item => item.Kind == kind).ToList();
            if (list.Count == 0)
            {
                continue;
            }

            var section = Section(kind, list, view, sideBySide, compact);
            if (sideBySide)
            {
                Grid.SetColumn(section, index);
            }

            host.Children.Add(section);
            index++;
        }

        if (host.Children.Count == 0)
        {
            host.Children.Add(Ui.Hint("这个范围内还没有数据"));
        }

        return host;
    }

    private static UIElement Section(string kind, IReadOnlyList<CategoryTotal> list, string view, bool sideBySide, bool compact)
    {
        var symbol = Settings.Load().CurrencySymbol;
        var total = list.Sum(item => item.Amount);
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 2),
        };
        head.Children.Add(Ui.Label(kind, Ui.FontSmall, kind == Kinds.Income ? Ui.Income : Ui.Expense, bold: true));
        head.Children.Add(Ui.Label($"　合计 {Valid.Money(total.ToString(CultureInfo.InvariantCulture), symbol)}",
            Ui.FontSmall, Ui.Muted));

        // Grid 两行（Auto 标题 / Star 图），图区有确定高度，太小就能在卡片内滚（不显示滚动条）
        var box = new Grid { Margin = new Thickness(0, 0, sideBySide ? 4 : 0, sideBySide ? 0 : 6) };
        box.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        box.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        box.Children.Add(head);
        var body = ChartBody(view, list, compact);
        Grid.SetRow(body, 1);
        box.Children.Add(body);
        return box;
    }

    private static UIElement ChartBody(string view, IReadOnlyList<CategoryTotal> list, bool compact) => view switch
    {
        "饼状图" => Pie(list, compact ? 92 : 108),
        "柱状图" => Bar(list, compact ? 46 : 62),
        "方格图" => Waffle(list, compact ? 8 : 10),
        _ => Ui.Hint("未知的视图"),
    };
}
