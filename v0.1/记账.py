#!/usr/bin/env python
"""记账本入口。

用法：
    python 记账.py                 打开窗口（也可以直接双击 记账.bat）
    python 记账.py api <命令> '<json 参数>'   命令行接口，JSON 进 JSON 出
    python 记账.py api list        查看全部命令行命令
    python 记账.py 自测            跑一遍不弹窗的自检（核心功能 + 界面构建）

以后换成别的语言写界面时，用第二种方式把本程序当子进程调用即可。
"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

USAGE = __doc__


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    if argv and argv[0] in ("api", "cli"):
        from ledger import cli

        return cli.run(argv[1:])
    if argv and argv[0] in ("自测", "selftest", "test"):
        from selftest import main as selftest_main

        return selftest_main()
    if argv and argv[0] in ("-h", "--help", "help", "帮助"):
        print(USAGE)
        return 0

    from ledger.ui import main as ui_main

    return ui_main()


if __name__ == "__main__":
    raise SystemExit(main())
