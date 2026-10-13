# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller 打包配置：双击「打包exe.bat」即可生成 LedgerBook\\记账本.exe。

打包后的程序不依赖 Python 环境；数据目录（数据/配置/导出/备份）会在 exe 同级
目录自动创建。想换数据位置，设置环境变量 JIZHANG_ROOT 指向目标目录即可。

输出位置写死在这里（不靠命令行参数），所以不管从哪里调用，产物都只会落到
LedgerBook\\ 这个下属文件夹里 —— 同时版本备份、打包工具、说明文档也都在这个
文件夹内，删掉它不会影响源码。
"""

import os

from PyInstaller.config import CONF

_HERE = os.path.dirname(os.path.abspath(SPEC))  # noqa: F821 - SPEC 由 PyInstaller 注入
_OUT = os.path.join(_HERE, "LedgerBook")
CONF["distpath"] = _OUT                       # 成品 exe
CONF["workpath"] = os.path.join(_OUT, "build", "work")   # 中间产物
CONF["specpath"] = _HERE
# 改了 CONF 就得自己把目录建出来（PyInstaller 只会建它自己解析出来的默认路径）
for _path in (CONF["distpath"], CONF["workpath"]):
    os.makedirs(_path, exist_ok=True)

a = Analysis(
    ["记账.py"],
    pathex=[],
    binaries=[],
    datas=[],
    hiddenimports=[
        "tkinter",
        "tkinter.ttk",
        "tkinter.font",
        "tkinter.filedialog",
        "tkinter.messagebox",
        "tkinter.simpledialog",
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=["numpy", "pandas", "PIL", "matplotlib", "pytest"],
    noarchive=False,
    optimize=0,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="记账本",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

# 同一份分析结果再出一个「带控制台」的版本：GUI 版没有控制台，
# 命令行 JSON 接口（记账本-命令行.exe api ...）就看不到输出。
cli_exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="记账本-命令行",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

# 用「一个文件夹」的方式发布（onedir）：exe 和它需要的 DLL / pyd 放在一起，
# 启动时不用往临时目录解压 —— 启动更快，也不会被安全软件拦（很多公司的机器
# 禁止往 %TEMP% 写可执行文件，单文件版在那里会起不来）。
coll = COLLECT(
    exe,
    cli_exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name="记账本",
)
