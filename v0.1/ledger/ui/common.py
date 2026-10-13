"""界面公共控件：标签页基类、日期框、可排序表格、记录修改对话框。"""

from __future__ import annotations

import datetime as dt
import os
import subprocess
import sys
import tkinter as tk
from tkinter import font as tkfont
from tkinter import messagebox, simpledialog, ttk

from .. import config, model as m


def open_path(path) -> None:
    """用系统默认程序打开目录或文件。"""
    target = str(path)
    try:
        if os.name == "nt":
            os.startfile(target)  # type: ignore[attr-defined]
        elif sys.platform == "darwin":
            subprocess.Popen(["open", target])
        else:
            subprocess.Popen(["xdg-open", target])
    except OSError as exc:
        messagebox.showerror("打开失败", f"{target}\n{exc}")


def font_size() -> int:
    """当前界面字号：来自「设置 → 通用 → 界面字号」，读不到就用 10。"""
    try:
        from .. import config

        label = str(config.get("字号", "") or "")
    except Exception:  # 配置还没就绪（比如刚启动）时按默认
        return 10
    digits = "".join(ch for ch in label if ch.isdigit())
    if digits:
        return max(8, min(20, int(digits)))
    return 10


def ui_font(delta: int = 0, bold: bool = False, underline: bool = False):
    """统一的界面字体：字号一律由「设置 → 通用 → 界面字号」决定。

    只允许 ±1~2 号的层次差（标题 +1、链接下划线、表头加粗），不允许写死字号 ——
    否则高 DPI 下会出现「某个地方字特别大」这种不一致。
    """
    size = max(8, min(22, font_size() + delta))
    styles = []
    if bold:
        styles.append("bold")
    if underline:
        styles.append("underline")
    family = pick_font()[0]
    return (family, size, *styles) if styles else (family, size)


def pick_font(size: int | None = None) -> tuple[str, int]:
    """挑一个能正常显示中文的字体，字号按设置走。"""
    try:
        from tkinter import font as tkfont

        families = set(tkfont.families())
    except Exception:  # pragma: no cover - 极端环境下退回默认
        families = set()
    chosen = size or font_size()
    for name in ("Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑", "SimHei", "SimSun"):
        if name in families:
            return (name, chosen)
    return ("TkDefaultFont", chosen)


#: 当前 DPI 缩放比例（96 DPI = 1.0）。程序启动时由 enable_dpi_awareness() 设置。
UI_SCALE = 1.0

#: 界面配色（浅色卡片 + 蓝色主色）
THEME = {
    "bg": "#f5f6f8",      # 页面底色
    "card": "#ffffff",    # 卡片 / 列表底
    "line": "#e2e5ea",    # 描边、分隔线
    "text": "#1f2329",
    "muted": "#8a9099",
    "accent": "#4d6bfe",  # 主色
    "accent_dark": "#3a55e0",
}


def enable_dpi_awareness() -> float:
    """声明本进程「DPI 感知」，高 DPI 屏幕上界面才不会被 Windows 位图拉伸（糊）。

    必须在创建 Tk 根窗口之前调用。返回缩放比例（96 DPI = 1.0），
    例如 175% 缩放返回 1.75，之后用 px() 换算尺寸、用 tk scaling 放大字体。
    """
    global UI_SCALE
    if os.name != "nt":
        return UI_SCALE
    try:
        import ctypes

        try:  # Win10 1703+：按显示器感知（第二版，最清晰）
            ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
        except Exception:
            try:  # Win8.1+：按显示器感知
                ctypes.windll.shcore.SetProcessDpiAwareness(2)
            except Exception:
                ctypes.windll.user32.SetProcessDPIAware()  # 老系统兜底
        handle = ctypes.windll.user32.GetDC(0)
        dpi = int(ctypes.windll.gdi32.GetDeviceCaps(handle, 88))  # LOGPIXELSX
        ctypes.windll.user32.ReleaseDC(0, handle)
        if dpi > 0:
            UI_SCALE = max(1.0, dpi / 96.0)
    except Exception:  # 环境不许改就按 100% 走，界面照常能用
        UI_SCALE = 1.0
    return UI_SCALE


def px(value: float) -> int:
    """把设计尺寸按当前 DPI 缩放（1280×720 时代的 30px 按钮，在 175% 下应该是 52px）。"""
    return max(1, int(round(value * UI_SCALE)))


#: 圆角尺度（只有这四档，界面里任何地方都不许再写 7、9、11 这种随手数值）
RADIUS_SCALE = {
    "small": 6,     # 小控件：日历格子、标签、小药丸
    "control": 8,   # 输入框、按钮、下拉框（设计规范里的「标准档」）
    "card": 12,     # 卡片、列表、面板（「表现力档」）
    "window": 12,   # 窗口圆角，和卡片同档才不会显得两张皮
}


def corner_radius(name: str = "control") -> int:
    """统一圆角入口：保证全应用弧度一致（照设计规范的分档来）。

    参考新加坡政府设计系统的边框/圆角规范：数据表格 0px（保持结构感）、
    小控件 4px、**输入框/按钮/卡片默认 8px**、弹窗与大卡片 12~24px、头像/悬浮按钮 full
    （https://www.designsystem.tech.gov.sg/foundations/border ）；
    内层元素的圆角按「外圆角 − 内边距」同心推导（Apple 的 ConcentricRectangle 同名规则，
    https://developer.apple.com/documentation/SwiftUI/ConcentricRectangle ），
    所以卡片(12)里放的小控件用 6，视觉上是同一个圆心。
    """
    return px(RADIUS_SCALE.get(name, RADIUS_SCALE["control"]))


def apply_tk_scaling(root) -> None:
    """按 DPI 放大字体（Tk 的字体单位是点，scaling = 每点多少像素）。"""
    try:
        root.tk.call("tk", "scaling", UI_SCALE * 96.0 / 72.0)
    except tk.TclError:  # pragma: no cover
        pass


def unscale(value: float) -> int:
    """px() 的逆运算：把实际像素换算回「设计尺寸」，用来保存窗口大小。"""
    return max(1, int(round(value / UI_SCALE)))


def work_area() -> tuple[int, int]:
    """屏幕可用区域（不含任务栏）的宽高，单位是实际像素。"""
    if os.name == "nt":
        try:
            import ctypes

            class Rect(ctypes.Structure):
                _fields_ = [
                    ("left", ctypes.c_long),
                    ("top", ctypes.c_long),
                    ("right", ctypes.c_long),
                    ("bottom", ctypes.c_long),
                ]

            rect = Rect()
            if ctypes.windll.user32.SystemParametersInfoW(0x0030, 0, ctypes.byref(rect), 0):  # SPI_GETWORKAREA
                return rect.right - rect.left, rect.bottom - rect.top
        except Exception:
            pass
    return 0, 0


def shade_color(color: str, factor: float) -> str:
    """颜色变暗（factor < 1）或变亮（factor > 1），用来做悬停 / 立体效果。"""
    text = str(color or "").lstrip("#")
    if len(text) != 6:
        return color
    try:
        red, green, blue = (int(text[index : index + 2], 16) for index in (0, 2, 4))
    except ValueError:
        return color
    if factor < 1:
        red, green, blue = (int(value * factor) for value in (red, green, blue))
    else:
        extra = factor - 1
        red, green, blue = (min(255, int(value + (255 - value) * extra)) for value in (red, green, blue))
    return f"#{red:02x}{green:02x}{blue:02x}"


def parent_background(widget) -> str:
    """取 ttk 容器的底色，圆角外侧才不会露白边。"""
    try:
        return ttk.Style(widget).lookup("TFrame", "background") or "#f0f0f0"
    except tk.TclError:  # pragma: no cover
        return "#f0f0f0"


def draw_rounded_rect(canvas, x0: float, y0: float, x1: float, y1: float, radius: float, steps: int = 6, **options):
    """在 Canvas 上画精确的圆角矩形：四个角按 1/4 圆弧取点，直边照旧。

    不用 smooth=True 的样条 —— 那种做法会「穿过」角点，圆角被拉平，看起来还是方的。
    """
    import math

    corner = max(0.0, min(radius, (x1 - x0) / 2, (y1 - y0) / 2))
    centers = (
        (x0 + corner, y0 + corner, 180.0),  # 左上
        (x1 - corner, y0 + corner, 270.0),  # 右上
        (x1 - corner, y1 - corner, 0.0),    # 右下
        (x0 + corner, y1 - corner, 90.0),   # 左下
    )
    points: list[float] = []
    for center_x, center_y, start in centers:
        for step in range(steps + 1):
            angle = math.radians(start + 90.0 * step / steps)
            points.append(center_x + corner * math.cos(angle))
            points.append(center_y + corner * math.sin(angle))
    return canvas.create_polygon(points, **options)


def apply_rounded_window(window, preference: int = 2) -> bool:
    """让 Windows 把窗口画成圆角（Win11 生效，Win10/其它系统静默无效）。

    DWMWA_WINDOW_CORNER_PREFERENCE = 33，值 2 = DWMWCP_ROUND。
    """
    if os.name != "nt":
        return False
    try:
        import ctypes

        window.update_idletasks()
        handle = ctypes.windll.user32.GetParent(window.winfo_id()) or window.winfo_id()
        value = ctypes.c_int(preference)
        result = ctypes.windll.dwmapi.DwmSetWindowAttribute(
            ctypes.c_void_p(handle), ctypes.c_uint(33), ctypes.byref(value), ctypes.sizeof(value)
        )
        return int(result) == 0
    except Exception:  # 旧系统或没有 dwmapi：什么都不做
        return False


class RoundedCard(tk.Canvas):
    """圆角卡片：自绘圆角底 + 圆角描边，内容放进 `body`。

    列表、面板都可以放在里面，看起来就是一张圆角卡片（原生控件画不出圆角）。
    """

    def __init__(self, master, radius: int | None = None, padding: int = 6, fill: str | None = None,
                 outline: str | None = None, **kwargs) -> None:
        super().__init__(master, highlightthickness=0, bd=0, background=parent_background(master), **kwargs)
        self.radius = radius if radius is not None else corner_radius("card")
        self.padding = px(padding)
        self.fill = fill or THEME["card"]
        self.outline = outline or THEME["line"]
        self.body = ttk.Frame(self, style="Card.TFrame")
        self._window = self.create_window(self.padding, self.padding, anchor="nw", window=self.body)
        self.bind("<Configure>", self._on_configure)

    def _on_configure(self, event) -> None:
        self.delete("card")
        draw_rounded_rect(
            self, 0.5, 0.5, max(2, event.width) - 0.5, max(2, event.height) - 0.5, self.radius,
            fill=self.fill, outline=self.outline, tags="card",
        )
        self.tag_lower("card")
        self.itemconfigure(
            self._window,
            width=max(1, event.width - self.padding * 2),
            height=max(1, event.height - self.padding * 2),
        )


class RoundedScrollbar(tk.Canvas):
    """自绘圆角滚动条：浅色轨道 + 胶囊形滑块，没有老式方块箭头。

    用法和 ttk.Scrollbar 一样：
        bar = RoundedScrollbar(parent, orient="vertical", command=tree.yview)
        tree.configure(yscrollcommand=bar.set)
    支持拖动滑块、点轨道翻页、滚轮。内容没超出一屏时整条都不画，界面更干净。
    """

    def __init__(self, master, orient: str = "vertical", command=None, thickness: int | None = None, **kwargs) -> None:
        self.orient = orient
        size = px(thickness or 11)
        if orient == "vertical":
            super().__init__(master, width=size, highlightthickness=0, bd=0,
                             background=parent_background(master), **kwargs)
        else:
            super().__init__(master, height=size, highlightthickness=0, bd=0,
                             background=parent_background(master), **kwargs)
        self.command = command
        self.thickness = size
        self._first = 0.0
        self._last = 1.0
        self._dragging = False
        self._drag_origin = 0.0
        self._drag_first = 0.0
        self._hover = False
        self.bind("<Configure>", lambda _event: self._draw())
        self.bind("<ButtonPress-1>", self._on_press)
        self.bind("<B1-Motion>", self._on_drag)
        self.bind("<ButtonRelease-1>", self._on_release)
        self.bind("<MouseWheel>", self._on_wheel)
        self.bind("<Enter>", self._on_enter_bar)
        self.bind("<Leave>", self._on_leave_bar)
        self._draw()

    # ---------- 与 ttk.Scrollbar 一致的接口 ----------

    def set(self, first, last) -> None:
        """由被控制的控件回调（yscrollcommand / xscrollcommand）。"""
        try:
            self._first, self._last = float(first), float(last)
        except (TypeError, ValueError):  # pragma: no cover
            return
        self._draw()

    def get(self) -> tuple[float, float]:
        return self._first, self._last

    # ---------- 绘制 ----------

    def _length(self) -> float:
        return float(self.winfo_height() if self.orient == "vertical" else self.winfo_width())

    def _thumb_range(self) -> tuple[float, float]:
        length = max(1.0, self._length())
        span = max(0.0, min(1.0, self._last - self._first))
        pad = px(2)
        usable = max(1.0, length - pad * 2)
        size = max(px(24), usable * span)
        start = pad + usable * max(0.0, min(1.0, self._first))
        if start + size > length - pad:
            start = max(pad, length - pad - size)
        return start, size

    def _draw(self) -> None:
        self.delete("all")
        length = self._length()
        if length <= 1:
            return
        span = max(0.0, min(1.0, self._last - self._first))
        if span >= 0.999:  # 内容没超出一屏：不画滚动条
            return
        thickness = self.thickness
        start, size = self._thumb_range()
        # 只画胶囊滑块，不画轨道：轨道会和页面底色有色差，看起来像一条脏边
        bar = max(px(4), thickness * 0.55)
        offset = (thickness - bar) / 2
        color = "#9aa1ab" if self._hover else "#c2c7d0"
        pill = bar / 2
        if self.orient == "vertical":
            draw_rounded_rect(self, offset, start, offset + bar, start + size, pill,
                              fill=color, outline="", tags="thumb")
        else:
            draw_rounded_rect(self, start, offset, start + size, offset + bar, pill,
                              fill=color, outline="", tags="thumb")

    # ---------- 交互 ----------

    def _position(self, event) -> float:
        return float(event.y if self.orient == "vertical" else event.x)

    def _on_enter_bar(self, _event=None) -> None:
        self._hover = True
        self._draw()

    def _on_leave_bar(self, _event=None) -> None:
        self._hover = False
        self._draw()

    def _on_press(self, event) -> None:
        position = self._position(event)
        start, size = self._thumb_range()
        if start <= position <= start + size:
            self._dragging = True
            self._drag_origin = position
            self._drag_first = self._first
            return
        if self.command is not None:  # 点轨道：翻一页
            self.command("scroll", -1 if position < start else 1, "pages")

    def _on_drag(self, event) -> None:
        if not self._dragging or self.command is None:
            return
        length = max(1.0, self._length())
        pad = px(2)
        usable = max(1.0, length - pad * 2)
        moved = (self._position(event) - self._drag_origin) / usable
        target = max(0.0, min(1.0 - (self._last - self._first), self._drag_first + moved))
        self.command("moveto", target)

    def _on_release(self, _event) -> None:
        self._dragging = False

    def _on_wheel(self, event) -> None:
        if self.command is not None:
            self.command("scroll", int(-event.delta / 120), "units")


class RoundedEntry(ttk.Frame):
    """圆角输入框：外面自绘圆角底，里面放一个去掉边框的原生输入框。

    原生输入框的方角被同色底盖住，看起来就是圆角，同时**输入法、光标、选中、
    快捷键全都还是系统原生的**（这点很重要，纯自绘输入框会把中文输入法搞坏）。
    支持可选的左侧图标（比如搜索框的 🔍）。
    """

    def __init__(
        self,
        master,
        textvariable=None,
        width: int = 14,
        height: int = 30,
        radius: int | None = None,
        icon: str = "",
        show: str | None = None,
        font=None,
        **kwargs,
    ) -> None:
        # 里面的输入框是用 place 摆的，place 不会把尺寸传给父容器，
        # 所以这里必须自己报一个「我有多宽」，否则自动回车布局会算错。
        self._font = font or pick_font()
        metrics = tkfont.Font(font=self._font)
        width_px = metrics.measure("0") * int(width) + px(10) * 2 + (px(20) if icon else 0)
        super().__init__(master, height=px(height), width=width_px)
        self.radius = radius if radius is not None else corner_radius("control")
        self.icon = icon
        self.focus_border = THEME["accent"]
        self._focused = False

        self.canvas = tk.Canvas(self, highlightthickness=0, bd=0, background=parent_background(master))
        self.canvas.place(x=0, y=0, relwidth=1, relheight=1)
        self.var = textvariable if textvariable is not None else tk.StringVar()
        left = px(10) + (px(20) if icon else 0)
        self.entry = tk.Entry(
            self,
            textvariable=self.var,
            width=width,
            relief="flat",
            bd=0,
            highlightthickness=0,
            background=THEME["card"],
            foreground=THEME["text"],
            insertbackground=THEME["text"],
            font=self._font,
            show=show,
            **kwargs,
        )
        self.entry.place(x=left, y=px(2), relwidth=1, relheight=1, width=-(left + px(10)), height=-px(4))
        self.entry.bind("<FocusIn>", self._on_focus)
        self.entry.bind("<FocusOut>", self._on_focus)
        self.bind("<Configure>", self._redraw)

    @property
    def widget(self) -> tk.Entry:
        """真正的输入控件（需要对 Entry 做特殊操作时用）。"""
        return self.entry

    def __getattr__(self, name: str):
        """没定义的方法/属性一律转给里面的原生输入框。

        这样 select_range、icursor、index 这些调用处不用改，
        圆角只是外观，输入行为还是系统原生的。
        """
        entry = self.__dict__.get("entry")
        if entry is not None:
            return getattr(entry, name)
        raise AttributeError(name)

    def _on_focus(self, _event=None) -> None:
        self._focused = self.focus_get() is self.entry
        self._redraw()

    def _redraw(self, _event=None) -> None:
        width = self.winfo_width() if self.winfo_width() > 1 else px(120)
        height = self.winfo_height() if self.winfo_height() > 1 else px(30)
        self.canvas.delete("all")
        border = self.focus_border if self._focused else THEME["line"]
        draw_rounded_rect(
            self.canvas, 0.5, 0.5, width - 0.5, height - 0.5, self.radius,
            fill=THEME["card"], outline=border,
        )
        if self.icon:
            self.canvas.create_text(px(11), height / 2, text=self.icon, anchor="w", fill=THEME["muted"], font=self._font)

    # 让调用处能像用 Entry 一样用它
    def get(self) -> str:
        return self.entry.get()

    def delete(self, first, last=None) -> None:
        self.entry.delete(first, last)

    def insert(self, index, text) -> None:
        self.entry.insert(index, text)

    def focus_set(self) -> None:  # noqa: D102 - 直接转发
        self.entry.focus_set()

    def bind_entry(self, sequence, callback) -> None:
        self.entry.bind(sequence, callback)


class RoundedCombo(ttk.Frame):
    """圆角下拉框：外圈自绘圆角，里面放一个无边框的原生 Combobox。

    下拉列表本身是系统弹窗（Tk 画不了圆角弹窗），但输入框部分是圆角的，
    和整体风格一致。
    """

    def __init__(
        self,
        master,
        textvariable=None,
        values=(),
        width: int = 14,
        height: int = 30,
        radius: int | None = None,
        state: str = "readonly",
        font=None,
        **kwargs,
    ) -> None:
        super().__init__(master, height=px(height))
        self.radius = radius if radius is not None else corner_radius("control")
        self._font = font or pick_font()
        # 同上：里面用 place 摆控件，父容器不会自动有宽度，这里自己报一个
        metrics = tkfont.Font(font=self._font)
        self.configure(width=metrics.measure("0") * int(width) + px(9) * 2 + px(24))
        self._focused = False

        self.canvas = tk.Canvas(self, highlightthickness=0, bd=0, background=parent_background(master))
        self.canvas.place(x=0, y=0, relwidth=1, relheight=1)
        self.var = textvariable if textvariable is not None else tk.StringVar()
        self.combo = ttk.Combobox(
            self,
            textvariable=self.var,
            values=list(values),
            width=width,
            state=state,
            font=self._font,
            style="Embed.TCombobox",
            **kwargs,
        )
        self.combo.place(x=px(9), y=px(2), relwidth=1, relheight=1, width=-(px(9) + px(24)), height=-px(4))
        self.combo.bind("<FocusIn>", self._on_focus)
        self.combo.bind("<FocusOut>", self._on_focus)
        self.bind("<Configure>", self._redraw)

    @property
    def widget(self) -> ttk.Combobox:
        return self.combo

    def __getattr__(self, name: str):
        """没定义的方法/属性转给里面的原生 Combobox（外观是圆的，行为是原生的）。"""
        combo = self.__dict__.get("combo")
        if combo is not None:
            return getattr(combo, name)
        raise AttributeError(name)

    def configure_values(self, values) -> None:
        self.combo.configure(values=list(values))

    def _on_focus(self, _event=None) -> None:
        self._focused = self.focus_get() is self.combo
        self._redraw()

    def _redraw(self, _event=None) -> None:
        width = self.winfo_width() if self.winfo_width() > 1 else px(120)
        height = self.winfo_height() if self.winfo_height() > 1 else px(30)
        self.canvas.delete("all")
        border = THEME["accent"] if self._focused else THEME["line"]
        draw_rounded_rect(
            self.canvas, 0.5, 0.5, width - 0.5, height - 0.5, self.radius,
            fill=THEME["card"], outline=border,
        )
        # 右侧画一个小三角，代替系统下拉按钮（原生那个是方的）
        arrow_x, arrow_y = width - px(14), height / 2
        self.canvas.create_polygon(
            arrow_x - px(4), arrow_y - px(2), arrow_x + px(4), arrow_y - px(2), arrow_x, arrow_y + px(3),
            fill=THEME["muted"], outline="",
        )

    def get(self) -> str:
        return self.combo.get()

    def cget(self, key):
        """让 cget("values") 这类用法继续可用（转发给里面的原生 Combobox）。"""
        if str(key) == "values":
            return self.combo.cget("values")
        return super().cget(key)

    def set(self, value: str) -> None:
        self.combo.set(value)

    def current(self, index: int | None = None):
        return self.combo.current(index)

    def bind_combo(self, sequence, callback) -> None:
        self.combo.bind(sequence, callback)

    def event_generate(self, sequence, **kwargs):
        """事件转发给里面真正的下拉框。

        容器自己也继承了 event_generate，所以必须显式转发，
        否则对「这个框」触发 <<ComboboxSelected>> 时里面那个收不到。
        """
        return self.combo.event_generate(sequence, **kwargs)


class RoundedButton(tk.Canvas):
    """圆角按钮。

    ttk 的主题（vista/clam）画不出圆角，所以这里用 Canvas 自绘：圆角矩形 + 居中文字，
    带悬停/按下/禁用三种状态。用法尽量与原来的 ttk.Button 一致：
    `cget("text")`、`configure(text=...)`、`state(["disabled"])`、`invoke()` 都可用。
    """

    STYLES = {
        "normal": {"bg": "#f5f6f7", "fg": "#202020", "border": "#c9ccd1"},
        "primary": {"bg": THEME["accent"], "fg": "#ffffff", "border": THEME["accent_dark"]},
        "danger": {"bg": "#d9534f", "fg": "#ffffff", "border": "#c9433f"},
        "chip": {"bg": "#ffffff", "fg": "#303030", "border": "#e2e2e2"},
    }

    def __init__(
        self,
        master,
        text: str = "",
        command=None,
        width: int | None = None,
        height: int = 30,
        radius: int | None = None,
        style: str = "normal",
        font=None,
        padx: int = 14,
        bg: str | None = None,
        fg: str | None = None,
        border: str | None = None,
        **kwargs,
    ) -> None:
        self._font = font or pick_font()
        self._text = str(text)
        self.command = command
        self.radius = radius if radius is not None else corner_radius("control")
        self.style_name = style
        self.padx = px(padx)
        self._bg_override = bg
        self._fg_override = fg
        self._border_override = border
        self._enabled = True
        self._hover = False
        self._pressed = False
        self._after_id = None
        self._last_size = (0, 0)

        metrics = tkfont.Font(font=self._font)
        wanted = metrics.measure(self._text) + self.padx * 2
        if width:
            wanted = max(wanted, metrics.measure("0") * int(width))
        super().__init__(
            master,
            width=wanted,
            height=px(height),
            highlightthickness=0,
            bd=0,
            background=parent_background(master),
            cursor="hand2",
            **kwargs,
        )
        self.bind("<Enter>", self._on_enter)
        self.bind("<Leave>", self._on_leave)
        self.bind("<ButtonPress-1>", self._on_press)
        self.bind("<ButtonRelease-1>", self._on_release)
        self.bind("<Configure>", self._on_configure)
        self._draw()

    # ---------- 外观 ----------

    def palette(self) -> dict:
        spec = self.STYLES.get(self.style_name, self.STYLES["normal"])
        base = self._bg_override or spec["bg"] or parent_background(self)
        edge = self._border_override if self._border_override is not None else spec["border"]
        if not self._enabled:
            return {"bg": "#f1f1f1", "fg": "#a6a6a6", "border": "#dcdcdc"}
        if self._pressed:
            fill = shade_color(base, 0.9)
        elif self._hover:
            fill = shade_color(base, 1.06)
        else:
            fill = base
        return {
            "bg": fill,
            "fg": self._fg_override or spec["fg"],
            "border": shade_color(edge, 0.9) if (edge and (self._hover or self._pressed)) else edge,
        }

    def _draw(self) -> None:
        self.delete("all")
        width = self.winfo_width() if self.winfo_width() > 1 else int(self["width"])
        height = self.winfo_height() if self.winfo_height() > 1 else int(self["height"])
        width, height = max(2, width), max(2, height)
        colors = self.palette()
        radius = min(self.radius, height / 2)
        draw_rounded_rect(
            self,
            0.5,
            0.5,
            width - 0.5,
            height - 0.5,
            radius,
            fill=colors["bg"],
            outline=colors["border"] or "",
        )
        self.create_text(width / 2, height / 2, text=self._text, fill=colors["fg"], font=self._font)

    def shape_items(self) -> list:
        return [item for item in self.find_all() if self.type(item) == "polygon"]

    # ---------- 事件 ----------

    def _on_enter(self, _event=None) -> None:
        self._hover = True
        self._draw()

    def _on_leave(self, _event=None) -> None:
        self._hover = False
        self._pressed = False
        self._draw()

    def _on_press(self, _event=None) -> None:
        if not self._enabled:
            return
        self._pressed = True
        self._draw()

    def _on_release(self, _event=None) -> None:
        was_pressed = self._pressed
        self._pressed = False
        self._draw()
        if was_pressed and self._enabled:
            self.invoke()

    def _on_configure(self, event) -> None:
        # 被容器拉伸时重画一次（差得少就不重画，拖窗口时不添负担）
        size = (event.width, event.height)
        if abs(size[0] - self._last_size[0]) < 6 and abs(size[1] - self._last_size[1]) < 6:
            return
        self._last_size = size
        if self._after_id is not None:
            try:
                self.after_cancel(self._after_id)
            except (tk.TclError, ValueError):
                pass
        self._after_id = self.after(60, self._redraw_now)

    def _redraw_now(self) -> None:
        self._after_id = None
        self._draw()

    # ---------- 兼容 ttk.Button 的常用接口 ----------

    def invoke(self) -> None:
        if self._enabled and self.command is not None:
            self.command()

    def state(self, spec=None):
        if spec is None:
            return () if self._enabled else ("disabled",)
        for item in spec:
            name = str(item)
            if name == "disabled":
                self._enabled = False
            elif name == "!disabled":
                self._enabled = True
        super().configure(cursor="hand2" if self._enabled else "arrow")
        self._draw()
        return self.state()

    def cget(self, key):
        if str(key) == "text":
            return self._text
        return super().cget(key)

    def configure(self, cnf=None, **kwargs):
        if "text" in kwargs:
            self._text = str(kwargs.pop("text"))
        if "command" in kwargs:
            self.command = kwargs.pop("command")
        state = kwargs.pop("state", None)
        result = super().configure(cnf, **kwargs)
        if state is not None:
            self.state([state])
        self._draw()
        return result

    config = configure


class Tab(ttk.Frame):
    """标签页基类：子类实现 build()，必要时覆盖 on_show()。"""

    title = "标签页"

    def __init__(self, master, app) -> None:
        super().__init__(master, padding=8)
        self.app = app
        self.build()

    def build(self) -> None:  # pragma: no cover - 抽象方法
        raise NotImplementedError

    def on_show(self) -> None:
        """切到本页时调用。"""

    def on_data_changed(self) -> None:
        """别处改了数据时调用；默认等同于重新显示。"""
        self.on_show()

    def apply_settings(self) -> None:
        """设置页保存后调用：让改动立即生效，不用重启；默认等同于重新显示。"""
        self.on_show()

    # 常用小工具
    def notify(self, message: str, title: str = "记账本") -> None:
        messagebox.showinfo(title, message, parent=self)

    def warn(self, message: str, title: str = "操作未完成") -> None:
        messagebox.showwarning(title, message, parent=self)

    def error(self, message: str, title: str = "出错了") -> None:
        messagebox.showerror(title, message, parent=self)

    def confirm(self, message: str, title: str = "请确认") -> bool:
        return bool(messagebox.askyesno(title, message, parent=self))


class ScrollFrame(ttk.Frame):
    """带竖向滚动条的容器：内容放进 .body。

    设置项会随版本增加，所以设置页用可滚动容器，避免窗口越撑越高。
    """

    def __init__(self, master) -> None:
        super().__init__(master)
        self.canvas = tk.Canvas(self, highlightthickness=0, borderwidth=0)
        scrollbar = RoundedScrollbar(self, orient="vertical", command=self.canvas.yview)
        self.body = ttk.Frame(self.canvas)
        self._window = self.canvas.create_window((0, 0), window=self.body, anchor="nw")
        self.canvas.configure(yscrollcommand=scrollbar.set)
        self.canvas.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")
        self.body.bind("<Configure>", self._on_body)
        self.canvas.bind("<Configure>", self._on_canvas)
        self.bind("<Enter>", self._bind_wheel)
        self.bind("<Leave>", self._unbind_wheel)

    def _on_body(self, _event=None) -> None:
        self.canvas.configure(scrollregion=self.canvas.bbox("all"))

    def _on_canvas(self, event) -> None:
        self.canvas.itemconfigure(self._window, width=event.width)

    def bind_wheel(self) -> None:
        """把滚轮事件接管过来（鼠标进入本区域时调用）。"""
        self.canvas.bind_all("<MouseWheel>", self.on_wheel)

    def _bind_wheel(self, _event=None) -> None:
        self.bind_wheel()

    def _unbind_wheel(self, _event=None) -> None:
        self.canvas.unbind_all("<MouseWheel>")

    def on_wheel(self, event) -> None:
        self.canvas.yview_scroll(int(-event.delta / 120), "units")


class WrapRow(ttk.Frame):
    """自动回车布局：一行排不下就把后面的控件换到下一行，窄窗口下也不会被裁掉。

    用法（控件必须建在本容器里，再交给 add，别自己 pack/grid）：
        row = WrapRow(parent)
        row.pack(fill="x")
        row.add(ttk.Label(row, text="类型"))
        row.add(ttk.Combobox(row, ...))
        row.add(ttk.Button(row, text="查询"), gap=16)   # gap 可以单独调

    实现说明：用 place() 按算好的行/列坐标摆放，而不是 grid —— grid 里同一列会被
    上下两行的内容互相撑宽，结果是「明明换行了最后那个控件还是被裁掉」。

    拖动窗口的性能：宽度变化小于 MIN_DELTA 一律不重排，并且用 after() 做防抖，
    这样拖拽过程中几乎不做计算，松手后才排一次。
    """

    #: 宽度变化不到这个像素数就不重排（拖窗口时省掉大量无谓计算）
    MIN_DELTA = px(24)
    #: 防抖延迟（毫秒）：拖拽期间只记录宽度，停一下才真正排版
    DEBOUNCE_MS = 90

    def __init__(self, master, gap: int = 8, row_gap: int = 6) -> None:
        super().__init__(master)
        self.gap = gap
        self.row_gap = row_gap
        self.items: list[tk.Widget] = []
        self._row_of: dict[tk.Widget, int] = {}
        self._width = 0
        self._pending_width: int | None = None
        self._after_id = None
        self.pack_propagate(False)  # 高度由 relayout 按行数算出来
        self.bind("<Configure>", self._on_configure)

    def add(self, widget: tk.Widget, gap: int | None = None) -> tk.Widget:
        self.items.append(widget)
        if gap is not None:
            widget._wrap_gap = gap  # type: ignore[attr-defined]
        return widget

    def gap_of(self, widget: tk.Widget) -> int:
        return int(getattr(widget, "_wrap_gap", self.gap))

    def row_of(self, widget: tk.Widget) -> int:
        return self._row_of.get(widget, -1)

    def rows_used(self) -> int:
        return len(set(self._row_of.values())) if self._row_of else 0

    def _on_configure(self, event) -> None:
        if event.width <= 1 or abs(event.width - self._width) < self.MIN_DELTA:
            return  # 差得太少：不值得重排（拖窗口时绝大多数事件都走到这里）
        self._pending_width = event.width
        if self._after_id is not None:
            try:
                self.after_cancel(self._after_id)
            except (tk.TclError, ValueError):
                pass
        self._after_id = self.after(self.DEBOUNCE_MS, self._apply_pending_width)

    def _apply_pending_width(self) -> None:
        self._after_id = None
        width, self._pending_width = self._pending_width, None
        if width:
            self.layout_for_width(width)

    def flush_pending(self) -> None:
        """立刻处理挂起的重排（切换页面、保存设置时用，保证不会停在旧布局）。"""
        if self._after_id is not None:
            try:
                self.after_cancel(self._after_id)
            except (tk.TclError, ValueError):
                pass
            self._after_id = None
        width, self._pending_width = self._pending_width, None
        if width:
            self.layout_for_width(width)

    def layout_for_width(self, width: int) -> None:
        """按给定宽度重新排版（真实 <Configure> 与自测都走这里）。"""
        self._width = max(1, int(width))
        self.relayout()

    def relayout(self) -> None:
        available = self._width if self._width > 1 else self.winfo_width()
        if available <= 1:  # 还没被映射：先按一行摆，等 <Configure> 再校正
            available = 100000

        rows: list[list[tk.Widget]] = [[]]
        used = 0
        for widget in self.items:
            gap = self.gap_of(widget)
            need = widget.winfo_reqwidth()  # 这个调用本身会算好请求宽度，不用 update_idletasks
            if rows[-1] and used + gap + need > available:
                rows.append([])
                used = 0
            rows[-1].append(widget)
            used += need + gap

        self._row_of = {}
        y = 0
        for row_index, row_items in enumerate(rows):
            if not row_items:
                continue
            row_height = max(widget.winfo_reqheight() for widget in row_items)
            x = 0
            for widget in row_items:
                widget.place(x=x, y=y)
                self._row_of[widget] = row_index
                x += widget.winfo_reqwidth() + self.gap_of(widget)
            y += row_height + self.row_gap
        if rows[-1]:
            y -= self.row_gap
        self.configure(height=max(1, y))


class Tooltip:
    """鼠标停留一会儿后弹出的小提示（深色气泡，跟着控件走）。

    用法：Tooltip(某个控件, "拖动可以调整顺序")
    """

    def __init__(self, widget, text: str, delay: int = 450) -> None:
        self.widget = widget
        self.text = text
        self.delay = delay
        self._after_id = None
        self._tip = None
        widget.bind("<Enter>", self._schedule, add="+")
        widget.bind("<Leave>", self.hide, add="+")
        widget.bind("<ButtonPress>", self.hide, add="+")
        widget.bind("<B1-Motion>", self.hide, add="+")

    def _schedule(self, _event=None) -> None:
        self.hide()
        self._after_id = self.widget.after(self.delay, self.show)

    def show(self) -> None:
        if self._tip is not None or not self.text:
            return
        try:
            tip = tk.Toplevel(self.widget)
        except tk.TclError:
            return
        tip.wm_overrideredirect(True)
        try:
            tip.wm_attributes("-topmost", True)
        except tk.TclError:
            pass
        font = pick_font()
        tk.Label(
            tip,
            text=self.text,
            background="#2f2f2f",
            foreground="#ffffff",
            padx=10,
            pady=4,
            font=ui_font(delta=-1),
        ).pack()
        x = self.widget.winfo_rootx() + 14
        y = self.widget.winfo_rooty() + self.widget.winfo_height() + 6
        tip.wm_geometry(f"+{x}+{y}")
        self._tip = tip

    def hide(self, _event=None) -> None:
        if self._after_id is not None:
            try:
                self.widget.after_cancel(self._after_id)
            except (tk.TclError, ValueError):
                pass
            self._after_id = None
        if self._tip is not None:
            try:
                self._tip.destroy()
            except tk.TclError:
                pass
            self._tip = None


class SortableList(ttk.Frame):
    """每行左边一个「小横线」手柄，按住就能上下拖动排序的列表。

    用法：
        rowlist = SortableList(parent, items=["a", "b"], height=6,
                               on_change=保存函数, selectable=True)
        rowlist.items            # 当前顺序（拖动后即时更新）
        rowlist.set_items([...])
        rowlist.select(2) / rowlist.selected_index() / rowlist.move_item(2, 0)

    on_change 只在拖动真正改变了顺序时调用，用来把新顺序存下来。
    """

    #: 手柄上的悬浮提示文案（悬停约 0.45 秒后出现）
    HANDLE_HINT = "拖动可以调整顺序"

    def __init__(
        self,
        master,
        items=(),
        height: int = 6,
        width: int = 26,
        on_change=None,
        selectable: bool = False,
        on_double_click=None,
        border: bool = False,
        auto_height: bool = False,
        max_rows: int | None = 20,
        auto_width: bool = False,
        min_width: int = 220,
        row_height: int = 20,
    ) -> None:
        super().__init__(master)
        self.items: list[str] = [str(item) for item in items]
        self.on_change = on_change
        self.selectable = selectable
        self.on_double_click = on_double_click
        self.handle_tooltip_text = self.HANDLE_HINT
        self.border = border
        self.auto_height = auto_height
        self.max_rows = max_rows
        self.auto_width = auto_width
        self.min_width = min_width
        self.row_height = row_height

        style = ttk.Style(self)
        self.normal_bg = "#ffffff" if border else (style.lookup("TFrame", "background") or "#f0f0f0")
        self.border_color = THEME["line"] if border else ""
        self.selected_bg = "#eef2ff"

        self._selected: int | None = None
        self._drag_index: int | None = None
        self._drag_changed = False
        self._scrollbar_shown = False
        self._rows: list[tk.Frame] = []
        self._handles: list[tk.Canvas] = []
        self._labels: list[tk.Label] = []

        # 外圈用自绘圆角（原生 highlight 只能画方框，和整页圆角风格不一致）
        self.canvas = tk.Canvas(
            self,
            highlightthickness=0 if border else 0,
            borderwidth=0,
            height=max(60, height * row_height),
            width=max(120, width * 9),
            background=THEME["card"] if border else self.normal_bg,
        )
        if border:
            self.canvas.bind("<Configure>", self._draw_card, add="+")
        self._card_done = False
        self.scrollbar = RoundedScrollbar(self, orient="vertical", command=self.canvas.yview)
        self.inner = ttk.Frame(self.canvas)
        self._window = self.canvas.create_window((0, 0), window=self.inner, anchor="nw")
        self.canvas.configure(yscrollcommand=self.scrollbar.set)
        # 非自适应高度的列表始终显示滚动条；自适应高度时由 _autosize 决定要不要显示。
        self._scrollbar_shown = not auto_height
        if self._scrollbar_shown:
            self.scrollbar.pack(side="left", fill="y")
        self.canvas.pack(side="left", fill="both", expand=True)

        self.inner.bind("<Configure>", lambda _e: self.canvas.configure(scrollregion=self.canvas.bbox("all")))
        self.canvas.bind("<Configure>", self._on_canvas_configure)
        if border:
            self._draw_card()
        self.bind("<Enter>", self._bind_wheel)
        self.bind("<Leave>", self._unbind_wheel)

        self._rebuild()

    # ---------- 滚轮 ----------

    def _on_canvas_configure(self, event) -> None:
        """画布尺寸变了：内容跟着变宽，圆角描边重画。"""
        self.canvas.itemconfigure(self._window, width=event.width)
        if self.border:
            self._draw_card(event)

    def _draw_card(self, event=None) -> None:
        """给排序框画一圈圆角描边（内容在 canvas 上，描边画在最底层）。"""
        self.canvas.delete("card_border")
        width = event.width if event is not None else self.canvas.winfo_width()
        height = event.height if event is not None else self.canvas.winfo_height()
        if width <= 2:  # 还没排好版时退回请求尺寸，保证描边一定画得出来
            width = int(self.canvas["width"])
        if height <= 2:
            height = int(self.canvas["height"])
        if width <= 2 or height <= 2:
            return
        draw_rounded_rect(
            self.canvas, 0.5, 0.5, width - 0.5, height - 0.5, corner_radius("card"),
            fill="", outline=self.border_color, tags="card_border",
        )
        self.canvas.tag_lower("card_border")

    def _bind_wheel(self, _event=None) -> None:
        self.canvas.bind_all("<MouseWheel>", self._on_wheel)

    def _unbind_wheel(self, _event=None) -> None:
        """离开时把滚轮还给外层的滚动容器，别把整页的滚轮弄失灵。"""
        outer = self._outer_scroll()
        if outer is not None:
            outer.bind_wheel()
        else:
            self.canvas.unbind_all("<MouseWheel>")

    def _outer_scroll(self):
        widget = self.master
        for _ in range(6):
            if hasattr(widget, "bind_wheel"):
                return widget
            widget = getattr(widget, "master", None)
            if widget is None:
                return None
        return None

    def _on_wheel(self, event) -> None:
        self.canvas.yview_scroll(int(-event.delta / 120), "units")

    # ---------- 行 ----------

    def _rebuild(self) -> None:
        for row in self._rows:
            row.destroy()
        self._rows, self._handles, self._labels = [], [], []
        for index, name in enumerate(self.items):
            self._rows.append(self._make_row(index, name))
        self._repack()
        self._apply_selection()
        self._autosize()
        self.canvas.configure(scrollregion=self.canvas.bbox("all"))

    def row_pixels(self) -> int:
        """一行实际占多高：量出来，而不是拍一个数字（拍数字会把最后一行切掉）。"""
        if self._rows:
            self.inner.update_idletasks()
            measured = self._rows[0].winfo_reqheight()
            if measured > 1:
                return measured
        return self.row_height

    def _autosize(self) -> None:
        """自适应大小：完整显示所有选项（必要时才滚动），宽度也按最长的一项撑开。"""
        if self.auto_height:
            row_pixels = self.row_pixels()
            capped = self.max_rows is not None and len(self.items) > self.max_rows
            visible = self.max_rows if capped else max(1, len(self.items))
            self.canvas.configure(height=visible * row_pixels + 2)
            self._toggle_scrollbar(capped)
        if self.auto_width:
            from tkinter import font as tkfont

            measure = tkfont.Font(font=pick_font()).measure
            longest = max((measure(name) for name in self.items), default=40)
            self.canvas.configure(width=max(self.min_width, longest + 46))

    def _toggle_scrollbar(self, show: bool) -> None:
        if show == self._scrollbar_shown:
            return
        self.canvas.pack_forget()
        self.scrollbar.pack_forget()
        if show:
            self.scrollbar.pack(side="left", fill="y")
        self.canvas.pack(side="left", fill="both", expand=True)
        self._scrollbar_shown = show

    def _make_row(self, index: int, name: str) -> tk.Frame:
        row = tk.Frame(self.inner, background=self.normal_bg)
        handle = tk.Canvas(row, width=14, height=18, background=self.normal_bg, highlightthickness=0, cursor="fleur")
        for offset in (4, 9, 14):
            handle.create_line(3, offset, 11, offset, fill="#9a9a9a", width=1)
        handle.pack(side="left", padx=(4, 2))
        font = pick_font()
        label = tk.Label(
            row,
            text=name,
            anchor="w",
            background=self.normal_bg,
            foreground="#1f1f1f",
            font=font,
            padx=2,
        )
        label.pack(side="left", fill="x", expand=True)
        Tooltip(handle, self.handle_tooltip_text)

        for widget in (handle, label, row):
            widget.bind("<Button-1>", lambda event, r=row: self._start_drag(r, event))
            widget.bind("<B1-Motion>", self._drag_motion)
            widget.bind("<ButtonRelease-1>", self._end_drag)
        if self.on_double_click is not None:
            label.bind("<Double-1>", lambda event, r=row: self.on_double_click(self._index_of(r)))

        self._handles.append(handle)
        self._labels.append(label)
        return row

    def _repack(self) -> None:
        for row in self._rows:
            row.pack_forget()
        for row in self._rows:
            row.pack(fill="x")

    def _index_of(self, row) -> int:
        return self._rows.index(row) if row in self._rows else -1

    # ---------- 选中 ----------

    def _apply_selection(self) -> None:
        if not self.selectable:
            return
        for index, (row, label) in enumerate(zip(self._rows, self._labels)):
            selected = index == self._selected
            color = self.selected_bg if selected else self.normal_bg
            row.configure(background=color)
            label.configure(background=color)

    def select(self, index: int | None) -> None:
        self._selected = index if index is not None and 0 <= index < len(self.items) else None
        self._apply_selection()

    def selected_index(self) -> int | None:
        return self._selected

    # ---------- 拖动 ----------

    def begin_drag(self, index: int) -> None:
        """开始拖动第 index 行（鼠标按下时调用；测试里也可以直接调）。"""
        if 0 <= index < len(self.items):
            self._drag_index = index
            self._drag_changed = False
            if self.selectable:
                self.select(index)

    def drop(self) -> None:
        """松手：只有顺序真的变了才回调 on_change（避免白写一次配置）。"""
        if self._drag_index is None:
            return
        self._drag_index = None
        if self._drag_changed and self.on_change is not None:
            self.on_change(list(self.items))
        self._drag_changed = False

    def _start_drag(self, row, event=None) -> None:
        self.begin_drag(self._index_of(row))

    def _drag_motion(self, event) -> None:
        if self._drag_index is None:
            return
        target = self.index_at_root_y(event.y_root)
        if target is not None and target != self._drag_index:
            self.move_item(self._drag_index, target)
            self._drag_index = target

    def _end_drag(self, _event=None) -> None:
        self.drop()

    def index_at_root_y(self, y_root: int) -> int | None:
        """根据屏幕纵坐标判断应该插到第几位（拖到哪一行就换到哪一行）。"""
        if not self._rows:
            return None
        for index, row in enumerate(self._rows):
            middle = row.winfo_rooty() + max(1, row.winfo_height()) // 2
            if y_root < middle:
                return index
        return len(self._rows) - 1

    def move_item(self, index: int, target: int) -> None:
        """把第 index 项移动到 target 位置（拖动、上移/下移都走这里）。"""
        if index == target or not (0 <= index < len(self.items)) or not (0 <= target < len(self.items)):
            return
        selected_item = self.items[self._selected] if self._selected is not None else None
        item = self.items.pop(index)
        row = self._rows.pop(index)
        self.items.insert(target, item)
        self._rows.insert(target, row)
        self._repack()
        if selected_item is not None and selected_item in self.items:
            self._selected = self.items.index(selected_item)
        self._apply_selection()
        self._drag_changed = True

    # ---------- 数据 ----------

    def set_items(self, items) -> None:
        self.items = [str(item) for item in items]
        if self._selected is not None and self._selected >= len(self.items):
            self._selected = len(self.items) - 1 if self.items else None
        self._rebuild()

    def size(self) -> int:
        return len(self.items)

    def handle_count(self) -> int:
        return len(self._handles)

    def scrollbar_visible(self) -> bool:
        return self._scrollbar_shown


class DateBox(ttk.Frame):
    """日期输入框：可以直接手改，也可以点「今天」。"""

    def __init__(self, master, value: dt.date | None = None, width: int = 12) -> None:
        super().__init__(master)
        self.var = tk.StringVar(value=(value or dt.date.today()).isoformat())
        self.entry = RoundedEntry(self, textvariable=self.var, width=width, icon="📅")
        self.entry.pack(side="left")
        RoundedButton(self, text="今天", width=5, height=28, command=self.set_today).pack(
            side="left", padx=(6, 0)
        )

    def get(self) -> dt.date:
        return m.parse_date(self.var.get())

    def set(self, day: dt.date) -> None:
        self.var.set(day.isoformat())

    def set_today(self) -> None:
        self.set(dt.date.today())


class TreeTable(ttk.Frame):
    """带滚动条、点表头可排序的表格。"""

    def __init__(
        self,
        master,
        columns: list[str],
        headings: list[str] | None = None,
        widths: list[int] | None = None,
        anchors: list[str] | None = None,
        height: int = 8,
        on_activate=None,
    ) -> None:
        super().__init__(master)
        self.columns = list(columns)
        headings = headings or self.columns
        # 用圆角卡片包一层：列表看起来是圆角的，滚动条和表头功能不变
        self.card = RoundedCard(self, padding=px(6))
        self.card.pack(fill="both", expand=True)
        inner = self.card.body
        self.tree = ttk.Treeview(
            inner, columns=self.columns, show="headings", selectmode="extended", height=height, style="Card.Treeview"
        )
        for index, column in enumerate(self.columns):
            self.tree.heading(column, text=headings[index], command=lambda col=column: self.sort_by(col))
            self.tree.column(
                column,
                width=px(widths[index] if widths else 110),
                anchor=(anchors[index] if anchors else "w"),
                stretch=True,
            )
        vsb = RoundedScrollbar(inner, orient="vertical", command=self.tree.yview)
        hsb = RoundedScrollbar(inner, orient="horizontal", command=self.tree.xview)
        self.tree.configure(yscrollcommand=vsb.set, xscrollcommand=hsb.set)
        self.tree.grid(row=0, column=0, sticky="nsew")
        vsb.grid(row=0, column=1, sticky="ns")
        hsb.grid(row=1, column=0, sticky="ew")
        inner.rowconfigure(0, weight=1)
        inner.columnconfigure(0, weight=1)
        if on_activate:
            self.tree.bind("<Double-1>", lambda _event: on_activate())
            self.tree.bind("<Return>", lambda _event: on_activate())
        self._rows: list[list[str]] = []
        self._payloads: list = []

    # ---------- 数据 ----------

    def set_rows(self, rows, payloads=None) -> None:
        """rows 是显示用的单元格；payloads 与之平行，用来携带原始数据（如记录字典）。"""
        self._rows = [[("" if cell is None else str(cell)) for cell in row] for row in rows]
        self._payloads = list(payloads) if payloads is not None else [None] * len(self._rows)
        self.refresh()

    def refresh(self) -> None:
        self.tree.delete(*self.tree.get_children())
        for index, row in enumerate(self._rows):
            self.tree.insert("", "end", iid=str(index), values=row)

    def clear(self) -> None:
        self.set_rows([])

    def rows(self) -> list[list[str]]:
        """当前显示的所有行（用来在末尾追加，比如「本次已记」倒序累积）。"""
        return [list(row) for row in self._rows]

    # ---------- 选择 ----------

    def selected_indices(self) -> list[int]:
        return sorted(int(iid) for iid in self.tree.selection())

    def selected_rows(self) -> list[list[str]]:
        return [self._rows[i] for i in self.selected_indices()]

    def selected_payloads(self) -> list:
        return [self._payloads[i] for i in self.selected_indices()]

    def row_at_selection(self) -> list[str] | None:
        indices = self.selected_indices()
        return self._rows[indices[0]] if indices else None

    def payload_at_selection(self):
        indices = self.selected_indices()
        return self._payloads[indices[0]] if indices else None

    # ---------- 排序 ----------

    def sort_by(self, column: str) -> None:
        if column not in self.columns:
            return
        index = self.columns.index(column)
        same = getattr(self, "_sort_column", None) == column
        descending = (not getattr(self, "_sort_descending", False)) if same else False
        self._sort_column = column
        self._sort_descending = descending

        def key(row: list[str]):
            text = row[index]
            number = text.replace("¥", "").replace(",", "").replace("%", "").strip()
            try:
                return (0, float(number), "")
            except ValueError:
                return (1, 0.0, text)

        order = sorted(range(len(self._rows)), key=lambda i: key(self._rows[i]), reverse=descending)
        self._rows = [self._rows[i] for i in order]
        self._payloads = [self._payloads[i] for i in order]
        self.refresh()


class RecordDialog(tk.Toplevel):
    """修改一笔记录。用法：dialog = RecordDialog(parent, record_dict); result = dialog.show()"""

    def __init__(self, master, record: dict, title: str = "修改记录") -> None:
        super().__init__(master)
        self.result: dict | None = None
        self.record = dict(record)
        self.categories = config.load_categories()
        self.settings = config.load_settings()

        # 标题里不带编号：编号是内部主键，界面上不显示
        self.title(title)
        self.transient(master)
        self.resizable(False, False)

        body = ttk.Frame(self, padding=12)
        body.pack(fill="both", expand=True)

        ttk.Label(body, text="日期").grid(row=0, column=0, sticky="e", padx=(0, 6), pady=4)
        self.date_box = DateBox(body, value=m.parse_date(record.get("date")))
        self.date_box.grid(row=0, column=1, sticky="w", pady=4)

        ttk.Label(body, text="时间").grid(row=1, column=0, sticky="e", padx=(0, 6), pady=4)
        self.time_var = tk.StringVar(value=record.get("time", ""))
        RoundedEntry(body, textvariable=self.time_var, width=10).grid(row=1, column=1, sticky="w", pady=4)

        ttk.Label(body, text="类型").grid(row=2, column=0, sticky="e", padx=(0, 6), pady=4)
        self.kind_var = tk.StringVar(value=record.get("kind", m.KIND_EXPENSE))
        kinds = ttk.Frame(body)
        kinds.grid(row=2, column=1, sticky="w", pady=4)
        for kind in m.KINDS:
            ttk.Radiobutton(kinds, text=kind, value=kind, variable=self.kind_var, command=self._refresh_categories).pack(
                side="left", padx=(0, 10)
            )

        ttk.Label(body, text="金额").grid(row=3, column=0, sticky="e", padx=(0, 6), pady=4)
        self.amount_var = tk.StringVar(value=record.get("amount", ""))
        RoundedEntry(body, textvariable=self.amount_var, width=14).grid(row=3, column=1, sticky="w", pady=4)

        ttk.Label(body, text="分类").grid(row=4, column=0, sticky="e", padx=(0, 6), pady=4)
        self.category_var = tk.StringVar(value=record.get("category", ""))
        self.category_box = RoundedCombo(body, textvariable=self.category_var, width=18, state="normal")
        self.category_box.grid(row=4, column=1, sticky="w", pady=4)

        ttk.Label(body, text="支付方式").grid(row=5, column=0, sticky="e", padx=(0, 6), pady=4)
        self.pay_var = tk.StringVar(value=record.get("pay", ""))
        RoundedCombo(
            body,
            textvariable=self.pay_var,
            width=18,
            state="normal",
            values=self.settings.get("支付方式", []),
        ).grid(row=5, column=1, sticky="w", pady=4)

        ttk.Label(body, text="备注").grid(row=6, column=0, sticky="e", padx=(0, 6), pady=4)
        self.note_var = tk.StringVar(value=record.get("note", ""))
        RoundedEntry(body, textvariable=self.note_var, width=28).grid(row=6, column=1, sticky="we", pady=4)

        buttons = ttk.Frame(self, padding=(12, 0, 12, 12))
        buttons.pack(fill="x")
        RoundedButton(buttons, text="取消", command=self.destroy).pack(side="right")
        RoundedButton(buttons, text="确定", style="primary", command=self._on_ok).pack(side="right", padx=(0, 8))

        self._refresh_categories()
        self.bind("<Return>", lambda _event: self._on_ok())
        self.bind("<Escape>", lambda _event: self.destroy())
        center_on_parent(self, master)
        apply_rounded_window(self)

    def _refresh_categories(self) -> None:
        values = self.categories.get(self.kind_var.get(), [])
        self.category_box.configure_values(values)

    def _on_ok(self) -> None:
        try:
            changes = {
                "date": self.date_box.get(),
                "time": m.normalize_time(self.time_var.get()),
                "kind": m.parse_kind(self.kind_var.get()),
                "amount": m.parse_amount(self.amount_var.get()),
                "category": self.category_var.get().strip(),
                "pay": self.pay_var.get().strip(),
                "note": self.note_var.get().strip(),
            }
            if not changes["category"]:
                raise ValueError("请选择分类")
        except (ValueError, m.RecordError) as exc:
            messagebox.showwarning("还不能保存", str(exc), parent=self)
            return
        self.result = changes
        self.destroy()

    def show(self) -> dict | None:
        self.grab_set()
        self.wait_window()
        return self.result


class ItemListDialog(tk.Toplevel):
    """通用的「一串名字」编辑窗口：新增、改名、删除、拖动（或上移/下移）排序。

    记账本的「收入/支出种类」和「支付方式」都用它，用法：
        dialog = ItemListDialog(parent, "修改支付方式", ["微信", "支付宝"], noun="支付方式")
        new_list = dialog.show()      # 取消时返回 None

    顺序会被保留 —— 记账页的下拉框就按这个顺序显示，所以常用的可以往前挪。
    """

    def __init__(self, master, title: str, items, noun: str = "项目") -> None:
        super().__init__(master)
        self.result: list[str] | None = None
        self.noun = noun
        self.items: list[str] = [str(item) for item in items]

        self.title(title)
        self.transient(master)
        self.resizable(False, False)

        body = ttk.Frame(self, padding=12)
        body.pack(fill="both", expand=True)
        ttk.Label(
            body,
            text=f"{noun}（最左边的小横线可以拖动排序，顺序就是记账时下拉框的顺序）",
            style="Hint.TLabel",
        ).grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 6))

        self.rowlist = SortableList(
            body,
            items=self.items,
            height=10,
            width=22,
            selectable=True,
            border=True,
            on_change=self.sync_items,
            on_double_click=lambda _index: self.rename_item(),
        )
        self.rowlist.grid(row=1, column=0, sticky="nsew")

        side = ttk.Frame(body)
        side.grid(row=1, column=2, sticky="nw", padx=(10, 0))
        self.name_var = tk.StringVar()
        entry = RoundedEntry(side, textvariable=self.name_var, width=16)
        entry.pack(fill="x")
        entry.bind_entry("<Return>", lambda _event: self.add_item())
        RoundedButton(side, text="新增", style="primary", command=self.add_item).pack(fill="x", pady=(6, 0))
        RoundedButton(side, text="改名选中", command=self.rename_item).pack(fill="x", pady=(6, 0))
        RoundedButton(side, text="删除选中", command=self.remove_item).pack(fill="x", pady=(6, 0))
        RoundedButton(side, text="上移", command=lambda: self.move(-1)).pack(fill="x", pady=(6, 0))
        RoundedButton(side, text="下移", command=lambda: self.move(1)).pack(fill="x", pady=(6, 0))

        self.count_var = tk.StringVar()
        ttk.Label(body, textvariable=self.count_var, style="Hint.TLabel").grid(
            row=2, column=0, columnspan=3, sticky="w", pady=(6, 0)
        )

        buttons = ttk.Frame(self, padding=(12, 0, 12, 12))
        buttons.pack(fill="x")
        RoundedButton(buttons, text="取消", command=self.destroy).pack(side="right")
        RoundedButton(buttons, text="确定", style="primary", command=self.on_ok).pack(side="right", padx=(0, 8))

        self.refresh()
        entry.focus_set()
        self.bind("<Escape>", lambda _event: self.destroy())
        center_on_parent(self, master)
        apply_rounded_window(self)

    # ---------- 列表操作 ----------

    def sync_items(self, items) -> None:
        """拖动排序后把最新顺序同步给对话框（还没点确定，只存在内存里）。"""
        self.items = [str(item) for item in items]
        self.count_var.set(f"共 {len(self.items)} 个")

    def refresh(self, select: int | None = None) -> None:
        self.rowlist.set_items(self.items)
        self.count_var.set(f"共 {len(self.items)} 个")
        if select is not None and 0 <= select < len(self.items):
            self.rowlist.select(select)

    def selected_index(self) -> int | None:
        return self.rowlist.selected_index()

    def warn(self, message: str) -> None:
        messagebox.showwarning("还不能这样改", message, parent=self)

    def add_item(self) -> None:
        name = self.name_var.get().strip()
        if not name:
            self.warn(f"请先填写{self.noun}名称")
            return
        if name in self.items:
            self.warn(f"已经有「{name}」了")
            return
        self.items.append(name)
        self.name_var.set("")
        self.refresh(select=len(self.items) - 1)

    def rename_item(self) -> None:
        index = self.selected_index()
        if index is None:
            self.warn("请先在左边的列表里选中一项")
            return
        old = self.items[index]
        new = simpledialog.askstring("改名", f"把「{old}」改成：", initialvalue=old, parent=self)
        if not new or not new.strip() or new.strip() == old:
            return
        new = new.strip()
        if new in self.items:
            self.warn(f"已经有「{new}」了")
            return
        self.items[index] = new
        self.refresh(select=index)

    def remove_item(self) -> None:
        index = self.selected_index()
        if index is None:
            self.warn("请先在左边的列表里选中一项")
            return
        if len(self.items) <= 1:
            self.warn(f"「{self.noun}」至少要保留一个")
            return
        removed = self.items.pop(index)
        self.refresh(select=min(index, len(self.items) - 1))
        self.count_var.set(f"共 {len(self.items)} 个（已删除「{removed}」，点确定才生效）")

    def move(self, offset: int) -> None:
        index = self.selected_index()
        if index is None:
            self.warn("请先在左边的列表里选中一个种类")
            return
        target = index + offset
        if not 0 <= target < len(self.items):
            return
        self.rowlist.move_item(index, target)  # 与拖动共用同一套排序逻辑
        self.items = list(self.rowlist.items)
        self.count_var.set(f"共 {len(self.items)} 个")

    def on_ok(self) -> None:
        if not self.items:
            self.warn(f"「{self.noun}」至少要保留一个")
            return
        self.result = list(self.items)
        self.destroy()

    def show(self) -> list[str] | None:
        self.grab_set()
        self.wait_window()
        return self.result


class CategoryDialog(ItemListDialog):
    """修改某一类（收入 / 支出）的「种类」列表：就是 ItemListDialog 的专用壳。"""

    def __init__(self, master, kind: str, categories) -> None:
        super().__init__(master, f"修改{kind}种类", categories, noun="种类")
        self.kind = kind


class FlatTabs(ttk.Frame):
    """扁平分页标签条（当前页加粗 + 下方一条粗下划线），像常见客户端的设置页那样。

    用法：
        tabs = FlatTabs(parent, on_change=切换页面函数)
        tabs.add("常规", "常规")
        tabs.select("常规")
    """

    def __init__(self, master, on_change=None, accent: str = "#c20c0c") -> None:
        super().__init__(master)
        self.on_change = on_change
        self.accent = accent
        self.tabs: dict[str, dict] = {}
        self.order: list[str] = []
        self._active: str | None = None
        font = pick_font()
        self.normal_font = font
        self.active_font = (font[0], font[1] + 1, "bold")
        self.normal_color = "#3c3c3c"

    def _bg(self) -> str:
        return ttk.Style(self).lookup("TFrame", "background") or "#f0f0f0"

    def add(self, key: str, text: str) -> None:
        holder = ttk.Frame(self)
        holder.pack(side="left", padx=(0, 16))
        label = tk.Label(
            holder,
            text=text,
            font=self.normal_font,
            foreground=self.normal_color,
            background=self._bg(),
            cursor="hand2",
            padx=2,
        )
        label.pack()
        underline = tk.Frame(holder, height=3, background=self._bg())
        underline.pack(fill="x", pady=(3, 0))
        label.bind("<Button-1>", lambda _event, k=key: self.select(k))
        self.tabs[key] = {"holder": holder, "label": label, "underline": underline}
        self.order.append(key)

    def select(self, key: str) -> None:
        if key not in self.tabs:
            return
        self._active = key
        for name, parts in self.tabs.items():
            active = name == key
            parts["label"].configure(
                font=self.active_font if active else self.normal_font,
                foreground=self.accent if active else self.normal_color,
            )
            parts["underline"].configure(background=self.accent if active else self._bg())
        if self.on_change is not None:
            self.on_change(key)

    def active(self) -> str | None:
        return self._active


class DatePickerPopup(tk.Toplevel):
    """万年历：打开时停在「当前选中的那天」所在的月份，点某一天就选中并关闭。

    用法：
        picked = DatePickerPopup(parent, dt.date(2026, 2, 14)).show()   # 取消返回 None
    """

    WEEKDAYS = ("一", "二", "三", "四", "五", "六", "日")

    def __init__(self, master, value: dt.date, on_pick=None) -> None:
        super().__init__(master)
        self.result: dt.date | None = None
        self.on_pick = on_pick
        self.value = value or dt.date.today()
        self.year = self.value.year
        self.month = self.value.month
        self.today_bg = "#cfe4ff"
        self.normal_bg = "#ffffff"

        self.title("选择日期")
        self.transient(master.winfo_toplevel())
        self.resizable(False, False)

        body = ttk.Frame(self, padding=8)
        body.pack(fill="both", expand=True)

        head = ttk.Frame(body)
        head.pack(fill="x")
        RoundedButton(head, text="◀", width=3, height=26, command=lambda: self.shift_month(-1)).pack(side="left")
        self.title_var = tk.StringVar()
        ttk.Label(head, textvariable=self.title_var, width=12, anchor="center").pack(side="left", padx=4)
        RoundedButton(head, text="▶", width=3, height=26, command=lambda: self.shift_month(1)).pack(side="left")
        RoundedButton(head, text="今天", width=6, height=26, command=self.pick_today).pack(
            side="left", padx=(10, 0)
        )

        self.grid_frame = ttk.Frame(body)
        self.grid_frame.pack(pady=(6, 0))
        for column, name in enumerate(self.WEEKDAYS):
            ttk.Label(self.grid_frame, text=name, width=4, anchor="center").grid(row=0, column=column)

        self.day_buttons: dict[int, tk.Button] = {}
        self.render()

        RoundedButton(body, text="取消", command=self.destroy).pack(pady=(8, 0))
        self.bind("<Escape>", lambda _event: self.destroy())
        center_on_parent(self, master.winfo_toplevel())
        apply_rounded_window(self)

    def render(self) -> None:
        for button in self.day_buttons.values():
            button.destroy()
        self.day_buttons.clear()
        self.title_var.set(f"{self.year} 年 {self.month} 月")

        import calendar as _calendar

        weeks = _calendar.Calendar(firstweekday=0).monthdayscalendar(self.year, self.month)
        for row, week in enumerate(weeks, start=1):
            for column, day in enumerate(week):
                if day == 0:
                    ttk.Label(self.grid_frame, text="", width=4).grid(row=row, column=column)
                    continue
                is_selected = (self.year, self.month, day) == (self.value.year, self.value.month, self.value.day)
                button = RoundedButton(
                    self.grid_frame,
                    text=str(day),
                    width=3,
                    height=26,
                    radius=corner_radius("small"),
                    style="chip",
                    padx=6,
                    bg=self.today_bg if is_selected else self.normal_bg,
                    fg="#123a75" if is_selected else "#303030",
                    font=(pick_font()[0], pick_font()[1], "bold") if is_selected else pick_font(),
                    command=lambda d=day: self.pick(dt.date(self.year, self.month, d)),
                )
                button.grid(row=row, column=column, padx=1, pady=1)
                self.day_buttons[day] = button

    def shift_month(self, offset: int) -> None:
        month = self.month + offset
        year = self.year
        if month < 1:
            year, month = year - 1, 12
        elif month > 12:
            year, month = year + 1, 1
        self.year, self.month = year, month
        self.render()

    def pick(self, day: dt.date) -> None:
        self.result = day
        if self.on_pick is not None:
            self.on_pick(day)
        self.destroy()

    def pick_today(self) -> None:
        self.pick(dt.date.today())

    def show(self) -> dt.date | None:
        self.grab_set()
        self.wait_window()
        return self.result


class DateButton(ttk.Frame):
    """显示一个日期，点一下弹万年历改（从当前这天开始选）。"""

    def __init__(self, master, value: dt.date | None = None, on_change=None, width: int = 12) -> None:
        super().__init__(master)
        self._value = value or dt.date.today()
        self.on_change = on_change
        self.button = RoundedButton(
            self, text=self._value.isoformat(), width=width, height=28, command=self.open_picker
        )
        self.button.pack()

    def get(self) -> dt.date:
        return self._value

    def set(self, day: dt.date, notify: bool = True) -> None:
        self._value = day
        self.button.configure(text=day.isoformat())
        if notify and self.on_change is not None:
            self.on_change(day)

    def open_picker(self) -> None:
        picked = DatePickerPopup(self, self._value).show()
        if picked is not None:
            self.set(picked)


def center_on_parent(window: tk.Toplevel, parent) -> None:
    """把弹窗摆到父窗口中间。"""
    window.update_idletasks()
    try:
        px, py = parent.winfo_rootx(), parent.winfo_rooty()
        pw, ph = parent.winfo_width(), parent.winfo_height()
        w, h = window.winfo_width(), window.winfo_height()
        window.geometry(f"+{px + max(0, (pw - w) // 2)}+{py + max(0, (ph - h) // 3)}")
    except tk.TclError:
        pass


def money_text(amount, symbol: str = "¥") -> str:
    return m.money(amount, symbol)
