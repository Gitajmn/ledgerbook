"""导入 / 导出面板：嵌在「设置」页的「导入 / 导出」这一页里。

按用户要求：
- 导出区第一行先显示「导出路径为 …」，**点这行就能选目录**，右边保留「打开导出目录」；
- 导出带范围：从（默认上月同日）到（默认今天），两个日期点开都是万年历，从当前这天开始选；
- 导入区不重复放「冲突策略」——策略只在上面的设置项里有一份，这里只显示当前用的是哪个；
- 预览只有一张表：能导入的行和读不出来的行放一起，用「状态」列区分，不再分成两张表。
"""

from __future__ import annotations

import datetime as dt
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from .. import api, config, model as m, paths, transfer
from . import common
from .common import DateButton, RoundedButton, Tooltip, TreeTable, WrapRow, open_path, pick_font


class TransferPanel(ttk.Frame):
    """导入 / 导出面板（不是独立标签页，挂在设置页里）。"""

    def __init__(self, master, app, on_status=None) -> None:
        super().__init__(master)
        self.app = app
        self.on_status = on_status or (lambda _message: None)
        self.source_path: str | None = None
        self.parsed: dict | None = None
        self.errors: list[dict] = []
        self.build()

    # ---------------------------------------------------------------- 构建

    def build(self) -> None:
        self.build_export()
        self.build_import()

    def build_export(self) -> None:
        frame = ttk.LabelFrame(self, text="导出", padding=10)
        frame.pack(fill="x")

        path_line = WrapRow(frame, gap=10)
        path_line.pack(fill="x")
        self.export_path_title = "导出路径为"
        path_line.add(ttk.Label(path_line, text=self.export_path_title))
        self.export_path_var = tk.StringVar()
        self.export_path_label = tk.Label(
            path_line,
            textvariable=self.export_path_var,
            foreground="#1a4fbf",
            cursor="hand2",
            font=common.ui_font(underline=True),
        )
        path_line.add(self.export_path_label, gap=4)
        self.export_path_label.bind("<Button-1>", lambda _event: self.choose_export_dir())
        Tooltip(self.export_path_label, "点击可以选择导出到哪个文件夹")
        self.open_export_button = RoundedButton(path_line, text="打开导出目录", height=28, command=self.open_export_dir)
        path_line.add(self.open_export_button, gap=4)
        self.path_wrap = path_line

        today = dt.date.today()
        range_line = WrapRow(frame, gap=8, row_gap=4)
        range_line.pack(fill="x", pady=(8, 0))
        range_line.add(ttk.Label(range_line, text="导出范围"))
        range_line.add(ttk.Label(range_line, text="从"), gap=4)
        self.start_date = DateButton(range_line, value=m.month_before(today), on_change=lambda _d: self.refresh_range_hint())
        range_line.add(self.start_date, gap=4)
        range_line.add(ttk.Label(range_line, text="到"), gap=4)
        self.end_date = DateButton(range_line, value=today, on_change=lambda _d: self.refresh_range_hint())
        range_line.add(self.end_date, gap=10)
        self.export_button = RoundedButton(range_line, text="导出", style="primary", height=28, command=self.export_range)
        range_line.add(self.export_button)
        self.range_wrap = range_line

        self.range_hint_var = tk.StringVar()
        ttk.Label(frame, textvariable=self.range_hint_var, style="Hint.TLabel").pack(anchor="w", pady=(8, 0))

    def build_import(self) -> None:
        frame = ttk.LabelFrame(self, text="导入", padding=10)
        frame.pack(fill="both", expand=True, pady=(10, 0))

        picker = WrapRow(frame, gap=8)
        picker.pack(fill="x")
        self.choose_file_button = RoundedButton(picker, text="选择文件…", height=28, command=self.choose_file)
        picker.add(self.choose_file_button)
        self.file_var = tk.StringVar(value="还没有选择文件")
        picker.add(ttk.Label(picker, textvariable=self.file_var, style="Hint.TLabel"), gap=4)
        self.picker_wrap = picker

        options = WrapRow(frame, gap=12)
        options.pack(fill="x", pady=(8, 4))
        self.strategy_hint_var = tk.StringVar()
        options.add(ttk.Label(options, textvariable=self.strategy_hint_var))
        self.auto_var = tk.BooleanVar(value=False)
        options.add(
            ttk.Checkbutton(options, text="自动新增配置里没有的种类（按行内类型归入）", variable=self.auto_var)
        )
        self.options_wrap = options

        self.preview_var = tk.StringVar(
            value="支持 .csv / .tsv / .txt；自动识别编码、分隔符和列名。选好文件后会先预览，确认无误再执行。"
        )
        ttk.Label(frame, textvariable=self.preview_var, style="Big.TLabel").pack(anchor="w", pady=(4, 4))

        preview_frame = ttk.LabelFrame(frame, text="预览（最多 50 行；读不出来的行也在同一张表里，状态列会写原因）", padding=4)
        preview_frame.pack(fill="both", expand=True)
        self.preview_table = TreeTable(
            preview_frame,
            columns=["行号", "状态", "日期", "时间", "类型", "分类", "金额", "支付方式", "备注"],
            widths=[52, 150, 92, 58, 56, 92, 88, 88, 220],
            anchors=["e", "w", "w", "center", "center", "w", "e", "w", "w"],
            height=9,
        )
        self.preview_table.pack(fill="both", expand=True)

        footer = WrapRow(frame, gap=8, row_gap=2)
        footer.pack(fill="x", pady=(8, 0))
        self.import_button = RoundedButton(footer, text="执行导入", style="primary", height=28, command=self.do_import)
        footer.add(self.import_button)
        self.export_errors_button = RoundedButton(
            footer, text="导出读不出来的行 CSV", height=28, command=self.export_errors
        )
        footer.add(self.export_errors_button)
        footer.add(ttk.Label(footer, text="导入前会自动把受影响的月份备份到「备份」目录。", style="Hint.TLabel"))
        self.footer_wrap = footer

    # ---------------------------------------------------------------- 刷新

    def refresh(self) -> None:
        """设置变了以后刷新显示（导出路径、当前策略）。"""
        self.export_path_var.set(str(api.export_dir()))
        strategy = str(config.get("导入默认策略", transfer.STRATEGY_SKIP))
        self.strategy_hint_var.set(f"冲突策略：{strategy}（在上面的设置里改，这里不重复放选项）")
        self.refresh_range_hint()

    def refresh_range_hint(self) -> None:
        start, end = self.start_date.get(), self.end_date.get()
        if start > end:
            self.range_hint_var.set("提示：起始日期晚于结束日期，导出时会自动对调。")
        else:
            days = (end - start).days + 1
            self.range_hint_var.set(f"点日期会弹出万年历，从当前这天开始选；这次导出 {start} 到 {end}，共 {days} 天。")

    # ---------------------------------------------------------------- 导出

    def choose_export_dir(self) -> None:
        current = api.export_dir()
        current.mkdir(parents=True, exist_ok=True)
        picked = filedialog.askdirectory(parent=self, title="选择导出目录", initialdir=str(current))
        if not picked:
            return
        result = api.dispatch("save_settings", {"settings": {"导出目录": picked}})
        if not result.get("ok"):
            self.on_status(result.get("error", "保存导出目录失败"))
            return
        self.refresh()
        self.on_status(f"导出目录已改为 {picked}")

    def open_export_dir(self) -> None:
        directory = api.export_dir()
        directory.mkdir(parents=True, exist_ok=True)
        open_path(directory)

    def export_range(self) -> None:
        start, end = self.start_date.get(), self.end_date.get()
        if start > end:
            start, end = end, start
            self.start_date.set(start, notify=False)
            self.end_date.set(end, notify=False)
            self.refresh_range_hint()
        name = f"导出_{start}_{end}_{dt.datetime.now():%H%M%S}.csv"
        result = api.dispatch(
            "export",
            {"start": start.isoformat(), "end": end.isoformat(), "name": name, "prefix": "导出"},
        )
        if not result.get("ok"):
            self.on_status(result.get("error", "导出失败"))
            return
        message = f"已导出 {result['记录数']} 条到\n{result['文件']}"
        messagebox.showinfo("导出完成", message, parent=self)
        self.on_status(f"已导出 {result['记录数']} 条到 {result['文件']}")

    # ---------------------------------------------------------------- 导入

    def choose_file(self) -> None:
        target = filedialog.askopenfilename(
            parent=self,
            title="选择要导入的表格文件",
            initialdir=str(paths.root()),
            filetypes=[("表格文件", "*.csv *.tsv *.txt"), ("全部文件", "*.*")],
        )
        if target:
            self.preview(target)

    def preview(self, target: str) -> None:
        result = api.dispatch("import_preview", {"path": target})
        if not result.get("ok"):
            self.on_status(result.get("error", "读取文件失败"))
            return
        self.source_path = target
        self.parsed = result
        self.errors = result["错误"]
        self.file_var.set(Path(target).name)

        rows = [
            [
                str(row["行号"]),
                "可导入",
                row["日期"],
                row["时间"],
                row["类型"],
                row["分类"],
                row["金额"],
                row["支付方式"],
                row["备注"],
            ]
            for row in result["预览"]
        ]
        rows.extend(
            [
                str(item.get("行号", "")),
                f"读不出来：{item.get('原因', '')}",
                "",
                "",
                "",
                "",
                "",
                "",
                str(item.get("内容", "")),
            ]
            for item in self.errors
        )
        rows.sort(key=lambda row: int(row[0]) if str(row[0]).isdigit() else 0)
        self.preview_table.set_rows(rows)

        message = f"共 {result['总行数']} 行：可导入 {result['可导入']} 行，读不出来 {result['失败']} 行"
        if result["缺失分类"]:
            message += f"；配置里没有的种类：{'、'.join(result['缺失分类'])}"
        self.preview_var.set(message)

    def do_import(self) -> None:
        if not self.source_path:
            messagebox.showwarning("还不能导入", "请先点「选择文件…」选一个要导入的文件", parent=self)
            return
        strategy = str(config.get("导入默认策略", transfer.STRATEGY_SKIP))
        auto = bool(self.auto_var.get())
        if not messagebox.askyesno(
            "请确认",
            f"按「{strategy}」导入 {Path(self.source_path).name} 吗？\n{self.preview_var.get()}\n\n"
            "导入前会自动备份受影响的月份。",
            parent=self,
        ):
            return
        result = api.dispatch(
            "import_apply",
            {"path": self.source_path, "strategy": strategy, "auto_add_categories": auto},
        )
        if not result.get("ok"):
            self.on_status(result.get("error", "导入失败"))
            messagebox.showerror("导入失败", result.get("error", "导入失败"), parent=self)
            return

        lines = [
            f"策略：{result['策略']}",
            f"成功 {result['成功']} 条（其中覆盖同编号 {result['覆盖']} 条）",
            f"跳过 {result['跳过']} 条",
            f"读不出来 {result['失败']} 条",
        ]
        if result.get("备份"):
            lines.append("已备份：" + "；".join(result["备份"]))
        messagebox.showinfo("导入完成", "\n".join(lines), parent=self)
        self.errors = result.get("错误", [])
        self.app.notify_data_changed()
        self.on_status(f"导入完成：成功 {result['成功']} 条，跳过 {result['跳过']} 条")
        self.preview(self.source_path)

    def export_errors(self) -> None:
        if not self.errors:
            messagebox.showwarning("没有内容", "这次没有读不出来的行", parent=self)
            return
        result = api.dispatch("export_errors", {"errors": self.errors})
        if not result.get("ok"):
            self.on_status(result.get("error", "导出失败"))
            return
        messagebox.showinfo("导出完成", f"读不出来的行已写入：\n{result['file']}", parent=self)
        self.on_status(f"错误清单已写入 {result['file']}")
