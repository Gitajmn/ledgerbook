"""月度汇总：按日小计、分类小计，导出成 CSV。"""

from __future__ import annotations

import datetime as dt
import tkinter as tk
from tkinter import ttk

from .. import api, config, paths
from .common import RoundedButton, RoundedCombo, Tab, TreeTable, WrapRow, open_path


class SummaryTab(Tab):
    title = "月度汇总"

    def build(self) -> None:
        today = dt.date.today()
        self.currency = config.load_settings().get("货币符号", "¥")
        self.report: dict | None = None

        bar = WrapRow(self, gap=8)
        bar.pack(fill="x")
        bar.add(ttk.Label(bar, text="年份"))
        self.year_var = tk.StringVar(value=str(today.year))
        bar.add(
            RoundedCombo(
                bar,
                textvariable=self.year_var,
                width=8,
                values=[str(year) for year in range(today.year - 6, today.year + 2)],
                state="normal",
            )
        )
        bar.add(ttk.Label(bar, text="月份"))
        self.month_var = tk.StringVar(value=f"{today.month:02d}")
        bar.add(
            RoundedCombo(
                bar,
                textvariable=self.month_var,
                width=4,
                values=[f"{month:02d}" for month in range(1, 13)],
            )
        )
        self.generate_button = bar.add(
            RoundedButton(bar, text="生成汇总", style="primary", height=28, command=self.generate), gap=12
        )
        self.export_button = bar.add(RoundedButton(bar, text="导出月度汇总 CSV", height=28, command=self.export))
        self.open_button = bar.add(RoundedButton(bar, text="打开本月目录", height=28, command=self.open_month_dir))
        self.goto_button = bar.add(RoundedButton(bar, text="查看本月明细", height=28, command=self.goto_query))
        self.bar_wrap = bar

        self.totals_var = tk.StringVar(value="选择月份后点「生成汇总」")
        ttk.Label(self, textvariable=self.totals_var, style="Big.TLabel").pack(anchor="w", pady=(10, 4))

        day_frame = ttk.LabelFrame(self, text="按日汇总", padding=4)
        day_frame.pack(fill="both", expand=True)
        self.day_table = TreeTable(
            day_frame,
            columns=["日期", "星期", "笔数", "收入", "支出", "结余"],
            widths=[110, 60, 70, 120, 120, 120],
            anchors=["w", "center", "e", "e", "e", "e"],
            height=8,
        )
        self.day_table.pack(fill="both", expand=True)

        category_frame = ttk.LabelFrame(self, text="分类小计", padding=4)
        category_frame.pack(fill="both", expand=True, pady=(8, 0))
        self.category_table = TreeTable(
            category_frame,
            columns=["类型", "分类", "笔数", "金额", "占比"],
            widths=[70, 160, 70, 120, 90],
            anchors=["center", "w", "e", "e", "e"],
            height=6,
        )
        self.category_table.pack(fill="both", expand=True)

    # ---------------------------------------------------------------- 数据

    def _year_month(self) -> tuple[int, int]:
        year = int(str(self.year_var.get()).strip() or dt.date.today().year)
        month = int(str(self.month_var.get()).strip() or dt.date.today().month)
        if not 1 <= month <= 12:
            raise ValueError(f"月份要在 1-12 之间，收到：{month}")
        return year, month

    def generate(self, silent: bool = False) -> None:
        try:
            year, month = self._year_month()
        except ValueError as exc:
            self.error(str(exc))
            return
        result = api.dispatch("month", {"year": year, "month": month})
        if not result.get("ok"):
            self.error(result.get("error", "生成汇总失败"))
            return
        self.report = result
        self.currency = config.load_settings().get("货币符号", "¥")

        weekdays = "一二三四五六日"
        day_rows = []
        for line in result["days"]:
            day = dt.date.fromisoformat(line["date"])
            day_rows.append(
                [
                    line["date"],
                    f"周{weekdays[day.weekday()]}",
                    str(line["count"]),
                    f"{self.currency}{line['income']}",
                    f"{self.currency}{line['expense']}",
                    f"{self.currency}{line['balance']}",
                ]
            )
        self.day_table.set_rows(day_rows)

        totals = result["totals"]
        grand = sum(
            (abs(float(item["amount"])) for item in result["by_category"]),
            0.0,
        )
        category_rows = []
        for item in result["by_category"]:
            share = (float(item["amount"]) / grand * 100) if grand else 0.0
            category_rows.append(
                [
                    item["kind"],
                    item["category"],
                    str(item["count"]),
                    f"{self.currency}{item['amount']}",
                    f"{share:.1f}%",
                ]
            )
        self.category_table.set_rows(category_rows)

        self.totals_var.set(
            f"{totals['month']}：{totals['count']} 笔 ｜ 收入 {self.currency}{totals['income']}"
            f" ｜ 支出 {self.currency}{totals['expense']} ｜ 结余 {self.currency}{totals['balance']}"
        )
        if not silent:
            message = f"{totals['month']} 汇总已生成：{totals['count']} 笔"
            if result.get("skipped"):
                message += f"（有 {len(result['skipped'])} 行读不出来，已跳过）"
            self.app.set_status(message)

    def on_show(self) -> None:
        self.generate(silent=True)

    def apply_settings(self) -> None:
        self.currency = config.load_settings().get("货币符号", "¥")
        self.generate(silent=True)

    # ---------------------------------------------------------------- 操作

    def export(self) -> None:
        try:
            year, month = self._year_month()
        except ValueError as exc:
            self.error(str(exc))
            return
        result = api.dispatch("month_export", {"year": year, "month": month})
        if not result.get("ok"):
            self.error(result.get("error", "导出失败"))
            return
        self.notify(
            f"{result['month']} 汇总已写入：\n{result['summary']}\n{result['category']}",
            "导出完成",
        )
        self.app.set_status(f"已导出 {result['month']} 月度汇总")
        self.generate(silent=True)

    def open_month_dir(self) -> None:
        try:
            year, month = self._year_month()
        except ValueError as exc:
            self.error(str(exc))
            return
        directory = paths.month_dir(year, month)
        directory.mkdir(parents=True, exist_ok=True)
        open_path(directory)

    def goto_query(self) -> None:
        try:
            year, month = self._year_month()
        except ValueError as exc:
            self.error(str(exc))
            return
        first = dt.date(year, month, 1)
        last = (first.replace(day=28) + dt.timedelta(days=4)).replace(day=1) - dt.timedelta(days=1)
        query_tab = self.app.tabs.get("查询统计")
        if query_tab is None:
            return
        query_tab.start_box.set(first)
        query_tab.end_box.set(last)
        query_tab.kind_var.set("全部")
        query_tab.category_var.set("全部")
        query_tab.keyword_var.set("")
        self.app.notebook.select(query_tab)
        query_tab.run_query()
