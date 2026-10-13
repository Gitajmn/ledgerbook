"""导入 / 导出：和 Excel 打交道的唯一入口。

导出：查询结果或全部记录写成 CSV（带「日期」列，方便阅读和外部处理）。
导入：解析 CSV/TSV（自动认编码、认分隔符、认列名），逐行校验，按冲突策略写回，
      导入前自动备份受影响月份，最后给出「成功/跳过/覆盖/失败」报告。
"""

from __future__ import annotations

import datetime as dt
import re
from dataclasses import dataclass, field
from decimal import Decimal
from pathlib import Path

from . import config, model as m, paths, storage
from .model import EXPORT_FIELDS, FIELD_ALIASES
from .table import read_table, write_table

STRATEGY_SKIP = "跳过重复"
STRATEGY_OVERWRITE = "按编号覆盖"
STRATEGY_APPEND = "全部追加"
STRATEGIES = (STRATEGY_SKIP, STRATEGY_OVERWRITE, STRATEGY_APPEND)

ERROR_FIELDS = ("行号", "原因", "内容")
_DATE_IN_NAME = re.compile(r"(\d{4}-\d{2}-\d{2})")


@dataclass(slots=True)
class ParsedRow:
    line: int
    date: dt.date
    time: str
    kind: str
    category: str
    amount: Decimal
    pay: str = ""
    note: str = ""
    id: str | None = None

    def dedup_key(self) -> tuple:
        return (self.time, self.kind, self.category, m.fmt_amount(self.amount), self.pay, self.note)

    def to_record(self, record_id: str) -> m.Record:
        return m.Record(
            id=record_id,
            date=self.date,
            time=self.time,
            kind=self.kind,
            category=self.category,
            amount=self.amount,
            pay=self.pay,
            note=self.note,
        )

    def to_dict(self) -> dict:
        return {
            "行号": self.line,
            "编号": self.id or "",
            "日期": self.date.isoformat(),
            "时间": self.time,
            "类型": self.kind,
            "分类": self.category,
            "金额": m.fmt_amount(self.amount),
            "支付方式": self.pay,
            "备注": self.note,
        }


@dataclass(slots=True)
class Parsed:
    path: Path
    header: list[str] = field(default_factory=list)
    rows: list[ParsedRow] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)
    missing_categories: list[str] = field(default_factory=list)

    @property
    def total(self) -> int:
        return len(self.rows) + len(self.errors)

    def to_dict(self) -> dict:
        return {
            "文件": str(self.path),
            "表头": self.header,
            "总行数": self.total,
            "可导入": len(self.rows),
            "失败": len(self.errors),
            "缺失分类": self.missing_categories,
            "错误": self.errors[:200],
            "预览": [row.to_dict() for row in self.rows[:50]],
        }


def _column_indexes(header: list[str]) -> dict[str, int]:
    indexes: dict[str, int] = {}
    for key, aliases in FIELD_ALIASES.items():
        found = -1
        for i, cell in enumerate(header):
            if cell in aliases:
                found = i
                break
        if found < 0:
            for i, cell in enumerate(header):
                if any(alias in cell for alias in aliases):
                    found = i
                    break
        if found >= 0:
            indexes[key] = found
    return indexes


def parse_file(path: Path | str, default_date: dt.date | None = None) -> Parsed:
    """解析待导入文件。行级错误不会中断解析，全部收集到 errors 里。"""
    path = Path(path)
    if not path.exists():
        raise FileNotFoundError(f"文件不存在：{path}")
    header, raw_rows = read_table(path)
    if not raw_rows:
        raise ValueError("文件里没有数据行（第一行会被当作表头）")

    indexes = _column_indexes(header)
    for required, label in (("amount", "金额"), ("kind", "类型"), ("category", "分类")):
        if required not in indexes:
            raise ValueError(f"文件里找不到「{label}」列，表头是：{'、'.join(header)}")

    name_date = None
    match = _DATE_IN_NAME.search(path.stem)
    if match:
        try:
            name_date = dt.date.fromisoformat(match.group(1))
        except ValueError:
            name_date = None
    fallback_date = default_date or name_date

    parsed = Parsed(path=path, header=list(header))
    categories = config.load_categories()
    missing: list[str] = []

    for offset, raw in enumerate(raw_rows):
        line = offset + 2  # 表头占第 1 行

        def cell(key: str) -> str:
            index = indexes.get(key, -1)
            return (raw[index] if 0 <= index < len(raw) else "").strip()

        try:
            kind = m.parse_kind(cell("kind"))
            category = cell("category")
            if not category:
                raise m.RecordError("分类不能为空")
            amount = m.parse_amount(cell("amount"))

            raw_id = cell("id")
            record_id: str | None = None
            id_day: dt.date | None = None
            if raw_id:
                try:
                    id_day = m.id_date(raw_id)
                    record_id = raw_id
                except m.RecordError:
                    record_id = None

            date_text = cell("date")
            if date_text:
                day = m.parse_date(date_text)
            elif id_day is not None:
                day = id_day
            elif fallback_date is not None:
                day = fallback_date
            else:
                raise m.RecordError("没有「日期」列，编号里也没有日期，文件名里也没有日期")

            if record_id is not None and id_day != day:
                record_id = None  # 编号里的日期与日期列不一致，改由程序重新编号

            time_text = m.normalize_time(cell("time"))
            row = ParsedRow(
                line=line,
                date=day,
                time=time_text,
                kind=kind,
                category=category,
                amount=amount,
                pay=cell("pay"),
                note=cell("note"),
                id=record_id,
            )
            parsed.rows.append(row)
            if category not in categories.get(kind, []) and f"{kind}/{category}" not in missing:
                missing.append(f"{kind}/{category}")
        except (m.RecordError, ValueError) as exc:
            parsed.errors.append(
                {
                    "行号": line,
                    "原因": str(exc),
                    "内容": " | ".join(cell for cell in raw if cell.strip()),
                }
            )

    parsed.missing_categories = missing
    return parsed


def export_records(
    records: list[m.Record],
    name: str | None = None,
    out_dir: Path | str | None = None,
    prefix: str = "查询",
) -> dict:
    """把记录导出成 CSV，返回导出文件信息。"""
    directory = Path(out_dir) if out_dir else paths.export_dir()
    directory.mkdir(parents=True, exist_ok=True)
    if not name:
        name = f"{prefix}_{dt.datetime.now():%Y-%m-%d_%H%M%S}.csv"
    target = directory / name
    rows = [record.to_export_row() for record in sorted(records, key=lambda r: (r.date, r.id))]
    write_table(target, list(EXPORT_FIELDS), rows, delimiter=",")
    income = sum((r.amount for r in records if r.kind == m.KIND_INCOME), Decimal("0.00"))
    expense = sum((r.amount for r in records if r.kind != m.KIND_INCOME), Decimal("0.00"))
    return {
        "文件": str(target),
        "记录数": len(records),
        "收入合计": m.fmt_amount(income),
        "支出合计": m.fmt_amount(expense),
        "结余": m.fmt_amount(income - expense),
    }


def export_errors(errors: list[dict], name: str | None = None, out_dir: Path | str | None = None) -> Path:
    directory = Path(out_dir) if out_dir else paths.export_dir()
    directory.mkdir(parents=True, exist_ok=True)
    if not name:
        name = f"导入错误_{dt.datetime.now():%Y-%m-%d_%H%M%S}.csv"
    target = directory / name
    rows = [
        [str(item.get("行号", "")), str(item.get("原因", "")), str(item.get("内容", ""))]
        for item in errors
    ]
    write_table(target, list(ERROR_FIELDS), rows, delimiter=",")
    return target


def apply(
    parsed: Parsed,
    strategy: str = STRATEGY_SKIP,
    auto_add_categories: bool = False,
) -> dict:
    """把解析结果写回每日文件。"""
    if strategy not in STRATEGIES:
        raise ValueError(f"不认识的导入策略：{strategy}（可选：{'、'.join(STRATEGIES)}）")
    if not parsed.rows:
        return {
            "策略": strategy,
            "成功": 0,
            "跳过": 0,
            "覆盖": 0,
            "失败": len(parsed.errors),
            "总行数": parsed.total,
            "错误": parsed.errors,
            "备份": [],
        }

    if parsed.missing_categories:
        if not auto_add_categories:
            names = "、".join(parsed.missing_categories)
            raise ValueError(f"以下分类不在配置里：{names}（可以勾选「自动新增分类」再导入）")
        for item in parsed.missing_categories:
            kind, _, category = item.partition("/")
            try:
                config.add_category(kind, category)
            except ValueError:
                pass

    grouped: dict[dt.date, list[ParsedRow]] = {}
    for row in parsed.rows:
        grouped.setdefault(row.date, []).append(row)

    backups = storage.backup_days(sorted(grouped))
    stats = {"成功": 0, "跳过": 0, "覆盖": 0, "失败": len(parsed.errors)}

    for day in sorted(grouped):
        records, extra_rows = storage.read_day_full(day)
        by_id = {record.id: record for record in records}
        keys = {record.dedup_key() for record in records}

        for row in grouped[day]:
            key = row.dedup_key()
            if strategy == STRATEGY_SKIP:
                if (row.id and row.id in by_id) or key in keys:
                    stats["跳过"] += 1
                    continue
            elif strategy == STRATEGY_OVERWRITE and row.id and row.id in by_id:
                record = by_id[row.id]
                record.time = row.time
                record.kind = row.kind
                record.category = row.category
                record.amount = row.amount
                record.pay = row.pay
                record.note = row.note
                keys.add(key)
                stats["成功"] += 1
                stats["覆盖"] += 1
                continue

            if row.id and row.id not in by_id and strategy != STRATEGY_APPEND:
                record_id = row.id
            else:
                record_id = storage.allocate_id(day, list(by_id.values()))
            record = row.to_record(record_id)
            records.append(record)
            by_id[record_id] = record
            keys.add(key)
            stats["成功"] += 1

        storage.write_day(day, records, extra_rows)

    return {
        "策略": strategy,
        "成功": stats["成功"],
        "跳过": stats["跳过"],
        "覆盖": stats["覆盖"],
        "失败": stats["失败"],
        "总行数": parsed.total,
        "错误": parsed.errors,
        "备份": [str(path) for path in backups],
    }
