"""设置区：所有「低频、可配置、可能误触」的东西都放这里。

页面按图片那种「分页 + 分节」的排版来做：
- 顶部一行大字「设置」和「恢复全部默认」；
- 下面一条扁平标签条（当前页加粗并带下划线），一页只显示相关内容，避免一屏塞满、找不到东西；
- 页内每一项是「名称 + 控件 + 一句说明」的紧凑一行，长内容才换行。

约定（后续加功能请遵守）：
1. 每个设置项在 `ledger/config.py` 的 SETTING_SPECS 里声明一次（分组 = 所在页），
   本页会自动为它生成控件、自动持久化，`selftest.py` 也会自动覆盖到；
2. 每个设置项必须有默认值：删掉 配置\\设置.json 也照样能用；
3. 主线三页（记一笔 / 查询统计 / 月度汇总）只放日常操作，开关类的东西一律进本页；
4. 保存即生效（调用 app.apply_settings_changed()），不需要重启程序。
"""

from __future__ import annotations

import tkinter as tk
from tkinter import filedialog, ttk

from .. import api, config, model as m, paths, query, transfer
from . import common
from .common import (
    CategoryDialog,
    FlatTabs,
    ItemListDialog,
    RoundedButton,
    RoundedCombo,
    RoundedEntry,
    ScrollFrame,
    SortableList,
    Tab,
    Tooltip,
    WrapRow,
    open_path,
    pick_font,
)
from .transfer_panel import TransferPanel

#: 设置页的分页顺序。前五个名字必须和 SETTING_SPECS 里的「分组」一致，
#: 这样声明了新设置项就会自动出现在对应页上；后面两个是纯界面页。
PAGE_ORDER = ("通用", "记一笔", "查询", "种类与支付方式", "导入 / 导出", "数据与备份", "关于")

#: 设置页里「种类」方框的排列顺序（收入在前，与页面文案一致）
KIND_ORDER = (m.KIND_INCOME, m.KIND_EXPENSE)


class SettingsTab(Tab):
    title = "设置"

    # ---------------------------------------------------------------- 构建

    def build(self) -> None:
        self.status_var = tk.StringVar(value="改完即生效，不用重启程序")
        self.vars: dict[str, tk.Variable] = {}
        self.widgets: dict[str, tk.Widget] = {}
        self.comboboxes: dict[str, ttk.Combobox] = {}
        self.multi_vars: dict[str, dict[str, tk.BooleanVar]] = {}
        self.multi_buttons: dict[str, RoundedButton] = {}
        self.categories: dict[str, list[str]] = config.load_categories()
        self.pay_methods: list[str] = list(config.load_settings().get("支付方式", []))

        header = ttk.Frame(self)
        header.pack(fill="x", pady=(0, 6))
        ttk.Label(header, text="设置", font=common.ui_font(delta=2, bold=True)).pack(side="left")
        ttk.Label(
            header,
            text="　平时用不到的开关和偏好都在这里；上面几个标签页只放日常操作。",
            style="Hint.TLabel",
        ).pack(side="left")
        RoundedButton(header, text="恢复全部默认", command=self.reset_all).pack(side="right")

        self.tabs = FlatTabs(self, on_change=self.show_page)
        self.tabs.pack(fill="x")
        ttk.Separator(self, orient="horizontal").pack(fill="x", pady=(2, 8))

        self.scroll = ScrollFrame(self)
        self.scroll.pack(fill="both", expand=True)
        self.page_frames: dict[str, ttk.Frame] = {}
        for title in PAGE_ORDER:
            page = ttk.Frame(self.scroll.body, padding=(2, 6, 12, 12))
            self.page_frames[title] = page
            self.tabs.add(title, title)
            self.fill_page(title, page)

        ttk.Label(self, textvariable=self.status_var, style="Hint.TLabel").pack(anchor="w")
        self.show_page(PAGE_ORDER[0])
        self.refresh_values()

    def fill_page(self, title: str, page: ttk.Frame) -> None:
        specs = [spec for spec in config.SETTING_SPECS if spec.group == title and not spec.hidden]
        if specs:
            holder = ttk.Frame(page)
            holder.pack(fill="x")
            for spec in specs:
                self.render_setting(holder, spec)
        if title == "种类与支付方式":
            self.fill_items_page(page)
        elif title == "导入 / 导出":
            self.fill_transfer_page(page)
        elif title == "数据与备份":
            self.fill_dirs(page)
        elif title == "关于":
            self.fill_about(page)

    def fill_transfer_page(self, page: ttk.Frame) -> None:
        """导入 / 导出合并进设置：整块面板挂在「导入 / 导出」这一页里。"""
        self.transfer = TransferPanel(page, self.app, on_status=self.status_var.set)
        self.transfer.pack(fill="both", expand=True, pady=(10, 0))
        self.transfer.refresh()

    def show_page(self, title: str) -> None:
        """只显示当前页（其余页整个不显示），这就是「减少冗余信息」的做法。"""
        if title not in self.page_frames:  # 页名写错时退回第一页，别显示空白
            title = PAGE_ORDER[0]
        for name, frame in self.page_frames.items():
            if name == title:
                frame.pack(fill="both", expand=True)
            else:
                frame.pack_forget()
        if self.tabs.active() != title:
            self.tabs.select(title)
        self.scroll.canvas.yview_moveto(0)

    # ---------------------------------------------------------------- 单个设置项

    def render_setting(self, parent, spec: config.SettingSpec) -> None:
        """一个设置项 = 一行：左边「标题 + 说明」，右边圆角控件（可带单位后缀）。

        窗口窄了会自动变成上下两行（左边文字、右边控件各占一行），不会把控件挤没。
        """
        row = ttk.Frame(parent)
        row.pack(fill="x", pady=common.px(6))
        text_box = ttk.Frame(row)
        text_box.pack(side="left", fill="x", expand=True, anchor="w")
        ttk.Label(text_box, text=spec.label, font=common.ui_font(bold=True)).pack(anchor="w")
        if spec.hint:
            ttk.Label(text_box, text=spec.hint, style="Hint.TLabel").pack(anchor="w", pady=(common.px(1), 0))

        side = ttk.Frame(row)
        side.pack(side="right", anchor="e", padx=(common.px(10), 0))
        wrap = WrapRow(side, gap=common.px(6), row_gap=common.px(2))
        wrap.pack(fill="x")
        current = config.get(spec.key, spec.default)

        if spec.kind == "bool":
            var: tk.Variable = tk.BooleanVar(value=bool(current))
            widget: tk.Widget = ttk.Checkbutton(
                wrap, variable=var, command=lambda s=spec, v=var: self.save_one(s, v.get())
            )
        elif spec.kind == "multi":
            # 多选：勾完点「保存」才生效（这类改动要重建统计面板，别每勾一下就重画）
            var = tk.StringVar(value="|".join(current if isinstance(current, list) else []))
            holder = ttk.Frame(wrap)
            boxes: dict[str, tk.BooleanVar] = {}
            picked = list(current if isinstance(current, list) else [])
            for choice in spec.choices:
                flag = tk.BooleanVar(value=choice in picked)
                boxes[str(choice)] = flag
                ttk.Checkbutton(
                    holder, text=str(choice), variable=flag, command=lambda s=spec: self.mark_multi_dirty(s)
                ).pack(side="left", padx=(0, common.px(10)))
            button = RoundedButton(
                holder,
                text="保存",
                width=6,
                height=26,
                style="primary",
                command=lambda s=spec: self.save_one(s, self.multi_picked(s)),
            )
            button.pack(side="left", padx=(common.px(8), 0))
            widget = holder
            self.multi_vars[spec.key] = boxes
            self.multi_buttons[spec.key] = button
        elif spec.kind == "choice":
            var = tk.StringVar(value=self.choice_label(spec, current))
            widget = RoundedCombo(wrap, textvariable=var, width=16, height=32)
            self.comboboxes[spec.key] = widget  # type: ignore[assignment]
            widget.bind_combo("<<ComboboxSelected>>", lambda _event, s=spec, v=var: self.save_one(s, v.get()))
        elif spec.kind == "int":
            var = tk.StringVar(value=str(current))
            widget = ttk.Spinbox(wrap, textvariable=var, width=8, from_=0, to=1000000, increment=50)
            widget.bind("<Return>", lambda _event, s=spec, v=var: self.save_one(s, v.get()))
            widget.bind("<FocusOut>", lambda _event, s=spec, v=var: self.save_one(s, v.get()))
        else:
            var = tk.StringVar(value=str(current))
            widget = RoundedEntry(wrap, textvariable=var, width=16, height=32)
            widget.bind_entry("<Return>", lambda _event, s=spec, v=var: self.save_one(s, v.get()))
            widget.bind_entry("<FocusOut>", lambda _event, s=spec, v=var: self.save_one(s, v.get()))

        wrap.add(widget)
        if spec.unit:
            wrap.add(ttk.Label(wrap, text=spec.unit, style="Hint.TLabel"))
        self.vars[spec.key] = var
        self.widgets[spec.key] = widget

    def choice_values(self, spec: config.SettingSpec) -> list[str]:
        if spec.choices:
            return [str(item) for item in spec.choices]
        source = spec.choices_source
        if source == "支付方式":
            return list(config.load_settings().get("支付方式", []))
        if source == "预设范围":
            return list(query.PRESETS)
        if source == "导入策略":
            return list(transfer.STRATEGIES)
        return []

    def choice_label(self, spec: config.SettingSpec, value) -> str:
        """下拉框里要显示的文案。数字类预设把 0 显示成「不限」。"""
        text = str(value)
        if spec.int_choice and text.strip() in ("0", ""):
            return "不限"
        if spec.choices and text not in spec.choices:
            # 老配置里可能是 0/123 这种自由数值，尽量显示成能看懂的样子
            return text if not spec.int_choice else ("不限" if text == "0" else text)
        return text

    def multi_picked(self, spec: config.SettingSpec) -> list[str]:
        """当前勾了哪些（按声明顺序）。"""
        boxes = self.multi_vars.get(spec.key, {})
        return [choice for choice in spec.choices if boxes.get(str(choice)) and boxes[str(choice)].get()]

    def multi_saved(self, spec: config.SettingSpec) -> list[str]:
        saved = config.get(spec.key, spec.default)
        return [choice for choice in spec.choices if choice in (saved if isinstance(saved, list) else [])]

    def mark_multi_dirty(self, spec: config.SettingSpec) -> None:
        """勾选框变了就把「保存」按钮点亮，没变就置灰。"""
        button = self.multi_buttons.get(spec.key)
        if button is None:
            return
        if self.multi_picked(spec) == self.multi_saved(spec):
            button.state(["disabled"])
            self.status_var.set(f"「{spec.label}」没有改动")
        else:
            button.state(["!disabled"])
            self.status_var.set(f"「{spec.label}」有改动，点「保存」生效")

    def refresh_values(self) -> None:
        """把磁盘上的当前设置（含被纠正后的值）回填到界面。"""
        settings = config.load_settings()
        self.pay_methods = list(settings.get("支付方式", []))
        if hasattr(self, "pay_list"):
            self.pay_list.set_items(self.pay_methods)
        self.refresh_category_boxes()
        if hasattr(self, "transfer"):
            self.transfer.refresh()
        if hasattr(self, "backup_path_var"):
            self.backup_path_var.set(str(api.backup_dir()))
        for spec in config.SETTING_SPECS:
            value = settings.get(spec.key, spec.default)
            var = self.vars.get(spec.key)
            if var is not None:
                if spec.kind == "bool":
                    var.set(bool(value))
                elif spec.kind == "choice":
                    var.set(self.choice_label(spec, value))
                else:
                    var.set(str(value))
            box = self.comboboxes.get(spec.key)
            if box is not None:
                box.configure_values(self.choice_values(spec))
            flags = self.multi_vars.get(spec.key)
            if flags is not None:
                picked = set(value if isinstance(value, list) else [])
                for name, flag in flags.items():
                    flag.set(name in picked)
                button = self.multi_buttons.get(spec.key)
                if button is not None:
                    button.state(["disabled"] if self.multi_picked(spec) == self.multi_saved(spec) else ["!disabled"])

    def save_one(self, spec: config.SettingSpec, value) -> None:
        result = api.dispatch("save_settings", {"settings": {spec.key: value}})
        if not result.get("ok"):
            self.error(result.get("error", "保存失败"))
            self.refresh_values()
            return
        saved = result["settings"].get(spec.key)
        if str(saved) != str(value):  # 值被纠正过（比如填了负数），让界面显示真实值
            self.status_var.set(f"「{spec.label}」已按规则调整为 {saved}")
        else:
            self.status_var.set(f"已保存：{spec.label} = {saved}")
        self.refresh_values()
        self.app.apply_settings_changed()

    def reset_all(self) -> None:
        if not self.confirm("把「设置」里的所有偏好恢复成默认值吗？\n（种类、支付方式、已有的账目都不受影响）"):
            return
        result = api.dispatch("reset_settings")
        if not result.get("ok"):
            self.error(result.get("error", "恢复默认失败"))
            return
        self.refresh_values()
        self.app.apply_settings_changed()
        self.status_var.set("设置已恢复默认")

    # ---------------------------------------------------------------- 种类与支付方式

    def fill_items_page(self, page: ttk.Frame) -> None:
        self.kind_listboxes: dict[str, SortableList] = {}
        self.kind_buttons: dict[str, RoundedButton] = {}

        for kind in KIND_ORDER:
            section = ttk.LabelFrame(page, text=f"{kind}种类", padding=10)
            section.pack(fill="x", pady=(0, 10))
            ttk.Label(
                section,
                text="抓住每行最左边的小横线可以拖动排序（松手自动保存）；顺序就是记账时下拉框的顺序。",
                style="Hint.TLabel",
            ).pack(anchor="w", pady=(0, 6))
            line = ttk.Frame(section)
            line.pack(fill="x")
            rowlist = SortableList(
                line,
                items=self.categories.get(kind, []),
                border=True,
                auto_height=True,
                max_rows=None,
                auto_width=True,
                on_change=lambda items, k=kind: self.save_kind_order(k, items),
            )
            rowlist.pack(side="left", anchor="n")
            button = RoundedButton(line, text="修改", width=8, height=28, command=lambda k=kind: self.edit_categories(k))
            button.pack(side="left", anchor="n", padx=(10, 0))
            self.kind_listboxes[kind] = rowlist
            self.kind_buttons[kind] = button

        section = ttk.LabelFrame(page, text="支付方式", padding=10)
        section.pack(fill="x", pady=(0, 10))
        ttk.Label(
            section,
            text="同样可以拖动手柄排序；默认用哪一个，在「记一笔」页里的「默认支付方式」选。",
            style="Hint.TLabel",
        ).pack(anchor="w", pady=(0, 6))
        line = ttk.Frame(section)
        line.pack(fill="x")
        self.pay_list = SortableList(
            line,
            items=self.pay_methods,
            border=True,
            auto_height=True,
            max_rows=None,
            auto_width=True,
            on_change=self.save_pay_order,
        )
        self.pay_list.pack(side="left", anchor="n")
        self.pay_button = RoundedButton(line, text="修改", width=8, height=28, command=self.edit_pay_methods)
        self.pay_button.pack(side="left", anchor="n", padx=(10, 0))

        ttk.Label(
            page,
            text="删掉某一项不影响已经记过的账；只是以后下拉框里不再出现它。",
            style="Hint.TLabel",
        ).pack(anchor="w", pady=(2, 0))

    def refresh_category_boxes(self) -> None:
        """把当前种类回填到「收入种类 / 支出种类」两个方框里。"""
        self.categories = config.load_categories()
        for kind, rowlist in self.kind_listboxes.items():
            rowlist.set_items(self.categories.get(kind, []))

    def save_kind_order(self, kind: str, items: list[str]) -> None:
        """在设置页里拖动排序后立即整体保存。"""
        result = api.dispatch("save_categories", {"kind": kind, "categories": items})
        if not result.get("ok"):
            self.error(result.get("error", "保存顺序失败"))
            self.refresh_category_boxes()
            return
        self.categories = result["categories"]
        self.status_var.set(f"「{kind}种类」顺序已保存")
        self.app.notify_data_changed()

    def save_pay_order(self, items: list[str]) -> None:
        """支付方式拖动排序后立即保存。"""
        result = api.dispatch("save_settings", {"settings": {"支付方式": items}})
        if not result.get("ok"):
            self.error(result.get("error", "保存顺序失败"))
            self.refresh_values()
            return
        self.status_var.set("支付方式顺序已保存")
        self.refresh_values()
        self.app.apply_settings_changed()

    def edit_categories(self, kind: str) -> None:
        """点「修改」：打开该类的种类编辑窗口，确定后整体保存。"""
        dialog = CategoryDialog(self, kind, list(self.categories.get(kind, [])))
        new_list = dialog.show()
        if new_list is None:
            return
        result = api.dispatch("save_categories", {"kind": kind, "categories": new_list})
        if not result.get("ok"):
            self.error(result.get("error", "保存失败"))
            return
        self.categories = result["categories"]
        self.refresh_category_boxes()
        self.app.notify_data_changed()
        self.status_var.set(f"「{kind}种类」已更新，共 {len(new_list)} 个")

    def edit_pay_methods(self) -> None:
        """点「修改」：打开支付方式编辑窗口，确定后整体保存。"""
        dialog = ItemListDialog(self, "修改支付方式", list(self.pay_methods), noun="支付方式")
        new_list = dialog.show()
        if new_list is None:
            return
        result = api.dispatch("save_settings", {"settings": {"支付方式": new_list}})
        if not result.get("ok"):
            self.error(result.get("error", "保存失败"))
            return
        self.refresh_values()
        self.app.apply_settings_changed()
        self.status_var.set(f"支付方式已更新，共 {len(new_list)} 个")

    # ---------------------------------------------------------------- 数据与目录

    def fill_dirs(self, page: ttk.Frame) -> None:
        section = ttk.LabelFrame(page, text="数据与目录", padding=10)
        section.pack(fill="x", pady=(10, 0))

        data_wrap = WrapRow(section, gap=10)
        data_wrap.pack(fill="x")
        data_wrap.add(ttk.Label(data_wrap, text="数据目录为"))
        data_wrap.add(ttk.Label(data_wrap, text=str(paths.data_dir()), style="Hint.TLabel"))
        self.open_data_button = RoundedButton(
            data_wrap, text="打开数据目录", height=28, command=lambda: self.open_dir(paths.data_dir())
        )
        data_wrap.add(self.open_data_button, gap=4)
        self.data_wrap = data_wrap

        backup_wrap = WrapRow(section, gap=10)
        backup_wrap.pack(fill="x", pady=(6, 0))
        self.backup_path_title = "备份路径为"
        backup_wrap.add(ttk.Label(backup_wrap, text=self.backup_path_title))
        self.backup_path_var = tk.StringVar(value=str(api.backup_dir()))
        self.backup_path_label = tk.Label(
            backup_wrap,
            textvariable=self.backup_path_var,
            foreground="#1a4fbf",
            cursor="hand2",
            font=common.ui_font(underline=True),
        )
        backup_wrap.add(self.backup_path_label, gap=4)
        self.backup_path_label.bind("<Button-1>", lambda _event: self.choose_backup_dir())
        Tooltip(self.backup_path_label, "点击可以选择备份到哪个文件夹")
        self.open_backup_button = RoundedButton(
            backup_wrap, text="打开备份目录", height=28, command=lambda: self.open_dir(api.backup_dir())
        )
        backup_wrap.add(self.open_backup_button, gap=4)
        self.backup_wrap = backup_wrap

        ttk.Label(
            section,
            text=f"配置目录固定跟着程序走：{paths.config_dir()}",
            style="Hint.TLabel",
        ).pack(anchor="w", pady=(10, 0))
        ttk.Label(
            section,
            text="数据是纯文本（每日一个 TSV）；备份是改 / 删 / 导入前的整月快照，可以随时删旧的。",
            style="Hint.TLabel",
        ).pack(anchor="w", pady=(4, 0))

        self.fill_config_section(page)

    def fill_config_section(self, page: ttk.Frame) -> None:
        section = ttk.LabelFrame(page, text="配置数据", padding=10)
        section.pack(fill="x", pady=(10, 0))
        ttk.Label(
            section,
            text="把「设置和种类」打包成一个文件，换电脑、重装或想回退到从前的配置时用得到。\n"
            "导入前会先把现在的配置备份一份到备份目录的「配置」文件夹里，随时可以翻回去。",
            style="Hint.TLabel",
            justify="left",
        ).pack(anchor="w", pady=(0, 8))
        line = WrapRow(section, gap=8)
        line.pack(fill="x")
        self.export_config_button = RoundedButton(line, text="导出配置数据", height=28, command=self.export_config)
        line.add(self.export_config_button)
        self.import_config_button = RoundedButton(line, text="导入配置数据", height=28, command=self.import_config)
        line.add(self.import_config_button)
        self.config_wrap = line
        self.config_hint_var = tk.StringVar(value="")
        ttk.Label(section, textvariable=self.config_hint_var, style="Hint.TLabel").pack(anchor="w", pady=(8, 0))

    # ---------------------------------------------------------------- 目录与配置数据

    def choose_backup_dir(self) -> None:
        current = api.backup_dir()
        current.mkdir(parents=True, exist_ok=True)
        picked = filedialog.askdirectory(parent=self, title="选择备份目录", initialdir=str(current))
        if not picked:
            return
        result = api.dispatch("save_settings", {"settings": {"备份目录": picked}})
        if not result.get("ok"):
            self.error(result.get("error", "保存备份目录失败"))
            return
        self.refresh_values()
        self.app.apply_settings_changed()
        self.status_var.set(f"备份目录已改为 {picked}")

    def export_config(self) -> None:
        result = api.dispatch("export_config", {})
        if not result.get("ok"):
            self.error(result.get("error", "导出配置失败"))
            return
        self.config_hint_var.set(f"上次导出：{result['file']}")
        self.notify(
            f"已导出配置：\n{result['file']}\n\n含 {result['设置项数']} 项设置、{result['种类数']} 个种类。",
            "导出配置完成",
        )
        self.status_var.set(f"配置数据已导出到 {result['file']}")

    def import_config(self) -> None:
        paths.ensure_dirs()
        target = filedialog.askopenfilename(
            parent=self,
            title="选择配置文件",
            initialdir=str(api.export_dir()),
            filetypes=[("配置文件", "*.json"), ("全部文件", "*.*")],
        )
        if not target:
            return
        preview = api.dispatch("import_config", {"path": target})
        if not preview.get("ok"):
            self.error(preview.get("error", "读取配置失败"))
            return
        if not self.confirm(
            f"用这个文件里的配置覆盖当前的吗？\n\n{preview['summary']}\n\n"
            "导入前会自动把现在的配置备份到备份目录的「配置」文件夹。"
        ):
            return
        result = api.dispatch("import_config", {"path": target, "apply": True})
        if not result.get("ok"):
            self.error(result.get("error", "导入配置失败"))
            return
        self.refresh_values()
        self.app.apply_settings_changed()
        self.config_hint_var.set(f"上次导入：{result['file']}（旧配置已备份到 {result['backup']}）")
        self.notify(
            f"配置已导入：\n{result['summary']}\n\n旧配置备份在：\n{result['backup']}",
            "导入配置完成",
        )
        self.status_var.set("配置数据已导入")

    # ---------------------------------------------------------------- 关于

    def fill_about(self, page: ttk.Frame) -> None:
        section = ttk.LabelFrame(page, text="版本信息", padding=10)
        section.pack(fill="x")
        info = api.dispatch("info")
        version = info.get("version", "?") if info.get("ok") else "?"
        rows = (
            ("程序版本", f"{paths.APP_NAME} v{version}"),
            ("数据格式版本", str(paths.DATA_FORMAT_VERSION)),
            ("设置版本", str(config.SETTINGS_VERSION)),
            ("数据目录", str(paths.root())),
            ("设置项数量", f"{len(config.load_settings())} 项"),
        )
        for row, (label, value) in enumerate(rows):
            ttk.Label(section, text=label).grid(row=row, column=0, sticky="w", pady=4)
            ttk.Label(section, text=value).grid(row=row, column=1, sticky="w", padx=(16, 0), pady=4)
        ttk.Label(
            section,
            text="检查程序是否正常：命令行执行  python 记账.py 自测 （在临时目录里跑，不碰你的账本）。",
            style="Hint.TLabel",
        ).grid(row=len(rows), column=0, columnspan=2, sticky="w", pady=(8, 0))

    # ---------------------------------------------------------------- 其他

    def open_dir(self, path) -> None:
        path.mkdir(parents=True, exist_ok=True)
        open_path(path)

    def on_show(self) -> None:
        self.refresh_values()

    def apply_settings(self) -> None:
        self.refresh_values()
