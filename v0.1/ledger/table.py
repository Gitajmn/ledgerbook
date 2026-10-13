"""表格文件读写（每日 TSV、导出 CSV 共用）。

要处理的现实情况：用户可能用 Excel 打开并另存，编码可能是 UTF-8、带 BOM 的
UTF-8、GBK，甚至是 Excel 默认的 UTF-16。所以读的时候按 BOM 与候选编码依次尝试，
分隔符按首行里有没有制表符判断。写的时候统一 UTF-8 BOM + 制表符，Excel 双击不乱码。
"""

from __future__ import annotations

import csv
import io
import os
import tempfile
from pathlib import Path

ENCODING_WRITE = "utf-8-sig"
ENCODING_TRIES = ("utf-8-sig", "utf-8", "gbk", "utf-16")

BOM_UTF16_LE = b"\xff\xfe"
BOM_UTF16_BE = b"\xfe\xff"
BOM_UTF8 = b"\xef\xbb\xbf"


def decode_bytes(raw: bytes) -> str:
    """按 UTF-16 BOM → UTF-8 BOM → UTF-8 → GBK → UTF-16 的顺序解码。"""
    if raw.startswith(BOM_UTF16_LE) or raw.startswith(BOM_UTF16_BE):
        return raw.decode("utf-16")
    if raw.startswith(BOM_UTF8):
        return raw.decode("utf-8-sig")
    for encoding in ("utf-8", "gbk", "utf-16"):
        try:
            return raw.decode(encoding)
        except (UnicodeDecodeError, UnicodeError):
            continue
    return raw.decode("utf-8", errors="replace")


def read_text(path: Path | str) -> str:
    return decode_bytes(Path(path).read_bytes())


def sniff_delimiter(text: str, default: str = "\t") -> str:
    for line in text.splitlines():
        if not line.strip():
            continue
        return "\t" if "\t" in line else ","
    return default


def read_table(path: Path | str) -> tuple[list[str], list[list[str]]]:
    """读表格文件，返回（表头, 数据行）。表头按去空格后的原样返回。"""
    text = read_text(path)
    if not text.strip():
        return [], []
    delimiter = sniff_delimiter(text)
    rows = [
        [cell.strip() for cell in row]
        for row in csv.reader(io.StringIO(text, newline=""), delimiter=delimiter)
    ]
    rows = [row for row in rows if any(cell != "" for cell in row)]
    if not rows:
        return [], []
    header = [cell.lstrip("\ufeff") for cell in rows[0]]
    return header, rows[1:]


def atomic_write_text(path: Path | str, text: str, encoding: str = ENCODING_WRITE) -> None:
    """先写同目录临时文件，再原子替换，避免写一半崩溃把数据弄坏。

    这里刻意不用 tempfile.mkstemp：它在 Windows 上会创建权限很窄的文件，
    在某些受限运行环境里反而写不进去；自己拼名字更省事也更可靠。
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = path.parent / f".{path.name}.tmp-{os.getpid()}"
    try:
        with open(tmp_path, "w", encoding=encoding, newline="") as handle:
            handle.write(text)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(tmp_path, path)
    except BaseException:
        try:
            os.unlink(tmp_path)
        except OSError:
            pass
        raise


def write_table(
    path: Path | str,
    header: list[str],
    rows: list[list[str]],
    delimiter: str = "\t",
) -> None:
    buffer = io.StringIO(newline="")
    writer = csv.writer(buffer, delimiter=delimiter, lineterminator="\r\n")
    if header:
        writer.writerow(header)
    writer.writerows(rows)
    atomic_write_text(path, buffer.getvalue())
