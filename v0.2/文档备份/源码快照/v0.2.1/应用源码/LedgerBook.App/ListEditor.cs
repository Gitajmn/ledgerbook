using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LedgerBook.App;

/// <summary>
/// 可拖动排序的列表编辑器（分类、支付方式都用它）：
/// 每行左边一个「≡」拖动手柄，右边是改名 / 删除，底部是输入框 + 新增。
/// 拖动实现用 WPF 自带的拖放：按住手柄移动即开始拖，放到目标行就把顺序改过来。
/// </summary>
public sealed class ListEditor
{
    private readonly string _title;
    private readonly string _hint;
    private readonly Action<List<string>> _onChanged;
    private readonly StackPanel _rows = new();
    private readonly TextBox _input;
    private readonly Border _inputShell;
    private List<string> _items;
    private string? _editing;
    private Point _dragStart;

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
        content.Children.Add(_rows);

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

    private void Rebuild()
    {
        _rows.Children.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            _rows.Children.Add(BuildRow(index));
        }
    }

    private UIElement BuildRow(int index)
    {
        var name = _items[index];
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1), Background = Ui.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 拖动手柄
        var handle = Ui.Label("≡", Ui.FontSubtitle, Ui.Muted);
        handle.Cursor = Cursors.SizeAll;
        handle.Padding = new Thickness(6, 2, 10, 2);
        handle.ToolTip = "拖动可以调整顺序";
        handle.MouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this.Root);
        };
        handle.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var now = e.GetPosition(this.Root);
            if (Math.Abs(now.Y - _dragStart.Y) < 4)
            {
                return;
            }

            DragDrop.DoDragDrop(handle, index, DragDropEffects.Move);
        };
        Grid.SetColumn(handle, 0);
        row.Children.Add(handle);

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
        row.Children.Add(label);

        // 右侧操作
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var renameButton = Ui.GhostButton("改名", () =>
        {
            _editing = name;
            Rebuild();
        }, 28);
        renameButton.Margin = new Thickness(0, 0, 6, 0);
        actions.Children.Add(renameButton);
        var deleteButton = Ui.GhostButton("删除", () =>
        {
            _items.Remove(name);
            Rebuild();
            _onChanged(Items);
        }, 28);
        actions.Children.Add(deleteButton);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);

        // 行本身接收拖放
        row.AllowDrop = true;
        row.DragOver += (_, e) =>
        {
            e.Effects = DragDropEffects.Move;
            row.Background = Ui.Hover;
            e.Handled = true;
        };
        row.DragLeave += (_, _) => row.Background = Ui.Transparent;
        row.Drop += (_, e) =>
        {
            row.Background = Ui.Transparent;
            if (e.Data.GetData(typeof(int)) is not int from || from < 0 || from >= _items.Count)
            {
                return;
            }

            var target = _items.IndexOf(name);
            if (from == target)
            {
                return;
            }

            var moved = _items[from];
            _items.RemoveAt(from);
            _items.Insert(target, moved);
            Rebuild();
            _onChanged(Items);
            e.Handled = true;
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
            row.Children.Remove(label);
            row.Children.Add(editShell);
        }

        return row;
    }

    private void AddItem()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0)
        {
            _inputShell.BorderBrush = Ui.Expense;
            return;
        }

        if (_items.Contains(text))
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
