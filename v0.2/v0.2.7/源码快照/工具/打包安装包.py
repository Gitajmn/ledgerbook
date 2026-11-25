"""一键打单文件安装包：调用 Inno Setup 的 ISCC.exe 编译 记账本.iss，再把成品拷到「最新版」。"""
from __future__ import annotations
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(r"D:\desktop\记账")
PKG = ROOT / "v0.2" / "安装包"
LATEST = ROOT / "newest"
import os

LOCAL = os.environ.get("LOCALAPPDATA", "")
PF = os.environ.get("ProgramFiles", r"C:\Program Files")
PF86 = os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")
CANDIDATES = [
    Path(PF86) / "Inno Setup 6" / "ISCC.exe",
    Path(PF) / "Inno Setup 6" / "ISCC.exe",
    Path(PF86) / "Inno Setup 7" / "ISCC.exe",
    Path(PF) / "Inno Setup 7" / "ISCC.exe",
    Path(LOCAL) / "Programs" / "Inno Setup 6" / "ISCC.exe",
    Path(LOCAL) / "Programs" / "Inno Setup 7" / "ISCC.exe",
    Path(r"C:\InnoSetup\ISCC.exe"),
]
ISCC = next((p for p in CANDIDATES if p.exists()), None)
if ISCC is None:
    # 兜底：在常见位置浅层搜索
    for base in (Path(PF86), Path(PF), Path(LOCAL) / "Programs", Path(r"C:\InnoSetup")):
        if base.exists():
            hit = next(iter(sorted(base.glob("Inno Setup */ISCC.exe"))), None)
            if hit is not None:
                ISCC = hit
                break
if ISCC is None:
    print("找不到 ISCC.exe —— 请先安装 Inno Setup 6（https://jrsoftware.org/isdl.php）")
    sys.exit(1)

print("使用编译器：", ISCC)
result = subprocess.run([str(ISCC), str(PKG / "记账本.iss")], capture_output=True, text=True, encoding="utf-8", errors="replace")
print(result.stdout[-2000:] if result.stdout else "")
if result.returncode != 0:
    print("编译失败：", result.stderr[-2000:] if result.stderr else "")
    sys.exit(result.returncode)

out = LATEST / "记账本-安装包-v2.0.0.exe"
print("编译完成，产出：", out, f"{out.stat().st_size/1024/1024:.1f} MB" if out.exists() else "（未找到产物）")