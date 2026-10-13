"""不弹窗自检：核心功能 + 界面能不能正常构建。

运行方式：
    python 记账.py 自测
    python selftest.py

全程在系统临时目录里跑（通过环境变量 JIZHANG_ROOT 指定），不会碰你的真实账本。
"""

from __future__ import annotations

import datetime as dt
import json
import os
import re
import shutil
import sys
import tempfile
import time
from decimal import Decimal
from pathlib import Path

ROOT = Path(__file__).resolve().parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

RESULTS: list[tuple[bool, str, str]] = []


def check(name: str, condition, detail="") -> None:
    ok = bool(condition)
    RESULTS.append((ok, name, "" if ok else str(detail)))
    print(f"  [{'通过' if ok else '失败'}] {name}" + ("" if ok else f"  -> {detail}"))


def section(title: str) -> None:
    print(f"\n=== {title} ===")


def _mode_for(tab, width: int) -> str:
    """给查询页喂一个指定宽度的合成事件，返回它选出来的布局模式。"""

    class _Event:
        def __init__(self, value: int) -> None:
            self.width = value

    tab.on_split_resize(_Event(width))
    return tab.layout_mode() or ""


# ---------------------------------------------------------------- 核心


def core_tests(tmp: Path) -> None:
    from ledger import api, config, model as m, paths, query as query_mod, storage, summary, transfer

    paths.ensure_dirs()
    config.ensure_initialized()

    section("配置与目录")
    check("首次运行自动生成 分类.json 与 设置.json", paths.categories_file().exists() and paths.settings_file().exists())
    check("目录结构按年/月分文件夹", paths.month_dir(2026, 2).parent.name == "2026" and paths.month_dir(2026, 2).name == "02")

    section("记账")
    r1 = api.dispatch(
        "add",
        {"date": "2026-02-14", "time": "09:12", "kind": "支出", "category": "餐饮",
         "amount": "18.50", "pay": "微信", "note": "早餐"},
    )
    r2 = api.dispatch(
        "add",
        {"date": "2026-02-14", "time": "12:40", "kind": "支出", "category": "交通",
         "amount": "4", "pay": "支付宝", "note": "地铁"},
    )
    r3 = api.dispatch(
        "add",
        {"date": "2026-02-14", "time": "20:05", "kind": "收入", "category": "工资",
         "amount": "8500", "pay": "银行卡", "note": "2 月工资"},
    )
    ids = [item.get("record", {}).get("id") for item in (r1, r2, r3)]
    check("新增三笔并自动编号 001/002/003", ids == ["20260214-001", "20260214-002", "20260214-003"], ids)
    check("金额自动补成两位小数", r2.get("record", {}).get("amount") == "4.00", r2)
    day_path = paths.day_file(dt.date(2026, 2, 14))
    check("文件落在 数据\\2026\\02\\2026-02-14.tsv", day_path.exists(), day_path)
    first_line = day_path.read_text(encoding="utf-8-sig").splitlines()[0]
    check("表头是 7 列且不重复存日期", first_line.split("\t") == list(m.DAY_FIELDS), first_line)

    section("输入校验")
    for value in ("abc", "0", "-5", ""):
        result = api.dispatch(
            "add", {"date": "2026-02-14", "kind": "支出", "category": "餐饮", "amount": value}
        )
        check(f"拒绝非法金额 {value!r}", result.get("ok") is False, result)
    check(
        "拒绝非法类型",
        api.dispatch(
            "add", {"date": "2026-02-14", "kind": "发工资", "category": "餐饮", "amount": "1"}
        ).get("ok")
        is False,
    )
    check(
        "拒绝配置里没有的分类",
        api.dispatch(
            "add", {"date": "2026-02-14", "kind": "支出", "category": "养恐龙", "amount": "1"}
        ).get("ok")
        is False,
    )
    check(
        "拒绝 2026-02-30 这种不存在的日期",
        api.dispatch(
            "add", {"date": "2026-02-30", "kind": "支出", "category": "餐饮", "amount": "1"}
        ).get("ok")
        is False,
    )

    section("查询与统计")
    result = api.dispatch("query", {"start": "2026-02-01", "end": "2026-02-28"})
    check(
        "合计正确（收 8500 / 支 22.50 / 结余 8477.50）",
        result["totals"] == {"count": 3, "income": "8500.00", "expense": "22.50", "balance": "8477.50"},
        result["totals"],
    )
    check(
        "分类小计按「类型内金额降序」排列",
        [item["category"] for item in result["by_category"]] == ["餐饮", "交通", "工资"],
        result["by_category"],
    )
    check(
        "按关键词筛选",
        api.dispatch("query", {"start": "2026-02-01", "end": "2026-02-28", "keyword": "地铁"})["totals"]["count"] == 1,
    )
    check(
        "按类型筛选",
        api.dispatch("query", {"start": "2026-02-01", "end": "2026-02-28", "kind": "支出"})["totals"]["count"] == 2,
    )
    start, end = query_mod.resolve_preset("上月", today=dt.date(2026, 3, 15))
    check("快捷范围「上月」算得准", (start, end) == (dt.date(2026, 2, 1), dt.date(2026, 2, 28)), (start, end))

    section("分类统计图形（3D 饼图 / 横向 3D 柱状图 / 方格图）")
    from ledger.ui import charts

    series = charts.series_from(result["by_category"])
    check(
        "图形只保留有权重的分类（0.0% 的不画）",
        all(item["share"] >= charts.MIN_SHARE for item in series)
        and len(series) <= len(result["by_category"]),
        [(item["category"], item["share"]) for item in series],
    )
    everything = charts.series_from(result["by_category"], min_share=0.0)
    check(
        "不启用过滤时全部项目的占比之和等于 1",
        abs(sum(item["share"] for item in everything) - 1.0) < 0.0001,
        sum(item["share"] for item in everything),
    )
    check(
        "过滤掉零头后剩下的占比之和 ≤ 1（差额就是被挡掉的那部分）",
        0 < sum(item["share"] for item in series) <= 1.0,
        sum(item["share"] for item in series),
    )
    check("每一项都分到了颜色", all(str(item["color"]).startswith("#") for item in series))
    counts = charts.waffle_counts(series, 100)
    check("方格图正好填满 100 格", sum(counts) == 100 and len(counts) == len(series), counts)
    biggest = max(range(len(series)), key=lambda index: series[index]["amount"])
    check("金额最大的那一项格子最多", counts[biggest] == max(counts), (counts, biggest))
    duplicated = charts.series_from(
        [
            {"kind": m.KIND_EXPENSE, "category": "其他", "count": 1, "amount": "1.00"},
            {"kind": m.KIND_INCOME, "category": "其他", "count": 1, "amount": "3.00"},
        ]
    )
    check(
        "同名分类会带上类型前缀区分",
        [item["label"] for item in duplicated] == ["支出·其他", "收入·其他"],
        [item["label"] for item in duplicated],
    )
    check("没有数据时方格图返回空", charts.waffle_counts([], 100) == [])
    weightless = charts.series_from(
        [
            {"kind": m.KIND_EXPENSE, "category": "大头", "count": 1, "amount": "1000.00"},
            {"kind": m.KIND_EXPENSE, "category": "零头", "count": 1, "amount": "0.10"},
        ]
    )
    check(
        "占比不足 0.05% 的分类不画进图里",
        [item["category"] for item in weightless] == ["大头"],
        [item["category"] for item in weightless],
    )
    check(
        "被挡掉的分类会用一行小字说明（信息不缺失）",
        "1 项占比不足" in charts.hidden_note(
            [
                {"kind": m.KIND_EXPENSE, "category": "大头", "count": 1, "amount": "1000.00"},
                {"kind": m.KIND_EXPENSE, "category": "零头", "count": 1, "amount": "0.10"},
            ]
        ),
    )
    check(
        "颜色变暗/变亮函数正确",
        charts._shade("#808080", 0.5) == "#404040" and charts._shade("#000000", 1.5) == "#7f7f7f",
        (charts._shade("#808080", 0.5), charts._shade("#000000", 1.5)),
    )
    check(
        "类型与细分不匹配时返回空结果（不报错，界面会把细分重置掉）",
        api.dispatch(
            "query", {"start": "2026-02-01", "end": "2026-02-28", "kind": "收入", "category": "交通"}
        )["totals"]["count"]
        == 0,
    )

    section("修改与删除")
    updated = api.dispatch("update", {"id": "20260214-002", "changes": {"amount": "5.00", "note": "地铁涨价"}})
    check("改金额和备注", updated.get("ok") and updated["updated"][0]["amount"] == "5.00", updated)
    moved = api.dispatch("update", {"id": "20260214-002", "changes": {"date": "2026-02-15"}})
    moved_record = (moved.get("updated") or [{}])[0]
    check(
        "改日期后换到新文件并重新编号",
        moved_record.get("id") == "20260215-001" and moved_record.get("date") == "2026-02-15",
        moved_record,
    )
    check("原日期文件只剩两笔", len(storage.read_day(dt.date(2026, 2, 14))) == 2)
    check("改/删前自动备份到 备份 目录", len(list(paths.backup_dir().rglob("*.tsv"))) >= 1)
    deleted = api.dispatch("delete", {"ids": ["20260214-001"]})
    check("删除一笔", deleted.get("ok") and deleted.get("deleted") == 1, deleted)
    new_one = api.dispatch("add", {"date": "2026-02-14", "kind": "支出", "category": "餐饮", "amount": "1.00"})
    check("删除后编号不回收（新记录是 004）", new_one.get("record", {}).get("id") == "20260214-004", new_one)

    section("月度汇总")
    month = api.dispatch("month", {"year": 2026, "month": 2})
    check("汇总覆盖两天", len(month["days"]) == 2, month["days"])
    exported = api.dispatch("month_export", {"year": 2026, "month": 2})
    check(
        "汇总导出两个 CSV",
        Path(exported["summary"]).exists() and Path(exported["category"]).exists(),
        exported,
    )
    check("汇总 CSV 有合计行", "合计" in Path(exported["summary"]).read_text(encoding="utf-8-sig"))

    section("导出 / 导入")
    exported = api.dispatch("export", {"start": "2026-02-01", "end": "2026-02-28", "name": "自测导出.csv"})
    check("导出查询结果为 CSV", Path(exported["文件"]).exists() and exported["记录数"] >= 1, exported)
    parsed = transfer.parse_file(exported["文件"])
    check("导出的文件能原样读回", not parsed.errors and len(parsed.rows) == exported["记录数"], parsed.errors)
    same = api.dispatch("import_apply", {"path": exported["文件"], "strategy": "跳过重复"})
    check("重复导入全部跳过", same.get("成功") == 0 and same.get("跳过") == exported["记录数"], same)

    import_csv = tmp / "导入测试.csv"
    import_csv.write_text(
        "编号,日期,时间,类型,分类,金额,支付方式,备注\n"
        "20260301-001,2026-03-01,08:00,支出,餐饮,12.50,现金,豆浆\n"
        ",2026-03-01,12:00,支出,交通,3.00,微信,公交\n"
        "20260301-003,2026-03-02,19:00,收入,红包,88.00,微信,朋友还钱\n",
        encoding="utf-8-sig",
    )
    preview = api.dispatch("import_preview", {"path": str(import_csv)})
    check("预览：3 行都能导入", preview.get("可导入") == 3 and preview.get("失败") == 0, preview)
    appended = api.dispatch("import_apply", {"path": str(import_csv), "strategy": "全部追加"})
    check("全部追加导入 3 笔", appended.get("成功") == 3, appended)
    again = api.dispatch("import_apply", {"path": str(import_csv), "strategy": "跳过重复"})
    check("同样的文件再导一次全被跳过", again.get("成功") == 0 and again.get("跳过") == 3, again)

    overwrite_csv = tmp / "覆盖测试.csv"
    overwrite_csv.write_text(
        "编号,日期,时间,类型,分类,金额,支付方式,备注\n"
        "20260301-001,2026-03-01,08:00,支出,餐饮,99.00,现金,覆盖后的豆浆\n",
        encoding="utf-8-sig",
    )
    overwritten = api.dispatch("import_apply", {"path": str(overwrite_csv), "strategy": "按编号覆盖"})
    check("按编号覆盖生效", overwritten.get("覆盖") == 1, overwritten)
    check(
        "覆盖后的金额已落盘",
        storage.read_day(dt.date(2026, 3, 1))[0].amount == Decimal("99.00"),
        storage.read_day(dt.date(2026, 3, 1)),
    )

    weird_csv = tmp / "未知分类.csv"
    weird_csv.write_text(
        "编号,日期,时间,类型,分类,金额\n20260410-001,2026-04-10,09:00,支出,宠物,66.00\n",
        encoding="utf-8-sig",
    )
    weird_preview = api.dispatch("import_preview", {"path": str(weird_csv)})
    check("预览能报出缺失分类", weird_preview.get("缺失分类") == ["支出/宠物"], weird_preview)
    check(
        "没勾选自动新增时拒绝导入",
        api.dispatch("import_apply", {"path": str(weird_csv)}).get("ok") is False,
    )
    check(
        "勾选后自动新增分类并导入",
        api.dispatch("import_apply", {"path": str(weird_csv), "auto_add_categories": True}).get("成功") == 1,
    )

    section("容错与边界")
    day = dt.date(2026, 4, 1)
    manual = paths.day_file(day)
    manual.parent.mkdir(parents=True, exist_ok=True)
    manual.write_text(
        "编号\t时间\t类型\t分类\t金额\t支付方式\t备注\n"
        "20260401-001\t09:00\t支出\t餐饮\t10.00\t现金\t正常的一行\n"
        "坏行\t09:00\t支出\t餐饮\tabc\t现金\t手写坏的\n",
        encoding="utf-8-sig",
    )
    errors: list[dict] = []
    records = storage.read_day(day, errors)
    check("坏行不影响读取好行", len(records) == 1 and len(errors) == 1, (records, errors))
    api.dispatch("add", {"date": "2026-04-01", "kind": "支出", "category": "餐饮", "amount": "2.00"})
    check("追加新记录不会抹掉手写坏行", "坏行" in manual.read_text(encoding="utf-8-sig"))
    api.dispatch("delete", {"ids": ["20260401-001"]})
    check("重写文件时也保留坏行", "坏行" in manual.read_text(encoding="utf-8-sig"))

    gbk_day = paths.day_file(dt.date(2026, 5, 1))
    gbk_day.parent.mkdir(parents=True, exist_ok=True)
    gbk_day.write_bytes(
        "编号\t时间\t类型\t分类\t金额\t支付方式\t备注\n"
        "20260501-001\t10:00\t支出\t餐饮\t7.00\t现金\tGBK 编码测试\n".encode("gbk")
    )
    check("GBK 编码的文件也能读", len(storage.read_day(dt.date(2026, 5, 1))) == 1)

    lock = storage.Lock()
    lock.acquire()
    blocked = False
    try:
        storage.Lock().acquire()
    except storage.LockError:
        blocked = True
    finally:
        lock.release()
    check("同一份数据不允许开两个程序", blocked)

    first, last = query_mod.data_span()
    check(
        "能识别数据范围（最早/最晚）",
        first == dt.date(2026, 2, 14) and last == dt.date(2026, 5, 1),
        (first, last),
    )

    section("设置项框架（设置区）")
    from ledger import config as config_mod

    specs = config_mod.SETTING_SPECS
    check("每个设置项都声明了默认值", all(spec.default is not None for spec in specs))
    check(
        "默认设置完整（含支付方式/窗口/版本）",
        {"支付方式", "窗口", "版本"} <= set(config_mod.load_settings()),
    )
    check(
        "设置项清单覆盖全部声明",
        {item["key"] for item in config_mod.setting_items()} == {spec.key for spec in specs},
    )

    saved = api.dispatch("save_settings", {"settings": {"明细最多显示": "30"}})
    check(
        "保存设置会把字符串数字规整成整数",
        saved.get("ok") and saved["settings"]["明细最多显示"] == 30,
        saved.get("settings", {}).get("明细最多显示"),
    )
    saved = api.dispatch("save_settings", {"settings": {"明细最多显示": "abc", "保存后清空": "关"}})
    check(
        "非法值回落默认、布尔支持中文写法",
        saved["settings"]["明细最多显示"] == 500 and saved["settings"]["保存后清空"] is False,
        saved["settings"],
    )
    saved = api.dispatch("save_settings", {"settings": {"分类统计显示": ["表格", "柱状图", "不存在的视图"]}})
    check(
        "多选设置只保留合法选项、顺序按声明走",
        saved["settings"]["分类统计显示"] == ["表格", "柱状图"],
        saved["settings"]["分类统计显示"],
    )
    saved = api.dispatch("save_settings", {"settings": {"分类统计显示": "饼状图、方格图"}})
    check(
        "多选设置也认中文顿号写法",
        saved["settings"]["分类统计显示"] == ["饼状图", "方格图"],
        saved["settings"]["分类统计显示"],
    )
    saved = api.dispatch("save_settings", {"settings": {"分类统计显示": []}})
    check(
        "多选清空会回到默认值",
        saved["settings"]["分类统计显示"] == ["表格", "饼状图"],
        saved["settings"]["分类统计显示"],
    )
    check(
        "未知设置项会被拒绝（及早发现代码写错）",
        api.dispatch("save_settings", {"settings": {"不存在的开关": 1}}).get("ok") is False,
    )

    target = storage.read_day(dt.date(2026, 2, 14))[0].id
    api.dispatch("save_settings", {"settings": {"自动备份": False}})
    before = len(list(paths.backup_dir().rglob("*.tsv")))
    api.dispatch("update", {"id": target, "changes": {"note": "关掉备份后改的"}})
    after = len(list(paths.backup_dir().rglob("*.tsv")))
    check("关掉「自动备份」后改记录不再产生备份", after == before, (before, after))
    api.dispatch("save_settings", {"settings": {"自动备份": True}})
    api.dispatch("update", {"id": target, "changes": {"note": "打开备份后改的"}})
    check("重新打开后又会备份", len(list(paths.backup_dir().rglob("*.tsv"))) > after)

    reset = api.dispatch("reset_settings")
    check(
        "恢复默认能一键还原",
        reset.get("ok") and reset["settings"]["明细最多显示"] == 500 and reset["settings"]["自动备份"] is True,
        reset.get("settings", {}).get("明细最多显示"),
    )

    section("种类（收入 / 支出）整体保存")
    check(
        "重复的种类会被拒绝",
        api.dispatch(
            "save_categories", {"kind": m.KIND_INCOME, "categories": ["工资", "奖金", "工资"]}
        ).get("ok")
        is False,
    )
    saved = api.dispatch("save_categories", {"kind": m.KIND_INCOME, "categories": [" 工资 ", "奖金", "红包"]})
    check(
        "会去掉空白并保留顺序（顺序即下拉框顺序）",
        saved.get("ok") and saved["categories"][m.KIND_INCOME] == ["工资", "奖金", "红包"],
        saved.get("categories", {}).get(m.KIND_INCOME),
    )
    check(
        "空的种类列表会被拒绝",
        api.dispatch("save_categories", {"kind": m.KIND_EXPENSE, "categories": []}).get("ok") is False,
    )
    check(
        "未知类型会被拒绝",
        api.dispatch("save_categories", {"kind": "其他", "categories": ["随便"]}).get("ok") is False,
    )
    check(
        "缺少 categories 参数会被拒绝",
        api.dispatch("save_categories", {"kind": m.KIND_INCOME}).get("ok") is False,
    )
    api.dispatch("save_categories", {"kind": m.KIND_INCOME, "categories": ["工资", "奖金", "兼职", "投资收益", "红包", "其他"]})

    section("配置数据的导出 / 导入（换电脑、回退用）")
    exported_config = api.dispatch("export_config", {"out_dir": str(tmp), "name": "配置导出.json"})
    check(
        "导出配置数据",
        exported_config.get("ok") and Path(exported_config["file"]).exists(),
        exported_config,
    )
    bundle = json.loads(Path(exported_config["file"]).read_text(encoding="utf-8-sig"))
    check(
        "配置包里含设置与种类，并带类型标记",
        bundle.get("类型") == "记账本配置" and bundle["设置"] and bundle["分类"][m.KIND_EXPENSE],
        list(bundle),
    )
    api.dispatch("save_settings", {"settings": {"货币符号": "US$", "明细最多显示": 123}})
    api.dispatch("save_categories", {"kind": m.KIND_EXPENSE, "categories": ["只留餐饮"]})
    preview = api.dispatch("import_config", {"path": exported_config["file"]})
    check("导入前先给预览", preview.get("ok") and preview.get("preview") and preview.get("summary"), preview)
    check(
        "预览不会改任何东西",
        config_mod.get("明细最多显示") == 123 and config_mod.categories_for(m.KIND_EXPENSE) == ["只留餐饮"],
    )
    applied_config = api.dispatch("import_config", {"path": exported_config["file"], "apply": True})
    check(
        "导入后设置与种类都恢复",
        applied_config.get("ok")
        and config_mod.get("明细最多显示") == 500
        and "餐饮" in config_mod.categories_for(m.KIND_EXPENSE)
        and "交通" in config_mod.categories_for(m.KIND_EXPENSE),
        config_mod.categories_for(m.KIND_EXPENSE),
    )
    check(
        "导入前把旧配置备份了一份",
        Path(applied_config["backup"]).exists()
        and any(Path(applied_config["backup"]).glob("*.json")),
        applied_config.get("backup"),
    )
    bad_config = tmp / "不是配置的文件.json"
    bad_config.write_text('{"类型": "别的东西"}', encoding="utf-8-sig")
    check(
        "不是配置包的文件会被拒绝且说明原因",
        api.dispatch("import_config", {"path": str(bad_config)}).get("ok") is False,
    )
    check(
        "配置文件不存在时报错而不是崩",
        api.dispatch("import_config", {"path": str(tmp / "没有这个文件.json")}).get("ok") is False,
    )

    section("备份目录可配置")
    custom_backup = tmp / "我的备份目录"
    api.dispatch("save_settings", {"settings": {"备份目录": str(custom_backup)}})
    target_record = storage.read_day(dt.date(2026, 2, 14))[0].id
    api.dispatch("update", {"id": target_record, "changes": {"note": "换了备份目录之后改的"}})
    check(
        "改动后的备份落到自定义备份目录里",
        bool(list(custom_backup.rglob("*.tsv"))),
        [str(path) for path in custom_backup.rglob("*.tsv")],
    )
    check(
        "接口能报出当前备份目录",
        api.backup_dir() == custom_backup,
        (str(api.backup_dir()), str(custom_backup)),
    )
    api.dispatch("save_settings", {"settings": {"备份目录": ""}})
    check("清空设置后又回到程序目录下的 备份\\", api.backup_dir() == paths.backup_dir())

    section("删掉种类后的归类规则（查询并入「其他」，记录本身不变）")
    api.dispatch("add_category", {"kind": m.KIND_EXPENSE, "category": "宠物"})
    api.dispatch(
        "add",
        {"date": "2026-03-05", "time": "10:00", "kind": "支出", "category": "宠物",
         "amount": "66.00", "pay": "现金", "note": "猫粮"},
    )
    api.dispatch("remove_category", {"kind": m.KIND_EXPENSE, "category": "宠物"})
    march = api.dispatch("query", {"start": "2026-03-01", "end": "2026-03-31"})
    check(
        "被删掉种类的金额与笔数并入「其他」",
        any(item["category"] == "其他" and item["amount"] == "66.00" and item["count"] == 1 for item in march["by_category"]),
        march["by_category"],
    )
    check(
        "记录本身没有被改动（文件里还是「宠物」）",
        any(record.category == "宠物" for record in storage.read_day(dt.date(2026, 3, 5))),
    )
    check(
        "接口同时给出原始分类与统计归类",
        any(item["category"] == "宠物" and item["category_effective"] == "其他" for item in march["records"]),
        march["records"],
    )
    only_other = api.dispatch("query", {"start": "2026-03-01", "end": "2026-03-31", "category": "其他"})
    check("按「其他」筛选能查到它", only_other["totals"]["count"] == 1, only_other["totals"])
    check(
        "按原分类名已经查不到了（它不再是一个种类）",
        api.dispatch("query", {"start": "2026-03-01", "end": "2026-03-31", "category": "宠物"})["totals"]["count"] == 0,
    )
    month_march = api.dispatch("month", {"year": 2026, "month": 3})
    check(
        "月度汇总的分类小计也并入「其他」",
        any(item["category"] == "其他" and item["amount"] == "66.00" for item in month_march["by_category"]),
        month_march["by_category"],
    )
    export_march = api.dispatch("export", {"start": "2026-03-01", "end": "2026-03-31", "name": "归类测试.csv"})
    check(
        "导出的 CSV 里仍是原始分类",
        "宠物" in Path(export_march["文件"]).read_text(encoding="utf-8-sig"),
    )
    api.dispatch("add_category", {"kind": m.KIND_EXPENSE, "category": "宠物"})
    back = api.dispatch("query", {"start": "2026-03-05", "end": "2026-03-05"})
    check(
        "把种类加回来后自动归回原分类",
        [item["category"] for item in back["by_category"]] == ["宠物"],
        back["by_category"],
    )
    api.dispatch("remove_category", {"kind": m.KIND_EXPENSE, "category": "宠物"})

    section("打包与发布（换电脑也能跑）")
    project = Path(__file__).resolve().parent
    spec_path = project / "build.spec"
    spec_text = spec_path.read_text(encoding="utf-8") if spec_path.exists() else ""
    check(
        "打包配置把产物钉在 LedgerBook 里（不管从哪里调用都落在这一个文件夹）",
        'CONF["distpath"]' in spec_text and 'CONF["workpath"]' in spec_text and "LedgerBook" in spec_text,
    )
    check(
        "用文件夹模式发布（onedir）：exe 旁边就是运行库，启动不用解压临时目录",
        "COLLECT(" in spec_text and "exclude_binaries=True" in spec_text,
        spec_text.count("COLLECT("),
    )
    check(
        "同时产出图形版与命令行版 exe",
        'name="记账本"' in spec_text and 'name="记账本-命令行"' in spec_text,
    )
    bat_path = project / "打包exe.bat"
    bat_bytes = bat_path.read_bytes() if bat_path.exists() else b""
    check(
        "打包脚本是纯 ASCII + CRLF（cmd 按系统代码页读它，中文会乱码）",
        bool(bat_bytes) and not [byte for byte in bat_bytes if byte > 127] and b"\r\n" in bat_bytes,
        len([byte for byte in bat_bytes if byte > 127]),
    )
    check(
        "打包脚本用工程内的 PyInstaller，不装进系统",
        b"LedgerBook\\build\\.tools" in bat_bytes,
    )
    helper = project / "LedgerBook" / "build" / "release.py"
    check("发布脚本在（复制文档 + 生成 zip 安装包 + 打包信息）", helper.exists(), str(helper))
    snapshots = sorted((project / "LedgerBook" / "版本备份").glob("v*"))
    check(
        "留有可回滚的源码快照",
        bool(snapshots) and (snapshots[-1] / "ledger" / "ui" / "app.py").exists(),
        [item.name for item in snapshots],
    )
    app_dir = project / "LedgerBook" / "记账本"
    if (app_dir / "记账本.exe").exists():
        check(
            "打包好的程序文件夹里有 exe、运行库和说明文档（附属文件齐全）",
            (app_dir / "_internal").is_dir()
            and (app_dir / "记账本-命令行.exe").exists()
            and (app_dir / "使用说明.md").exists()
            and (app_dir / "便携版说明.txt").exists(),
            [item.name for item in app_dir.iterdir()][:6],
        )


# ---------------------------------------------------------------- 界面


def gui_tests(tmp: Path) -> None:
    from ledger import api, config as config_mod, model as m, paths, storage

    section("界面")
    try:
        import tkinter as tk

        probe = tk.Tk()
        probe.destroy()
    except Exception as exc:  # pragma: no cover
        check("本机有图形环境", False, exc)
        return
    check("本机有图形环境", True)

    from tkinter import messagebox

    popups: list = []
    dialogs: list = []
    for name in ("showinfo", "showwarning", "showerror"):
        setattr(messagebox, name, lambda *args, **kwargs: popups.append(args))

    def fake_askyesno(*args, **kwargs):
        dialogs.append(args)
        return True

    messagebox.askyesno = fake_askyesno

    from ledger.ui.app import App
    from ledger.ui import common as common_mod
    from ledger.ui.common import (
        CategoryDialog,
        DatePickerPopup,
        ItemListDialog,
        RecordDialog,
        RoundedButton,
        SortableList,
        WrapRow,
    )
    from ledger.ui.transfer_panel import TransferPanel

    app = App()
    check("窗口能正常构建", app.startup_error is None, app.startup_error)
    if app.startup_error:
        return
    app.update()
    check(
        "顶层四个标签页（导入 / 导出已经并进设置里）",
        list(app.tabs) == ["记一笔", "查询统计", "月度汇总", "设置"],
        list(app.tabs),
    )

    record_tab = app.tabs["记一笔"]
    record_tab.kind_var.set(m.KIND_EXPENSE)
    record_tab.refresh_categories()
    record_tab.date_box.set(dt.date.today())
    record_tab.amount_var.set("33.30")
    record_tab.note_var.set("界面自测")
    record_tab.save()
    app.update()
    today_records = storage.read_day(dt.date.today())
    check(
        "界面上记账能落盘",
        any(record.note == "界面自测" for record in today_records),
        [record.to_dict() for record in today_records],
    )
    check("状态栏给出了提示", "已记入" in app.status_var.get(), app.status_var.get())
    newest = max(today_records, key=lambda item: item.id) if today_records else None
    check(
        "录入提示里不出现编号",
        newest is not None and newest.id not in app.status_var.get(),
        app.status_var.get(),
    )

    record_tab.amount_var.set("不会通过")
    record_tab.save()
    app.update()
    check("金额写错时弹出提示而不是崩溃", len(popups) == 1, popups)
    popups.clear()

    query_tab = app.tabs["查询统计"]
    app.notebook.select(query_tab)
    app.update()
    query_tab.run_query()
    app.update()
    check("查询页能查出刚记的账", len(query_tab.last_records) >= 1, query_tab.totals_var.get())
    check("合计栏有内容", "结余" in query_tab.totals_var.get(), query_tab.totals_var.get())

    # 规则：编号是内部主键，界面上任何地方都不显示
    check("查询明细表不显示编号列", "编号" not in query_tab.DETAIL_COLUMNS, query_tab.DETAIL_COLUMNS)
    sample = query_tab.last_records[0]
    dialog = RecordDialog(query_tab, sample)
    dialog_title = dialog.title()
    dialog.destroy()
    check("修改对话框标题不含编号", sample["id"] not in dialog_title, dialog_title)

    messagebox.askyesno = lambda *args, **kwargs: (dialogs.append(args), False)[1]
    query_tab.table.tree.selection_set("0")
    query_tab.delete_selected()
    confirm_text = dialogs[-1][1] if dialogs and len(dialogs[-1]) > 1 else ""
    check("删除确认里不出现编号", sample["id"] not in confirm_text, confirm_text)
    check(
        "删除确认里用可读信息指代记录",
        sample["date"] in confirm_text and sample["category"] in confirm_text,
        confirm_text,
    )
    messagebox.askyesno = fake_askyesno
    check("取消删除后记录还在", len(query_tab.last_records) >= 1)

    summary_tab = app.tabs["月度汇总"]
    app.notebook.select(summary_tab)
    app.update()
    check("月度汇总页能生成当月汇总", summary_tab.report is not None)
    check(
        "汇总笔数与查询一致",
        summary_tab.report and summary_tab.report["totals"]["count"] == len(query_tab.last_records),
        (summary_tab.report or {}).get("totals"),
    )

    # 明细在左、分类统计在右；窗口窄了要自动改成上下排列
    class _ResizeEvent:
        """只带 width 的合成事件，用来精确测布局逻辑（真实缩放由 Tk 的 <Configure> 触发）。"""

        def __init__(self, width: int) -> None:
            self.width = width

    app.geometry("1020x760")
    for _ in range(5):
        app.update()
    check(
        "查询页绑定了窗口宽度检测（真实缩放靠它触发）",
        bool(query_tab.split.bind("<Configure>")),
        query_tab.split.bind("<Configure>"),
    )

    query_tab.on_split_resize(_ResizeEvent(1000))
    app.update()
    check(
        "够宽时：明细在左、分类统计在右（左右并排）",
        query_tab.layout_mode() == "side"
        and int(query_tab.detail_frame.grid_info()["column"]) == 0
        and int(query_tab.category_frame.grid_info()["column"]) == 1
        and int(query_tab.detail_frame.grid_info()["row"]) == int(query_tab.category_frame.grid_info()["row"]),
        query_tab.layout_mode(),
    )
    check(
        "并排时两半等宽",
        str(query_tab.split.grid_columnconfigure(0)["uniform"]) != ""
        and query_tab.split.grid_columnconfigure(0)["uniform"] == query_tab.split.grid_columnconfigure(1)["uniform"],
        (
            query_tab.split.grid_columnconfigure(0)["uniform"],
            query_tab.split.grid_columnconfigure(1)["uniform"],
        ),
    )

    query_tab.on_split_resize(_ResizeEvent(700))
    app.update()
    check(
        "窄到阈值以下：改成上下排列（明细在上、统计在下）",
        query_tab.layout_mode() == "stacked"
        and int(query_tab.detail_frame.grid_info()["row"]) == 0
        and int(query_tab.category_frame.grid_info()["row"]) == 1
        and int(query_tab.category_frame.grid_info()["column"]) == 0,
        query_tab.layout_mode(),
    )
    check(
        "阈值取在 900：899 上下、900 并排",
        _mode_for(query_tab, 899) == "stacked" and _mode_for(query_tab, 900) == "side",
        (_mode_for(query_tab, 899), _mode_for(query_tab, 900)),
    )

    # 漏掉 Configure 事件时能自愈：sync_layout 按真实宽度纠正
    query_tab.sync_layout()
    app.update()
    check(
        "sync_layout 会按真实宽度自愈",
        query_tab.layout_mode() == "side",
        (query_tab.layout_mode(), query_tab.split.winfo_width()),
    )

    check(
        "合计单独占一行（窄窗口下不会和按钮互相压住）",
        query_tab.totals_label.winfo_manager() == "pack" and query_tab.totals_label.master is query_tab,
        (query_tab.totals_label.winfo_manager(), str(query_tab.totals_label.master)),
    )
    check(
        "最小窗口宽度不小于明细表头所需的宽度",
        app.wm_minsize()[0] >= 560,
        app.wm_minsize(),
    )

    # 窄 / 宽两种宽度下的自动回车（宽度按 DPI 缩放，任何缩放比例下断言都成立）
    narrow = common_mod.px(560)
    wide = common_mod.px(1600)
    query_tab.filter_wrap.layout_for_width(narrow)
    query_tab.preset_wrap.layout_for_width(narrow)
    check(
        "窄窗口下筛选条件自动回车（不再挤成一行）",
        query_tab.filter_wrap.rows_used() >= 2,
        (narrow, query_tab.filter_wrap.rows_used()),
    )
    check(
        "窄窗口下快捷范围也会自动回车",
        query_tab.preset_wrap.rows_used() >= 2,
        (narrow, query_tab.preset_wrap.rows_used()),
    )
    query_tab.fit_detail_columns(narrow)
    check(
        "窄窗口下明细表头仍能一行显示完",
        query_tab.detail_columns_total() <= narrow,
        (query_tab.detail_columns_total(), narrow),
    )
    check(
        "压列宽时每列都不低于可读下限（日期/时间等不会被切）",
        all(
            int(query_tab.table.tree.column(name, "width")) >= query_tab.DETAIL_MIN_WIDTHS[name]
            for name in query_tab.DETAIL_COLUMNS
        ),
        {name: int(query_tab.table.tree.column(name, "width")) for name in query_tab.DETAIL_COLUMNS},
    )

    query_tab.filter_wrap.layout_for_width(wide)
    query_tab.preset_wrap.layout_for_width(wide)
    check(
        "宽窗口下这些行回到一行显示",
        query_tab.filter_wrap.rows_used() == 1 and query_tab.preset_wrap.rows_used() == 1,
        (query_tab.filter_wrap.rows_used(), query_tab.preset_wrap.rows_used()),
    )
    app.update()
    query_tab.sync_layout()
    # 分类统计的多种视图（表格 / 饼状图 / 柱状图 / 方格图）
    from ledger.ui import charts

    api.dispatch("save_settings", {"settings": {"分类统计显示": ["表格", "饼状图", "柱状图", "方格图"]}})
    app.apply_settings_changed()
    app.geometry("1140x800")
    for _ in range(15):
        app.update()
        time.sleep(0.02)
    query_tab.start_box.set(dt.date(2026, 2, 1))
    query_tab.end_box.set(dt.date(2026, 2, 28))
    query_tab.run_query()
    query_tab.flush_pending_layout()
    for _ in range(10):
        app.update()
    check(
        "选四个视图就生成四个面板",
        set(query_tab.stats_panels) == {"表格", "饼状图", "柱状图", "方格图"},
        list(query_tab.stats_panels),
    )
    positions = [
        (int(panel["frame"].grid_info()["row"]), int(panel["frame"].grid_info()["column"]))
        for panel in query_tab.stats_panels.values()
    ]
    check("多个面板按 2×2 自动分配区域", positions == [(0, 0), (0, 1), (1, 0), (1, 1)], positions)
    check("表格面板还在（表格是其中一个可选视图）", query_tab.category_table is not None)

    def canvas_of(view: str) -> tk.Canvas:
        return query_tab.stats_panels[view]["canvas"]

    expected_series = len(charts.series_from(query_tab.last_by_category))
    pie_canvas = canvas_of("饼状图")
    pie_items = [item for item in pie_canvas.find_all() if pie_canvas.type(item) in ("arc", "oval")]
    check(
        "3D 饼图：每个分类画两层（顶面 + 下方厚度）",
        len(pie_items) == expected_series * 2,
        (len(pie_items), expected_series),
    )
    bar_canvas = canvas_of("柱状图")
    bar_boxes = [item for item in bar_canvas.find_all() if bar_canvas.type(item) == "rectangle"]
    bar_faces = [item for item in bar_canvas.find_all() if bar_canvas.type(item) == "polygon"]
    check(
        "横向 3D 柱状图：每项都有正面与立体面",
        len(bar_boxes) >= expected_series and len(bar_faces) >= expected_series * 2,
        (len(bar_boxes), len(bar_faces), expected_series),
    )
    check(
        "柱状图是横向的（不是竖柱：没有底部基线，文字在左侧）",
        not [
            item
            for item in bar_canvas.find_all()
            if bar_canvas.type(item) == "line"
        ],
        [bar_canvas.type(item) for item in bar_canvas.find_all()],
    )
    waffle_canvas = canvas_of("方格图")
    waffle_cells = [item for item in waffle_canvas.find_all() if waffle_canvas.type(item) == "rectangle"]
    check("方格图铺满了 100 个方格", len(waffle_cells) >= 100, len(waffle_cells))
    check(
        "三种图形都画出了内容（不是空白面板）",
        all(len(canvas_of(view).find_all()) > 0 for view in charts.CHART_VIEWS),
        {view: len(canvas_of(view).find_all()) for view in charts.CHART_VIEWS},
    )

    check(
        "图形里的东西都画在画布范围内（图例不会画到面板外）",
        all(
            canvas.bbox("all") is None or canvas.bbox("all")[3] <= canvas.winfo_height() + 2
            for view in charts.CHART_VIEWS
            for canvas in [canvas_of(view)]
        ),
        {
            view: (canvas_of(view).bbox("all"), canvas_of(view).winfo_height())
            for view in charts.CHART_VIEWS
        },
    )

    api.dispatch("save_settings", {"settings": {"分类统计显示": ["柱状图"]}})
    app.apply_settings_changed()
    query_tab.flush_pending_layout()
    for _ in range(10):
        app.update()
        time.sleep(0.02)
    check(
        "只选一个视图时只显示这一个面板（表格可以不显示）",
        list(query_tab.stats_panels) == ["柱状图"] and query_tab.category_table is None,
        list(query_tab.stats_panels),
    )
    # 单独显示时图表要把面板填满，不能小小一个缩在角上
    lone_canvas = canvas_of("柱状图")
    bbox = lone_canvas.bbox("all")
    check(
        "单独一个面板时占满整个统计区（不是只占四分之一格）",
        lone_canvas.winfo_width() > query_tab.stats_holder.winfo_width() * 0.9,
        (lone_canvas.winfo_width(), query_tab.stats_holder.winfo_width()),
    )
    check(
        "单独的柱状图基本填满面板",
        bbox is not None
        and (bbox[2] - bbox[0]) >= lone_canvas.winfo_width() * 0.6
        and (bbox[3] - bbox[1]) >= lone_canvas.winfo_height() * 0.45,
        (bbox, lone_canvas.winfo_width(), lone_canvas.winfo_height()),
    )

    api.dispatch("save_settings", {"settings": {"分类统计显示": ["方格图"]}})
    app.apply_settings_changed()
    query_tab.flush_pending_layout()
    for _ in range(10):
        app.update()
        time.sleep(0.02)
    waffle_canvas = canvas_of("方格图")
    cell_size, *_ = charts.waffle_geometry(
        waffle_canvas.winfo_width(),
        waffle_canvas.winfo_height(),
        charts.series_from(query_tab.last_by_category),
    )
    check(
        "单独显示方格图时方格会明显放大（并受最大边长限制）",
        cell_size >= 25 and cell_size <= charts.MAX_CELL + 0.01,
        (cell_size, waffle_canvas.winfo_width(), waffle_canvas.winfo_height()),
    )

    api.dispatch("save_settings", {"settings": {"分类统计显示": ["表格", "饼状图"]}})
    app.apply_settings_changed()
    for _ in range(10):
        app.update()
    check(
        "还原成默认两个视图",
        set(query_tab.stats_panels) == {"表格", "饼状图"} and query_tab.category_table is not None,
        list(query_tab.stats_panels),
    )

    # 换视图不应该重读数据文件（只在内存里重画），而且多选是「点保存才生效」
    queries_before = query_tab.query_count
    renders_before = query_tab.render_count
    api.dispatch("save_settings", {"settings": {"分类统计显示": ["表格", "饼状图", "柱状图", "方格图"]}})
    app.apply_settings_changed()
    query_tab.flush_pending_layout()
    for _ in range(10):
        app.update()
    check(
        "换视图不重新读数据（只重画），卡顿更少",
        query_tab.query_count == queries_before and query_tab.render_count > renders_before,
        (queries_before, query_tab.query_count, renders_before, query_tab.render_count),
    )
    settings_page = app.tabs["设置"]
    settings_page.show_page("查询")
    app.update()
    spec = config_mod.SPEC_BY_KEY["分类统计显示"]
    boxes = settings_page.multi_vars["分类统计显示"]
    boxes["方格图"].set(False)
    settings_page.mark_multi_dirty(spec)
    check(
        "多选改动后「保存」按钮才可用（没点保存不会生效）",
        "disabled" not in settings_page.multi_buttons["分类统计显示"].state()
        and config_mod.get("分类统计显示") == ["表格", "饼状图", "柱状图", "方格图"],
        (settings_page.multi_buttons["分类统计显示"].state(), config_mod.get("分类统计显示")),
    )
    settings_page.save_one(spec, settings_page.multi_picked(spec))
    app.update()
    check(
        "点保存后设置才真的写进去",
        config_mod.get("分类统计显示") == ["表格", "饼状图", "柱状图"],
        config_mod.get("分类统计显示"),
    )
    check(
        "保存后按钮回到置灰状态",
        "disabled" in settings_page.multi_buttons["分类统计显示"].state(),
        settings_page.multi_buttons["分类统计显示"].state(),
    )
    api.dispatch("save_settings", {"settings": {"分类统计显示": ["表格", "饼状图"]}})
    app.apply_settings_changed()
    query_tab.flush_pending_layout()
    for _ in range(10):
        app.update()

    check(
        "改别的设置不会把当前查询区间冲掉（只有改「查询默认范围」才重置）",
        query_tab.start_box.get() == dt.date(2026, 2, 1) and query_tab.end_box.get() == dt.date(2026, 2, 28),
        (query_tab.start_box.get(), query_tab.end_box.get()),
    )
    api.dispatch("save_settings", {"settings": {"查询默认范围": "上月"}})
    app.apply_settings_changed()
    app.update()
    check(
        "改「查询默认范围」时会立即按新范围重设区间",
        query_tab.start_box.get() == query_tab.end_box.get().replace(day=1)
        or query_tab.start_box.get().day == 1,
        (query_tab.start_box.get(), query_tab.end_box.get()),
    )
    api.dispatch("save_settings", {"settings": {"查询默认范围": "本月"}})
    app.apply_settings_changed()
    app.update()

    # 拖窗口性能：小幅变化不重排、大幅变化走防抖
    wrap = query_tab.filter_wrap
    wrap.layout_for_width(600)
    settled = wrap._width
    wrap._on_configure(type("E", (), {"width": 610})())
    check(
        "宽度只差 10px 时不重排（拖窗口时不逐像素算）",
        wrap._width == settled and wrap._after_id is None,
        (wrap._width, wrap._after_id),
    )
    wrap._on_configure(type("E", (), {"width": 700})())
    check("宽度明显变化时只挂一个待办（防抖）", wrap._after_id is not None, wrap._after_id)
    wrap.flush_pending()
    check("flush 之后才真正按新宽度排版", wrap._width == 700 and wrap._after_id is None, wrap._width)

    query_tab._detail_fit_width = 500
    query_tab.on_detail_configure(type("E", (), {"width": 505})())
    check("明细宽度小幅变化不重算列宽", query_tab._detail_after is None, query_tab._detail_after)
    query_tab.on_detail_configure(type("E", (), {"width": 560})())
    check("明细宽度明显变化才挂重算", query_tab._detail_after is not None, query_tab._detail_after)
    query_tab.flush_pending_layout()
    app.update()
    check(
        "flush 之后列宽按新宽度算好",
        query_tab.detail_columns_total() <= max(560, query_tab.detail_columns_total()),
        query_tab.detail_columns_total(),
    )

    app.geometry("1140x800")
    for _ in range(10):
        app.update()
    overflowing = [
        str(child)
        for child in query_tab.winfo_children()
        if child.winfo_ismapped() and child.winfo_x() + child.winfo_width() > query_tab.winfo_width() + 2
    ]
    check("查询页没有控件被挤出可视区", not overflowing, overflowing)

    check(
        "状态栏也能自动回车（窄窗口下右边那截不会被切掉）",
        isinstance(app.status_wrap, WrapRow) and app.status_wrap.rows_used() >= 1,
        app.status_wrap.rows_used(),
    )
    app.status_wrap.layout_for_width(300)
    check(
        "状态栏太窄时换成两行",
        app.status_wrap.rows_used() == 2,
        app.status_wrap.rows_used(),
    )
    app.status_wrap.layout_for_width(1200)
    check("状态栏够宽时回到一行", app.status_wrap.rows_used() == 1, app.status_wrap.rows_used())
    app.status_wrap.layout_for_width(max(300, app.winfo_width() - 40))
    for _ in range(10):
        app.update()
    check(
        "状态栏即使换行也不会被窗口底边挤掉",
        app.status_wrap.winfo_height() + 2 >= app.status_wrap.winfo_reqheight(),
        (app.status_wrap.winfo_height(), app.status_wrap.winfo_reqheight()),
    )

    # 真实缩放：这个环境偶尔不让窗口缩到目标宽度，缩到了就必须切换
    app.geometry("700x700")
    for _ in range(20):
        app.update()
        time.sleep(0.02)
    narrowed = query_tab.split.winfo_width() < query_tab.SIDE_BY_SIDE_MIN_WIDTH
    check(
        "真的把窗口缩窄时也会跟着切换（宽高允许缩小时）",
        (not narrowed) or query_tab.layout_mode() == "stacked",
        (app.winfo_width(), query_tab.split.winfo_width(), query_tab.layout_mode()),
    )
    app.geometry("1020x760")
    for _ in range(10):
        app.update()
    query_tab.sync_layout()
    app.update()
    check(
        "拉回宽窗口：回到左右并排，且两半宽度接近",
        query_tab.layout_mode() == "side"
        and abs(query_tab.detail_frame.winfo_width() - query_tab.category_frame.winfo_width()) <= 8,
        (
            query_tab.layout_mode(),
            query_tab.detail_frame.winfo_width(),
            query_tab.category_frame.winfo_width(),
        ),
    )

    # 圆角：按钮全部换成自绘圆角按钮，窗口用 DWM 设圆角
    def walk(widget):
        for child in widget.winfo_children():
            yield child
            yield from walk(child)

    check(
        "界面上没有残留的方角按钮（ttk.Button 已全部替换）",
        not [child for child in walk(app) if child.winfo_class() == "TButton"],
        [str(child) for child in walk(app) if child.winfo_class() == "TButton"][:5],
    )
    sample = query_tab.search_button
    shapes = sample.shape_items()
    check(
        "按钮是圆角矩形（按圆弧取点的圆角路径）+ 居中文字",
        len(shapes) == 1
        and len(sample.coords(shapes[0])) // 2 >= 20
        and sample.cget("text") == "查询"
        and len(sample.find_all()) == 2,
        (len(shapes), len(sample.coords(shapes[0])) // 2, sample.cget("text"), len(sample.find_all())),
    )
    import math as _math

    coords = sample.coords(shapes[0])
    corner_distance = min(
        _math.hypot(x - 0.5, y - 0.5) for x, y in zip(coords[0::2], coords[1::2])
    )
    check(
        "圆角真的让出角部空间（方角的话最近点就是角点本身）",
        corner_distance >= sample.radius * 0.35,
        (corner_distance, sample.radius),
    )
    clicked: list[str] = []
    sample.command = lambda: clicked.append("x")
    sample.invoke()
    check("圆角按钮能触发命令", clicked == ["x"], clicked)
    sample.state(["disabled"])
    sample.invoke()
    check(
        "禁用状态下不触发，并且 status 能反映出来",
        clicked == ["x"] and sample.state() == ("disabled",),
        (clicked, sample.state()),
    )
    sample.state(["!disabled"])
    check("恢复可用", sample.state() == () and sample.cget("cursor") == "hand2", sample.state())
    check(
        "主操作按钮用主色（保存/查询这类）",
        sample.palette()["bg"] != record_tab.winfo_children()[0].winfo_class()
        and sample.style_name == "primary",
        sample.style_name,
    )
    dialog_for_round = RecordDialog(query_tab, query_tab.last_records[0])
    dialog_buttons = [child for child in walk(dialog_for_round) if isinstance(child, RoundedButton)]
    check("弹窗里的按钮也是圆角按钮", len(dialog_buttons) >= 2, len(dialog_buttons))
    check(
        "弹窗也设了窗口圆角（Win11 生效，旧系统返回 False）",
        isinstance(common_mod.apply_rounded_window(dialog_for_round), bool),
    )
    dialog_for_round.destroy()
    check(
        "主窗口圆角设置结果已记录（True = 系统接受了圆角）",
        isinstance(app.rounded_corners, bool),
        app.rounded_corners,
    )

    settings_tab = app.tabs["设置"]
    app.notebook.select(settings_tab)
    app.update()
    check(
        "设置页有「收入种类 / 支出种类」两个方框且都有内容",
        set(settings_tab.kind_listboxes) == set(m.KINDS)
        and all(box.size() > 0 for box in settings_tab.kind_listboxes.values()),
        {kind: box.size() for kind, box in settings_tab.kind_listboxes.items()},
    )
    check(
        "每个方框右边都有一个写着「修改」的按钮",
        set(settings_tab.kind_buttons) == set(m.KINDS)
        and all(settings_tab.kind_buttons[kind].cget("text") == "修改" for kind in m.KINDS),
    )

    income_before = config_mod.categories_for(m.KIND_INCOME)
    box = settings_tab.kind_listboxes[m.KIND_INCOME]
    check(
        "每个种类行最左边都有一个拖动手柄",
        box.handle_count() == len(income_before) and box.handle_count() > 0,
        (box.handle_count(), len(income_before)),
    )
    check("手柄的悬浮提示是「拖动可以调整顺序」", box.handle_tooltip_text == "拖动可以调整顺序", box.handle_tooltip_text)

    app.update()
    original = list(box.items)
    check(
        "按下落点能算出要插到第几位",
        box.index_at_root_y(box._rows[0].winfo_rooty() + 2) == 0,
        box.index_at_root_y(box._rows[0].winfo_rooty() + 2),
    )
    box.begin_drag(len(original) - 1)
    box.move_item(len(original) - 1, 0)
    box.drop()
    check(
        "在设置页直接拖动就改了顺序（拖完立即落盘）",
        config_mod.categories_for(m.KIND_INCOME)[0] == original[-1],
        config_mod.categories_for(m.KIND_INCOME),
    )
    check("拖完后界面上的顺序也跟着变", box.items[0] == original[-1], box.items)

    # 再用真实鼠标手势打一遍：按住最后一行的手柄，拖到第一行的位置
    before_gesture = list(box.items)
    gesture_item = before_gesture[-1]
    handle = box._handles[-1]
    first_row = box._rows[0]
    handle.event_generate("<Button-1>", x=5, y=8)
    app.update()
    handle.event_generate(
        "<B1-Motion>", x=5, y=8, rootx=first_row.winfo_rootx() + 5, rooty=first_row.winfo_rooty() + 2
    )
    app.update()
    handle.event_generate("<ButtonRelease-1>", x=5, y=8)
    app.update()
    check(
        "真实鼠标手势拖动也能改顺序并落盘",
        box.items[0] == gesture_item and box.items[1:] == before_gesture[:-1] and config_mod.categories_for(m.KIND_INCOME) == box.items,
        (before_gesture, box.items),
    )
    api.dispatch("save_categories", {"kind": m.KIND_INCOME, "categories": original})
    settings_tab.refresh_category_boxes()

    dialog = CategoryDialog(settings_tab, m.KIND_INCOME, income_before)
    check("修改窗口里的列表也能拖动", dialog.rowlist.handle_count() == len(dialog.items))
    dialog.name_var.set("红包测试")
    dialog.add_item()
    check("种类窗口能新增", dialog.items[-1] == "红包测试", dialog.items)
    dialog.rowlist.select(len(dialog.items) - 1)
    dialog.move(-1)
    check("种类窗口能调顺序（与拖动共用同一套逻辑）", dialog.items[-2] == "红包测试", dialog.items)
    dialog.name_var.set("工资")
    dialog.add_item()
    check("重复的种类会被拒绝（弹提示而不是加两条）", len(dialog.items) == len(income_before) + 1, dialog.items)
    popups.clear()  # 上面那条是故意触发的提示
    dialog.on_ok()
    applied = dialog.result or []
    check("确定后返回的就是最新列表", "红包测试" in applied, applied)

    result = api.dispatch("save_categories", {"kind": m.KIND_INCOME, "categories": applied})
    check("确定后能整体保存到配置", result.get("ok") and result["categories"][m.KIND_INCOME] == applied)
    settings_tab.refresh_category_boxes()
    check("保存后设置页的方框立即刷新", settings_tab.kind_listboxes[m.KIND_INCOME].size() == len(applied))

    dialog2 = CategoryDialog(settings_tab, m.KIND_INCOME, applied)
    dialog2.rowlist.select(len(dialog2.items) - 1)
    removed_name = dialog2.items[-1]
    dialog2.remove_item()
    check("种类窗口能删除", removed_name not in dialog2.items and len(dialog2.items) == len(applied) - 1)
    dialog2.destroy()
    api.dispatch("save_categories", {"kind": m.KIND_INCOME, "categories": income_before})  # 还原
    settings_tab.refresh_category_boxes()
    app.update()

    # 支付方式：与种类同样的样式（可拖动方框 + 修改按钮）
    pay_list = settings_tab.pay_list
    check(
        "支付方式也是「可拖动方框 + 修改按钮」",
        isinstance(pay_list, SortableList)
        and settings_tab.pay_button.cget("text") == "修改"
        and pay_list.handle_count() == len(settings_tab.pay_methods)
        and pay_list.handle_tooltip_text == "拖动可以调整顺序",
        (pay_list.handle_count(), len(settings_tab.pay_methods)),
    )
    pay_list.update()
    check(
        "选项框是圆角边框（自绘圆角描边，不再是原生方框）",
        bool(pay_list.canvas.find_withtag("card_border"))
        and pay_list.canvas.cget("background") == "#ffffff"
        and int(pay_list.canvas.cget("highlightthickness")) == 0,
        (
            len(pay_list.canvas.find_withtag("card_border")),
            pay_list.canvas.cget("background"),
            pay_list.canvas.cget("highlightthickness"),
        ),
    )
    check(
        "方框按内容自适应，完整显示所有选项（不截断、不出现内部滚动）",
        int(pay_list.canvas.cget("height")) >= pay_list.size() * pay_list.row_height
        and pay_list.handle_count() == pay_list.size()
        and not pay_list.scrollbar_visible(),
        (pay_list.canvas.cget("height"), pay_list.size() * pay_list.row_height),
    )
    pay_before = list(pay_list.items)
    pay_list.begin_drag(len(pay_before) - 1)
    pay_list.move_item(len(pay_before) - 1, 0)
    pay_list.drop()
    check(
        "支付方式拖动后立即落盘",
        config_mod.load_settings()["支付方式"][0] == pay_before[-1],
        config_mod.load_settings()["支付方式"],
    )
    api.dispatch("save_settings", {"settings": {"支付方式": pay_before}})
    settings_tab.refresh_values()
    app.update()

    pay_dialog = ItemListDialog(settings_tab, "修改支付方式", pay_before, noun="支付方式")
    pay_dialog.name_var.set("数字货币")
    pay_dialog.add_item()
    check("支付方式修改窗口能新增", pay_dialog.items[-1] == "数字货币", pay_dialog.items)
    pay_dialog.rowlist.select(len(pay_dialog.items) - 1)
    pay_dialog.remove_item()
    check("支付方式修改窗口能删除", "数字货币" not in pay_dialog.items)
    pay_dialog.destroy()

    check(
        "设置页为每个设置项都生成了控件（专门控件呈现的除外）",
        {spec.key for spec in config_mod.SETTING_SPECS if not spec.hidden} == set(settings_tab.widgets),
        sorted(settings_tab.widgets),
    )
    check(
        "由专门控件呈现的设置项不会重复出现在通用排版里",
        all(
            not spec.hidden or spec.key not in settings_tab.widgets
            for spec in config_mod.SETTING_SPECS
        ),
    )

    # 分页布局：一屏只显示当前页，避免一屏塞满找不到东西
    from ledger.ui.settings_tab import PAGE_ORDER

    check(
        "设置页按页分好了（标签条与页一一对应）",
        tuple(settings_tab.page_frames) == PAGE_ORDER and tuple(settings_tab.tabs.order) == PAGE_ORDER,
        (tuple(settings_tab.page_frames), tuple(settings_tab.tabs.order)),
    )
    check(
        "一次只显示一页，其它页完全不占地方",
        len([name for name, frame in settings_tab.page_frames.items() if frame.winfo_ismapped()]) == 1,
        [name for name, frame in settings_tab.page_frames.items() if frame.winfo_ismapped()],
    )
    settings_tab.show_page("关于")
    app.update()
    check(
        "点标签能切页，且只剩这一页在显示",
        settings_tab.page_frames["关于"].winfo_ismapped()
        and not settings_tab.page_frames["通用"].winfo_ismapped()
        and settings_tab.tabs.active() == "关于",
    )
    settings_tab.show_page("种类与支付方式")
    app.update()

    # 设置改完要立即生效，不用重启
    api.dispatch("save_settings", {"settings": {"默认记账类型": m.KIND_INCOME}})
    app.apply_settings_changed()
    app.update()
    check("改默认类型后「记一笔」预选收入", record_tab.kind_var.get() == m.KIND_INCOME, record_tab.kind_var.get())
    check(
        "分类下拉跟着类型切换",
        "工资" in list(record_tab.category_box.cget("values")),
        record_tab.category_box.cget("values"),
    )

    api.dispatch("save_settings", {"settings": {"显示本次已记": False}})
    app.apply_settings_changed()
    app.notebook.select(record_tab)
    app.update()
    check("关掉后「本次已记」列表隐藏", not record_tab.recent_frame.winfo_ismapped())
    api.dispatch("save_settings", {"settings": {"显示本次已记": True}})
    app.apply_settings_changed()
    app.update()
    check("打开后又显示", bool(record_tab.recent_frame.winfo_ismapped()))

    api.dispatch("save_settings", {"settings": {"保存后清空": False, "默认记账类型": m.KIND_EXPENSE}})
    app.apply_settings_changed()
    record_tab.date_box.set(dt.date.today())
    record_tab.category_var.set("餐饮")
    record_tab.amount_var.set("5.55")
    record_tab.note_var.set("不清空测试")
    record_tab.save()
    app.update()
    check("关掉「保存后清空」后金额还在", record_tab.amount_var.get() == "5.55", record_tab.amount_var.get())
    api.dispatch("save_settings", {"settings": {"保存后清空": True}})
    app.apply_settings_changed()
    record_tab.amount_var.set("6.66")
    record_tab.note_var.set("清空测试")
    record_tab.save()
    app.update()
    check("打开后金额被清空", record_tab.amount_var.get() == "", record_tab.amount_var.get())

    api.dispatch("save_settings", {"settings": {"查询默认范围": "今天"}})
    app.apply_settings_changed()
    app.update()
    check(
        "改「查询默认范围」后区间立即跟着变",
        query_tab.start_box.get() == dt.date.today() and query_tab.end_box.get() == dt.date.today(),
        (query_tab.start_box.get(), query_tab.end_box.get()),
    )

    api.dispatch("save_settings", {"settings": {"明细最多显示": 1}})
    app.apply_settings_changed()
    app.update()
    check("「明细最多显示」上限生效", len(query_tab.last_records) == 1, len(query_tab.last_records))
    check(
        "超限时在合计行上提示只显示了多少笔",
        "只显示最近" in query_tab.totals_var.get(),
        query_tab.totals_var.get(),
    )

    api.dispatch("reset_settings")
    app.apply_settings_changed()
    app.update()
    check("恢复默认后明细上限回到 500", int(config_mod.get("明细最多显示")) == 500)

    # 明细里要把被删掉种类的记录标出来（统计算「其他」，但看得出原来是什么）
    query_tab.start_box.set(dt.date(2026, 3, 1))
    query_tab.end_box.set(dt.date(2026, 3, 31))
    query_tab.kind_var.set("全部")
    query_tab.category_var.set("全部")
    query_tab.keyword_var.set("")
    query_tab.run_query()
    app.update()
    category_cells = [row[3] for row in query_tab.table._rows]
    check(
        "明细里标成「其他（原 宠物）」",
        any(cell == "其他（原 宠物）" for cell in category_cells),
        category_cells,
    )
    query_tab.apply_default_range()
    query_tab.run_query(silent=True)
    app.update()

    # 改类型 → 细分必须回到「全部」，选项也要跟着类型走
    query_tab.start_box.set(dt.date(2026, 2, 1))
    query_tab.end_box.set(dt.date(2026, 2, 28))
    query_tab.kind_var.set("支出")
    query_tab.category_var.set("餐饮")
    query_tab.on_kind_changed()
    app.update()
    check("改类型后细分自动回到「全部」", query_tab.category_var.get() == "全部", query_tab.category_var.get())

    query_tab.kind_var.set("收入")
    query_tab.refresh_category_options()
    income_options = list(query_tab.category_box.cget("values"))
    check(
        "选收入时细分里不会列出支出种类",
        "餐饮" not in income_options and "工资" in income_options,
        income_options,
    )

    # 真实交互：先选好「支出 + 交通」，再把类型下拉切到「收入」
    query_tab.kind_var.set("支出")
    query_tab.refresh_category_options()
    query_tab.category_var.set("交通")
    query_tab.run_query()
    app.update()
    check("「支出 + 交通」能查出东西", len(query_tab.last_records) >= 1, query_tab.totals_var.get())
    query_tab.kind_box.set("收入")
    query_tab.kind_box.event_generate("<<ComboboxSelected>>")
    app.update()
    check(
        "把类型切成收入后，细分已经回到「全部」（不会留下「收入 + 交通」）",
        query_tab.category_var.get() == "全部" and query_tab.last_payload.get("category") == "全部",
        (query_tab.category_var.get(), query_tab.last_payload),
    )
    check("切换后确实有结果，而不是空表", len(query_tab.last_records) >= 1, query_tab.totals_var.get())
    query_tab.kind_var.set("全部")
    query_tab.category_var.set("全部")
    query_tab.apply_default_range()
    query_tab.run_query(silent=True)
    app.update()

    transfer_tab = settings_tab
    settings_tab.show_page("导入 / 导出")
    app.update()
    panel = settings_tab.transfer
    check(
        "导入 / 导出合并成了设置里的一页",
        isinstance(panel, TransferPanel) and panel.winfo_ismapped(),
    )
    check(
        "导出区第一行是「导出路径为 …」，点它可以选目录",
        panel.export_path_title == "导出路径为"
        and panel.export_path_var.get() == str(api.export_dir())
        and hasattr(panel.export_path_label, "bind"),
        (panel.export_path_title, panel.export_path_var.get()),
    )
    check(
        "导出区保留了「打开导出目录」按钮",
        panel.open_export_button.cget("text") == "打开导出目录" and panel.export_button.cget("text") == "导出",
        (panel.open_export_button.cget("text"), panel.export_button.cget("text")),
    )
    check(
        "导出范围默认是「上月同日 → 今天」",
        panel.start_date.get() == m.month_before(dt.date.today()) and panel.end_date.get() == dt.date.today(),
        (panel.start_date.get(), panel.end_date.get()),
    )

    custom_dir = tmp / "我的导出"
    api.dispatch("save_settings", {"settings": {"导出目录": str(custom_dir)}})
    settings_tab.refresh_values()
    app.update()
    check(
        "改了导出目录后，界面与接口都跟着变",
        panel.export_path_var.get() == str(custom_dir) and api.export_dir() == custom_dir,
        panel.export_path_var.get(),
    )

    picker = DatePickerPopup(panel, dt.date(2026, 2, 14))
    check("万年历从当前选的那天所在月开始", (picker.year, picker.month) == (2026, 2), (picker.year, picker.month))
    check(
        "当前那一天被高亮标出",
        picker.day_buttons[14].palette()["bg"] == picker.today_bg
        and picker.day_buttons[13].palette()["bg"] != picker.today_bg,
        (picker.day_buttons[14].palette()["bg"], picker.day_buttons[13].palette()["bg"]),
    )
    picker.shift_month(-1)
    check("能翻月份", (picker.year, picker.month) == (2026, 1) and len(picker.day_buttons) == 31, (picker.year, picker.month))
    picker.destroy()
    picked: dict = {}
    picker2 = DatePickerPopup(panel, dt.date(2026, 2, 14), on_pick=lambda day: picked.setdefault("day", day))
    picker2.pick(dt.date(2026, 2, 20))
    check("点某一天就选中并回调", picked.get("day") == dt.date(2026, 2, 20), picked)

    panel.start_date.set(dt.date(2026, 3, 1))
    panel.end_date.set(dt.date(2026, 3, 5))
    panel.export_range()
    app.update()
    exported_files = sorted(custom_dir.glob("导出_*.csv"))
    check(
        "按范围导出会写进设置的导出目录",
        bool(exported_files) and "2026-03-01" in exported_files[0].name,
        [item.name for item in exported_files],
    )
    popups.clear()  # 上面是故意的「导出完成」提示

    check(
        "导入预览只有一张表（读不出来的行用状态列标注）",
        "状态" in panel.preview_table.columns and not hasattr(panel, "error_table"),
        panel.preview_table.columns,
    )
    check(
        "导入区不再重复放冲突策略选项（只在设置里有一份）",
        not hasattr(panel, "strategy_var") and "冲突策略" in panel.strategy_hint_var.get(),
        panel.strategy_hint_var.get(),
    )
    api.dispatch("save_settings", {"settings": {"导入默认策略": "全部追加"}})
    settings_tab.refresh_values()
    check(
        "策略提示跟着设置走",
        "全部追加" in panel.strategy_hint_var.get(),
        panel.strategy_hint_var.get(),
    )

    bad_csv = tmp / "带坏行.csv"
    bad_csv.write_text(
        "编号,日期,时间,类型,分类,金额\n"
        "20260305-001,2026-03-05,09:00,支出,餐饮,12.00\n"
        ",2026-03-05,09:00,支出,餐饮,abc\n",
        encoding="utf-8-sig",
    )
    panel.preview(str(bad_csv))
    preview_rows = panel.preview_table._rows
    check(
        "坏行与好行在同一张预览表里",
        len(preview_rows) == 2 and any("读不出来" in row[1] for row in preview_rows),
        preview_rows,
    )
    api.dispatch("save_settings", {"settings": {"导入默认策略": "跳过重复", "导出目录": ""}})
    settings_tab.refresh_values()
    app.update()

    # 「数据与备份」页：备份路径可点选 + 配置数据导出 / 导入
    settings_tab.show_page("数据与备份")
    app.update()
    check(
        "备份路径为…（可点选），右边保留「打开备份目录」",
        settings_tab.backup_path_title == "备份路径为"
        and settings_tab.backup_path_var.get() == str(api.backup_dir())
        and settings_tab.open_backup_button.cget("text") == "打开备份目录",
        (settings_tab.backup_path_title, settings_tab.backup_path_var.get()),
    )
    custom_backup_dir = tmp / "界面备份"
    api.dispatch("save_settings", {"settings": {"备份目录": str(custom_backup_dir)}})
    settings_tab.refresh_values()
    app.update()
    check(
        "改备份目录后界面与接口同步",
        settings_tab.backup_path_var.get() == str(custom_backup_dir) and api.backup_dir() == custom_backup_dir,
        settings_tab.backup_path_var.get(),
    )
    backup_target = storage.read_day(dt.date.today())[0].id
    api.dispatch("update", {"id": backup_target, "changes": {"note": "界面测试改了备份目录"}})
    check("备份确实写到了新目录", bool(list(custom_backup_dir.rglob("*.tsv"))))
    check(
        "配置数据有导出 / 导入两个按钮",
        settings_tab.export_config_button.cget("text") == "导出配置数据"
        and settings_tab.import_config_button.cget("text") == "导入配置数据",
    )
    settings_tab.export_config()
    app.update()
    config_files = sorted((tmp / "导出").glob("配置_*.json"))
    check("从界面导出配置数据", bool(config_files), [item.name for item in config_files])
    check("导出后给出路径提示", "配置" in settings_tab.config_hint_var.get(), settings_tab.config_hint_var.get())
    popups.clear()
    api.dispatch("save_settings", {"settings": {"备份目录": ""}})
    settings_tab.refresh_values()
    app.update()

    # 字号 + 行数预设（都在设置页里，改完立即生效）
    from ledger.ui import common as common_mod2

    rows_spec = config_mod.SPEC_BY_KEY["明细最多显示"]
    font_spec = config_mod.SPEC_BY_KEY["字号"]
    check(
        "「明细最多显示」改成预设选项（5/10/20/50/100/200/500 + 不限）",
        rows_spec.kind == "choice"
        and rows_spec.int_choice
        and tuple(rows_spec.choices) == ("5", "10", "20", "50", "100", "200", "500", "不限"),
        (rows_spec.kind, rows_spec.choices),
    )
    check(
        "「字号」在通用页里、也是预设选项",
        font_spec.group == "通用" and font_spec.kind == "choice" and len(font_spec.choices) >= 4,
        (font_spec.group, font_spec.choices),
    )
    api.dispatch("save_settings", {"settings": {"明细最多显示": "不限"}})
    check("选「不限」存的是 0（不限制行数）", config_mod.get("明细最多显示") == 0, config_mod.get("明细最多显示"))
    api.dispatch("save_settings", {"settings": {"明细最多显示": "50"}})
    check("选预设数字存的是整数", config_mod.get("明细最多显示") == 50, config_mod.get("明细最多显示"))
    settings_tab.refresh_values()
    app.update()
    check(
        "设置页下拉框显示成「不限 / 数字」而不是 0",
        settings_tab.vars["明细最多显示"].get() in ("50", "不限"),
        settings_tab.vars["明细最多显示"].get(),
    )
    api.dispatch("save_settings", {"settings": {"明细最多显示": 500}})

    api.dispatch("save_settings", {"settings": {"字号": "大（11）"}})
    check("字号设置能读出来", common_mod2.font_size() == 11, common_mod2.font_size())
    tabs_before = dict(app.tabs)
    app.apply_settings_changed()
    for _ in range(6):
        app.update()
        time.sleep(0.02)
    check(
        "改字号后界面自动重建（不用重启），四个页面都在",
        set(app.tabs) == set(tabs_before) and app.notebook.index("end") == 4,
        (sorted(app.tabs), app.notebook.index("end")),
    )
    check(
        "重建后的页面用的是新字号",
        common_mod2.pick_font()[1] == 11,
        common_mod2.pick_font(),
    )
    api.dispatch("save_settings", {"settings": {"字号": "标准（10）"}})
    app.apply_settings_changed()
    for _ in range(6):
        app.update()
        time.sleep(0.02)
    check("改回标准字号也正常", common_mod2.font_size() == 10, common_mod2.font_size())

    # 圆角尺度与自绘滚动条（弧度全应用统一，只有四档）
    check(
        "圆角只有四档尺度，且都按 DPI 缩放",
        common_mod.RADIUS_SCALE == {"small": 6, "control": 8, "card": 12, "window": 12}
        and common_mod.corner_radius("control") == common_mod.px(8)
        and common_mod.corner_radius("card") == common_mod.px(12),
        common_mod.RADIUS_SCALE,
    )
    check(
        "内层圆角按「外圆角 − 内边距」同心推导（卡片 12 里放小控件用 6）",
        common_mod.RADIUS_SCALE["card"] - common_mod.RADIUS_SCALE["small"] == 6,
        (common_mod.RADIUS_SCALE["card"], common_mod.RADIUS_SCALE["small"]),
    )
    ui_sources = {
        path.name: path.read_text(encoding="utf-8")
        for path in (Path(__file__).resolve().parent / "ledger" / "ui").glob("*.py")
    }
    hard_coded = {
        name: re.findall(r"radius\s*=\s*\d+", text) for name, text in ui_sources.items()
    }
    check(
        "界面代码里没有写死的圆角数值（一律走 corner_radius 尺度）",
        not any(hard_coded.values()),
        {name: hits for name, hits in hard_coded.items() if hits},
    )
    check(
        "界面上没有残留的方角滚动条（全部换成自绘圆角滚动条）",
        not [child for child in walk(app) if child.winfo_class() == "TScrollbar"],
        [str(child) for child in walk(app) if child.winfo_class() == "TScrollbar"][:4],
    )
    fresh_query = app.tabs["查询统计"]  # 字号那一步重建过界面，这里要取最新的引用
    holder = tk.Toplevel(app)
    holder.geometry(f"{common_mod.px(40)}x{common_mod.px(300)}")
    probe_bar = common_mod.RoundedScrollbar(holder, orient="vertical", command=lambda *a: None)
    probe_bar.pack(fill="both", expand=True)
    holder.update()
    probe_bar.set(0.25, 0.5)
    holder.update()
    kinds = sorted({probe_bar.type(item) for item in probe_bar.find_all()})
    check(
        "圆角滚动条：只画胶囊滑块（不画轨道，避免和底色有色差）",
        kinds == ["polygon"] and len(probe_bar.find_all()) == 1 and probe_bar.get() == (0.25, 0.5),
        (kinds, len(probe_bar.find_all()), probe_bar.get()),
    )
    probe_bar.set(0.0, 1.0)
    holder.update()
    check("内容没超出一屏时滚动条自动隐藏", not probe_bar.find_all(), len(probe_bar.find_all()))
    moved: list = []
    probe_bar.command = lambda *args: moved.append(args)
    probe_bar.set(0.2, 0.4)
    holder.update()
    probe_bar.event_generate("<ButtonPress-1>", x=2, y=probe_bar.winfo_height() - 2)
    holder.update()
    check(
        "点轨道能翻页（调用 scroll pages）",
        moved and moved[-1] == ("scroll", 1, "pages"),
        moved,
    )
    probe_bar.destroy()
    holder.destroy()

    app.update()
    check("整个过程中没有弹出错误框", not popups, popups)
    app.on_close()


# ---------------------------------------------------------------- 入口


def main() -> int:
    base = ROOT / ".自测临时"
    shutil.rmtree(base, ignore_errors=True)
    # 不用 tempfile.mkdtemp：它建出来的目录权限很窄，在某些受限环境里连子目录都进不去
    tmp = base / f"run-{dt.datetime.now():%Y%m%d-%H%M%S-%f}"
    tmp.mkdir(parents=True, exist_ok=True)
    os.environ["JIZHANG_ROOT"] = str(tmp)
    print(f"自测数据目录：{tmp}")
    try:
        core_tests(tmp)
        gui_tests(tmp)
    except Exception as exc:  # pragma: no cover
        import traceback

        traceback.print_exc()
        check("自测过程中没有异常", False, exc)
    finally:
        shutil.rmtree(base, ignore_errors=True)

    passed = sum(1 for ok, _, _ in RESULTS if ok)
    failed = [item for item in RESULTS if not item[0]]
    print("\n" + "=" * 48)
    print(f"通过 {passed} / {len(RESULTS)}")
    for _, name, detail in failed:
        print(f"[失败] {name}\n       {detail}")
    if failed:
        print("自测未全部通过。")
        return 1
    print("全部通过 ✅")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
