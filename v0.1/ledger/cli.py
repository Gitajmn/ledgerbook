"""命令行入口：把核心能力以 JSON 形式暴露出去。

这样以后即使把界面换成别的语言（C#/Electron/网页…），也能直接把 exe 当子进程调用：

    记账本.exe api add '{"kind":"支出","category":"餐饮","amount":"18.50"}'
    记账本.exe api query '{"preset":"本月"}'
    记账本.exe api list

统一输出 UTF-8 的 JSON；成功退出码 0，失败 1。
"""

from __future__ import annotations

import argparse
import json
import sys

from . import api
from . import paths


def _write(text: str) -> None:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass
    sys.stdout.write(text)
    if not text.endswith("\n"):
        sys.stdout.write("\n")
    sys.stdout.flush()


def _load_payload(text: str | None, file: str | None, use_stdin: bool = False) -> dict:
    """三种传参方式，按稳妥程度排序：--stdin > --file > 命令行字符串。

    Windows 的 shell 很容易吃掉 JSON 里的引号，所以跨语言调用时优先用 --stdin 或 --file。
    """
    if use_stdin:
        text = sys.stdin.read()
    if file:
        from pathlib import Path

        text = Path(file).read_text(encoding="utf-8-sig")
    if not text or not text.strip():
        return {}
    try:
        data = json.loads(text)
    except ValueError as exc:
        raise ValueError(
            f"JSON 解析失败（{exc}）。注意 shell 会吃掉双引号，建议改用 "
            "`... | 记账本.exe api <命令> --stdin` 或 `--file 参数.json`"
        ) from None
    if not isinstance(data, dict):
        raise ValueError("参数必须是 JSON 对象，例如 '{\"preset\":\"本月\"}'")
    return data


def run(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="记账.py api",
        description=f"{paths.APP_NAME} v{paths.VERSION} 的命令行接口（JSON 进，JSON 出）",
    )
    parser.add_argument("command", help="命令名；用 list 查看全部命令")
    parser.add_argument("payload", nargs="?", help="JSON 参数，例如 '{\"preset\":\"本月\"}'")
    parser.add_argument("--file", help="从文件读取 JSON 参数（避免命令行转义麻烦）")
    parser.add_argument("--stdin", action="store_true", help="从标准输入读取 JSON 参数（最稳妥）")
    parser.add_argument("--pretty", action="store_true", help="格式化输出")
    args = parser.parse_args(argv)

    if args.command == "list":
        result = {
            "ok": True,
            "commands": sorted(api.COMMANDS),
            "usage": "记账.py api <命令> '<json 参数>' ｜ ... | 记账.py api <命令> --stdin ｜ --file 参数.json",
            "examples": [
                "记账.py api add '{\"kind\":\"支出\",\"category\":\"餐饮\",\"amount\":\"18.50\",\"note\":\"早餐\"}'",
                "记账.py api query '{\"preset\":\"本月\"}'",
                "记账.py api month '{\"year\":2026,\"month\":2}'",
                "echo {\"preset\":\"本月\"} | 记账.py api query --stdin",
            ],
        }
    else:
        try:
            payload = _load_payload(args.payload, args.file, args.stdin)
        except (ValueError, OSError) as exc:
            result = {"ok": False, "error": f"参数解析失败：{exc}"}
        else:
            result = api.dispatch(args.command, payload)

    _write(json.dumps(result, ensure_ascii=False, indent=2 if args.pretty else None))
    return 0 if result.get("ok") else 1


def main(argv: list[str] | None = None) -> int:
    return run(list(sys.argv[1:] if argv is None else argv))
