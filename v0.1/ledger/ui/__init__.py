"""界面层：tkinter 窗口。

这一层只做「收集输入 → 调 api.py → 显示结果」，不含任何业务规则，
所以以后把界面换成别的语言时，这一层整个丢掉即可，核心层不受影响。
"""

from .app import App, main

__all__ = ["App", "main"]
