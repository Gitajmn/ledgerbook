"""核心接口门面：给界面、命令行、以及**以后用别的语言写的前端**用。

约定：
- 入参、返回值都是可以直接 JSON 序列化的普通字典；
- 键名用英文（跨语言友好），值里的类型仍是数据里的中文「支出/收入」；
- 金额一律用字符串（如 "18.50"），避免浮点误差；
- 成功返回 {"ok": True, ...}，失败返回 {"ok": False, "error": "人话说明"}；
- 所有函数都不弹窗、不碰界面，只做数据的事。

命令行用法见 cli.py：python 记账.py api <命令> '<json 参数>'
"""

from __future__ import annotations

import datetime as dt
import json
import shutil
from decimal import Decimal
from pathlib import Path

from . import config, model as m, paths, query as query_mod, storage, summary, transfer
from .model import KINDS


def export_dir() -> Path:
    """导出目录：设置里填了就用它，否则用程序目录下的 导出\\。"""
    custom = str(config.load_settings().get("导出目录") or "").strip()
    return Path(custom).expanduser() if custom else paths.export_dir()


def backup_dir() -> Path:
    """备份目录：设置里填了就用它，否则用程序目录下的 备份\\。"""
    return storage.backup_root()

# ---------------------------------------------------------------- 小工具


def _ok(**payload) -> dict:
    return {"ok": True, **payload}


def _fail(message: str, kind: str = "错误") -> dict:
    return {"ok": False, "error": str(message), "error_type": kind}


def _record(record: m.Record) -> dict:
    return record.to_dict()


def _records(records) -> list[dict]:
    return [_record(record) for record in records]


def _date(value, default: dt.date | None = None) -> dt.date | None:
    if value in (None, ""):
        return default
    return m.parse_date(value)


def _payload_date(payload: dict, key: str) -> dt.date | None:
    return _date(payload.get(key))


def _filters_from(payload: dict) -> query_mod.Filters:
    start = _payload_date(payload, "start")
    end = _payload_date(payload, "end")
    preset = payload.get("preset")
    if preset:
        preset_start, preset_end = query_mod.resolve_preset(preset)
        start = start or preset_start
        end = end or preset_end
    return query_mod.Filters(
        start=start,
        end=end,
        kind=payload.get("kind") or None,
        category=payload.get("category") or None,
        keyword=payload.get("keyword") or None,
    )


# ---------------------------------------------------------------- 各命令


def cmd_info(payload: dict | None = None) -> dict:
    """版本、目录、数据范围、本月概况。界面状态栏和「关于」用。"""
    paths.ensure_dirs()
    first, last = query_mod.data_span()
    today = dt.date.today()
    month = query_mod.month_result(today.year, today.month)
    return _ok(
        app=paths.APP_NAME,
        version=paths.VERSION,
        data_format_version=paths.DATA_FORMAT_VERSION,
        dirs=paths.describe(),
        span={
            "first": first.isoformat() if first else None,
            "last": last.isoformat() if last else None,
        },
        this_month={**month.totals_dict(), "month": paths.month_name(today.year, today.month)},
        kinds=list(KINDS),
        presets=list(query_mod.PRESETS),
        strategies=list(transfer.STRATEGIES),
    )


def cmd_init(payload: dict | None = None) -> dict:
    config.ensure_initialized()
    return cmd_info(payload)


def cmd_add(payload: dict) -> dict:
    """新增一笔：{"date": "2026-02-14", "time": "09:12", "kind": "支出",
    "category": "餐饮", "amount": "18.50", "pay": "微信", "note": "早餐"}"""
    payload = payload or {}
    kind = m.parse_kind(payload.get("kind"))
    category = str(payload.get("category") or "").strip()
    if not category:
        raise ValueError("请选择分类")
    categories = config.load_categories()
    if category not in categories.get(kind, []):
        raise ValueError(f"「{kind}」里没有分类「{category}」，请先在设置里新增，或换个分类")
    record = storage.add(payload)
    return _ok(record=_record(record), message=f"已记入 {record.date} 第 {m.id_seq(record.id)} 笔")


def cmd_query(payload: dict | None = None) -> dict:
    """查询：{"preset": "本月"} 或 {"start": "...", "end": "...", "kind": "支出",
    "category": "餐饮", "keyword": "早餐"}"""
    payload = payload or {}
    result = query_mod.run(_filters_from(payload))
    limit = payload.get("limit")
    records = result.records
    truncated = False
    if isinstance(limit, int) and limit > 0 and len(records) > limit:
        records = records[-limit:]
        truncated = True

    # 带上「统计时算哪个分类」：类别被删掉的记录这里会是「其他」，但记录本身不变
    items = []
    for record in records:
        item = _record(record)
        item["category_effective"] = result.effective.get(record.id, record.category)
        items.append(item)

    return _ok(
        filters={
            "start": result.start.isoformat() if result.start else None,
            "end": result.end.isoformat() if result.end else None,
            "kind": payload.get("kind"),
            "category": payload.get("category"),
            "keyword": payload.get("keyword"),
        },
        totals=result.totals_dict(),
        records=items,
        truncated=truncated,
        by_category=[item.to_dict() for item in result.by_category],
        skipped=result.errors,
    )


def cmd_update(payload: dict) -> dict:
    """修改：{"ids": ["20260214-001"], "changes": {"金额": "20.00"}} 或
    {"id": "20260214-001", "changes": {...}}；changes 的键用英文字段名
    （date/time/kind/category/amount/pay/note）。"""
    payload = payload or {}
    ids = payload.get("ids")
    if not ids and payload.get("id"):
        ids = [payload["id"]]
    changes = payload.get("changes") or {
        key: payload[key]
        for key in ("date", "time", "kind", "category", "amount", "pay", "note")
        if key in payload
    }
    if not changes:
        raise ValueError("没有要修改的内容")
    touched = storage.update(ids or [], changes)
    return _ok(updated=_records(touched), count=len(touched))


def cmd_delete(payload: dict) -> dict:
    """删除：{"ids": ["20260214-001", "20260214-002"]}（编号不回收）"""
    payload = payload or {}
    ids = payload.get("ids")
    if not ids and payload.get("id"):
        ids = [payload["id"]]
    removed = storage.delete(ids or [])
    return _ok(deleted=removed)


def cmd_month(payload: dict) -> dict:
    """某月汇总：{"year": 2026, "month": 2}"""
    payload = payload or {}
    today = dt.date.today()
    year = int(payload.get("year") or today.year)
    month = int(payload.get("month") or today.month)
    if not 1 <= month <= 12:
        raise ValueError(f"月份要在 1-12 之间，收到：{month}")
    report = summary.build(year, month)
    return _ok(
        totals=report.totals_dict(),
        days=[line.to_dict() for line in report.days],
        by_category=[item.to_dict() for item in report.categories],
        skipped=report.errors,
    )


def cmd_month_export(payload: dict) -> dict:
    """把某月汇总写成 CSV：{"year": 2026, "month": 2}"""
    payload = payload or {}
    today = dt.date.today()
    year = int(payload.get("year") or today.year)
    month = int(payload.get("month") or today.month)
    return _ok(**summary.export(year, month))


def cmd_export(payload: dict | None = None) -> dict:
    """导出查询结果（不给条件就是全部）：{"preset": "本月", "name": "自定义文件名.csv",
    "out_dir": "D:\\\\某目录"}"""
    payload = payload or {}
    result = query_mod.run(_filters_from(payload))
    info = transfer.export_records(
        result.records,
        name=payload.get("name"),
        out_dir=payload.get("out_dir") or export_dir(),
        prefix=payload.get("prefix") or "查询",
    )
    return _ok(**info, totals=result.totals_dict())


def cmd_import_preview(payload: dict) -> dict:
    """导入预览：{"path": "D:\\...\\xx.csv"}"""
    payload = payload or {}
    path = payload.get("path")
    if not path:
        raise ValueError("请提供要导入的文件路径")
    default_date = _payload_date(payload, "default_date")
    parsed = transfer.parse_file(path, default_date=default_date)
    return _ok(**parsed.to_dict())


def cmd_import_apply(payload: dict) -> dict:
    """执行导入：{"path": "...", "strategy": "跳过重复", "auto_add_categories": False}"""
    payload = payload or {}
    path = payload.get("path")
    if not path:
        raise ValueError("请提供要导入的文件路径")
    default_date = _payload_date(payload, "default_date")
    parsed = transfer.parse_file(path, default_date=default_date)
    report = transfer.apply(
        parsed,
        strategy=payload.get("strategy") or transfer.STRATEGY_SKIP,
        auto_add_categories=bool(payload.get("auto_add_categories")),
    )
    return _ok(**report)


def cmd_days(payload: dict) -> dict:
    """某月里哪些天有数据：{"year": 2026, "month": 2}"""
    payload = payload or {}
    today = dt.date.today()
    year = int(payload.get("year") or today.year)
    month = int(payload.get("month") or today.month)
    items = []
    for day in storage.month_days(year, month):
        records = storage.read_day(day)
        income = sum((r.amount for r in records if r.kind == m.KIND_INCOME), Decimal("0.00"))
        expense = sum((r.amount for r in records if r.kind != m.KIND_INCOME), Decimal("0.00"))
        items.append(
            {
                "date": day.isoformat(),
                "count": len(records),
                "income": m.fmt_amount(income),
                "expense": m.fmt_amount(expense),
                "balance": m.fmt_amount(income - expense),
            }
        )
    return _ok(days=items)


def cmd_categories(payload: dict | None = None) -> dict:
    return _ok(categories=config.load_categories())


def cmd_add_category(payload: dict) -> dict:
    payload = payload or {}
    categories = config.add_category(payload.get("kind"), payload.get("category") or payload.get("name"))
    return _ok(categories=categories)


def cmd_rename_category(payload: dict) -> dict:
    payload = payload or {}
    categories = config.rename_category(
        payload.get("kind"), payload.get("old") or payload.get("from"), payload.get("new") or payload.get("to")
    )
    return _ok(categories=categories)


def cmd_remove_category(payload: dict) -> dict:
    payload = payload or {}
    categories = config.remove_category(payload.get("kind"), payload.get("category") or payload.get("name"))
    return _ok(categories=categories)


def cmd_save_categories(payload: dict) -> dict:
    """整体替换某一类的种类列表（设置页「修改」用）：
    {"kind": "收入", "categories": ["工资", "奖金"]}；顺序即下拉框顺序。"""
    payload = payload or {}
    names = payload.get("categories")
    if names is None:
        raise ValueError("请提供 categories 列表")
    categories = config.set_categories(payload.get("kind"), names)
    return _ok(categories=categories)


def cmd_settings(payload: dict | None = None) -> dict:
    """读设置：{"settings": {...}, "items": [{key, group, label, kind, value, choices...}]}"""
    return _ok(settings=config.load_settings(), items=config.setting_items())


def cmd_save_settings(payload: dict) -> dict:
    """改设置：{"settings": {"明细最多显示": 300}} 或直接 {"明细最多显示": 300}。

    只改传入的键，未知键会报错；键名与可选项见 cmd_settings 的 items。
    """
    payload = payload or {}
    incoming = payload.get("settings") if isinstance(payload.get("settings"), dict) else payload
    changes = {key: value for key, value in incoming.items() if key != "settings"}
    if not changes:
        raise ValueError("没有要修改的设置项")
    settings = config.apply_settings(changes)
    return _ok(settings=settings, changed=sorted(changes))


def cmd_reset_settings(payload: dict | None = None) -> dict:
    """全部恢复默认设置（分类.json 不受影响）。"""
    return _ok(settings=config.reset_settings(), message="设置已恢复默认")


def cmd_export_errors(payload: dict) -> dict:
    payload = payload or {}
    path = transfer.export_errors(payload.get("errors") or [], out_dir=export_dir())
    return _ok(file=str(path))


# ---------------------------------------------------------------- 配置数据

CONFIG_BUNDLE_KIND = "记账本配置"


def _config_bundle() -> dict:
    settings = config.load_settings()
    categories = config.load_categories()
    return {
        "类型": CONFIG_BUNDLE_KIND,
        "设置版本": config.SETTINGS_VERSION,
        "数据格式版本": paths.DATA_FORMAT_VERSION,
        "导出时间": dt.datetime.now().isoformat(timespec="seconds"),
        "程序版本": paths.VERSION,
        "设置": settings,
        "分类": categories,
    }


def _read_config_bundle(path: Path) -> dict:
    if not path.exists():
        raise FileNotFoundError(f"文件不存在：{path}")
    from .table import read_text

    try:
        data = json.loads(read_text(path))
    except ValueError as exc:
        raise ValueError(f"这个文件不是配置文件（JSON 解析失败：{exc}）") from None
    if not isinstance(data, dict) or data.get("类型") != CONFIG_BUNDLE_KIND:
        raise ValueError(f"这个文件不是记账本的配置包（缺少「类型：{CONFIG_BUNDLE_KIND}」标记）")
    settings = data.get("设置")
    categories = data.get("分类")
    if not isinstance(settings, dict) or not isinstance(categories, dict):
        raise ValueError("配置包里缺少「设置」或「分类」")
    for kind in KINDS:
        if not isinstance(categories.get(kind), list) or not categories[kind]:
            raise ValueError(f"配置包里的「{kind}」种类列表不合法")
    return data


def cmd_export_config(payload: dict | None = None) -> dict:
    """把「设置 + 种类」打包成一个文件（默认写到导出目录）。"""
    payload = payload or {}
    bundle = _config_bundle()
    directory = Path(payload.get("out_dir") or export_dir())
    directory.mkdir(parents=True, exist_ok=True)
    name = payload.get("name") or f"配置_{dt.datetime.now():%Y-%m-%d_%H%M%S}.json"
    target = directory / name
    from .table import atomic_write_text

    atomic_write_text(target, json.dumps(bundle, ensure_ascii=False, indent=2) + "\n")
    return _ok(
        file=str(target),
        设置项数=len(bundle["设置"]),
        种类数=sum(len(bundle["分类"][kind]) for kind in KINDS),
        summary=f"已导出配置：{target}",
    )


def cmd_import_config(payload: dict) -> dict:
    """导入配置数据：{"path": "...", "apply": false}

    不给 apply 时只做预览（不改任何东西）；给了 apply 才真正覆盖设置与种类，
    覆盖前会先把现有配置复制一份到备份目录的 配置\\时间戳\\ 下。
    """
    payload = payload or {}
    path_text = payload.get("path")
    if not path_text:
        raise ValueError("请提供配置文件路径")
    target = Path(path_text)
    bundle = _read_config_bundle(target)
    kinds_summary = "、".join(f"{kind} {len(bundle['分类'][kind])} 个" for kind in KINDS)
    summary = f"{target.name}：设置 {len(bundle['设置'])} 项，{kinds_summary}"
    if not payload.get("apply"):
        return _ok(preview=True, file=str(target), summary=summary, bundle=bundle)

    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    keep_dir = backup_dir() / "配置" / stamp
    keep_dir.mkdir(parents=True, exist_ok=True)
    for name in ("设置.json", "分类.json"):
        source = paths.config_dir() / name
        if source.exists():
            shutil.copy2(source, keep_dir / name)

    config.save_settings(bundle["设置"])
    config.save_categories(bundle["分类"])
    return _ok(
        preview=False,
        applied=True,
        file=str(target),
        summary=summary,
        backup=str(keep_dir),
        settings=config.load_settings(),
        categories=config.load_categories(),
    )


# ---------------------------------------------------------------- 分发

COMMANDS = {
    "info": cmd_info,
    "init": cmd_init,
    "add": cmd_add,
    "query": cmd_query,
    "update": cmd_update,
    "delete": cmd_delete,
    "month": cmd_month,
    "month_export": cmd_month_export,
    "export": cmd_export,
    "export_errors": cmd_export_errors,
    "import_preview": cmd_import_preview,
    "import_apply": cmd_import_apply,
    "days": cmd_days,
    "categories": cmd_categories,
    "add_category": cmd_add_category,
    "rename_category": cmd_rename_category,
    "remove_category": cmd_remove_category,
    "save_categories": cmd_save_categories,
    "settings": cmd_settings,
    "save_settings": cmd_save_settings,
    "reset_settings": cmd_reset_settings,
    "export_config": cmd_export_config,
    "import_config": cmd_import_config,
}


def dispatch(command: str, payload: dict | None = None) -> dict:
    """按命令名调用，永远返回字典，不抛异常。"""
    handler = COMMANDS.get(str(command or "").strip())
    if handler is None:
        return _fail(f"不认识的命令：{command}（可用命令：{'、'.join(sorted(COMMANDS))}）", "命令不存在")
    try:
        return handler(payload or {})
    except (ValueError, KeyError, OSError, storage.LockError) as exc:
        return _fail(exc, type(exc).__name__)
    except Exception as exc:  # 兜底，避免把堆栈弹到界面上
        return _fail(f"{type(exc).__name__}: {exc}", type(exc).__name__)
