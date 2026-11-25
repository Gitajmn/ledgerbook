# -*- coding: utf-8 -*-
"""把"解包器 + 两套向导 + 两个负载"组装成单文件免 .NET 安装包（记账本-安装包-*.exe）。

单文件格式（自研容器 v2，不用任何安装器框架、绝不碰 %TEMP%）：
    [stub.exe]             .NET Framework 4.8 的 WinForms 解包器，Windows 预装，双击即跑
    [wizard-slim.zip]      框架依赖向导（0.4MB）：机器已装 .NET 8 时用它，向导自己就能跑，
                           负载不用解出来 —— 向导直接从安装包尾部切需要的那个负载
    [wizard-full.zip]      自包含向导（0.4MB）：机器没装 .NET 时用；它的运行时由 payload-full 提供
    [payload-slim.zip]     精简负载（0.4MB）：本体（用系统 .NET）
    [payload-full.zip]     自包含负载（61.9MB）：本体 + 内置 .NET 8 运行时
    [footer 40 字节]       "LBSETUP2" + int64×4 = {full, slim, wizardFull, wizardSlim} 的长度
                          （尾部往前依次是 full → slim → wizardFull → wizardSlim，与物理顺序相反）

用法：
    python 组装安装包.py --stub <stub.exe> --wizard-slim <zip> --wizard-full <zip>
                         --slim <zip> --full <zip> --out <输出.exe>
"""

import argparse
import os
import shutil
import struct
import sys

MAGIC = b"LBSETUP2"
FOOTER_SIZE = 40

# 物理顺序（紧跟 stub 之后）
PHYSICAL = ["wizard-slim.zip", "wizard-full.zip", "payload-slim.zip", "payload-full.zip"]
# footer 里的记录顺序（与物理顺序相反：从尾部往前读）
FOOTER_ORDER = ["payload-full.zip", "payload-slim.zip", "wizard-full.zip", "wizard-slim.zip"]


def assemble(parts, stub, out):
    """parts: {名字: 路径}（PHYSICAL 里的四个）；stub: 解包器 exe 路径。"""
    if not stub or not os.path.isfile(stub):
        raise SystemExit("缺少解包器 stub.exe：" + str(stub))
    if os.path.getsize(stub) == 0:
        raise SystemExit("解包器是空文件：" + stub)
    for name in PHYSICAL:
        path = parts.get(name)
        if not path or not os.path.isfile(path):
            raise SystemExit("缺少文件：" + str(name) + " → " + str(path))
        if os.path.getsize(path) == 0:
            raise SystemExit("文件是空的：" + path)

    with open(out, "wb") as target:
        # ① 解包器本体（必须是可执行文件，双击跑的就是它）
        with open(stub, "rb") as src:
            shutil.copyfileobj(src, target, 1 << 20)
        # ② 四段负载（物理顺序）
        for name in PHYSICAL:
            with open(parts[name], "rb") as src:
                shutil.copyfileobj(src, target, 1 << 20)
        # ③ 尾部：标记 + 长度（记录顺序与物理顺序相反）
        sizes = [os.path.getsize(parts[name]) for name in FOOTER_ORDER]
        target.write(MAGIC + b"".join(struct.pack("<q", n) for n in sizes))

    return sizes


def verify(path):
    """按解包器的读法回读一遍：尾部标记 + 四段长度能不能对上。"""
    size = os.path.getsize(path)
    with open(path, "rb") as f:
        f.seek(-FOOTER_SIZE, os.SEEK_END)
        footer = f.read(FOOTER_SIZE)
    if footer[:8] != MAGIC:
        raise SystemExit("尾部标记不对，组装失败")
    sizes = struct.unpack("<qqqq", footer[8:])
    body = size - FOOTER_SIZE
    if sum(sizes) > body:
        raise SystemExit("四段负载长度加起来超过文件本体，组装失败")
    return size, sizes


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--stub", required=True)
    parser.add_argument("--wizard-slim", required=True, dest="wizard_slim")
    parser.add_argument("--wizard-full", required=True, dest="wizard_full")
    parser.add_argument("--slim", required=True)
    parser.add_argument("--full", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    parts = {
        "wizard-slim.zip": args.wizard_slim,
        "wizard-full.zip": args.wizard_full,
        "payload-slim.zip": args.slim,
        "payload-full.zip": args.full,
    }
    sizes = assemble(parts, args.stub, args.out)
    size, read_sizes = verify(args.out)
    if tuple(sizes) != read_sizes:
        raise SystemExit("回读的长度和写入的不一致，组装失败")

    with open(args.out, "rb") as f:
        head = f.read(2)
    if head != b"MZ":
        raise SystemExit("组装出来的文件不是可执行文件（开头不是 MZ），组装失败")

    print("已组装：" + args.out)
    print("  总大小      ：{:.1f} MB（{} 字节）".format(size / 1048576, size))
    for name, value in zip(FOOTER_ORDER, sizes):
        print("  {:<16}：{:.1f} MB".format(name, value / 1048576))
    print("  stub.exe        ：{} 字节".format(size - FOOTER_SIZE - sum(sizes)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
