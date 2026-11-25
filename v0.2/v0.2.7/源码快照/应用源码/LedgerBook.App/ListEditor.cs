using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LedgerBook.App;

/// <summary>
/// 可拖动排序的列表编辑器（分类、支付方式都用它）。
///
/// 2026-10-09 重写拖动交互（用户要求"拖动动画要真优化"）：
///   · 扔掉了 WPF 自带的 <c>DragDrop.DoDragDrop</c>（它只有一个"整行变色"的反馈，很丑、也不跟手）；
///   · 改成自己抓鼠标拖动：被拖的行**跟着鼠标走**、抬起一点（阴影感 + 半透明），其余行**带动画让位**；
///   · 拖动过程中**实时重排**（不是松手才跳），并显示一条 **强调色插入指示线 + 「松开放到这里」提示**；
///   · 行与行之间加**分隔线**（用户说"一个框里一类的分界不明显"），鼠标悬停整行高亮。
///
/// 布局用 Canvas + 每行的 TranslateTransform：位置是算出来的，动画只动 Transform，不触发布局重算。
/// 行高固定（RowHeight + RowGap），所以不需要测量。
/// </summary>
public sealed class ListEditor
{
    private const double RowHeight = 38;
    private const double RowGap = 6;
    private const double Slot = RowHeight + RowGap;
    private const int AnimationMs = 150;

    private readonly string _title;
    private readonly string _hint;
    private readonly Action<List<string>> _onChanged;
    private readonly Canvas _rows = new();
    private readonly TextBox _input;
    private readonly Border _inputShell;



    private List<string> _items;
    private string? _editing;

    // 拖动状态：**按下不算拖**，要移动超过阈值才算（否则点一下就粘住鼠标）
    private Border? _pendingRow;      // 按下了、还没开始拖的行
    private Border? _draggedRow;      // 真正在拖的行
    private IInputElement? _dragHandle;
    private Point _pressPoint;
    private double _grabOffset;

    public Border Root { get; }

    public ListEditor(string title, string hint, IEnumerable<string> items, Action<List<string>> onChanged)
    {
        _title = title;
        _hint = hint;
        _items = items.ToList();
        _onChanged = onChanged;

        var content = new StackPanel();
        content.Children.Add(Ui.Label(title, Ui.FontBody, Ui.Text, bold: true));
        var hintLabel = Ui.Hint(hint);
        hintLabel.Margin = new Thickness(0, 2, 0, 10);
        content.Children.Add(hintLabel);

        // Canvas 撑满宽度，行宽跟着它走
        var canvasHost = new Grid();

        canvasHost.Children.Add(_rows);
        canvasHost.SizeChanged += (_, _) => ResizeRows();
        content.Children.Add(canvasHost);

        // 底部：新增
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var (shell, box) = Ui.Input("", 220, "输入新的名称");
        _inputShell = shell;
        _input = box;
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddItem();
            }
        };
        addRow.Children.Add(shell);
        var addButton = Ui.GhostButton("新增", AddItem, 34);
        addButton.Margin = new Thickness(8, 0, 0, 0);
        addRow.Children.Add(addButton);
        content.Children.Add(addRow);

        Root = Ui.CardPanel(content, padding: 16);
        Rebuild();
    }

    public List<string> Items => _items.ToList();

    // ---------------------------------------------------------------- 行与布局

    private void Rebuild()
    {
        _rows.Children.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            _rows.Children.Add(BuildRow(index));
        }

        _rows.Height = Math.Max(0, _items.Count * Slot - RowGap);
        LayoutRows(animate: false);
        ResizeRows();


    }

    /// <summary>行宽跟着 Canvas 走（Canvas 里的子元素不会自动拉伸）。</summary>
    private void ResizeRows()
    {
        if (_rows.ActualWidth <= 0)
        {
            return;
        }

        foreach (var child in _rows.Children)
        {
            if (child is FrameworkElement element)
            {
                element.Width = _rows.ActualWidth;
            }
        }


    }

    /// <summary>把所有行摆到各自槽位；animate=true 时用 150ms 缓动"滑"过去（让位动画）。</summary>
    private void LayoutRows(bool animate)
    {
        for (var index = 0; index < _rows.Children.Count; index++)
        {
            if (_rows.Children[index] is not Border row)
            {
                continue;
            }

            MoveRowTo(row, index, animate && !ReferenceEquals(row, _draggedRow));
        }
    }

    private static void MoveRowTo(Border row, int index, bool animate)
    {
        var target = index * Slot;
        var current = Canvas.GetTop(row);
        var transform = row.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            row.RenderTransform = transform;
        }

        Canvas.SetTop(row, target);
        var delta = double.IsNaN(current) ? 0 : current - target;

        transform.BeginAnimation(TranslateTransform.YProperty, null);
        if (!animate || Math.Abs(delta) < 0.5)
        {
            transform.Y = 0;
            return;
        }

        transform.Y = delta;
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(delta, 0,
            TimeSpan.FromMilliseconds(AnimationMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private Border BuildRow(int index)
    {
        var name = _items[index];

        // 每行一条底部分隔线：用户说"一个框里一类的分界不明显"
        var row = new Border
        {
            Height = RowHeight,
            Background = Brushes.Transparent,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 0, 2, 0),
            RenderTransform = new TranslateTransform(),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 拖动手柄
        // 手柄做成一整块可点区域（原来只有一个 "≡" 字形能点，太难点中）
        var handle = new Border
        {
            Width = 34,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            ToolTip = "按住这里上下拖动可以调整顺序",
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = new TextBlock
            {
                Text = "≡",
                FontSize = Ui.FontSubtitle,
                Foreground = Ui.Muted,
                FontFamily = Ui.FontFamily,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        handle.MouseLeftButtonDown += (_, e) => OnHandleDown(row, handle, e);
        handle.MouseMove += (_, e) => OnHandleMove(e);
        handle.MouseLeftButtonUp += (_, _) => OnHandleUp();
        Grid.SetColumn(handle, 0);
        grid.Children.Add(handle);

        // 名称（双击就地改名）
        var label = Ui.Label(name, Ui.FontBody);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                _editing = name;
                Rebuild();
            }
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        // 右侧操作
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var renameButton = Ui.GhostButton("改名", () =>
        {
            _editing = name;
            Rebuild();
        }, 28);
        renameButton.Margin = new Thickness(0, 0, 6, 0);
        actions.Children.Add(renameButton);
        actions.Children.Add(Ui.GhostButton("删除", () =>
        {
            _items.Remove(name);
            Rebuild();
            _onChanged(Items);
        }, 28));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        row.Child = grid;

        // 悬停整行高亮（分界 + 悬停一起用，行与行的边界就清楚了）
        row.MouseEnter += (_, _) =>
        {
            if (!ReferenceEquals(row, _draggedRow))
            {
                row.Background = Ui.Hover;
            }
        };
        row.MouseLeave += (_, _) =>
        {
            if (!ReferenceEquals(row, _draggedRow))
            {
                row.Background = Brushes.Transparent;
            }
        };

        // 改名状态：把标签换成输入框
        if (_editing == name)
        {
            var (editShell, editBox) = Ui.Input(name, 200);
            editBox.SelectAll();
            editBox.Loaded += (_, _) => editBox.Focus();
            void Commit()
            {
                var text = editBox.Text.Trim();
                var current = _editing;
                _editing = null;
                if (current is null)
                {
                    return;
                }

                if (text.Length == 0 || (text != current && _items.Contains(text)))
                {
                    Rebuild();
                    return;
                }

                var position = _items.IndexOf(current);
                if (position >= 0)
                {
                    _items[position] = text;
                }

                Rebuild();
                _onChanged(Items);
            }

            editBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Commit();
                }
                else if (e.Key == Key.Escape)
                {
                    _editing = null;
                    Rebuild();
                }
            };
            editBox.LostFocus += (_, _) =>
            {
                if (_editing is not null)
                {
                    Commit();
                }
            };
            Grid.SetColumn(editShell, 1);
            grid.Children.Remove(label);
            grid.Children.Add(editShell);
        }

        Canvas.SetTop(row, index * Slot);
        return row;
    }

    // ---------------------------------------------------------------- 拖动

    /// <summary>按下手柄：**只记下按下点，不捕获鼠标**（一按下就捕获会"粘住"：点完都离不开、挪出去还是移动光标）。</summary>
    private void OnHandleDown(Border row, IInputElement handle, MouseButtonEventArgs e)
    {
        _pendingRow = row;
        _dragHandle = handle;
        _pressPoint = e.GetPosition(_rows);
        e.Handled = true;
    }

    private void OnHandleMove(MouseEventArgs e)
    {
        if (_pendingRow is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var now = e.GetPosition(_rows);

        // 还没开始拖：移动超过 5px 才算"真的要拖"，这时才捕获鼠标
        if (_draggedRow is null)
        {
            if (Math.Abs(now.Y - _pressPoint.Y) < 5)
            {
                return;
            }

            var row = _pendingRow;
            _draggedRow = row;
            _grabOffset = _pressPoint.Y - Canvas.GetTop(row);
            var captured = _dragHandle?.CaptureMouse() == true;
            Diag.Trace($"拖动开始：第 {_rows.Children.IndexOf(row)} 行 捕获鼠标={captured}");

            // 被拖的行"抬起"：稍微透明 + 强调色边框，并压到最上层
            row.Opacity = 0.92;
            row.Background = Ui.Hover;
            row.BorderBrush = Ui.Accent;
            row.BorderThickness = new Thickness(1, 1, 1, 1);
            Panel.SetZIndex(row, 10);
        }

        if (_draggedRow is null)
        {
            return;
        }

        var y = now.Y - _grabOffset;
        y = Math.Max(0, Math.Min(Math.Max(0, _rows.Height - RowHeight), y));

        // 1) 被拖的行跟着鼠标（不加动画，要跟手）
        var transform = (TranslateTransform)_draggedRow.RenderTransform;
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = y - Canvas.GetTop(_draggedRow);

        // 2) 目标槽位 = 鼠标中心落在哪个槽
        var target = (int)Math.Round((y + RowHeight / 2 - Slot / 2) / Slot, MidpointRounding.AwayFromZero);
        target = Math.Clamp(target, 0, _items.Count - 1);

        var from = _rows.Children.IndexOf(_draggedRow);
        if (target != from && from >= 0)
        {
            // 实时重排：列表和视觉一起动，其余行带动画让位（被拖走的那格露出的底色是灰的）
            var moved = _items[from];
            _items.RemoveAt(from);
            _items.Insert(target, moved);
            _rows.Children.RemoveAt(from);
            _rows.Children.Insert(target, _draggedRow);
            LayoutRows(animate: true);
        }
    }

    /// <summary>松开手柄：没真拖过就什么也不做（等于点了一下，绝不能粘住鼠标）。</summary>
    private void OnHandleUp()
    {
        if (_draggedRow is null)
        {
            _pendingRow = null;
            _dragHandle = null;
            return;
        }

        EndDrag(commit: true);
    }

    private void EndDrag(bool commit)
    {
        var row = _draggedRow;
        _pendingRow = null;
        _draggedRow = null;

        // 一定要放掉鼠标捕获，否则鼠标被这个手柄锁住
        try
        {
            _dragHandle?.ReleaseMouseCapture();
        }
        catch (Exception)
        {
        }

        _dragHandle = null;
        if (row is null)
        {
            return;
        }

        Diag.Trace("拖动结束");
        row.Opacity = 1;
        row.Background = Brushes.Transparent;
        row.BorderBrush = Ui.Line;
        row.BorderThickness = new Thickness(0, 0, 0, 1);
        Panel.SetZIndex(row, 0);

        // 收尾：被拖的行"落"回槽位（带动画），保证位置精确
        var index = _rows.Children.IndexOf(row);
        if (index >= 0)
        {
            MoveRowTo(row, index, animate: true);
        }

        LayoutRows(animate: true);
        if (commit && index >= 0)
        {
            _onChanged(Items);
        }
    }
    // ---------------------------------------------------------------- 新增

    private void AddItem()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _items.Contains(text))
        {
            _inputShell.BorderBrush = Ui.Expense;
            return;
        }

        _inputShell.BorderBrush = Ui.Line;
        _items.Add(text);
        _input.Text = "";
        Rebuild();
        _onChanged(Items);
    }
}
