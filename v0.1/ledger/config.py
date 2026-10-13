"""配置读写：分类.json 与 设置.json。

设置项采用**一处声明**的做法：在 SETTING_SPECS 里写一行，界面会自动出现对应控件、
自动持久化、自测里也会自动覆盖到。后续要加「低频、可配置、可能误触」的开关时，
只需要在 SETTING_SPECS 里加一行，再在用到它的地方 config.get(键) 即可。

三条约定（后续版本请遵守）：
1. 每个设置项都必须有默认值：删掉 配置\\设置.json 也照样能用；
2. 低频开关不要放到主线页面（记一笔 / 查询统计 / 月度汇总）上，一律进「设置」页；
3. 设置项只放「用户偏好」，不动数据本身；金额、编号这类规则不允许做成开关。
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from . import paths
from .model import KIND_EXPENSE, KIND_INCOME, KINDS
from .table import atomic_write_text

SETTINGS_VERSION = 2

DEFAULT_CATEGORIES: dict[str, list[str]] = {
    KIND_EXPENSE: ["餐饮", "交通", "购物", "居住", "医疗", "娱乐", "通讯", "人情", "其他"],
    KIND_INCOME: ["工资", "奖金", "兼职", "投资收益", "红包", "其他"],
}


@dataclass(frozen=True, slots=True)
class SettingSpec:
    """一个设置项的声明。"""

    key: str  # 写进 设置.json 的键名
    group: str  # 界面上的分组（= 设置页里的那一页）
    label: str  # 界面标签
    kind: str  # text / int / bool / choice / multi
    default: object  # 默认值（必须有）
    hint: str = ""  # 界面上的说明文字
    choices: tuple = ()  # kind == choice / multi 时的选项
    choices_source: str = ""  # kind == choice 且选项动态时，由界面按来源解析
    hidden: bool = False  # True = 由专门的控件呈现，不参与自动排版（但要持久化、要校验）
    int_choice: bool = False  # kind == choice 且存的是数字（「不限」= 0）时置 True
    unit: str = ""  # 控件右边的单位后缀（例如「行」「px」），照设置页那种一行一个的风格显示


SETTING_SPECS: tuple[SettingSpec, ...] = (
    SettingSpec("货币符号", "通用", "货币符号", "text", "¥", "显示在金额前面的符号"),
    SettingSpec(
        "字号",
        "通用",
        "界面字号",
        "choice",
        "标准（10）",
        "改完立即生效：所有页面按新字号重排",
        ("小（9）", "标准（10）", "大（11）", "很大（12）", "特大（13）"),
    ),
    SettingSpec(
        "默认记账类型",
        "记一笔",
        "默认类型",
        "choice",
        KIND_EXPENSE,
        "打开「记一笔」时预选的类型",
        KINDS,
    ),
    SettingSpec(
        "默认支付方式",
        "记一笔",
        "默认支付方式",
        "choice",
        "微信",
        "记一笔时预选；可选项来自下面的「支付方式」",
        (),
        "支付方式",
    ),
    SettingSpec(
        "保存后清空",
        "记一笔",
        "保存后自动清空金额与备注",
        "bool",
        True,
        "关掉后连续记同样金额更方便，但要自己点「清空」",
    ),
    SettingSpec(
        "显示本次已记",
        "记一笔",
        "显示「本次打开后已记」列表",
        "bool",
        True,
        "关掉可以让录入区更简洁",
    ),
    SettingSpec(
        "查询默认范围",
        "查询",
        "打开查询页时的默认范围",
        "choice",
        "本月",
        "保存设置后会立即重新应用",
        (),
        "预设范围",
    ),
    SettingSpec(
        "明细最多显示",
        "查询",
        "明细最多显示行数",
        "choice",
        500,
        "只决定明细列表显示多少行；合计与统计永远按全部记录计算",
        ("5", "10", "20", "50", "100", "200", "500", "不限"),
        int_choice=True,
        unit="行",
    ),
    SettingSpec(
        "分类统计显示",
        "查询",
        "分类统计里显示哪些视图",
        "multi",
        ["表格", "饼状图"],
        "可多选（表格 / 饼状图 / 柱状图 / 方格图）；多个视图会自动分配区域",
        ("表格", "饼状图", "柱状图", "方格图"),
    ),
    SettingSpec(
        "导入默认策略",
        "导入 / 导出",
        "导入时默认的冲突策略",
        "choice",
        "跳过重复",
        "导入时按这个策略执行，不另外再放一遍选项",
        (),
        "导入策略",
    ),
    SettingSpec(
        "导出目录",
        "导入 / 导出",
        "导出目录",
        "text",
        "",
        "留空 = 程序目录下的 导出\\；在下面点路径也能改",
        (),
        "",
        True,
    ),
    SettingSpec(
        "自动备份",
        "数据与备份",
        "改 / 删 / 导入前自动备份",
        "bool",
        True,
        "关掉能省空间，但改错了就找不回来了",
    ),
    SettingSpec(
        "备份目录",
        "数据与备份",
        "备份目录",
        "text",
        "",
        "留空 = 程序目录下的 备份\\；在下面点路径也能改",
        (),
        "",
        True,
    ),
)

SPEC_BY_KEY: dict[str, SettingSpec] = {spec.key: spec for spec in SETTING_SPECS}
#: 界面上的分组顺序（按 SETTING_SPECS 里首次出现的顺序）
GROUPS: tuple[str, ...] = tuple(dict.fromkeys(spec.group for spec in SETTING_SPECS))

DEFAULT_PAY_METHODS = ["微信", "支付宝", "现金", "银行卡", "其他"]
DEFAULT_WINDOW = {"宽": 1020, "高": 700}
WINDOW_LIMITS = {"宽": (600, 6000), "高": (400, 4000)}

DEFAULT_SETTINGS: dict = {spec.key: spec.default for spec in SETTING_SPECS}
DEFAULT_SETTINGS.update(
    {
        "版本": SETTINGS_VERSION,
        "支付方式": list(DEFAULT_PAY_METHODS),
        "窗口": dict(DEFAULT_WINDOW),
    }
)

_TRUE_WORDS = {"1", "true", "yes", "on", "y", "是", "开", "显示", "启用"}
_FALSE_WORDS = {"0", "false", "no", "off", "n", "否", "关", "隐藏", "禁用"}


# ---------------------------------------------------------------- 底层读写


def _read_json(path: Path) -> dict:
    if not path.exists():
        return {}
    try:
        from .table import read_text

        data = json.loads(read_text(path))
    except (ValueError, OSError):
        return {}
    return data if isinstance(data, dict) else {}


def _write_json(path: Path, data: dict) -> None:
    text = json.dumps(data, ensure_ascii=False, indent=2) + "\n"
    atomic_write_text(path, text, encoding="utf-8-sig")


def _coerce(spec: SettingSpec, value) -> object:
    """把用户手改的值规整成声明的类型；实在解析不了就用默认值。"""
    if spec.kind == "bool":
        if isinstance(value, bool):
            return value
        text = str(value).strip().lower()
        if text in _TRUE_WORDS:
            return True
        if text in _FALSE_WORDS:
            return False
        return bool(spec.default)
    if spec.kind == "int":
        try:
            return int(float(str(value).strip()))
        except (TypeError, ValueError):
            return int(spec.default)
    if spec.kind == "multi":  # 多选：只保留合法的选项，顺序按声明的顺序
        if isinstance(value, str):
            items = [part.strip() for part in value.replace("，", ",").replace("、", ",").split(",")]
        elif isinstance(value, (list, tuple, set)):
            items = [str(part).strip() for part in value]
        else:
            items = []
        allowed = [str(choice) for choice in spec.choices]
        picked = [choice for choice in allowed if choice in items]
        return picked or list(spec.default)
    text = str(value).strip()
    if spec.kind == "choice" and spec.int_choice:
        # 数字类的预设选项：界面上选「不限」或数字，存进去还是整数（「不限」= 0）
        if text in ("", "不限", "不限（全部）"):
            return 0
        try:
            return max(0, int(float(text)))
        except ValueError:
            return int(spec.default)
    if spec.kind == "choice" and spec.choices and text not in spec.choices:
        return spec.default
    return text or str(spec.default)


def _int_in(value, default: int, limits: tuple[int, int]) -> int:
    try:
        number = int(float(str(value).strip()))
    except (TypeError, ValueError):
        return default
    low, high = limits
    return max(low, min(high, number))


def ensure_initialized() -> None:
    """首次运行：建目录、写默认配置；旧版本配置文件会被就地升级。"""
    paths.ensure_dirs()
    if not paths.categories_file().exists():
        save_categories(DEFAULT_CATEGORIES)
    if not paths.settings_file().exists():
        save_settings(DEFAULT_SETTINGS)
        return
    raw = _read_json(paths.settings_file())
    if int(raw.get("版本", 1) or 1) != SETTINGS_VERSION:
        save_settings(load_settings())  # 补上新增的设置项，写成当前版本


# ---------------------------------------------------------------- 分类


def load_categories() -> dict[str, list[str]]:
    raw = _read_json(paths.categories_file())
    result: dict[str, list[str]] = {}
    for kind in KINDS:
        values = raw.get(kind)
        if not isinstance(values, list):
            values = list(DEFAULT_CATEGORIES[kind])
        cleaned: list[str] = []
        for item in values:
            text = str(item).strip()
            if text and text not in cleaned:
                cleaned.append(text)
        result[kind] = cleaned or list(DEFAULT_CATEGORIES[kind])
    return result


def save_categories(categories: dict[str, list[str]]) -> None:
    payload = {"版本": paths.DATA_FORMAT_VERSION}
    for kind in KINDS:
        values = categories.get(kind) or DEFAULT_CATEGORIES[kind]
        payload[kind] = [str(v).strip() for v in values if str(v).strip()]
    _write_json(paths.categories_file(), payload)


def categories_for(kind: str) -> list[str]:
    return load_categories().get(kind, [])


def add_category(kind: str, name: str) -> dict[str, list[str]]:
    name = str(name or "").strip()
    if not name:
        raise ValueError("分类名不能为空")
    if kind not in KINDS:
        raise ValueError(f"类型只能是「{KIND_EXPENSE}」或「{KIND_INCOME}」")
    categories = load_categories()
    if name in categories[kind]:
        raise ValueError(f"「{kind}」里已经有分类「{name}」了")
    categories[kind].append(name)
    save_categories(categories)
    return categories


def rename_category(kind: str, old: str, new: str) -> dict[str, list[str]]:
    new = str(new or "").strip()
    if not new:
        raise ValueError("分类名不能为空")
    categories = load_categories()
    if old not in categories.get(kind, []):
        raise ValueError(f"「{kind}」里没有分类「{old}」")
    if new in categories[kind]:
        raise ValueError(f"「{kind}」里已经有分类「{new}」了")
    categories[kind][categories[kind].index(old)] = new
    save_categories(categories)
    return categories


def remove_category(kind: str, name: str) -> dict[str, list[str]]:
    categories = load_categories()
    if name not in categories.get(kind, []):
        raise ValueError(f"「{kind}」里没有分类「{name}」")
    categories[kind].remove(name)
    if not categories[kind]:
        raise ValueError(f"「{kind}」至少保留一个分类")
    save_categories(categories)
    return categories


def set_categories(kind: str, names) -> dict[str, list[str]]:
    """整体替换某一类的种类列表（设置页「修改」按钮用）。

    顺序会被保留，因为下拉框就按这个顺序显示；会去掉空白、去重后校验。
    """
    if kind not in KINDS:
        raise ValueError(f"类型只能是「{KIND_EXPENSE}」或「{KIND_INCOME}」")
    cleaned: list[str] = []
    for item in names or []:
        text = str(item).strip()
        if not text:
            continue
        if text in cleaned:
            raise ValueError(f"种类「{text}」重复了")
        cleaned.append(text)
    if not cleaned:
        raise ValueError(f"「{kind}」至少要保留一个种类")
    categories = load_categories()
    categories[kind] = cleaned
    save_categories(categories)
    return categories


# ---------------------------------------------------------------- 设置


def load_settings() -> dict:
    """读设置；缺项按默认值补齐，类型不对就纠正，所以永远能拿到一份完整可用的配置。"""
    raw = _read_json(paths.settings_file())
    settings: dict = {"版本": SETTINGS_VERSION}
    for spec in SETTING_SPECS:
        settings[spec.key] = _coerce(spec, raw[spec.key]) if spec.key in raw else spec.default

    methods = raw.get("支付方式")
    if not isinstance(methods, list):
        methods = []
    cleaned = [str(item).strip() for item in methods if str(item).strip()]
    settings["支付方式"] = cleaned or list(DEFAULT_PAY_METHODS)
    if settings.get("默认支付方式") not in settings["支付方式"]:
        settings["默认支付方式"] = settings["支付方式"][0]

    window = raw.get("窗口") if isinstance(raw.get("窗口"), dict) else {}
    settings["窗口"] = {
        key: _int_in(window.get(key), DEFAULT_WINDOW[key], WINDOW_LIMITS[key])
        for key in ("宽", "高")
    }
    return settings


def save_settings(settings: dict) -> None:
    payload: dict = {"版本": SETTINGS_VERSION}
    for spec in SETTING_SPECS:
        payload[spec.key] = _coerce(spec, settings.get(spec.key, spec.default))

    methods = settings.get("支付方式") or DEFAULT_PAY_METHODS
    cleaned = [str(item).strip() for item in methods if str(item).strip()]
    payload["支付方式"] = cleaned or list(DEFAULT_PAY_METHODS)
    if payload["默认支付方式"] not in payload["支付方式"]:
        payload["默认支付方式"] = payload["支付方式"][0]

    window = settings.get("窗口") if isinstance(settings.get("窗口"), dict) else {}
    payload["窗口"] = {
        key: _int_in(window.get(key), DEFAULT_WINDOW[key], WINDOW_LIMITS[key])
        for key in ("宽", "高")
    }
    _write_json(paths.settings_file(), payload)


def apply_settings(changes: dict) -> dict:
    """只改传入的键（界面保存单项设置用），未知键会报错以便尽早发现代码写错。"""
    unknown = [key for key in changes if key not in SPEC_BY_KEY and key not in ("支付方式", "窗口")]
    if unknown:
        raise ValueError(f"不认识的设置项：{'、'.join(sorted(unknown))}")
    settings = load_settings()
    for key, value in changes.items():
        settings[key] = value
    save_settings(settings)
    return load_settings()


def get(key: str, default=None):
    """读单个设置项；键不存在时返回给定默认值。"""
    settings = load_settings()
    if key in settings:
        return settings[key]
    if key in SPEC_BY_KEY:
        return SPEC_BY_KEY[key].default
    return default


def reset_settings() -> dict:
    """全部恢复默认（分类.json 不动）。"""
    fresh = {spec.key: spec.default for spec in SETTING_SPECS}
    fresh["支付方式"] = list(DEFAULT_PAY_METHODS)
    fresh["窗口"] = dict(DEFAULT_WINDOW)
    save_settings(fresh)
    return load_settings()


def setting_items() -> list[dict]:
    """给界面和外部程序用的设置项清单（含当前值和可选项）。"""
    settings = load_settings()
    items = []
    for spec in SETTING_SPECS:
        items.append(
            {
                "key": spec.key,
                "group": spec.group,
                "label": spec.label,
                "kind": spec.kind,
                "value": settings.get(spec.key, spec.default),
                "default": spec.default,
                "hint": spec.hint,
                "choices": list(spec.choices),
                "choices_source": spec.choices_source,
                "hidden": spec.hidden,
            }
        )
    return items
