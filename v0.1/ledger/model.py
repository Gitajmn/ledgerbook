"""记录模型：字段定义、编号规则、金额与日期解析。

这是**跨语言的格式契约**：每日 TSV 的列顺序、编号格式、金额与日期的写法都在这里，
以后换语言重写程序时，必须与这里保持一致。
"""

from __future__ import annotations

import datetime as dt
import re
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation, ROUND_HALF_UP

KIND_EXPENSE = "支出"
KIND_INCOME = "收入"
KINDS = (KIND_EXPENSE, KIND_INCOME)

#: 每日 TSV 列顺序。不存日期列：编号前 8 位就是日期，文件名也带日期。
DAY_FIELDS = ("编号", "时间", "类型", "分类", "金额", "支付方式", "备注")

#: 导出 CSV 列顺序。多一列日期，方便人工阅读和外部工具处理。
EXPORT_FIELDS = ("编号", "日期", "时间", "类型", "分类", "金额", "支付方式", "备注")

#: 导入时认可的列名（含英文名，方便别人用程序生成的文件直接导入）。
FIELD_ALIASES: dict[str, tuple[str, ...]] = {
    "id": ("编号", "id", "序号", "记录编号"),
    "date": ("日期", "date", "记账日期"),
    "time": ("时间", "time"),
    "kind": ("类型", "kind", "收支", "收支类型"),
    "category": ("分类", "category", "类别", "科目"),
    "amount": ("金额", "amount", "金额(元)", "金额（元）", "money"),
    "pay": ("支付方式", "pay", "付款方式", "账户", "支付"),
    "note": ("备注", "note", "说明", "摘要", "备注说明"),
}

KIND_ALIASES = {
    "支出": KIND_EXPENSE,
    "支": KIND_EXPENSE,
    "expense": KIND_EXPENSE,
    "out": KIND_EXPENSE,
    "-": KIND_EXPENSE,
    "收入": KIND_INCOME,
    "收": KIND_INCOME,
    "income": KIND_INCOME,
    "in": KIND_INCOME,
    "+": KIND_INCOME,
}

QUANT = Decimal("0.01")

_ID_RE = re.compile(r"^(\d{4})(\d{2})(\d{2})-(\d{3,})$")
_TIME_RE = re.compile(r"^(\d{1,2})[:：](\d{2})(?::\d{2})?$")
_AMOUNT_CLEAN = re.compile(r"[,\s¥￥$元]")
_DATE_FORMATS = ("%Y-%m-%d", "%Y/%m/%d", "%Y.%m.%d", "%Y%m%d", "%Y年%m月%d日")


class RecordError(ValueError):
    """记录格式不合法。"""


def fmt_amount(value) -> str:
    """金额统一两位小数字符串。"""
    if isinstance(value, Decimal):
        dec = value
    else:
        dec = Decimal(str(value))
    return f"{dec.quantize(QUANT, rounding=ROUND_HALF_UP):.2f}"


def parse_amount(value) -> Decimal:
    """解析金额，必须大于 0（正负由「类型」决定）。"""
    if isinstance(value, Decimal):
        dec = value
    elif isinstance(value, int):
        dec = Decimal(value)
    elif isinstance(value, float):
        dec = Decimal(str(value))
    else:
        text = _AMOUNT_CLEAN.sub("", str(value or "").strip())
        text = text.replace("−", "-").replace("－", "-").replace("＋", "+")
        if not text:
            raise RecordError("金额不能为空")
        try:
            dec = Decimal(text)
        except InvalidOperation:
            raise RecordError(f"金额格式不对：{value!r}") from None
    if not dec.is_finite():
        raise RecordError(f"金额格式不对：{value!r}")
    dec = dec.quantize(QUANT, rounding=ROUND_HALF_UP)
    if dec <= 0:
        raise RecordError("金额必须大于 0（支出/收入由「类型」区分）")
    return dec


def parse_kind(value) -> str:
    text = str(value or "").strip().lower()
    if text in KIND_ALIASES:
        return KIND_ALIASES[text]
    raise RecordError(f"类型只能是「{KIND_EXPENSE}」或「{KIND_INCOME}」，收到：{value!r}")


def parse_date(value, default_year: int | None = None) -> dt.date:
    """解析日期，兼容 2026-02-14 / 2026/2/14 / 20260214 等写法。"""
    if isinstance(value, dt.datetime):
        return value.date()
    if isinstance(value, dt.date):
        return value
    text = str(value or "").strip()
    if not text:
        raise RecordError("日期不能为空")
    if re.fullmatch(r"\d{4,5}", text) and default_year:  # Excel 里常见的日期序列号
        try:
            return dt.date(1899, 12, 30) + dt.timedelta(days=int(text))
        except (OverflowError, ValueError):
            pass
    for fmt in _DATE_FORMATS:
        try:
            return dt.datetime.strptime(text, fmt).date()
        except ValueError:
            continue
    raise RecordError(f"日期格式应为 2026-02-14，收到：{value!r}")


def normalize_time(value, fallback: dt.datetime | None = None) -> str:
    """时间统一成 HH:MM；留空则用当前时间。"""
    text = str(value or "").strip()
    if not text:
        moment = fallback or dt.datetime.now()
        return f"{moment.hour:02d}:{moment.minute:02d}"
    match = _TIME_RE.match(text)
    if not match:
        raise RecordError(f"时间格式应为 HH:MM，收到：{value!r}")
    hour, minute = int(match.group(1)), int(match.group(2))
    if not (0 <= hour <= 23 and 0 <= minute <= 59):
        raise RecordError(f"时间超出范围：{value!r}")
    return f"{hour:02d}:{minute:02d}"


def make_id(day: dt.date, seq: int) -> str:
    """编号规则：YYYYMMDD-NNN；同一日内递增，超过 999 自动加宽。"""
    return f"{day:%Y%m%d}-{seq:03d}"


def id_date(record_id: str) -> dt.date:
    match = _ID_RE.match(str(record_id or "").strip())
    if not match:
        raise RecordError(f"编号格式应为 20260214-001，收到：{record_id!r}")
    year, month, day = int(match.group(1)), int(match.group(2)), int(match.group(3))
    try:
        return dt.date(year, month, day)
    except ValueError:
        raise RecordError(f"编号里的日期不存在：{record_id!r}") from None


def id_seq(record_id: str) -> int:
    match = _ID_RE.match(str(record_id or "").strip())
    if not match:
        raise RecordError(f"编号格式应为 20260214-001，收到：{record_id!r}")
    return int(match.group(4))


def money(value, symbol: str = "¥") -> str:
    return f"{symbol}{Decimal(str(value)):,.2f}"


def month_before(day: dt.date, months: int = 1) -> dt.date:
    """往前推若干个月的「同一天」；遇到短月自动收到月末（3-31 往前一个月 = 2-28/29）。"""
    year, month = day.year, day.month
    for _ in range(max(1, months)):
        month -= 1
        if month == 0:
            year, month = year - 1, 12
    import calendar as _calendar

    last_day = _calendar.monthrange(year, month)[1]
    return dt.date(year, month, min(day.day, last_day))


@dataclass(slots=True)
class Record:
    """一笔收支。"""

    id: str
    date: dt.date
    time: str
    kind: str
    category: str
    amount: Decimal
    pay: str = ""
    note: str = ""

    # ---------- 序列化 ----------

    def to_day_row(self) -> list[str]:
        return [self.id, self.time, self.kind, self.category, fmt_amount(self.amount), self.pay, self.note]

    def to_export_row(self) -> list[str]:
        return [
            self.id,
            self.date.isoformat(),
            self.time,
            self.kind,
            self.category,
            fmt_amount(self.amount),
            self.pay,
            self.note,
        ]

    def to_dict(self) -> dict:
        return {
            "id": self.id,
            "date": self.date.isoformat(),
            "time": self.time,
            "kind": self.kind,
            "category": self.category,
            "amount": fmt_amount(self.amount),
            "pay": self.pay,
            "note": self.note,
        }

    def dedup_key(self) -> tuple:
        return (self.time, self.kind, self.category, fmt_amount(self.amount), self.pay, self.note)

    def sort_key(self) -> tuple:
        return (self.id, self.time)

    # ---------- 反序列化 ----------

    @classmethod
    def from_day_row(cls, day: dt.date, row: list[str], id_hint: str | None = None) -> "Record":
        """从每日 TSV 的一行构造记录；行不合法时抛 RecordError。"""
        cells = list(row) + [""] * (len(DAY_FIELDS) - len(row))
        raw_id = (cells[0] or "").strip() or (id_hint or "")
        if raw_id:
            try:
                if id_date(raw_id) != day:
                    raw_id = ""
            except RecordError:
                raw_id = ""
        if not raw_id:
            raise RecordError("缺少合法编号，无法确定这是第几笔")
        return cls(
            id=raw_id,
            date=day,
            time=normalize_time(cells[1]),
            kind=parse_kind(cells[2]),
            category=(cells[3] or "").strip() or "未分类",
            amount=parse_amount(cells[4]),
            pay=(cells[5] or "").strip(),
            note=(cells[6] or "").strip(),
        )
