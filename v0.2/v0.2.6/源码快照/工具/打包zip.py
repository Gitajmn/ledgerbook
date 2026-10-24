# -*- coding: utf-8 -*-
"""把一个目录压成 zip（安装包负载 / 便携包都用它），条目放在 zip 根下、正斜杠路径。

用法：
    python 打包zip.py --源 <目录> --输出 <zip> [--只要前缀 lbsetup.] [--排除 名字]...

为什么单独写一个：.NET 的 ZipFile 在 Windows 上会把条目名写成反斜杠，Explorer 解压没问题，
但跨工具（Python/PowerShell 读）容易出意外；统一用正斜杠更稳。
压缩用 deflate（和以前的负载一致：157 MB → 65 MB 那种），解压快。
"""

import argparse
import os
import sys
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--源", required=True)
    parser.add_argument("--输出", required=True)
    parser.add_argument("--只要前缀", default="")
    parser.add_argument("--排除", action="append", default=[])
    args = parser.parse_args()

    src = os.path.abspath(args.源)
    out = os.path.abspath(args.输出)
    if not os.path.isdir(src):
        raise SystemExit("源目录不存在：" + src)

    if os.path.exists(out):
        os.remove(out)

    out_dir = os.path.dirname(out)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)

    count = 0
    raw = 0
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for root, dirs, files in os.walk(src):
            dirs.sort()
            for name in sorted(files):
                full = os.path.join(root, name)
                rel = os.path.relpath(full, src).replace("\\", "/")
                if args.只要前缀 and not name.startswith(args.只要前缀):
                    continue
                if name in args.排除 or rel in args.排除:
                    continue
                if os.path.abspath(full) == out:
                    continue
                z.write(full, rel)
                count += 1
                raw += os.path.getsize(full)

    size = os.path.getsize(out)
    print("已打包：{}".format(out))
    print("  条目 {} 个，原始 {:.1f} MB → 压缩后 {:.1f} MB（{:.0f}%）".format(
        count, raw / 1048576, size / 1048576, 100.0 * size / raw if raw else 0))
    with zipfile.ZipFile(out) as z:
        bad = [n for n in z.namelist() if "\\" in n]
        if bad:
            raise SystemExit("条目名里有反斜杠，打包失败：" + bad[0])
    return 0


if __name__ == "__main__":
    sys.exit(main())
