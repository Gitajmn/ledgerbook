# -*- coding: utf-8 -*-
"""校验组装好的单文件安装包（容器 v2）：尾部标记、四段负载对不对得上、里面的东西是不是我们要的。

用法：
    python 校验安装包.py <安装包.exe> <stub.exe> <wizard-slim.zip> <wizard-full.zip>
                         <payload-slim.zip> <payload-full.zip> [框架依赖向导构建目录] [自包含向导目录]
"""

import hashlib
import os
import struct
import sys
import zipfile

MAGIC = b"LBSETUP2"
FOOTER_SIZE = 40
FOOTER_ORDER = ["payload-full.zip", "payload-slim.zip", "wizard-full.zip", "wizard-slim.zip"]
PHYSICAL = ["wizard-slim.zip", "wizard-full.zip", "payload-slim.zip", "payload-full.zip"]

results = []


def check(what, ok, extra=""):
    results.append(ok)
    print(("  [通过] " if ok else "  [失败] ") + what + (("  " + extra) if extra else ""))


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def has_text(blob, text):
    """二进制里找一段文字：字符串字面量在元数据里是 UTF-16（且不一定 2 字节对齐，所以按字节模式找），
    方法名/类型名在 #Strings 堆里是 UTF-8。两种都试。"""
    return text.encode("utf-16-le") in blob or text.encode("utf-8") in blob


def main():
    if len(sys.argv) < 7:
        print(__doc__)
        return 2

    exe, stub, wiz_slim, wiz_full, slim, full = sys.argv[1:7]
    build_dir = sys.argv[7] if len(sys.argv) > 7 else None
    selfcontained_dir = sys.argv[8] if len(sys.argv) > 8 else None

    size = os.path.getsize(exe)
    print("安装包：{}（{:.1f} MB）".format(exe, size / 1048576))

    with open(exe, "rb") as f:
        f.seek(-FOOTER_SIZE, os.SEEK_END)
        footer = f.read(FOOTER_SIZE)
        check("尾部标记 LBSETUP2", footer[:8] == MAGIC, repr(footer[:8]))
        sizes = dict(zip(FOOTER_ORDER, struct.unpack("<qqqq", footer[8:])))
        body = size - FOOTER_SIZE
        check("四段负载长度和 ≤ 文件本体", sum(sizes.values()) <= body)
        stub_len = body - sum(sizes.values())
        check("stub 长度等于编译出来的 stub.exe", stub_len == os.path.getsize(stub),
              "包内 {} / 文件 {}".format(stub_len, os.path.getsize(stub)))

        slices = {}
        f.seek(0)
        slices["stub"] = f.read(stub_len)
        offset = stub_len
        for name in PHYSICAL:
            f.seek(offset)
            slices[name] = f.read(sizes[name])
            offset += sizes[name]

    expect = {
        "stub": stub,
        "wizard-slim.zip": wiz_slim,
        "wizard-full.zip": wiz_full,
        "payload-slim.zip": slim,
        "payload-full.zip": full,
    }
    for name, path in expect.items():
        check("包内 {} 与源文件逐字节一致".format(name), sha256_bytes(slices[name]) == sha256(path),
              sha256_bytes(slices[name])[:16])

    # 两套向导：slim 必须是框架依赖（快路靠系统 .NET），full 必须是自包含
    with zipfile.ZipFile(wiz_slim) as z:
        names = sorted(z.namelist())
        check("wizard-slim 里有 lbsetup.exe 与 runtimeconfig", "lbsetup.exe" in names and "lbsetup.runtimeconfig.json" in names)
        cfg = z.read("lbsetup.runtimeconfig.json").decode("utf-8-sig")
        check("wizard-slim 是框架依赖版（快路上用系统 .NET）", '"frameworks"' in cfg and "includedFrameworks" not in cfg)
        dll = z.read("lbsetup.dll")
        check("向导里有 --uninstall 识别", has_text(dll, "--uninstall"))
        check("向导里有按文件名认卸载（卸载记账本）", has_text(dll, "卸载记账本"))
        check("向导里有 --setup（从安装包尾部取负载）", has_text(dll, "--setup"))
        check("向导里有工作目录锁文件协议", has_text(dll, "lb-setup-running.lock"))
        check("向导里没有 rd /s（不会连数据一起删）", not has_text(dll, "rd /s"))
        check("向导里没把注册表失败当致命错误（非致命文案在）", has_text(dll, "未登记"))
        if build_dir and os.path.isfile(os.path.join(build_dir, "lbsetup.dll")):
            check("包内 slim 向导与构建产物一致",
                  sha256_bytes(dll) == sha256(os.path.join(build_dir, "lbsetup.dll")), sha256_bytes(dll)[:16])

    with zipfile.ZipFile(wiz_full) as z:
        cfg = z.read("lbsetup.runtimeconfig.json").decode("utf-8-sig")
        check("wizard-full 是自包含版（没装 .NET 也能起来）", "includedFrameworks" in cfg)
        if selfcontained_dir and os.path.isfile(os.path.join(selfcontained_dir, "lbsetup.dll")):
            check("包内 full 向导与自包含发布产物一致",
                  sha256_bytes(z.read("lbsetup.dll")) == sha256(os.path.join(selfcontained_dir, "lbsetup.dll")))

    # 免NET：payload-full 必须是"真自带运行时"的自包含产物
    with zipfile.ZipFile(full) as z:
        names = set(z.namelist())
        for need in ("hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "记账本.exe",
                     "记账本.runtimeconfig.json", "记账本.deps.json"):
            check("payload-full 里有 " + need, need in names)
        cfg = z.read("记账本.runtimeconfig.json").decode("utf-8-sig")
        check("payload-full 用 includedFrameworks（真自带运行时）", "includedFrameworks" in cfg)
        deps = z.read("记账本.deps.json").decode("utf-8-sig")
        check("payload-full 的 deps.json 带 runtime pack 资产",
              "Microsoft.NETCore.App.Runtime.win-x64/" in deps and
              "Microsoft.WindowsDesktop.App.Runtime.win-x64/" in deps)
        check("payload-full 没混进用不到的 AspNetCore",
              "AspNetCore" not in deps and not any(n.startswith("Microsoft.AspNetCore") for n in names))
        check("payload-full 不再是被平铺复制的 shared 目录", not any(n.startswith("shared/") for n in names))

    # 精简负载：框架依赖（用系统 .NET）
    with zipfile.ZipFile(slim) as z:
        cfg = z.read("记账本.runtimeconfig.json").decode("utf-8-sig")
        check("payload-slim 是框架依赖版", '"frameworks"' in cfg and "includedFrameworks" not in cfg)

    # 解包器里的新逻辑
    stub_bytes = slices["stub"]
    check("解包器认容器 v2（LBSETUP2）", has_text(stub_bytes, "LBSETUP2"))
    check("解包器有快路（wizard-slim）", has_text(stub_bytes, "wizard-slim.zip"))
    check("解包器有完整路（payload-full + wizard-full）",
          has_text(stub_bytes, "wizard-full.zip") and has_text(stub_bytes, "payload-full.zip"))
    check("解包器会跳过已就绪的工作目录（标记文件）", has_text(stub_bytes, "lb-setup-ready.txt"))
    check("解包器会检测已在运行的向导",
          has_text(stub_bytes, "lb-setup-running.lock") and has_text(stub_bytes, "ActivateExistingWizard"))
    check("解包器把安装包路径传给向导（--setup）", has_text(stub_bytes, "--setup"))
    check("解包器里没有 tasklist（不靠枚举进程判断）", not has_text(stub_bytes, "tasklist"))
    check("解包器里没有旧的失败日志文案（说明换成了新脚本）", not has_text(stub_bytes, "无法删除工作目录"))

    failed = results.count(False)
    print("RESULT " + ("PASS" if failed == 0 else "FAIL") + " 通过 {} / 失败 {}".format(len(results) - failed, failed))
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
