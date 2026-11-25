# -*- coding: utf-8 -*-
"""解包器（stub）端到端实测：真的组装一个安装包、真的双击跑一遍、真的看它有没有留垃圾。

验的是这几条：
  ① 解包器启动向导后【立即退出】（不再等向导）—— 旧写法会一直挂到用户关掉向导；
  ② **快路**：本机装了 .NET 8，所以只该解 0.4MB 的 wizard-slim，**不许**解 payload-full
     （旧版一上来就解 146MB 运行时，又慢又容易被占用锁住）；
  ③ 工作目录由独立的延迟清理脚本收拾：向导还在跑时绝不提前删，向导一退出就删干净，
     脚本自己也会删掉自己，不留 .cmd / 锁文件 / 日志。

用法：python 解包器端到端测试.py <stub.exe> <假向导.exe>
"""

import importlib.util
import os
import shutil
import subprocess
import sys
import time
import zipfile

BASE = r"D:\desktop\记账\临时\解包器 端到端测试"
ASSEMBLER = r"D:\desktop\记账\v0.2\工具\组装安装包.py"

results = []


def check(what, ok, extra=""):
    results.append((what, ok))
    print(("  [通过] " if ok else "  [失败] ") + what + (("  " + extra) if extra else ""))


def load_assembler():
    spec = importlib.util.spec_from_file_location("assembler", ASSEMBLER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def make_zip(path, entries):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, content in entries.items():
            z.writestr(name, content)


def main():
    if len(sys.argv) < 3:
        print("用法：python 解包器端到端测试.py <stub.exe> <假向导.exe>")
        return 2

    stub, fake_wizard = sys.argv[1], sys.argv[2]
    for path in (stub, fake_wizard):
        if not os.path.isfile(path):
            print("缺少文件：" + path)
            return 2

    print("测试目录：" + BASE + "（故意带空格和中文，压引号/代码页）")
    if os.path.isdir(BASE):
        shutil.rmtree(BASE, ignore_errors=True)
    os.makedirs(BASE)

    # 两套假向导内容一样（真流程里 slim/full 的差别在 runtimeconfig，这里只验解包器的取舍）
    wiz_slim = os.path.join(BASE, "wizard-slim.zip")
    wiz_full = os.path.join(BASE, "wizard-full.zip")
    for target in (wiz_slim, wiz_full):
        with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
            z.write(fake_wizard, "lbsetup.exe")
            z.writestr("lbsetup.runtimeconfig.json", "{}")
    slim = os.path.join(BASE, "payload-slim.zip")
    full = os.path.join(BASE, "payload-full.zip")
    make_zip(slim, {"LedgerBook.Core.dll": "slim payload"})
    make_zip(full, {"runtime/假运行时.txt": "full payload" * 100, "coreclr.dll": "fake"})

    setup = os.path.join(BASE, "测试安装包.exe")
    load_assembler().assemble(
        {
            "wizard-slim.zip": wiz_slim,
            "wizard-full.zip": wiz_full,
            "payload-slim.zip": slim,
            "payload-full.zip": full,
        },
        stub,
        setup,
    )
    print("已组装测试安装包：{}（{:.2f} MB）".format(setup, os.path.getsize(setup) / 1048576))

    work = os.path.join(BASE, "lb-setup")
    lock = os.path.join(BASE, "lb-setup-running.lock")
    log = os.path.join(BASE, "lb-setup-清理失败.txt")

    print("开始运行解包器（假向导会活 8 秒）…")
    started = time.time()
    proc = subprocess.Popen([setup], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        code = proc.wait(timeout=120)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=30)
        check("解包器自己在几秒内就退出了（不等向导）", False)
        failed = [w for w, ok in results if not ok]
        print("RESULT FAIL 通过 {} / 失败 {}".format(len(results) - len(failed), len(failed)))
        return 1
    elapsed = time.time() - started
    print("解包器已退出：退出码 {}，用时 {:.1f} 秒".format(code, elapsed))

    check("解包器自己在几秒内就退出了（不等向导）", elapsed < 8)
    check("退出码为 0", code == 0)

    # 此刻假向导应该还在跑（8 秒），所以工作目录必须还在
    check("向导还在跑：工作目录还在", os.path.isdir(work))
    check("向导已上锁（锁文件在）", os.path.isfile(lock))
    check("假向导确实启动了", os.path.isfile(os.path.join(work, "假向导已启动.txt")))
    scripts = [f for f in os.listdir(BASE) if f.lower().endswith(".cmd")]
    check("清理脚本已就位", len(scripts) == 1)

    # 快路：只解了向导，没有解负载
    files = os.listdir(work) if os.path.isdir(work) else []
    check("快路：工作目录里有向导本体与就绪标记",
          "lbsetup.exe" in files and "lb-setup-ready.txt" in files, "、".join(sorted(files)))
    check("快路：没有把 payload-full 解出来",
          not os.path.exists(os.path.join(work, "runtime")) and "payload-full.zip" not in files)
    check("快路：没有解出 coreclr.dll（说明没解运行时）", not os.path.exists(os.path.join(work, "coreclr.dll")))

    # 等清理脚本动手（向导 8 秒后退，脚本最多再等 3 秒左右就删）
    deadline = time.time() + 60
    while time.time() < deadline and os.path.isdir(work):
        time.sleep(0.5)

    check("向导退出后：工作目录被删干净", not os.path.isdir(work))
    check("锁文件被删掉", not os.path.isfile(lock))
    check("清理脚本自删（不留 .cmd）", not [f for f in os.listdir(BASE) if f.lower().endswith(".cmd")])
    check("没有留下清理失败日志", not os.path.isfile(log))
    check("安装包自己还在（没被误删）", os.path.isfile(setup))

    failed = [w for w, ok in results if not ok]
    print("RESULT " + ("PASS" if not failed else "FAIL") + " 通过 {} / 失败 {}".format(len(results) - len(failed), len(failed)))
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
