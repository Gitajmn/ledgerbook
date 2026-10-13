"""查询统计：条件筛选、明细、合计、分类小计，以及编辑/删除/导出。"""

from __future__ import annotations

import datetime as dt
import math
import tkinter as tk
from decimal import Decimal
from pathlib import Path
from tkinter import filedialog, ttk

from .. import api, config, model as m, paths, query
from . import charts
from .common import (
    DateBox,
    RecordDialog,
    RoundedButton,
    RoundedCombo,
    RoundedEntry,
    Tab,
    TreeTable,
    WrapRow,
    px,
)


class QueryTab(Tab):
    title = "查询统计"

    #: 明细区 + 分类统计区合起来的宽度小于这个值时，就从「左右并排」改成「上下排列」
    #: 所有像素常量都按 DPI 缩放：设计尺寸 × 当前缩放（175% 屏幕上就是 1.75 倍）
    SIDE_BY_SIDE_MIN_WIDTH = px(900)
    #: 尺寸变化不到这个值就不重算（拖窗口时省算力）
    LAYOUT_MIN_DELTA = px(24)

    # 编号是内部主键（删除不回收、导入去重要用），界面一律不显示，
    # 所以明细表里没有「编号」列；要指代一笔记录就用 日期+时间+类型+分类+金额+备注。
    DETAIL_COLUMNS = ["日期", "时间", "类型", "分类", "金额", "支付方式", "备注"]
    #: 宽窗口下的理想列宽（备注把多余空间吃掉）
    DETAIL_BASE_WIDTHS = {
        "日期": px(88), "时间": px(52), "类型": px(46), "分类": px(96),
        "金额": px(88), "支付方式": px(76), "备注": px(200),
    }
    #: 每列至少留这么宽（保证「2026-02-14」这种内容不被切掉）
    DETAIL_MIN_WIDTHS = {
        "日期": px(80), "时间": px(48), "类型": px(44), "分类": px(70),
        "金额": px(70), "支付方式": px(62), "备注": px(70),
    }
    DETAIL_ANCHORS = ["w", "center", "center", "w", "e", "w", "w"]

    CATEGORY_COLUMNS = ["类型", "分类", "笔数", "金额", "占比"]
    CATEGORY_BASE_WIDTHS = {
        "类型": px(58), "分类": px(110), "笔数": px(56), "金额": px(96), "占比": px(62)
    }
    CATEGORY_MIN_WIDTHS = {
        "类型": px(44), "分类": px(62), "笔数": px(40), "金额": px(70), "占比": px(46)
    }
    CATEGORY_ANCHORS = ["center", "w", "e", "e", "e"]

    def build(self) -> None:
        self.currency = config.load_settings().get("货币符号", "¥")
        self.last_payload: dict = {}
        self.last_records: list[dict] = []
        self.last_by_category: list[dict] = []
        self.last_category_rows: list[list[str]] = []
        self.query_count = 0  # 自测用来确认「换视图不重读文件」
        self.render_count = 0

        # 快捷范围：一排按钮，窄窗口时自动回车换行
        preset_bar = ttk.Frame(self)
        preset_bar.pack(fill="x")
        self.preset_wrap = WrapRow(preset_bar, gap=6)
        self.preset_wrap.pack(fill="x")
        self.preset_wrap.add(ttk.Label(self.preset_wrap, text="快捷范围："))
        self.preset_buttons: dict[str, RoundedButton] = {}
        for name in query.PRESETS:
            button = RoundedButton(
                self.preset_wrap, text=name, width=6, height=28, command=lambda n=name: self.apply_preset(n)
            )
            self.preset_wrap.add(button)
            self.preset_buttons[name] = button

        cond = ttk.LabelFrame(self, text="筛选条件", padding=8)
        cond.pack(fill="x", pady=(8, 0))
        self.filter_wrap = WrapRow(cond, gap=14)
        self.filter_wrap.pack(fill="x")

        self.start_box = self.add_field("起", lambda parent: DateBox(parent))
        self.end_box = self.add_field("止", lambda parent: DateBox(parent))
        self.kind_var = tk.StringVar(value="全部")
        self.kind_box = self.add_field(
            "类型", lambda parent: RoundedCombo(parent, textvariable=self.kind_var, values=["全部", *m.KINDS], width=6)
        )
        # 改了收入/支出，后面的细分必须回到「全部」，再按新类型重列选项并重查
        self.kind_box.bind_combo("<<ComboboxSelected>>", lambda _event: self.on_kind_changed())
        self.category_var = tk.StringVar(value="全部")
        self.category_box = self.add_field(
            "分类",
            lambda parent: RoundedCombo(parent, textvariable=self.category_var, width=12),
        )
        self.keyword_var = tk.StringVar()
        keyword_entry = self.add_field(
            "关键词", lambda parent: RoundedEntry(parent, textvariable=self.keyword_var, width=16, icon="🔍")
        )
        keyword_entry.bind_entry("<Return>", lambda _event: self.run_query())
        self.search_button = RoundedButton(self.filter_wrap, text="查询", style="primary", height=28, command=self.run_query)
        self.filter_wrap.add(self.search_button, gap=6)

        ttk.Label(
            cond,
            text="关键词会匹配备注、分类和支付方式。\n被删掉的种类，统计时并入「其他」（记录本身不变）。",
            style="Hint.TLabel",
            justify="left",
        ).pack(anchor="w", pady=(6, 0))

        # 操作按钮：同样会自动回车
        actions_wrap = WrapRow(self, gap=8)
        actions_wrap.pack(fill="x", pady=(8, 2))
        self.actions_wrap = actions_wrap
        self.edit_button = RoundedButton(actions_wrap, text="编辑选中（双击行）", height=28, command=self.edit_selected)
        self.delete_button = RoundedButton(actions_wrap, text="删除选中", height=28, command=self.delete_selected)
        self.export_button = RoundedButton(actions_wrap, text="导出结果 CSV", height=28, command=self.export_selected)
        for widget in (self.edit_button, self.delete_button, self.export_button):
            actions_wrap.add(widget)

        # 合计单独占一行：窗口窄的时候才不会和按钮挤在一起互相压住
        self.totals_var = tk.StringVar(value="还没有查询结果")
        self.totals_label = ttk.Label(self, textvariable=self.totals_var, style="Big.TLabel")
        self.totals_label.pack(anchor="w", pady=(0, 6))

        # 明细（左半）与分类统计（右半）并排；窗口太窄时自动改成上下排列
        self.split = ttk.Frame(self)
        self.split.pack(fill="both", expand=True)
        self.split.bind("<Configure>", self.on_split_resize)

        self.detail_frame = ttk.LabelFrame(self.split, text="明细", padding=4)
        self.table = TreeTable(
            self.detail_frame,
            columns=self.DETAIL_COLUMNS,
            widths=[self.DETAIL_BASE_WIDTHS[name] for name in self.DETAIL_COLUMNS],
            anchors=self.DETAIL_ANCHORS,
            height=12,
            on_activate=self.edit_selected,
        )
        self.table.pack(fill="both", expand=True)
        self._detail_after = None
        self._detail_fit_width = 0
        self.detail_frame.bind("<Configure>", self.on_detail_configure)

        self.category_frame = ttk.LabelFrame(self.split, text="分类统计", padding=4)
        self.stats_holder = ttk.Frame(self.category_frame)
        self.stats_holder.pack(fill="both", expand=True)
        self.stats_panels: dict[str, dict] = {}
        self.category_table: TreeTable | None = None
        self._stats_views: list[str] = []
        self._stats_note = ""
        self._chart_after = None
        self._chart_size = (0, 0)
        self.build_stats_panels()

        self._layout_mode: str | None = None
        self.apply_layout("side")

        start, end = query.resolve_preset(str(config.get("查询默认范围", "本月")))
        self.start_box.set(start or dt.date.today())
        self.end_box.set(end or dt.date.today())
        self._applied_preset = str(config.get("查询默认范围", "本月"))

    def add_field(self, label_text: str, factory):
        """在筛选区里放一个「标签 + 控件」小组，交给自动回车容器排版。"""
        box = ttk.Frame(self.filter_wrap)
        ttk.Label(box, text=label_text).pack(side="left", padx=(0, 4))
        widget = factory(box)
        widget.pack(side="left")
        self.filter_wrap.add(box)
        return widget

    # ---------------------------------------------------------------- 分类统计面板

    def stats_views(self) -> list[str]:
        """设置里选了哪些视图（按声明顺序，至少留一个表格兜底）。"""
        picked = list(config.get("分类统计显示", ["表格", "饼状图"]) or [])
        views = [view for view in charts.STATS_VIEWS if view in picked]
        return views or ["表格"]

    def build_stats_panels(self) -> None:
        """按选择的视图建面板；多个面板按「接近正方形」的网格自动分配区域。"""
        for panel in self.stats_panels.values():
            panel["frame"].destroy()
        self.stats_panels = {}
        self.category_table = None

        views = self.stats_views()
        self._stats_views = views
        columns = max(1, math.ceil(math.sqrt(len(views))))
        rows = max(1, math.ceil(len(views) / columns))
        # 先把上一次的网格权重清干净，否则单个面板会只占四分之一格
        for index in range(8):
            self.stats_holder.columnconfigure(index, weight=0, uniform="")
            self.stats_holder.rowconfigure(index, weight=0, uniform="")
        for index, view in enumerate(views):
            frame = ttk.LabelFrame(self.stats_holder, text=view, padding=2)
            frame.grid(row=index // columns, column=index % columns, sticky="nsew", padx=2, pady=2)
            if view == "表格":
                table = TreeTable(
                    frame,
                    columns=self.CATEGORY_COLUMNS,
                    widths=[self.CATEGORY_BASE_WIDTHS[name] for name in self.CATEGORY_COLUMNS],
                    anchors=self.CATEGORY_ANCHORS,
                    height=6,
                )
                table.pack(fill="both", expand=True)
                self.category_table = table
                self.stats_panels[view] = {"frame": frame, "table": table}
                frame.bind("<Configure>", lambda _event: self.fit_category_columns())
            else:
                canvas = tk.Canvas(frame, highlightthickness=0, background="#ffffff")
                canvas.pack(fill="both", expand=True)
                canvas.bind("<Configure>", self.on_chart_configure)
                self.stats_panels[view] = {"frame": frame, "canvas": canvas, "series": []}
        for column in range(columns):
            self.stats_holder.columnconfigure(column, weight=1, uniform="stats")
        for row in range(rows):
            self.stats_holder.rowconfigure(row, weight=1, uniform="stats")

    def on_chart_configure(self, event) -> None:
        """图形只在尺寸明显变化后重画一次（防抖），拖窗口时不做无用功。"""
        if abs(event.width - self._chart_size[0]) < self.LAYOUT_MIN_DELTA and abs(event.height - self._chart_size[1]) < self.LAYOUT_MIN_DELTA:
            return
        self._chart_size = (event.width, event.height)
        if self._chart_after is not None:
            try:
                self.after_cancel(self._chart_after)
            except (tk.TclError, ValueError):
                pass
        self._chart_after = self.after(150, self.redraw_charts)

    def redraw_charts(self) -> None:
        self._chart_after = None
        note = self._stats_note
        for view, panel in self.stats_panels.items():
            canvas = panel.get("canvas")
            if canvas is not None:
                charts.draw(view, canvas, panel.get("series", []), note)

    def render_stats(self, result: dict, category_rows: list[list[str]]) -> None:
        """把查询结果铺到各面板：表格填行（含全部），图形只画有权重的分类。"""
        self.render_count += 1
        by_category = result.get("by_category", [])
        series = charts.series_from(by_category)
        self._stats_note = charts.hidden_note(by_category)
        for view, panel in self.stats_panels.items():
            if "table" in panel:
                panel["table"].set_rows(category_rows)
            else:
                panel["series"] = series
        self.fit_category_columns()
        self.redraw_charts()

    # ---------------------------------------------------------------- 列宽与布局

    def on_detail_configure(self, event) -> None:
        """明细宽度变化才重算列宽，而且做防抖：拖窗口时不逐像素算。"""
        if abs(event.width - self._detail_fit_width) < self.LAYOUT_MIN_DELTA:
            return
        if self._detail_after is not None:
            try:
                self.after_cancel(self._detail_after)
            except (tk.TclError, ValueError):
                pass
        self._detail_after = self.after(120, self._apply_pending_fit)

    def _apply_pending_fit(self) -> None:
        self._detail_after = None
        self.fit_detail_columns()

    def flush_pending_layout(self) -> None:
        """把挂起的列宽/图形重算立刻做掉（切页、保存设置时用）。"""
        for attribute, callback in (("_detail_after", self.fit_detail_columns), ("_chart_after", self.redraw_charts)):
            after_id = getattr(self, attribute, None)
            if after_id is not None:
                try:
                    self.after_cancel(after_id)
                except (tk.TclError, ValueError):
                    pass
                setattr(self, attribute, None)
        self.fit_detail_columns()
        self.redraw_charts()

    def fit_detail_columns(self, available: int | None = None) -> None:
        """按当前可用宽度分配明细列宽：先压备注，再按比例压其它列，但不低于可读下限。

        这样窄窗口下「日期 / 时间 / 类型 / 分类 / 金额 / 支付方式 / 备注」表头仍能一行显示完。
        available 可以在自测里直接指定，用来验证最窄窗口下的表现。
        """
        tree = self.table.tree
        if available is None:
            available = self.detail_frame.winfo_width() - px(24)
        if available < px(200):
            return
        self._detail_fit_width = available
        widths = dict(self.DETAIL_BASE_WIDTHS)
        total = sum(widths.values())
        if total > available:  # 先让备注让步
            spare = min(widths["备注"] - self.DETAIL_MIN_WIDTHS["备注"], total - available)
            widths["备注"] -= spare
            total -= spare
        if total > available:  # 还不够就按比例压缩其余列
            shrinkable = {
                name: widths[name] - self.DETAIL_MIN_WIDTHS[name]
                for name in widths
                if name != "备注" and widths[name] > self.DETAIL_MIN_WIDTHS[name]
            }
            room = sum(shrinkable.values())
            if room > 0:
                need = total - available
                for name, capacity in shrinkable.items():
                    cut = min(capacity, round(need * capacity / room))
                    widths[name] -= cut
        for name in self.DETAIL_COLUMNS:
            tree.column(name, width=widths[name])

    def detail_columns_total(self) -> int:
        tree = self.table.tree
        return sum(int(tree.column(name, "width")) for name in self.DETAIL_COLUMNS)

    def fit_category_columns(self) -> None:
        """分类统计表格的列宽也自适应：面板窄了先压「分类」，再按比例压其它列。"""
        if self.category_table is None:
            return
        available = self.category_table.winfo_width() - px(20)
        if available < px(120):
            return
        widths = dict(self.CATEGORY_BASE_WIDTHS)
        total = sum(widths.values())
        if total > available:
            spare = min(widths["分类"] - self.CATEGORY_MIN_WIDTHS["分类"], total - available)
            widths["分类"] -= spare
            total -= spare
        if total > available:
            shrinkable = {
                name: widths[name] - self.CATEGORY_MIN_WIDTHS[name]
                for name in widths
                if name != "分类" and widths[name] > self.CATEGORY_MIN_WIDTHS[name]
            }
            room = sum(shrinkable.values())
            if room > 0:
                need = total - available
                for name, capacity in shrinkable.items():
                    widths[name] -= min(capacity, round(need * capacity / room))
        tree = self.category_table.tree
        for name in self.CATEGORY_COLUMNS:
            tree.column(name, width=widths[name])

    def on_split_resize(self, event) -> None:
        """窗口宽度检测：够宽就左右并排，窄了就上下排列。"""
        mode = "side" if event.width >= self.SIDE_BY_SIDE_MIN_WIDTH else "stacked"
        self.apply_layout(mode)

    def sync_layout(self) -> None:
        """按当前真实宽度纠正一次布局（漏掉 Configure 事件时也能自愈）。"""
        width = self.split.winfo_width()
        if width > 1:
            self.apply_layout("side" if width >= self.SIDE_BY_SIDE_MIN_WIDTH else "stacked")
        self.fit_detail_columns()

    def apply_layout(self, mode: str) -> None:
        if self._layout_mode != mode:
            self._layout_mode = mode
            self.detail_frame.grid_forget()
            self.category_frame.grid_forget()
            if mode == "side":  # 左半明细、右半分类统计，两边等宽
                self.split.columnconfigure(0, weight=1, uniform="half")
                self.split.columnconfigure(1, weight=1, uniform="half")
                self.split.rowconfigure(0, weight=1, minsize=px(200))
                self.split.rowconfigure(1, weight=0)
                self.detail_frame.grid(row=0, column=0, sticky="nsew", padx=(0, 6))
                self.category_frame.grid(row=0, column=1, sticky="nsew")
            else:  # 窗口窄了：明细在上、分类统计在下
                self.split.columnconfigure(0, weight=1, uniform="")
                self.split.columnconfigure(1, weight=0)
                self.split.rowconfigure(0, weight=3, minsize=px(160))
                self.split.rowconfigure(1, weight=2, minsize=px(120))
                self.detail_frame.grid(row=0, column=0, sticky="nsew", pady=(0, 6))
                self.category_frame.grid(row=1, column=0, sticky="nsew")
        self.fit_detail_columns()

    def layout_mode(self) -> str | None:
        return self._layout_mode

    # ---------------------------------------------------------------- 查询

    def apply_default_range(self) -> None:
        """按设置里的「查询默认范围」重设日期区间。"""
        name = str(config.get("查询默认范围", "本月"))
        try:
            start, end = query.resolve_preset(name)
        except ValueError:
            start, end = query.resolve_preset("本月")
        if start is None or end is None:  # 「全部」
            first, last = query.data_span()
            today = dt.date.today()
            start = first or today
            end = last or today
        self.start_box.set(start)
        self.end_box.set(end)

    def apply_preset(self, name: str) -> None:
        start, end = query.resolve_preset(name)
        if start and end:
            self.start_box.set(start)
            self.end_box.set(end)
        else:
            first, last = query.data_span()
            today = dt.date.today()
            self.start_box.set(first or today)
            self.end_box.set(last or today)
        self.run_query()

    def current_payload(self) -> dict:
        try:
            start = self.start_box.get()
            end = self.end_box.get()
        except (ValueError, m.RecordError) as exc:
            raise ValueError(f"日期看不懂：{exc}") from None
        payload = {
            "start": start.isoformat(),
            "end": end.isoformat(),
            "kind": self.kind_var.get(),
            "category": self.category_var.get(),
            "keyword": self.keyword_var.get().strip(),
        }
        limit = int(config.get("明细最多显示", 500) or 0)
        if limit > 0:
            payload["limit"] = limit
        return payload

    def run_query(self, silent: bool = False, payload: dict | None = None) -> None:
        try:
            payload = payload or self.current_payload()
        except (ValueError, m.RecordError) as exc:
            self.error(str(exc))
            return
        result = api.dispatch("query", payload)
        if not result.get("ok"):
            self.error(result.get("error", "查询失败"))
            return

        self.currency = config.load_settings().get("货币符号", "¥")
        self.last_payload = payload
        self.last_records = result["records"]
        self.last_by_category = result.get("by_category", [])

        rows = [
            [
                record["date"],
                record["time"],
                record["kind"],
                self.category_text(record),
                f"{record['amount']}",
                record["pay"],
                record["note"],
            ]
            for record in self.last_records
        ]
        self.table.set_rows(rows, payloads=self.last_records)

        totals = result["totals"]
        totals_text = (
            f"共 {totals['count']} 笔 ｜ 收入 {self.currency}{totals['income']}"
            f" ｜ 支出 {self.currency}{totals['expense']} ｜ 结余 {self.currency}{totals['balance']}"
        )
        if result.get("truncated"):
            totals_text += f"　（明细只显示最近 {len(self.last_records)} 笔）"
        self.totals_var.set(totals_text)

        grand = sum((Decimal(item["amount"]) for item in result["by_category"]), Decimal("0.00"))
        category_rows = []
        for item in result["by_category"]:
            share = (Decimal(item["amount"]) / grand * 100) if grand else Decimal("0.00")
            category_rows.append(
                [
                    item["kind"],
                    item["category"],
                    str(item["count"]),
                    f"{self.currency}{item['amount']}",
                    f"{share:.1f}%",
                ]
            )
        self.last_category_rows = category_rows
        self.query_count += 1
        self.render_stats(result, category_rows)
        self.sync_layout()

        if not silent:
            span = f"{result['filters']['start']} ~ {result['filters']['end']}"
            message = f"{span} 命中 {totals['count']} 笔"
            if result.get("truncated"):
                message += f"（明细只显示最近 {len(self.last_records)} 笔）"
            if result.get("skipped"):
                message += f"（有 {len(result['skipped'])} 行读不出来，已跳过）"
            self.app.set_status(message)

    def refresh_category_options(self) -> None:
        """细分选项跟着类型走：选了收入就只列收入的种类，选「全部」才把两边都列出来。"""
        categories = config.load_categories()
        kind = self.kind_var.get()
        if kind in m.KINDS:
            names = list(categories.get(kind, []))
        else:
            names = sorted({name for values in categories.values() for name in values})
        options = ["全部", *names]
        self.category_box.configure_values(options)
        # 兜底：当前选中的细分不在新选项里（比如种类刚被删掉），也回到「全部」
        if self.category_var.get() not in options:
            self.category_var.set("全部")

    def on_kind_changed(self) -> None:
        """类型一变，细分一律回到「全部」，再按新类型刷新选项并重查。"""
        self.category_var.set("全部")
        self.refresh_category_options()
        self.run_query()

    def on_show(self) -> None:
        self.currency = config.load_settings().get("货币符号", "¥")
        # 切回本页时按真实宽度再定一次布局（窗口可能是在别的页面上被拉伸/缩小的）
        self.sync_layout()
        self.refresh_category_options()
        self.run_query(silent=True)
        self.flush_pending_layout()

    def apply_settings(self) -> None:
        """设置页保存后：只有真正影响查询的改动才重新读数据，其它情况直接复用上次结果。"""
        self.currency = config.load_settings().get("货币符号", "¥")
        views = self.stats_views()
        views_changed = views != self._stats_views
        if views_changed:
            self.build_stats_panels()

        preset = str(config.get("查询默认范围", "本月"))
        preset_changed = preset != self._applied_preset
        if preset_changed:
            self._applied_preset = preset
            self.apply_default_range()

        self.refresh_category_options()
        try:
            payload = self.current_payload()
        except (ValueError, m.RecordError):
            payload = None

        if preset_changed or payload is None or payload != self.last_payload:
            self.run_query(silent=True)  # 范围/行数上限变了：重新读数据
        else:
            self.render_from_cache()  # 只是换了视图或改了别的设置：不重读文件

    def render_from_cache(self) -> None:
        """用上次的查询结果重画统计区（改视图时不必再读一遍文件）。"""
        self.render_stats({"by_category": self.last_by_category}, self.last_category_rows)
        self.flush_pending_layout()

    # ---------------------------------------------------------------- 编辑删除

    def category_text(self, record: dict) -> str:
        """明细里的分类文字：类别被删掉的记录会写成「其他（原 xxx）」。

        统计口径是「其他」，但把原名字标出来，方便你决定要不要回去改成别的种类。
        """
        effective = record.get("category_effective") or record.get("category", "")
        original = record.get("category", "")
        if effective == original:
            return original
        return f"{effective}（原 {original}）"

    def describe(self, record: dict) -> str:
        """用「日期 时间 类型 分类 金额 备注」来指代一笔记录（编号对用户隐藏）。"""
        parts = [
            record.get("date", ""),
            record.get("time", ""),
            record.get("kind", ""),
            record.get("category", ""),
            f"{self.currency}{record.get('amount', '')}",
        ]
        if record.get("note"):
            parts.append(record["note"])
        if record.get("pay"):
            parts.append(f"[{record['pay']}]")
        return " ".join(part for part in parts if part)

    def edit_selected(self) -> None:
        record = self.table.payload_at_selection()
        if not record:
            self.warn("请先在「明细」里选中一行（双击也能直接改）")
            return
        changes = RecordDialog(self, record).show()
        if not changes:
            return
        result = api.dispatch("update", {"id": record["id"], "changes": changes})
        if not result.get("ok"):
            self.error(result.get("error", "修改失败"))
            return
        updated = (result.get("updated") or [record])[0]
        self.app.notify_data_changed()
        self.app.set_status(f"已修改：{self.describe(updated)}")

    def delete_selected(self) -> None:
        payloads = self.table.selected_payloads()
        if not payloads:
            self.warn("请先在「明细」里选中要删除的行（可按住 Ctrl 或 Shift 多选）")
            return
        ids = [record["id"] for record in payloads]
        shown = "\n".join(self.describe(record) for record in payloads[:5])
        more = f"\n…… 还有 {len(payloads) - 5} 笔" if len(payloads) > 5 else ""
        if not self.confirm(
            f"确定删除这 {len(payloads)} 笔记录吗？\n\n{shown}{more}\n\n"
            "删除前的文件已经备份到「备份」目录，需要时可以手工找回。"
        ):
            return
        result = api.dispatch("delete", {"ids": ids})
        if not result.get("ok"):
            self.error(result.get("error", "删除失败"))
            return
        self.app.notify_data_changed()
        self.app.set_status(f"已删除 {result['deleted']} 笔记录")

    # ---------------------------------------------------------------- 导出

    def export_selected(self) -> None:
        if not self.last_records:
            self.warn("当前没有可导出的记录，先查一下再说")
            return
        paths.ensure_dirs()
        default_name = f"查询_{dt.datetime.now():%Y-%m-%d_%H%M%S}.csv"
        target = filedialog.asksaveasfilename(
            parent=self,
            title="导出查询结果",
            initialdir=str(paths.export_dir()),
            initialfile=default_name,
            defaultextension=".csv",
            filetypes=[("CSV 文件", "*.csv"), ("全部文件", "*.*")],
        )
        if not target:
            return
        payload = dict(self.last_payload)
        payload["name"] = Path(target).name
        payload["out_dir"] = str(Path(target).parent)
        result = api.dispatch("export", payload)
        if not result.get("ok"):
            self.error(result.get("error", "导出失败"))
            return
        self.notify(f"已导出 {result['记录数']} 条记录：\n{result['文件']}", "导出完成")
        self.app.set_status(f"已导出 {result['记录数']} 条到 {result['文件']}")
