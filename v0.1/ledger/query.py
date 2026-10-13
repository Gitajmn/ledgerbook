"""查询与统计（与界面无关）。

日期范围、类型、分类、关键词过滤，以及收入/支出/结余合计和分类小计。
「本月/上月/今年」等快捷范围也在这里定义，前端只传一个名字即可，
这样以后换界面语言也不会把日期算错。
"""

from __future__ import annotations

import datetime as dt
from dataclasses import dataclass, field
from decimal import Decimal

from . import config
from . import model as m
from . import paths, storage

PRESETS = ("今天", "昨天", "本周", "上周", "本月", "上月", "近7天", "近30天", "今年", "全部")

#: 分类（种类）被删掉之后，统计时并入这个分类。记录本身不会被改动。
OTHER_CATEGORY = "其他"


def effective_category(record: m.Record, categories: dict[str, list[str]] | None = None) -> str:
    """这条记录在「查询统计」里算哪个分类。

    类别被删掉之后，它的记录仍然留在文件里（不删记录、不改内容），但统计和筛选时
    一律并入「其他」：金额、笔数、占比都算到「其他」名下；把类别加回来就又归回原分类。
    """
    if categories is None:
        categories = config.load_categories()
    allowed = categories.get(record.kind, [])
    return record.category if record.category in allowed else OTHER_CATEGORY


@dataclass(slots=True)
class CategoryTotal:
    kind: str
    category: str
    count: int
    amount: Decimal

    def to_dict(self) -> dict:
        return {
            "kind": self.kind,
            "category": self.category,
            "count": self.count,
            "amount": m.fmt_amount(self.amount),
        }


@dataclass(slots=True)
class Filters:
    start: dt.date | None = None
    end: dt.date | None = None
    kind: str | None = None
    category: str | None = None
    keyword: str | None = None

    def cleaned(self) -> "Filters":
        start, end = self.start, self.end
        if start and end and start > end:
            start, end = end, start
        kind = m.parse_kind(self.kind) if self.kind and self.kind not in ("全部", "") else None
        category = (self.category or "").strip()
        if category in ("全部", ""):
            category = None
        keyword = (self.keyword or "").strip() or None
        return Filters(start=start, end=end, kind=kind, category=category, keyword=keyword)

    def to_dict(self) -> dict:
        return {
            "start": self.start.isoformat() if self.start else None,
            "end": self.end.isoformat() if self.end else None,
            "kind": self.kind,
            "category": self.category,
            "keyword": self.keyword,
        }


@dataclass(slots=True)
class QueryResult:
    records: list[m.Record] = field(default_factory=list)
    income: Decimal = Decimal("0.00")
    expense: Decimal = Decimal("0.00")
    by_category: list[CategoryTotal] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)
    start: dt.date | None = None
    end: dt.date | None = None
    #: 记录编号 → 统计时算作哪个分类（类别被删掉的那些会指向「其他」）
    effective: dict[str, str] = field(default_factory=dict)

    @property
    def balance(self) -> Decimal:
        return self.income - self.expense

    @property
    def count(self) -> int:
        return len(self.records)

    def totals_dict(self) -> dict:
        return {
            "count": self.count,
            "income": m.fmt_amount(self.income),
            "expense": m.fmt_amount(self.expense),
            "balance": m.fmt_amount(self.balance),
        }


def resolve_preset(name: str, today: dt.date | None = None) -> tuple[dt.date | None, dt.date | None]:
    """把快捷范围名解析成（起, 止）；「全部」返回 (None, None)。"""
    today = today or dt.date.today()
    name = (name or "").strip()
    if name in ("", "全部"):
        return None, None
    if name == "今天":
        return today, today
    if name == "昨天":
        day = today - dt.timedelta(days=1)
        return day, day
    if name == "本周":
        return today - dt.timedelta(days=today.weekday()), today
    if name == "上周":
        monday = today - dt.timedelta(days=today.weekday() + 7)
        return monday, monday + dt.timedelta(days=6)
    if name == "本月":
        first = today.replace(day=1)
        return first, _month_end(first)
    if name == "上月":
        last_month_end = today.replace(day=1) - dt.timedelta(days=1)
        return last_month_end.replace(day=1), last_month_end
    if name == "近7天":
        return today - dt.timedelta(days=6), today
    if name == "近30天":
        return today - dt.timedelta(days=29), today
    if name == "今年":
        return dt.date(today.year, 1, 1), dt.date(today.year, 12, 31)
    raise ValueError(f"不认识的时间范围：{name}（可选：{'、'.join(PRESETS)}）")


def _month_end(day: dt.date) -> dt.date:
    return (day.replace(day=28) + dt.timedelta(days=4)).replace(day=1) - dt.timedelta(days=1)


def data_span() -> tuple[dt.date | None, dt.date | None]:
    """数据里最早和最晚的一天。"""
    days: list[dt.date] = []
    for path in paths.data_dir().glob("*/*/*.tsv"):
        try:
            days.append(dt.date.fromisoformat(path.stem))
        except ValueError:
            continue
    if not days:
        return None, None
    return min(days), max(days)


def run(filters: Filters | None = None) -> QueryResult:
    filters = (filters or Filters()).cleaned()
    start, end = filters.start, filters.end
    if start is None or end is None:
        first, last = data_span()
        if first is None:
            return QueryResult(start=None, end=None)
        start = start or first
        end = end or last

    errors: list[dict] = []
    records = storage.read_range(start, end, errors)

    categories = config.load_categories()
    keyword = filters.keyword.lower() if filters.keyword else None
    kept: list[m.Record] = []
    effective: dict[str, str] = {}
    for record in records:
        if filters.kind and record.kind != filters.kind:
            continue
        category = effective_category(record, categories)
        # 按分类筛选时按「统计归类」算：选「其他」能把被删掉类别的记录也查出来
        if filters.category and category != filters.category:
            continue
        if keyword:
            haystack = f"{record.category} {record.note} {record.pay}".lower()
            if keyword not in haystack:
                continue
        kept.append(record)
        effective[record.id] = category

    income = Decimal("0.00")
    expense = Decimal("0.00")
    grouped: dict[tuple[str, str], list] = {}
    for record in kept:
        if record.kind == m.KIND_INCOME:
            income += record.amount
        else:
            expense += record.amount
        bucket = grouped.setdefault((record.kind, effective[record.id]), [0, Decimal("0.00")])
        bucket[0] += 1
        bucket[1] += record.amount

    by_category = [
        CategoryTotal(kind=kind, category=category, count=info[0], amount=info[1])
        for (kind, category), info in grouped.items()
    ]
    by_category.sort(key=lambda item: (item.kind, -item.amount, item.category))

    return QueryResult(
        records=kept,
        income=income,
        expense=expense,
        by_category=by_category,
        errors=errors,
        start=start,
        end=end,
        effective=effective,
    )


def run_preset(name: str, **extra) -> QueryResult:
    start, end = resolve_preset(name)
    return run(Filters(start=start, end=end, **extra))


def month_result(year: int, month: int) -> QueryResult:
    first = dt.date(year, month, 1)
    return run(Filters(start=first, end=_month_end(first)))
