"""ASCII 名字的启动入口，只给 .bat / .cmd 调用。

为什么需要它：Windows 的批处理文件是**按系统代码页**读取内容的（中文 Windows 上是
GBK），而本程序的目录名和主程序名都是中文（源码是 UTF-8）。如果 .bat 里直接写
"记账.py"，cmd 会把 UTF-8 字节当成 GBK 解释，于是报「系统找不到指定的文件」。
所以 .bat 只调用这个纯 ASCII 名字的 run.py，由 Python 按 UTF-8 去读中文名的入口。

用法（和 记账.py 完全一致）：
    python run.py                打开窗口
    python run.py api <命令>      命令行 JSON 接口
    python run.py 自测            跑自检

另外它还兜住「用 pythonw 启动时出错却什么都看不到」这种情况：
一旦启动过程中抛异常，会把完整堆栈写进程序目录的 运行错误.log，并弹窗提示。
"""

from __future__ import annotations

import datetime as dt
import runpy
import sys
import traceback
from pathlib import Path

ROOT = Path(__file__).resolve().parent
ENTRY = ROOT / "记账.py"
LOG = ROOT / "运行错误.log"


def write_log(text: str) -> Path:
    try:
        with open(LOG, "a", encoding="utf-8") as handle:
            handle.write(text.rstrip() + "\n" + "-" * 60 + "\n")
    except OSError:
        pass
    return LOG


def alert(message: str) -> None:
    """弹窗提示；没有图形环境时退回控制台输出。"""
    try:
        import tkinter as tk
        from tkinter import messagebox

        root = tk.Tk()
        root.withdraw()
        messagebox.showerror("记账本启动失败", message)
        root.destroy()
    except Exception:
        try:
            sys.stderr.write(message + "\n")
        except Exception:
            pass


def main() -> int:
    if str(ROOT) not in sys.path:
        sys.path.insert(0, str(ROOT))
    if not ENTRY.exists():
        message = f"找不到主程序：{ENTRY}\n请确认 run.py 与 记账.py 在同一个目录里。"
        alert(message)
        return 1
    try:
        runpy.run_path(str(ENTRY), run_name="__main__")
    except SystemExit as exc:  # 入口自己决定退出码
        code = exc.code
        if code is None:
            return 0
        return code if isinstance(code, int) else 1
    except BaseException:
        detail = traceback.format_exc()
        stamp = f"时间：{dt.datetime.now():%Y-%m-%d %H:%M:%S}\n版本：见 配置\\设置.json\n"
        path = write_log(stamp + detail)
        last_line = detail.strip().splitlines()[-1] if detail.strip() else "未知错误"
        alert(f"记账本启动失败，详细信息已写入：\n{path}\n\n{last_line}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
