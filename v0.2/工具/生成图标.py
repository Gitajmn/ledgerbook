"""生成应用图标（可调图片占比）：白底圆角方形（squircle），「呆毛02」图片右下贴边。

同时输出一张 66% / 90% 的对比图，放在灰底上方便看清圆角形状。
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(r"D:\desktop\记账")
SOURCE = ROOT / "v0.2" / "v0.2.1.5" / "素材" / "呆毛02.png"
PNG_OUT = ROOT / "v0.2" / "v0.2.1.5" / "素材" / "图标.png"
ICO_OUT = ROOT / "v0.2" / "安装" / "素材" / "appicon.ico"
PREVIEW_ICO = ROOT / "v0.2" / "v0.2.1.5" / "素材" / "记账本.ico"
COMPARE = ROOT / "v0.2" / "v0.2.1.5" / "素材" / "图标对比-66与90.png"

SIZE = 1024
SUPER = 4
EXPONENT = 4.6
ICO_SIZES = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]


def squircle_mask(size: int) -> Image.Image:
    big = size * SUPER
    yy, xx = np.mgrid[0:big, 0:big]
    center = (big - 1) / 2
    x = (xx - center) / center
    y = (yy - center) / center
    inside = (np.abs(x) ** EXPONENT + np.abs(y) ** EXPONENT) <= 1.0
    alpha = (inside * 255).astype(np.uint8)
    return Image.fromarray(alpha, mode="L").resize((size, size), Image.LANCZOS)


def build(ratio: float, size: int = SIZE) -> Image.Image:
    """ratio = 图片最大边占图标边长的比例；图片右下贴边。"""
    icon = Image.new("RGBA", (size, size), (255, 255, 255, 255))
    source = Image.open(SOURCE).convert("RGBA")
    limit = int(size * ratio)
    scale = limit / max(source.width, source.height)
    art = source.resize((max(1, round(source.width * scale)), max(1, round(source.height * scale))), Image.LANCZOS)
    icon.alpha_composite(art, (size - art.width, size - art.height))   # 右下角对齐，不留边距
    icon.putalpha(squircle_mask(size))
    return icon


def main() -> None:
    if not SOURCE.exists():
        raise SystemExit(f"找不到源图片：{SOURCE}")

    chosen = build(1.10)          # 110%（定稿）：图片满铺四边贴边，超出部分裁掉
    chosen.save(PNG_OUT)
    chosen.save(ICO_OUT, format="ICO", sizes=ICO_SIZES)
    chosen.resize((256, 256), Image.LANCZOS).save(PREVIEW_ICO, format="ICO", sizes=ICO_SIZES)

    # 对比图：灰底 + 66% / 90% 并排（缩到 420 展示）
    show = 360
    gap = 30
    sheet = Image.new("RGBA", (show * 3 + gap * 4, show + gap * 2), (140, 145, 152, 255))
    for index, ratio in enumerate((0.90, 0.95, 1.00)):
        small = build(ratio, show)
        sheet.alpha_composite(small, (gap + index * (show + gap), gap))
    sheet.save(COMPARE)

    print(f"已生成 110% 图标（定稿）：{PNG_OUT}")
    print(f"已生成 ICO：{ICO_OUT}")
    print(f"已生成对比图：{COMPARE}")
    src = Image.open(SOURCE)
    print(f"源图尺寸：{src.width}x{src.height}；95% 时最大边 {int(SIZE * 0.95)} px；100% 时气泡正好贴左上边")


if __name__ == "__main__":
    main()
