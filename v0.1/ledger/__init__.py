"""记账本 · 核心包。

本包内的模块（paths / table / model / config / storage / query / summary / transfer / api）
**不依赖任何界面库**，只处理文件与业务规则。任何前端（tkinter、命令行、
以后换成别的语言写的界面）都应通过 api.py 调用这里的逻辑。
"""

from .paths import VERSION, DATA_FORMAT_VERSION, APP_NAME

__all__ = ["VERSION", "DATA_FORMAT_VERSION", "APP_NAME"]
