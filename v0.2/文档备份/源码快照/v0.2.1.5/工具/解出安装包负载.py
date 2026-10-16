# -*- coding: utf-8 -*-
"""从组装好的单文件安装包里切出各段负载（并可选解压），用于"免NET 实跑"自检。

用法：
    python 解出安装包负载.py --包 <安装包.exe> --输出 <目录> [--解压]

容器 v2： [stub][wizard-slim][wizard-full][payload-slim][payload-full][footer 40B]
--解压 时把 wizard-full.zip 与 payload-full.zip 解开到 <目录>\\lb-setup\\，
正好就是"机器没装 .NET"时解包器解出来的工作目录内容（记账本.exe + hostfxr.dll + ... + lbsetup.exe）。
"""

import argparse
import os
import struct
import sys
import zipfile

MAGIC = b"LBSETUP2"
FOOTER_SIZE = 40
# footer 记录顺序（与物理顺序相反）
FOOTER_ORDER = ["payload-full.zip", "payload-slim.zip", "wizard-full.zip", "wizard-slim.zip"]
# 物理顺序
PHYSICAL = ["wizard-slim.zip", "wizard-full.zip", "payload-slim.zip", "payload-full.zip"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--包", required=True)
    parser.add_argument("--输出", required=True)
    parser.add_argument("--解压", action="store_true")
    args = parser.parse_args()

    exe = os.path.abspath(args.包)
    out = os.path.abspath(args.输出)
    os.makedirs(out, exist_ok=True)

    size = os.path.getsize(exe)
    with open(exe, "rb") as f:
        f.seek(-FOOTER_SIZE, os.SEEK_END)
        footer = f.read(FOOTER_SIZE)
        if footer[:8] != MAGIC:
            raise SystemExit("尾部标记不对（不是 LBSETUP2 容器）")
        sizes = dict(zip(FOOTER_ORDER, struct.unpack("<qqqq", footer[8:])))
        stub_len = size - FOOTER_SIZE - sum(sizes.values())
        if stub_len <= 0:
            raise SystemExit("尾部长度不自洽")

        layout = [("stub.exe", 0, stub_len)]
        offset = stub_len
        for name in PHYSICAL:
            layout.append((name, offset, sizes[name]))
            offset += sizes[name]

        for name, start, length in layout:
            f.seek(start)
            data = f.read(length)
            with open(os.path.join(out, name), "wb") as g:
                g.write(data)

    for name, _, length in layout:
        print("  {:<18} {:>8.2f} MB".format(name, length / 1048576))

    if args.解压:
        target = os.path.join(out, "lb-setup")
        os.makedirs(target, exist_ok=True)
        for name in ("payload-full.zip", "wizard-full.zip"):
            with zipfile.ZipFile(os.path.join(out, name)) as z:
                z.extractall(target)
        count = sum(len(files) for _, _, files in os.walk(target))
        print("已解到 {}（{} 个文件）".format(target, count))

    return 0


if __name__ == "__main__":
    sys.exit(main())
