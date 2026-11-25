using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LedgerBook.App;

/// <summary>
/// 任务栏右下角的托盘图标（0.2.6.5 新增，用户 2026-10-24 要求）。
///
/// 为什么要有：主窗口一旦被系统隐藏（例如弹「更新内容」时按了「显示桌面」），
/// 程序既不在任务栏、也没有托盘入口，用户只能开任务管理器杀进程（用户实际踩过）。
///
/// 行为（用户原话）：
///   · 双击 / 左键 → 调出主窗口
///   · 右键 → 「打开主界面 / 更新内容 / 退出」
///
/// 实现：用 Win32 的 Shell_NotifyIcon（不经 WinForms），图标取自本程序 exe 内嵌的那个，
/// 回调消息由一个**消息专用窗口**接收（HWND_MESSAGE，不占任务栏）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ---- 托盘回调用的消息号与鼠标消息（经典版 NOTIFYICON，不用 NIM_SETVERSION）----
    private const int WM_APP = 0x8000;
    private const int WM_TRAY = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;

    // ---- Shell_NotifyIcon ----
    private const int NIM_ADD = 0;
    private const int NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;

    // ---- 菜单 ----
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const int ID_OPEN = 1;
    private const int ID_WHATS_NEW = 2;
    private const int ID_EXIT = 3;

    private readonly Action _open;
    private readonly Action _whatsNew;
    private readonly Action _exit;
    private readonly int _id = Environment.ProcessId;   // 每个进程一个 id，多开也不会互相顶掉
    private HwndSource? _source;
    private IntPtr _icon;
    private bool _added;
    private bool _disposed;

    public TrayIcon(Action open, Action whatsNew, Action exit)
    {
        _open = open;
        _whatsNew = whatsNew;
        _exit = exit;
    }

    /// <summary>创建图标（必须在界面线程调用）。返回 false = 系统不给加图标（不影响程序其它功能）。</summary>
    public bool TryInstall()
    {
        try
        {
            // 用**普通隐藏窗口**接消息（不用 HWND_MESSAGE 消息专用窗口）：
            // 实测（2026-10-24）消息专用窗口 + Shell_NotifyIcon 会被系统拒绝，托盘图标加不上去。
            var parameters = new HwndSourceParameters("记账本托盘")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,                    // 没有 WS_VISIBLE → 不显示
                ExtendedWindowStyle = 0x00000080 | 0x08000000,   // WS_EX_TOOLWINDOW（不进 Alt+Tab）| WS_EX_NOACTIVATE
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            _icon = LoadAppIcon();

            var data = NewData(_source.Handle);
            _added = Shell_NotifyIconW(NIM_ADD, ref data);
            if (_added)
            {
                Core.Log.Info("托盘图标已创建（双击/左键调出窗口，右键有菜单）");
            }
            else
            {
                Core.Log.Info($"托盘图标创建失败（系统拒绝，程序照常可用）：错误码 {Marshal.GetLastWin32Error()}，结构体 {data.cbSize} 字节，图标 {_icon}");
            }

            return _added;
        }
        catch (Exception exc)
        {
            Core.Log.Error("托盘图标创建异常（不影响使用）", exc);
            return false;
        }
    }

    private NOTIFYICONDATA NewData(IntPtr hwnd) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = _id,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = TrimTip($"{Core.Paths.AppName} v{Core.Paths.Version}"),
    };

    private static string TrimTip(string text) => text.Length <= 120 ? text : text[..120];

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAY)
        {
            return IntPtr.Zero;
        }

        handled = true;
        var mouse = lParam.ToInt32() & 0xFFFF;
        switch (mouse)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                Core.Log.Info("托盘：左键/双击 → 调出主窗口");
                _open();
                break;
            case WM_RBUTTONUP:
                ShowMenu(hwnd);
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>右键菜单：打开主界面 / 更新内容 / 退出。</summary>
    private void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, MF_STRING, new IntPtr(ID_OPEN), "打开主界面");
            AppendMenuW(menu, MF_STRING, new IntPtr(ID_WHATS_NEW), "更新内容");
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, "");
            AppendMenuW(menu, MF_STRING, new IntPtr(ID_EXIT), "退出");

            GetCursorPos(out var point);
            // 先把自己放到前台：否则点菜单外面时菜单收不起来（Win32 托盘的经典坑）
            SetForegroundWindow(hwnd);
            var picked = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, point.X, point.Y, 0, hwnd, IntPtr.Zero);
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (picked)
            {
                case ID_OPEN:
                    Core.Log.Info("托盘菜单：打开主界面");
                    _open();
                    break;
                case ID_WHATS_NEW:
                    Core.Log.Info("托盘菜单：更新内容");
                    _whatsNew();
                    break;
                case ID_EXIT:
                    Core.Log.Info("托盘菜单：退出");
                    _exit();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>取本程序 exe 里内嵌的图标（取不到就退回系统默认图标）。</summary>
    private static IntPtr LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)
                && ExtractIconEx(exe, 0, out var large, out var small, 1) > 0)
            {
                // 托盘用 16×16 那个；没有小的就用大的
                var wanted = small != IntPtr.Zero ? small : large;
                if (wanted != IntPtr.Zero)
                {
                    return wanted;
                }
            }
        }
        catch (Exception)
        {
            // 取不到就往下走默认图标
        }

        return LoadIconW(IntPtr.Zero, new IntPtr(32512));   // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_added && _source is not null)
            {
                var data = NewData(_source.Handle);
                Shell_NotifyIconW(NIM_DELETE, ref data);
            }

            _source?.RemoveHook(WndProc);
            _source?.Dispose();
        }
        catch (Exception)
        {
            // 退出路径上出了问题也不要拦着程序关
        }
        finally
        {
            _source = null;
            _added = false;
        }
    }

    // ---------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flag, IntPtr id, string text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
