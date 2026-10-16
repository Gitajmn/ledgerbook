using System.Globalization;
using Calendar = System.Windows.Controls.Calendar;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LedgerBook.Core;

namespace LedgerBook.App;

/// <summary>
/// 日期选择框：看起来是个输入框，点一下**弹出日历**（不再手打日期，避免格式出错）。
/// 选中后回填文本，回车也能确认；日历里也能直接用快捷范围。
/// </summary>
public sealed class DateField
{
    private readonly Border _shell;
    private readonly TextBox _box;
    private readonly Calendar _calendar;
    private readonly Popup _popup;
    private readonly List<Action> _listeners = new();
    private DateOnly _value;

    public Border Root => _shell;

    /// <summary>日历弹窗是否开着（自动化验证用）。</summary>
    public bool IsOpen => _popup.IsOpen;

    public DateOnly Value
    {
        get => _value;
        set
        {
            _value = value;
            _box.Text = value.ToString("yyyy-MM-dd");
            _calendar.SelectedDate = value.ToDateTime(TimeOnly.MinValue);
            _calendar.DisplayDate = value.ToDateTime(TimeOnly.MinValue);
        }
    }

    public DateField(DateOnly initial, double width = 130)
    {
        _value = initial;
        _calendar = new Calendar
        {
            FontFamily = Ui.FontFamily,
            FontSize = Ui.FontBody,
            Background = Ui.Card,
            Foreground = Ui.Text,
            BorderThickness = new Thickness(0),
            SelectionMode = CalendarSelectionMode.SingleDate,
            DisplayDate = initial.ToDateTime(TimeOnly.MinValue),
            SelectedDate = initial.ToDateTime(TimeOnly.MinValue),
        };

        var calendarBox = new Border
        {
            Background = Ui.Card,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Ui.RadiusCard),
            Padding = new Thickness(8),
            Child = _calendar,
        };

        _popup = new Popup
        {
            Child = calendarBox,
            Placement = PlacementMode.Bottom,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
        };

        var (shell, box) = Ui.Input(initial.ToString("yyyy-MM-dd"), width, "点这里选日期");
        _shell = shell;
        _box = box;
        _box.IsReadOnly = true;          // 只让点日历选，避免手打出错
        _box.Cursor = Cursors.Hand;
        _box.PreviewMouseLeftButtonDown += (_, _) =>
        {
            OpenCalendar();
        };

        _calendar.SelectedDatesChanged += (_, _) =>
        {
            if (_calendar.SelectedDate is { } picked)
            {
                _value = DateOnly.FromDateTime(picked);
                _box.Text = _value.ToString("yyyy-MM-dd");
                _popup.IsOpen = false;
                foreach (var listener in _listeners)
                {
                    listener();
                }
            }
        };
    }

    /// <summary>值变化时回调（快捷范围设置日期也会触发）。</summary>
    public void OnChanged(Action listener) => _listeners.Add(listener);

    public void Set(DateOnly value)
    {
        Value = value;
        foreach (var listener in _listeners)
        {
            listener();
        }
    }

    /// <summary>自动化截图用：直接把日历弹出来。</summary>
    public void OpenForAutomation() => OpenCalendar();

    private void OpenCalendar()
    {
        _calendar.SelectedDate = _value.ToDateTime(TimeOnly.MinValue);
        _calendar.DisplayDate = _value.ToDateTime(TimeOnly.MinValue);
        _popup.PlacementTarget = _shell;
        _popup.IsOpen = true;
    }

    /// <summary>一排「本月 / 上月 / 今年 / 全部」快捷按钮 + 日历框，组成一行。</summary>
    public static StackPanel Range(Dictionary<string, (DateOnly Start, DateOnly End)> presets, DateField start, DateField end)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(start.Root);
        row.Children.Add(Ui.Label("　~　", Ui.FontBody, Ui.Muted));
        row.Children.Add(end.Root);
        foreach (var (label, range) in presets)
        {
            var captured = range;
            var button = Ui.GhostButton(label, () =>
            {
                start.Set(captured.Start);
                end.Set(captured.End);
            }, 34);
            button.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(button);
        }

        return row;
    }

    public static string Describe(DateOnly start, DateOnly end)
        => $"{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}（共 {(end.DayNumber - start.DayNumber + 1)} 天）";
}
