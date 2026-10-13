"""每日文件存储：读、写、追加、编号分配、改删、备份、单实例锁。

设计要点：
- 每日 TSV 是唯一数据源，所有写操作都落到这里；
- 追加新记录用「追加一行」，不会重写整个文件，所以文件里即使有手工写坏的行也不会被抹掉；
- 修改/删除需要重写当日文件，此时把无法识别的行原样保留到文件末尾，并记入 errors 供界面提示；
- 重写用「临时文件 + 原子替换」，写一半崩溃不会损坏数据；
- 改、删、导入前自动把受影响月份快照到 备份\\YYYY-MM\\时间戳\\。
"""

from __future__ import annotations

import csv
import datetime as dt
import os
import shutil
from pathlib import Path

from . import config
from . import model as m
from . import paths
from .model import DAY_FIELDS, Record, RecordError
from .table import read_table, write_table

TSV_DELIMITER = "\t"


# ---------------------------------------------------------------- 读

def _is_header(row: list[str]) -> bool:
    if not row:
        return False
    if m._ID_RE.match((row[0] or "").strip()):
        return False
    joined = "".join(row)
    return any(field in joined for field in DAY_FIELDS)


def _field_indexes(header: list[str]) -> list[int]:
    """按列名定位 7 个字段；列名完全认不出时退回按位置。"""
    indexes: list[int] = []
    for field in DAY_FIELDS:
        found = -1
        for i, cell in enumerate(header):
            if cell == field:
                found = i
                break
        if found < 0:
            for i, cell in enumerate(header):
                if field in cell:
                    found = i
                    break
        indexes.append(found)
    if all(i < 0 for i in indexes):
        return list(range(len(DAY_FIELDS)))
    return indexes


def read_day_full(day: dt.date, errors: list[dict] | None = None) -> tuple[list[Record], list[list[str]]]:
    """读一天的记录，返回（记录列表, 无法识别的原始行）。"""
    path = paths.day_file(day)
    if not path.exists():
        return [], []
    header, rows = read_table(path)
    if _is_header(header):
        indexes = _field_indexes(header)
        first_line = 2
    else:  # 表头被删掉了，把首行当数据
        indexes = list(range(len(DAY_FIELDS)))
        rows = [header] + rows
        first_line = 1

    records: list[Record] = []
    bad: list[list[str]] = []
    for offset, raw in enumerate(rows):
        cells = [(raw[i] if 0 <= i < len(raw) else "") for i in indexes]
        if not any(cell.strip() for cell in cells):
            continue
        try:
            records.append(Record.from_day_row(day, cells))
        except (RecordError, KeyError, ValueError) as exc:
            bad.append(raw)
            if errors is not None:
                errors.append({"文件": str(path), "行号": first_line + offset, "原因": str(exc)})
    records.sort(key=lambda r: m.id_seq(r.id))
    return records, bad


def read_day(day: dt.date, errors: list[dict] | None = None) -> list[Record]:
    return read_day_full(day, errors)[0]


def month_days(year: int, month: int) -> list[dt.date]:
    """这个月里哪些日子有数据文件。"""
    directory = paths.month_dir(year, month)
    if not directory.exists():
        return []
    days: list[dt.date] = []
    for item in sorted(directory.glob("*.tsv")):
        try:
            days.append(dt.date.fromisoformat(item.stem))
        except ValueError:
            continue
    return days


def read_range(
    start: dt.date,
    end: dt.date,
    errors: list[dict] | None = None,
) -> list[Record]:
    """读一段日期内的全部记录（只碰相关月份目录）。"""
    records: list[Record] = []
    cursor = dt.date(start.year, start.month, 1)
    last = dt.date(end.year, end.month, 1)
    while cursor <= last:
        for day in month_days(cursor.year, cursor.month):
            if start <= day <= end:
                records.extend(read_day(day, errors))
        cursor = (cursor.replace(day=28) + dt.timedelta(days=4)).replace(day=1)
    records.sort(key=lambda r: (r.date, r.id))
    return records


# ---------------------------------------------------------------- 写

def allocate_id(day: dt.date, records) -> str:
    """当日最大序号 + 1；删除不回收号。"""
    biggest = 0
    for record in records:
        try:
            if m.id_date(record.id) == day:
                biggest = max(biggest, m.id_seq(record.id))
        except RecordError:
            continue
    return m.make_id(day, biggest + 1)


def write_day(day: dt.date, records: list[Record], extra_rows: list[list[str]] | None = None) -> Path:
    """重写一天的整个文件（按编号排序），保留无法识别的原始行。"""
    path = paths.day_file(day)
    ordered = sorted(records, key=lambda r: m.id_seq(r.id))
    rows = [record.to_day_row() for record in ordered]
    if extra_rows:
        rows.extend(extra_rows)
    write_table(path, list(DAY_FIELDS), rows, delimiter=TSV_DELIMITER)
    return path


def append_record(day: dt.date, record: Record) -> Path:
    """追加一行（不重写文件，最安全的新增方式）。"""
    path = paths.day_file(day)
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists() or path.stat().st_size == 0:
        write_table(path, list(DAY_FIELDS), [record.to_day_row()], delimiter=TSV_DELIMITER)
        return path
    with open(path, "a", encoding="utf-8", newline="") as handle:
        csv.writer(handle, delimiter=TSV_DELIMITER, lineterminator="\r\n").writerow(record.to_day_row())
        handle.flush()
        os.fsync(handle.fileno())
    return path


def _clean(**fields) -> dict:
    """校验并规整待写入字段。"""
    out: dict = {}
    if "date" in fields and fields["date"] is not None:
        out["date"] = m.parse_date(fields["date"])
    if "time" in fields:
        out["time"] = m.normalize_time(fields["time"])
    if "kind" in fields and fields["kind"] is not None:
        out["kind"] = m.parse_kind(fields["kind"])
    if "category" in fields:
        category = str(fields["category"] or "").strip()
        if not category:
            raise RecordError("请选择分类")
        out["category"] = category
    if "amount" in fields and fields["amount"] is not None:
        out["amount"] = m.parse_amount(fields["amount"])
    if "pay" in fields:
        out["pay"] = str(fields["pay"] or "").strip()
    if "note" in fields:
        out["note"] = str(fields["note"] or "").strip()
    return out


def add(draft: dict) -> Record:
    """新增一笔。draft 里 date/time 可省略（默认今天/此刻）。"""
    day = m.parse_date(draft.get("date")) if draft.get("date") else dt.date.today()
    fields = _clean(
        time=draft.get("time") or "",
        kind=draft.get("kind"),
        category=draft.get("category"),
        amount=draft.get("amount"),
        pay=draft.get("pay") or "",
        note=draft.get("note") or "",
    )
    records = read_day(day)
    record = Record(
        id=allocate_id(day, records),
        date=day,
        time=fields["time"],
        kind=fields["kind"],
        category=fields["category"],
        amount=fields["amount"],
        pay=fields["pay"],
        note=fields["note"],
    )
    append_record(day, record)
    return record


def _group_by_day(ids: list[str]) -> tuple[dict[dt.date, dict[str, Record]], dict[dt.date, list[list[str]]], dict[dt.date, list[str]]]:
    days: dict[dt.date, dict[str, Record]] = {}
    extras: dict[dt.date, list[list[str]]] = {}
    order: dict[dt.date, list[str]] = {}
    for record_id in ids:
        day = m.id_date(record_id)
        if day not in days:
            records, bad = read_day_full(day)
            days[day] = {r.id: r for r in records}
            extras[day] = bad
            order[day] = [r.id for r in records]
        if record_id not in days[day]:
            raise KeyError(f"找不到记录 {record_id}")
    return days, extras, order


def _as_list(ids) -> list[str]:
    if isinstance(ids, str):
        return [ids.strip()] if ids.strip() else []
    return [str(i).strip() for i in (ids or []) if str(i).strip()]


def update(ids, changes: dict) -> list[Record]:
    """修改记录；改了日期就移动到另一天，并重新分配编号。"""
    id_list = _as_list(ids)
    if not id_list:
        raise ValueError("没有选中要修改的记录")
    days, extras, order = _group_by_day(id_list)
    fields = _clean(**{k: v for k, v in (changes or {}).items() if k in
                       ("date", "time", "kind", "category", "amount", "pay", "note")})
    if not fields:
        raise ValueError("没有任何要修改的内容")

    touched: list[Record] = []
    for record_id in id_list:
        day = m.id_date(record_id)
        record = days[day][record_id]
        new_date = fields.get("date", record.date)
        for name, value in fields.items():
            if name != "date":
                setattr(record, name, value)
        if new_date != day:
            del days[day][record_id]
            order[day].remove(record_id)
            if new_date not in days:
                records, bad = read_day_full(new_date)
                days[new_date] = {r.id: r for r in records}
                extras[new_date] = bad
                order[new_date] = [r.id for r in records]
            record.date = new_date
            record.id = allocate_id(new_date, list(days[new_date].values()))
            days[new_date][record.id] = record
            order[new_date].append(record.id)
        touched.append(record)

    backup_days(sorted(days))
    for day in sorted(days):
        write_day(day, [days[day][i] for i in order[day] if i in days[day]], extras.get(day))
    return touched


def delete(ids) -> int:
    """删除记录，返回删除条数；编号不回收、不重排。"""
    id_list = _as_list(ids)
    if not id_list:
        raise ValueError("没有选中要删除的记录")
    days, extras, order = _group_by_day(id_list)
    backup_days(sorted(days))
    removed = 0
    for day in sorted(days):
        targets = {i for i in id_list if m.id_date(i) == day}
        remaining = [days[day][i] for i in order[day] if i in days[day] and i not in targets]
        removed += len(days[day]) - len(remaining)
        write_day(day, remaining, extras.get(day))
    return removed


# ---------------------------------------------------------------- 备份

def backup_root() -> Path:
    """备份根目录：设置里填了就用它，否则用程序目录下的 备份\\。"""
    custom = str(config.load_settings().get("备份目录") or "").strip()
    return Path(custom).expanduser() if custom else paths.backup_dir()


def backup_days(days: list[dt.date]) -> list[Path]:
    """把受影响月份的每日文件快照到 <备份目录>\\YYYY-MM\\YYYYMMDD-HHMMSS\\。

    备份目录可以在「设置 → 数据与备份」里改；自动备份也能在那里关掉（关掉时这里直接返回空）。
    """
    if not days:
        return []
    if not config.get("自动备份", True):
        return []
    months = sorted({(day.year, day.month) for day in days})
    created: list[Path] = []
    for year, month in months:
        source = paths.month_dir(year, month)
        if not source.exists():
            continue
        stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
        target = backup_root() / f"{year:04d}-{month:02d}" / stamp
        suffix = 1
        while target.exists():
            suffix += 1
            target = target.parent / f"{stamp}-{suffix}"
        target.mkdir(parents=True, exist_ok=True)
        for item in sorted(source.glob("*.tsv")):
            shutil.copy2(item, target / item.name)
        created.append(target)
    return created


# ---------------------------------------------------------------- 单实例锁

class LockError(RuntimeError):
    """已有另一个程序实例在运行。"""


class Lock:
    """用系统文件锁保证同一份数据只有一个程序在写。"""

    def __init__(self, path: Path | str | None = None) -> None:
        self.path = Path(path) if path else paths.lock_file()
        self._handle = None

    def acquire(self) -> "Lock":
        self.path.parent.mkdir(parents=True, exist_ok=True)
        handle = open(self.path, "a+", encoding="utf-8")
        try:
            if os.name == "nt":
                import msvcrt

                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl

                fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            handle.close()
            raise LockError("记账本已经在运行了：同一份数据不能同时开两个窗口") from None
        self._handle = handle
        try:  # 写点人看得懂的信息；锁本身由系统文件锁保证
            from .paths import VERSION

            info = f"pid={os.getpid()} 版本={VERSION} 启动={dt.datetime.now():%Y-%m-%d %H:%M:%S}"
            handle.seek(0)
            handle.write(f"{info:<72}")
            handle.flush()
        except OSError:
            pass
        return self

    def release(self) -> None:
        if self._handle is None:
            return
        try:
            if os.name == "nt":
                import msvcrt

                self._handle.seek(0)
                msvcrt.locking(self._handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl

                fcntl.flock(self._handle.fileno(), fcntl.LOCK_UN)
        except OSError:
            pass
        finally:
            try:
                self._handle.close()
            except OSError:
                pass
            self._handle = None

    def __enter__(self) -> "Lock":
        return self.acquire()

    def __exit__(self, *exc) -> None:
        self.release()


# ---------------------------------------------------------------- 其他

def total_records() -> int:
    count = 0
    for path in paths.data_dir().glob("*/*/*.tsv"):
        count += len(read_day(dt.date.fromisoformat(path.stem)))
    return count
