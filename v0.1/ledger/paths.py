"""路径规则。

所有目录都由这里推导，不依赖「当前工作目录」，因此：
- 源码运行时，根目录 = 项目目录（本文件的上一级）；
- 打包成 exe 后，根目录 = exe 所在目录（数据跟着 exe 走，方便绿色版）；
- 需要把数据放到别处（如网盘同步目录）时，设置环境变量 JIZHANG_ROOT 或 记账本根目录。

换语言重写前端时，只要保持同样的目录结构即可。
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

APP_NAME = "记账本"
VERSION = "0.1.0"
DATA_FORMAT_VERSION = 1

ROOT_ENV = "JIZHANG_ROOT"
ROOT_ENV_CN = "记账本根目录"


def app_dir() -> Path:
    """程序所在目录：exe 同级目录，或源码下的项目目录。"""
    if getattr(sys, "frozen", False):  # PyInstaller 等打包后的运行环境
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent.parent


def root() -> Path:
    """数据根目录：允许用环境变量覆盖。"""
    env = os.environ.get(ROOT_ENV) or os.environ.get(ROOT_ENV_CN)
    if env:
        return Path(env).expanduser().resolve()
    return app_dir()


def data_dir() -> Path:
    return root() / "数据"


def config_dir() -> Path:
    return root() / "配置"


def export_dir() -> Path:
    return root() / "导出"


def backup_dir() -> Path:
    return root() / "备份"


def categories_file() -> Path:
    return config_dir() / "分类.json"


def settings_file() -> Path:
    return config_dir() / "设置.json"


def lock_file() -> Path:
    return data_dir() / "记账.lock"


def month_dir(year: int, month: int) -> Path:
    return data_dir() / f"{year:04d}" / f"{month:02d}"


def month_name(year: int, month: int) -> str:
    return f"{year:04d}-{month:02d}"


def day_file(day) -> Path:
    return month_dir(day.year, day.month) / f"{day:%Y-%m-%d}.tsv"


def summary_file(year: int, month: int) -> Path:
    return month_dir(year, month) / "_月度汇总.csv"


def category_summary_file(year: int, month: int) -> Path:
    return month_dir(year, month) / "_分类小计.csv"


def ensure_dirs() -> None:
    """首次运行时建好全部目录。"""
    for path in (data_dir(), config_dir(), export_dir(), backup_dir()):
        path.mkdir(parents=True, exist_ok=True)


def describe() -> dict:
    """给界面和命令行看的目录信息。"""
    return {
        "root": str(root()),
        "data": str(data_dir()),
        "config": str(config_dir()),
        "export": str(export_dir()),
        "backup": str(backup_dir()),
    }
