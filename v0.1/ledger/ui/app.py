"""主窗口：装配五个标签页、状态栏、单实例锁。"""

from __future__ import annotations

import tkinter as tk
from tkinter import messagebox, ttk

from .. import api, config, paths, storage
from . import common
from .query_tab import QueryTab
from .record_tab import RecordTab
from .settings_tab import SettingsTab
from .summary_tab import SummaryTab

#: 现在的顶层标签页。导入 / 导出已经并进「设置」页里，不再是独立标签页。
TAB_CLASSES = (RecordTab, QueryTab, SummaryTab, SettingsTab)


class App(tk.Tk):
    """记账本主窗口。"""

    def __init__(self) -> None:
        # 必须在建窗口之前声明 DPI 感知，否则高 DPI 屏幕上整个界面会被系统位图拉伸（糊）
        common.enable_dpi_awareness()
        super().__init__()
        common.apply_tk_scaling(self)
        self.startup_error: str | None = None
        config.ensure_initialized()

        self._lock = storage.Lock()
        try:
            self._lock.acquire()
        except storage.LockError as exc:
            self.startup_error = str(exc)
            self.withdraw()
            return

        self.title(f"{paths.APP_NAME} v{paths.VERSION}")
        # 最小尺寸：刚好够「明细」表头一行显示完（各页的行都用自动回车布局，窄了会换行，不会藏内容）；
        # 最大尺寸也限制一下，免得窗口宽到没有意义（也少触发无谓的布局重排）。
        # 尺寸按 DPI 缩放，高 DPI 下按钮和文字才不会挤在一起。
        self.minsize(common.px(620), common.px(520))
        self.maxsize(common.px(2600), common.px(1700))
        self._geometry_done = False
        self._font_size = common.font_size()
        self.build_ui()

        self.protocol("WM_DELETE_WINDOW", self.on_close)
        self.refresh_summary()
        # 窗口圆角（Win11 生效，旧系统自动忽略）
        self.rounded_corners = common.apply_rounded_window(self)
        # 首次显示后按各页实际需要的高度把窗口撑够，保证每页都能完整显示（不用在外框再拖滚动条）
        self.after(60, self.fit_window_to_pages)
        self.after(150, self._show_current_tab)

    def build_ui(self) -> None:
        """搭出状态栏和各标签页（字号改了就整块重建，所以抽成方法）。"""
        self._setup_style()
        settings = config.load_settings()
        self.currency = settings.get("货币符号", "¥")
        self._font_size = common.font_size()
        if not self._geometry_done:
            window = settings.get("窗口", {})
            # 存的是「设计尺寸」（96 DPI 下的像素），这里乘上当前 DPI 缩放，
            # 175% 屏幕上窗口才会和 100% 时一样宽敞；再按屏幕可用区兜住，别开出屏幕外。
            width = common.px(int(window.get("宽", 1020)))
            height = common.px(int(window.get("高", 720)))
            work_w, work_h = common.work_area()
            if work_w and work_h:
                width = min(width, work_w - common.px(20))
                height = min(height, work_h - common.px(40))
            self.geometry(f"{width}x{height}")
            self._geometry_done = True

        self.status_var = tk.StringVar(value=f"数据目录：{paths.data_dir()}")
        self.summary_var = tk.StringVar(value="")

        # 先把状态栏按 side="bottom" 摆好：pack 是按调用顺序分配空间的，
        # 先摆它，窗口再矮也不会把状态栏挤掉（内容不会被藏起来）。
        bar = common.WrapRow(self, gap=18, row_gap=0)
        bar.pack(side="bottom", fill="x", padx=10, pady=(4, 6))
        bar.add(ttk.Label(bar, textvariable=self.status_var))
        bar.add(ttk.Label(bar, textvariable=self.summary_var, foreground="#4a4a4a"))
        self.status_wrap = bar

        self.notebook = ttk.Notebook(self)
        self.notebook.pack(side="top", fill="both", expand=True, padx=8, pady=(8, 0))
        self.tabs: dict[str, common.Tab] = {}
        for tab_class in TAB_CLASSES:
            tab = tab_class(self.notebook, self)
            self.notebook.add(tab, text=tab_class.title)
            self.tabs[tab_class.title] = tab
        self.notebook.bind("<<NotebookTabChanged>>", self._on_tab_changed)

    def rebuild_ui(self) -> None:
        """重建状态栏和各页（字号这类影响所有控件的设置改了以后用，不用重启）。"""
        for widget in (getattr(self, "notebook", None), getattr(self, "status_wrap", None)):
            if widget is not None:
                widget.destroy()
        self.build_ui()
        self.refresh_summary()
        self.fit_window_to_pages()
        self._show_current_tab()

    # ---------------------------------------------------------------- 窗口尺寸

    def page_needs(self) -> tuple[int, int]:
        """所有页「完整显示」需要的最大宽高（设计尺寸，已换算成实际像素）。"""
        self.update_idletasks()
        need_w = need_h = 0
        for tab in self.tabs.values():
            need_w = max(need_w, tab.winfo_reqwidth())
            need_h = max(need_h, tab.winfo_reqheight())
        # 加上标签栏、状态栏和四周留白
        return need_w + common.px(40), need_h + common.px(96)

    def fit_window_to_pages(self) -> None:
        """把窗口撑到「最高的一页也能完整显示」，但不超出屏幕可用区。

        这样录入页、汇总页、统计页在最外框都不需要单独拖动；
        里层会无限长的东西（明细、分类统计、设置项）该滚动还是它们自己滚动。
        """
        need_w, need_h = self.page_needs()
        work_w, work_h = common.work_area()
        limit_w = work_w - common.px(20) if work_w else need_w
        limit_h = work_h - common.px(40) if work_h else need_h
        target_w = max(self.winfo_width(), min(need_w, limit_w))
        target_h = max(self.winfo_height(), min(need_h, limit_h))
        # 最小高度也按「能完整显示」来定，用户再缩小也不会把内容挤没
        self.minsize(min(common.px(620), target_w), min(need_h, limit_h))
        if (target_w, target_h) != (self.winfo_width(), self.winfo_height()):
            self.geometry(f"{target_w}x{target_h}")

    # ---------------------------------------------------------------- 外观

    def _setup_style(self) -> None:
        font = common.pick_font()
        style = ttk.Style(self)
        for theme in ("vista", "winnative", "clam", "default"):
            try:
                style.theme_use(theme)
                break
            except tk.TclError:
                continue
        try:
            style.configure(".", font=font)
            # 行高按 DPI 放大，字变大时行距才够
            style.configure("Treeview", rowheight=common.px(24), font=font, borderwidth=0)
            style.configure("Treeview.Heading", font=font)
            style.configure("Big.TLabel", font=common.ui_font(delta=1, bold=True))
            style.configure("Hint.TLabel", foreground=common.THEME["muted"])
            # 配色：浅灰底 + 白色卡片
            style.configure("TFrame", background=common.THEME["bg"])
            style.configure("TLabel", background=common.THEME["bg"], foreground=common.THEME["text"])
            style.configure("TLabelframe", background=common.THEME["bg"], bordercolor=common.THEME["line"])
            style.configure("TLabelframe.Label", background=common.THEME["bg"], foreground=common.THEME["text"])
            style.configure("TCheckbutton", background=common.THEME["bg"], foreground=common.THEME["text"])
            style.configure("TRadiobutton", background=common.THEME["bg"], foreground=common.THEME["text"])
            style.configure("Card.TFrame", background=common.THEME["card"])
            # 列表去边框（边框是方的，会把圆角卡片的角顶出来）
            style.configure(
                "Card.Treeview",
                background=common.THEME["card"],
                fieldbackground=common.THEME["card"],
                borderwidth=0,
                font=font,
            )
            style.configure("Card.Treeview.Heading", font=font)
            style.layout("Card.Treeview", [("Card.Treeview.treearea", {"sticky": "nswe"})])
            # 嵌在圆角框里的原生控件要去掉自己的方角边框和箭头，否则就是「框里还有框」
            style.configure(
                "Embed.TCombobox",
                borderwidth=0,
                relief="flat",
                fieldbackground=common.THEME["card"],
                background=common.THEME["card"],
                foreground=common.THEME["text"],
                arrowcolor=common.THEME["muted"],
                padding=0,
                font=font,
            )
            style.layout(
                "Embed.TCombobox",
                [
                    (
                        "Embed.TCombobox.field",
                        {
                            "sticky": "nswe",
                            "children": [
                                ("Embed.TCombobox.padding", {"sticky": "nswe", "children": [
                                    ("Embed.TCombobox.textarea", {"sticky": "nswe"}),
                                ]}),
                            ],
                        },
                    )
                ],
            )
            style.map(
                "Embed.TCombobox",
                fieldbackground=[("readonly", common.THEME["card"]), ("!disabled", common.THEME["card"])],
                background=[("readonly", common.THEME["card"])],
            )
            style.configure("TNotebook", background=common.THEME["bg"], borderwidth=0)
        except tk.TclError:  # pragma: no cover
            pass

    # ---------------------------------------------------------------- 协作

    def set_status(self, message: str) -> None:
        self.status_var.set(message)

    def set_default_status(self) -> None:
        self.status_var.set(f"数据目录：{paths.data_dir()}")

    def refresh_summary(self) -> None:
        """状态栏右侧：本月收支概况。"""
        info = api.dispatch("info")
        if not info.get("ok"):
            self.summary_var.set("")
            return
        self.currency = config.load_settings().get("货币符号", "¥")
        month = info["this_month"]
        self.summary_var.set(
            f"{month['month']}：{month['count']} 笔 ｜ 收 {self.currency}{month['income']}"
            f" ｜ 支 {self.currency}{month['expense']} ｜ 结余 {self.currency}{month['balance']}"
        )

    def notify_data_changed(self) -> None:
        """任何一处改了数据后，让各页刷新。"""
        self.refresh_summary()
        for tab in self.tabs.values():
            try:
                tab.on_data_changed()
            except Exception as exc:  # 单个页出错不该拖垮整个窗口
                self.set_status(f"刷新「{tab.title}」时出错：{exc}")

    def apply_settings_changed(self) -> None:
        """设置页保存后调用：让新设置立即生效（货币符号、默认范围、可见性…）。"""
        # 字号变了：所有控件的字体都要重设，整块界面重建最稳（放到空闲时做，
        # 免得在设置页自己的回调里把设置页销毁掉）
        size = common.font_size()
        if size != getattr(self, "_font_size", None):
            self._font_size = size
            self.after_idle(self.rebuild_ui)
            return
        self.refresh_summary()
        for tab in self.tabs.values():
            try:
                tab.apply_settings()
            except Exception as exc:
                self.set_status(f"应用设置到「{tab.title}」时出错：{exc}")

    def _on_tab_changed(self, _event=None) -> None:
        self._show_current_tab()

    def _show_current_tab(self) -> None:
        try:
            current = self.notebook.nametowidget(self.notebook.select())
        except (tk.TclError, KeyError):
            return
        for tab in self.tabs.values():
            if tab is current:
                try:
                    tab.on_show()
                except Exception as exc:
                    self.set_status(f"刷新「{tab.title}」时出错：{exc}")

    # ---------------------------------------------------------------- 退出

    def on_close(self) -> None:
        try:
            settings = config.load_settings()
            settings["窗口"] = {"宽": common.unscale(self.winfo_width()), "高": common.unscale(self.winfo_height())}
            settings["货币符号"] = self.currency
            config.save_settings(settings)
        except Exception:
            pass
        try:
            self._lock.release()
        finally:
            self.destroy()


def main(argv=None) -> int:
    app = App()
    if app.startup_error:
        messagebox.showerror(paths.APP_NAME, app.startup_error)
        app.destroy()
        return 1
    app.mainloop()
    return 0
