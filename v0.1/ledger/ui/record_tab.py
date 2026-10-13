"""记一笔：录入界面。

刻意**不设**「撤销上一笔」按钮：那和「查询统计」里的删除是同一件事，
两个入口只会让人犹豫。记错了就到查询页双击改掉或删掉。
"""

from __future__ import annotations

import datetime as dt
import tkinter as tk
from tkinter import ttk

from .. import api, config, model as m
from .common import DateBox, RoundedButton, RoundedCombo, RoundedEntry, Tab, TreeTable


class RecordTab(Tab):
    title = "记一笔"

    def build(self) -> None:
        self.settings = config.load_settings()
        self.currency = self.settings.get("货币符号", "¥")
        self.session_records: list[dict] = []

        form = ttk.LabelFrame(self, text="新增一笔", padding=12)
        form.pack(fill="x", anchor="n")

        ttk.Label(form, text="日期").grid(row=0, column=0, sticky="e", padx=(0, 6), pady=4)
        self.date_box = DateBox(form)
        self.date_box.grid(row=0, column=1, sticky="w", pady=4)
        ttk.Label(form, text="可改日期补记以前的账", style="Hint.TLabel").grid(row=0, column=2, sticky="w", padx=8)

        ttk.Label(form, text="时间").grid(row=1, column=0, sticky="e", padx=(0, 6), pady=4)
        self.time_var = tk.StringVar()
        RoundedEntry(form, textvariable=self.time_var, width=12).grid(row=1, column=1, sticky="w", pady=4)
        ttk.Label(form, text="留空就用此刻时间", style="Hint.TLabel").grid(row=1, column=2, sticky="w", padx=8)

        ttk.Label(form, text="类型").grid(row=2, column=0, sticky="e", padx=(0, 6), pady=4)
        self.kind_var = tk.StringVar(value=self.settings.get("默认记账类型", m.KIND_EXPENSE))
        kinds = ttk.Frame(form)
        kinds.grid(row=2, column=1, sticky="w", pady=4)
        for kind in m.KINDS:
            ttk.Radiobutton(
                kinds, text=kind, value=kind, variable=self.kind_var, command=self.refresh_categories
            ).pack(side="left", padx=(0, 12))

        ttk.Label(form, text="金额").grid(row=3, column=0, sticky="e", padx=(0, 6), pady=4)
        self.amount_var = tk.StringVar()
        self.amount_entry = RoundedEntry(form, textvariable=self.amount_var, width=14)
        self.amount_entry.grid(row=3, column=1, sticky="w", pady=4)
        self.currency_label = ttk.Label(form, text=f"元（{self.currency}）", style="Hint.TLabel")
        self.currency_label.grid(row=3, column=2, sticky="w", padx=8)

        ttk.Label(form, text="分类").grid(row=4, column=0, sticky="e", padx=(0, 6), pady=4)
        self.category_var = tk.StringVar()
        self.category_box = RoundedCombo(form, textvariable=self.category_var, width=20, state="normal")
        self.category_box.grid(row=4, column=1, sticky="w", pady=4)
        ttk.Label(form, text="可在「设置」里增删分类", style="Hint.TLabel").grid(row=4, column=2, sticky="w", padx=8)

        ttk.Label(form, text="支付方式").grid(row=5, column=0, sticky="e", padx=(0, 6), pady=4)
        self.pay_var = tk.StringVar()
        self.pay_box = RoundedCombo(form, textvariable=self.pay_var, width=20, state="normal")
        self.pay_box.grid(row=5, column=1, sticky="w", pady=4)

        ttk.Label(form, text="备注").grid(row=6, column=0, sticky="e", padx=(0, 6), pady=4)
        self.note_var = tk.StringVar()
        self.note_entry = RoundedEntry(form, textvariable=self.note_var, width=40)
        self.note_entry.grid(row=6, column=1, columnspan=2, sticky="we", pady=4)

        buttons = ttk.Frame(form)
        buttons.grid(row=7, column=0, columnspan=3, sticky="w", pady=(8, 0))
        RoundedButton(buttons, text="保存并继续（Ctrl+Enter）", style="primary", height=32, command=self.save).pack(
            side="left"
        )
        RoundedButton(buttons, text="清空", height=32, command=self.clear_inputs).pack(side="left", padx=8)
        form.columnconfigure(1, weight=1)

        self.recent_frame = ttk.LabelFrame(self, text="本次打开后已记", padding=8)
        ttk.Label(
            self.recent_frame,
            text="记错了不用慌：到「查询统计」里选中那一行，双击改掉，或选中后删除即可。"
            "不想要这个列表可以在「设置」里关掉。",
            style="Hint.TLabel",
        ).pack(anchor="w")
        # 用表格控件而不是原生 Listbox：原生 Listbox 的字体不受字号设置控制（会显得特别大），
        # 而且是方角边框，和整页的圆角风格不搭。
        self.recent_list = TreeTable(
            self.recent_frame,
            columns=["已记"],
            widths=[600],
            anchors=["w"],
            height=6,
        )
        self.recent_list.pack(fill="both", expand=True, pady=(6, 0))

        for widget in (self.amount_entry, self.category_box, self.pay_box, self.note_entry):
            widget.bind("<Return>", lambda _event: self.save())
            widget.bind("<Control-Return>", lambda _event: self.save())
        self.refresh_fields()
        self.amount_entry.focus_set()

    # ---------------------------------------------------------------- 数据

    def refresh_fields(self) -> None:
        """从配置刷新下拉框内容、货币符号，以及「本次已记」列表的可见性。"""
        self.settings = config.load_settings()
        self.currency = self.settings.get("货币符号", "¥")
        self.currency_label.configure(text=f"元（{self.currency}）")
        self.pay_box.configure_values(self.settings.get("支付方式", []))
        if not self.pay_var.get():
            self.pay_var.set(self.settings.get("默认支付方式", ""))
        self.refresh_categories()
        if self.settings.get("显示本次已记", True):
            self.recent_frame.pack(fill="both", expand=True, pady=(10, 0))
        else:
            self.recent_frame.pack_forget()

    def refresh_categories(self) -> None:
        categories = config.load_categories().get(self.kind_var.get(), [])
        self.category_box.configure_values(categories)
        if self.category_var.get() not in categories:
            self.category_var.set(categories[0] if categories else "")

    def on_show(self) -> None:
        self.refresh_fields()

    def apply_settings(self) -> None:
        """设置页保存后：默认类型、货币、列表可见性等都跟着变。"""
        self.settings = config.load_settings()
        self.kind_var.set(self.settings.get("默认记账类型", m.KIND_EXPENSE))
        self.refresh_fields()

    # ---------------------------------------------------------------- 操作

    def clear_inputs(self) -> None:
        self.amount_var.set("")
        self.note_var.set("")
        self.time_var.set("")
        self.amount_entry.focus_set()

    def save(self) -> None:
        payload = {
            "date": self.date_box.var.get().strip(),
            "time": self.time_var.get().strip(),
            "kind": self.kind_var.get(),
            "category": self.category_var.get().strip(),
            "amount": self.amount_var.get().strip(),
            "pay": self.pay_var.get().strip(),
            "note": self.note_var.get().strip(),
        }
        result = api.dispatch("add", payload)
        if not result.get("ok"):
            self.error(result.get("error", "保存失败"))
            self.amount_entry.focus_set()
            return

        record = result["record"]
        pay = f"（{record['pay']}）" if record.get("pay") else ""
        self.app.set_status(
            f"已记入 {record['date']} {record['time']}：{record['kind']} {record['category']} "
            f"{self.currency}{record['amount']}{pay}"
        )
        self.session_records.insert(0, record)
        self.recent_list.set_rows(
            [
                [
                    f"{record['date']} {record['time']}   {record['kind']}   {record['category']}   "
                    f"{self.currency}{record['amount']}   {record['pay']}   {record['note']}"
                ],
                *self.recent_list.rows(),
            ]
        )
        if config.get("保存后清空", True):
            self.amount_var.set("")
            self.note_var.set("")
            self.time_var.set("")
        else:
            # 不清空时把金额全选，直接输入就能替换，方便连记同金额
            self.amount_entry.select_range(0, "end")
        self.amount_entry.focus_set()
        self.app.notify_data_changed()
