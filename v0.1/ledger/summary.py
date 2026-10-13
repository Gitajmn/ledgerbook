"""月度汇总：按日小计、分类小计，以及导出成 CSV（衍生文件，可随时重建）。"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass, field
from decimal import Decimal

from . import config
from . import model as m
from . import paths, query, storage
from .table import write_table

SUMMARY_FIELDS = ("日期", "笔数", "收入合计", "支出合计", "结余")
CATEGORY_FIELDS = ("类型", "分类", "笔数", "金额", "占比")


@dataclass(slots=True)
class DayLine:
    day: dt.date
    count: int
    income: Decimal
    expense: Decimal

    @property
    def balance(self) -> Decimal:
        return self.income - self.expense

    def to_row(self) -> list[str]:
        return [
            self.day.isoformat(),
            str(self.count),
            m.fmt_amount(self.income),
            m.fmt_amount(self.expense),
            m.fmt_amount(self.balance),
        ]

    def to_dict(self) -> dict:
        return {
            "date": self.day.isoformat(),
            "count": self.count,
            "income": m.fmt_amount(self.income),
            "expense": m.fmt_amount(self.expense),
            "balance": m.fmt_amount(self.balance),
        }


@dataclass(slots=True)
class MonthReport:
    year: int
    month: int
    days: list[DayLine] = field(default_factory=list)
    categories: list[query.CategoryTotal] = field(default_factory=list)
    income: Decimal = Decimal("0.00")
    expense: Decimal = Decimal("0.00")
    errors: list[dict] = field(default_factory=list)

    @property
    def balance(self) -> Decimal:
        return self.income - self.expense

    @property
    def count(self) -> int:
        return sum(line.count for line in self.days)

    @property
    def label(self) -> str:
        return paths.month_name(self.year, self.month)

    def totals_dict(self) -> dict:
        return {
            "month": self.label,
            "count": self.count,
            "income": m.fmt_amount(self.income),
            "expense": m.fmt_amount(self.expense),
            "balance": m.fmt_amount(self.balance),
        }


def build(year: int, month: int) -> MonthReport:
    first = dt.date(year, month, 1)
    last = query._month_end(first)
    errors: list[dict] = []
    records = storage.read_range(first, last, errors)

    buckets: dict[dt.date, list] = {}
    income = Decimal("0.00")
    expense = Decimal("0.00")
    grouped: dict[tuple[str, str], list] = {}
    categories_config = config.load_categories()
    for record in records:
        bucket = buckets.setdefault(record.date, [0, Decimal("0.00"), Decimal("0.00")])
        bucket[0] += 1
        if record.kind == m.KIND_INCOME:
            bucket[1] += record.amount
            income += record.amount
        else:
            bucket[2] += record.amount
            expense += record.amount
        # 与查询一致：被删掉类别的记录并入「其他」
        category = query.effective_category(record, categories_config)
        info = grouped.setdefault((record.kind, category), [0, Decimal("0.00")])
        info[0] += 1
        info[1] += record.amount

    days = [
        DayLine(day=day, count=info[0], income=info[1], expense=info[2])
        for day, info in sorted(buckets.items())
    ]
    categories = [
        query.CategoryTotal(kind=kind, category=category, count=info[0], amount=info[1])
        for (kind, category), info in grouped.items()
    ]
    categories.sort(key=lambda item: (item.kind, -item.amount, item.category))
    return MonthReport(
        year=year,
        month=month,
        days=days,
        categories=categories,
        income=income,
        expense=expense,
        errors=errors,
    )


def export(year: int, month: int) -> dict:
    """写出该月目录下的 _月度汇总.csv 与 _分类小计.csv。"""
    report = build(year, month)
    summary_path = paths.summary_file(year, month)
    category_path = paths.category_summary_file(year, month)

    rows = [line.to_row() for line in report.days]
    rows.append(
        [
            "合计",
            str(report.count),
            m.fmt_amount(report.income),
            m.fmt_amount(report.expense),
            m.fmt_amount(report.balance),
        ]
    )
    write_table(summary_path, list(SUMMARY_FIELDS), rows, delimiter=",")

    total = sum((item.amount for item in report.categories), Decimal("0.00"))
    category_rows: list[list[str]] = []
    for item in report.categories:
        share = (item.amount / total * 100) if total else Decimal("0.00")
        category_rows.append(
            [
                item.kind,
                item.category,
                str(item.count),
                m.fmt_amount(item.amount),
                f"{share:.2f}%".rstrip("0").rstrip("."),
            ]
        )
    write_table(category_path, list(CATEGORY_FIELDS), category_rows, delimiter=",")

    return {
        "month": report.label,
        "summary": str(summary_path),
        "category": str(category_path),
        "totals": report.totals_dict(),
    }
