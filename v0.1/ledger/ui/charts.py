"""分类统计的图形：饼状图、柱状图、10×10 方格填色。

只用标准库的 tkinter.Canvas 手绘，不引入任何第三方绘图库（打包体积和依赖都不变）。

排版原则（用户要求「图表要完整、并且把面板填满」）：
- 先按面板的长宽比决定图例放右边还是下面，再让图形本体吃掉剩下的空间；
- 图形本体在剩余区域里**居中**，不会挤在左上角留一大片空白；
- 柱状图在「面板够宽且分类不多」时画成竖柱（填满宽度），否则画横向条（填满高度）；
- 图例按可用高度截断，末尾补「…… 还有 N 项」，绝不画到面板外面。
"""

from __future__ import annotations

import math
import tkinter as tk
from decimal import Decimal

from .common import px, ui_font


def _text(canvas, *args, **kwargs):
    """图形里的文字统一用界面字体（受「设置 → 通用 → 界面字号」控制）。"""
    kwargs.setdefault("font", ui_font())
    return tk.Canvas.create_text(canvas, *args, **kwargs)

#: 图形配色（按分类顺序取，循环使用）
PALETTE = (
    "#4e79a7",
    "#f28e2b",
    "#e15759",
    "#76b7b2",
    "#59a14f",
    "#edc948",
    "#b07aa1",
    "#ff9da7",
    "#9c755f",
    "#bab0ac",
    "#1f77b4",
    "#d62728",
)

EMPTY_TEXT = "这个范围内还没有数据"
GRID_BG = "#e8e8e8"
MAX_CELL = px(46)  # 方格图单个方格最大边长，免得面板很大时糊成一整块
#: 占比小于这个值的分类不画进图里（显示出来就是 0.0%，看不出权重）
MIN_SHARE = 0.0005
#: 3D 效果的厚度（像素）
DEPTH = px(7)
PIE_FLATTEN = 0.62  # 饼图压扁成椭圆的比例，看起来才有立体感


def _shade(color: str, factor: float) -> str:
    """颜色变暗（factor < 1）或变亮（factor > 1），用来做 3D 的侧面/顶面。"""
    text = str(color).lstrip("#")
    if len(text) != 6:
        return color
    red, green, blue = (int(text[index : index + 2], 16) for index in (0, 2, 4))
    if factor < 1:
        red, green, blue = (int(value * factor) for value in (red, green, blue))
    else:
        extra = factor - 1
        red, green, blue = (min(255, int(value + (255 - value) * extra)) for value in (red, green, blue))
    return f"#{red:02x}{green:02x}{blue:02x}"


def series_from(by_category: list[dict], min_share: float = MIN_SHARE) -> list[dict]:
    """把「分类小计」整理成画图用的序列，**只保留有权重的分类**。

    同名分类出现在收入和支出两边时，标签写成「收入·其他」这种形式区分开；
    占比小到显示出来就是 0.0% 的分类不画（这种在图里根本看不出来，只会挤乱图例）。
    """
    total = sum((Decimal(str(item.get("amount", "0"))) for item in by_category), Decimal("0.00"))
    seen: dict[str, int] = {}
    for item in by_category:
        category = str(item.get("category", ""))
        seen[category] = seen.get(category, 0) + 1

    series: list[dict] = []
    for item in by_category:
        amount = Decimal(str(item.get("amount", "0")))
        share = float(amount / total) if total else 0.0
        if share < min_share:
            continue
        category = str(item.get("category", ""))
        kind = str(item.get("kind", ""))
        label = f"{kind}·{category}" if seen.get(category, 0) > 1 else category
        series.append(
            {
                "label": label,
                "kind": kind,
                "category": category,
                "count": int(item.get("count", 0)),
                "amount": amount,
                "share": share,
                "color": PALETTE[len(series) % len(PALETTE)],
            }
        )
    return series


def hidden_note(by_category: list[dict], min_share: float = MIN_SHARE) -> str:
    """被「只显示有权重种类」挡掉的那几项，用一行小字说明，避免信息看起来缺失。"""
    total = sum((Decimal(str(item.get("amount", "0"))) for item in by_category), Decimal("0.00"))
    if not total:
        return ""
    hidden = 0
    for item in by_category:
        if float(Decimal(str(item.get("amount", "0"))) / total) < min_share:
            hidden += 1
    if not hidden:
        return ""
    return f"另有 {hidden} 项占比不足 {min_share * 100:g}%，未在图中显示"


def series_total(series: list[dict]) -> Decimal:
    return sum((item["amount"] for item in series), Decimal("0.00"))


# ---------------------------------------------------------------- 公共小工具


def _ready(canvas) -> tuple[int, int] | None:
    width, height = canvas.winfo_width(), canvas.winfo_height()
    if width <= 30 or height <= 30:
        return None
    return width, height


def _empty(canvas, width: int, height: int, note: str = "") -> None:
    _text(canvas, width // 2, height // 2, text=EMPTY_TEXT, fill="#909090")
    if note:
        _text(canvas, 6, height - 6, anchor="sw", text=note, fill="#909090")


def _draw_note(canvas, note: str, height: int) -> None:
    if note:
        _text(canvas, 6, height - 5, anchor="sw", text=note, fill="#909090")


def _shorten(text: str, limit: int) -> str:
    return text if len(text) <= limit else text[: max(1, limit - 1)] + "…"


def _clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


# ---------------------------------------------------------------- 图例


def legend_height_needed(series: list[dict], line_height: int | None = None) -> float:
    return len(series) * (line_height if line_height is not None else px(16))


def draw_legend(
    canvas,
    series,
    x: float,
    y: float,
    width: float,
    line_height: int | None = None,
    max_height: float | None = None,
    max_lines: int = 40,
) -> float:
    """在 (x, y) 处画图例，返回占用的高度。每行：色块 + 名称 + 金额（占比）。"""
    line_height = line_height if line_height is not None else px(16)
    lines = min(max_lines, len(series))
    if max_height is not None:
        limit = max(1, int(max_height // line_height))
        if len(series) > limit:  # 还要留一行写「…… 还有 N 项」
            limit = max(1, limit - 1)
        lines = min(lines, limit)

    drawn = 0
    for item in series[:lines]:
        line_y = y + drawn * line_height
        canvas.create_rectangle(x, line_y + 3, x + 10, line_y + 13, fill=item["color"], outline="")
        name = _shorten(item["label"], max(4, int((width - 130) / 12)))
        _text(canvas, 
            x + 16,
            line_y + 8,
            anchor="w",
            text=f"{name}  ¥{item['amount']:.2f}  {item['share'] * 100:.1f}%",
            fill="#303030",
        )
        drawn += 1
    if len(series) > lines:
        _text(canvas, 
            x + 16,
            y + drawn * line_height + 8,
            anchor="w",
            text=f"…… 还有 {len(series) - lines} 项",
            fill="#909090",
        )
        drawn += 1
    return drawn * line_height


def _legend_beside(width: float, height: float, series: list[dict], line_height: float | None = None) -> bool:
    """面板够宽、且图例放在右边放得下时才放右边；否则放下面。

    条目多的时候优先「图例在右」，这样图例能一次列全，不用截断。
    """
    if width < max(height * 1.05, px(340)):
        return False
    return width - max(px(150), width * 0.42) >= px(110)


# ---------------------------------------------------------------- 饼状图（3D）


def pie_geometry(width: int, height: int, series: list[dict]) -> dict:
    """算 3D 饼图的地盘：返回饼身（椭圆）的外接矩形与图例位置。"""
    beside = _legend_beside(width, height, series)
    depth = DEPTH
    if beside:
        legend_width = _clamp(width * 0.42, 150.0, width - 130.0)
        pie_area_w = width - legend_width - 12
        pie_area_h = height - 12
        legend_x = width - legend_width
        legend_y = max(6.0, (height - legend_height_needed(series)) / 2)
        legend_w = legend_width - 6
    else:
        legend_lines = max(1, int((height * 0.45) // px(16)))
        legend_h = min(len(series), legend_lines) * px(16)
        pie_area_w = width - 16
        pie_area_h = max(40.0, height - legend_h - 22 - depth)
        legend_x = 10.0
        legend_y = 0.0
        legend_w = width - 20

    size_x = max(40.0, min(pie_area_w - 6, (pie_area_h - depth) / PIE_FLATTEN))
    size_y = size_x * PIE_FLATTEN
    left = (6 + (pie_area_w - size_x) / 2) if beside else ((width - size_x) / 2)
    top = 6 + max(0.0, (pie_area_h - size_y - depth) / 2)
    if not beside:
        legend_y = top + size_y + depth + 10
    return {
        "box": (left, top, left + size_x, top + size_y),
        "depth": depth,
        "legend": (legend_x, legend_y, legend_w),
        "beside": beside,
        "size_y": size_y,
    }


def draw_pie(canvas, series: list[dict], note: str = "") -> None:
    """3D 饼图：先画下方偏移的深色「厚度」，再把顶面盖上去。"""
    canvas.delete("all")
    size = _ready(canvas)
    if size is None:
        return
    width, height = size
    if not series:
        _empty(canvas, width, height, note)
        return

    geometry = pie_geometry(width, height, series)
    x0, y0, x1, y1 = geometry["box"]
    depth = geometry["depth"]
    legend_x, legend_y, legend_w = geometry["legend"]
    # 图例可用高度 = 画布高 - 图例起点 - 底部留白（注意别把 legend_y 减两次）
    legend_max_height = max(px(16), height - legend_y - px(6) - (px(14) if note else 0))

    # 下半部分的厚度：每个扇区在下方再画一份深色的
    if len(series) == 1:
        canvas.create_oval(x0, y0 + depth, x1, y1 + depth, fill=_shade(series[0]["color"], 0.65), outline="")
    else:
        start = 90.0
        for item in series:
            extent = -max(0.4, item["share"] * 360.0)
            canvas.create_arc(
                x0,
                y0 + depth,
                x1,
                y1 + depth,
                start=start,
                extent=extent,
                style="pieslice",
                fill=_shade(item["color"], 0.65),
                outline=_shade(item["color"], 0.55),
            )
            start += extent

    # 顶面
    if len(series) == 1:
        canvas.create_oval(*geometry["box"], fill=series[0]["color"], outline="#ffffff")
    else:
        start = 90.0
        for item in series:
            extent = -max(0.4, item["share"] * 360.0)
            canvas.create_arc(
                *geometry["box"],
                start=start,
                extent=extent,
                style="pieslice",
                fill=item["color"],
                outline="#ffffff",
            )
            start += extent

    draw_legend(
        canvas,
        series,
        legend_x,
        legend_y,
        legend_w,
        max_height=legend_max_height,
    )
    _draw_note(canvas, note, height)


# ---------------------------------------------------------------- 柱状图（横向 3D）


def draw_bar(canvas, series: list[dict], note: str = "") -> None:
    """横向 3D 柱状图：每项一条，带顶面与右侧面，看起来像一块有厚度的板。"""
    canvas.delete("all")
    size = _ready(canvas)
    if size is None:
        return
    width, height = size
    if not series:
        _empty(canvas, width, height, note)
        return

    rows = len(series)
    label_width = _clamp(width * 0.2, 52.0, 110.0)
    value_width = 96.0
    bar_left = 8 + label_width
    budget_bottom = px(16) + (px(14) if note else 0)
    bar_space = max(24.0, width - bar_left - value_width - 10 - DEPTH)
    row_height = _clamp((height - budget_bottom - 8) / rows, 20.0, 60.0)
    block_height = rows * row_height
    y0 = 8 + max(0.0, (height - budget_bottom - 8 - block_height) / 2)
    thickness = _clamp(row_height * 0.55, 10.0, 26.0)
    max_amount = max((item["amount"] for item in series), default=Decimal("0.00")) or Decimal("1.00")

    for index, item in enumerate(series):
        row_top = y0 + index * row_height
        middle = row_top + row_height / 2
        top = middle - thickness / 2
        bottom = middle + thickness / 2
        _text(canvas, 6, middle, anchor="w", text=_shorten(item["label"], 8), fill="#303030")

        # 底槽
        canvas.create_rectangle(
            bar_left, top, bar_left + bar_space + DEPTH, bottom, fill="#f2f2f2", outline=""
        )
        length = max(2.0, float(item["amount"] / max_amount) * bar_space)
        base = item["color"]
        # 正面
        canvas.create_rectangle(bar_left, top, bar_left + length, bottom, fill=base, outline="")
        # 顶面（往右上挪一点，做出厚度）
        canvas.create_polygon(
            bar_left,
            top,
            bar_left + DEPTH,
            top - DEPTH,
            bar_left + length + DEPTH,
            top - DEPTH,
            bar_left + length,
            top,
            fill=_shade(base, 1.35),
            outline="",
        )
        # 右侧面
        canvas.create_polygon(
            bar_left + length,
            top,
            bar_left + length + DEPTH,
            top - DEPTH,
            bar_left + length + DEPTH,
            bottom - DEPTH,
            bar_left + length,
            bottom,
            fill=_shade(base, 0.7),
            outline="",
        )
        _text(canvas, 
            width - 6,
            middle,
            anchor="e",
            text=f"¥{item['amount']:.2f} {item['share'] * 100:.1f}%",
            fill="#303030",
        )
    _draw_note(canvas, note, height)


# ---------------------------------------------------------------- 10×10 方格填色


def waffle_counts(series: list[dict], cells: int = 100) -> list[int]:
    """把占比换算成方格数，用「最大余数法」保证总数正好等于 cells。"""
    if not series:
        return []
    exact = [item["share"] * cells for item in series]
    counts = [int(math.floor(value)) for value in exact]
    remainder = cells - sum(counts)
    if remainder > 0:
        order = sorted(range(len(series)), key=lambda i: exact[i] - counts[i], reverse=True)
        for position in range(remainder):
            counts[order[position % len(order)]] += 1
    elif remainder < 0:
        order = sorted(range(len(series)), key=lambda i: exact[i] - counts[i])
        for position in range(-remainder):
            index = order[position % len(order)]
            if counts[index] > 0:
                counts[index] -= 1
    return counts


def waffle_geometry(
    width: int,
    height: int,
    series: list[dict] | None = None,
    columns: int = 10,
    rows: int = 10,
    note: str = "",
):
    """算方格图的地盘：返回 (cell, grid_left, grid_top, legend_x, legend_y, legend_w, beside)。"""
    series = series or []
    note_space = px(14) if note else 0
    beside = _legend_beside(width, height, series, line_height=px(15))
    if beside:
        legend_width = _clamp(width * 0.42, 140.0, width - 130.0)
        grid_area_w = width - legend_width - 14
        grid_area_h = height - 26 - note_space  # 底下留一行说明
        legend_x = width - legend_width
        legend_y = max(6.0, (height - legend_height_needed(series, px(15))) / 2)
        legend_w = legend_width - 6
    else:
        legend_lines = max(1, int((height * 0.4) // px(15)))
        legend_h = min(len(series), legend_lines) * px(15)
        grid_area_w = width - 16
        grid_area_h = height - legend_h - 30 - note_space
        legend_x = 10.0
        legend_y = 0.0  # 画完方格再定
        legend_w = width - 20

    cell = min(grid_area_w / columns, grid_area_h / rows, MAX_CELL)
    cell = max(2.0, cell)
    grid_w = cell * columns
    grid_h = cell * rows
    left = (width - grid_w) / 2 if not beside else 6 + (grid_area_w - grid_w) / 2
    top = 6 + max(0.0, (grid_area_h - grid_h) / 2)
    return cell, left, top, legend_x, legend_y, legend_w, beside


def draw_waffle(canvas, series: list[dict], note: str = "", columns: int = 10, rows: int = 10) -> None:
    canvas.delete("all")
    size = _ready(canvas)
    if size is None:
        return
    width, height = size
    if not series:
        _empty(canvas, width, height, note)
        return

    cells = columns * rows
    counts = waffle_counts(series, cells)
    cell, left, top, legend_x, legend_y, legend_w, beside = waffle_geometry(
        width, height, series, columns, rows, note
    )
    if cell < 3:  # 实在太扁就只画图例，别糊成一团
        draw_legend(canvas, series, 10, 8, width - 20, line_height=px(15), max_height=height - 12 - (px(14) if note else 0))
        _draw_note(canvas, note, height)
        return

    colors: list[str] = []
    for item, count in zip(series, counts):
        colors.extend([item["color"]] * count)
    while len(colors) < cells:
        colors.append(GRID_BG)

    filled = 0
    for index in range(cells):
        column = index % columns
        row = index // columns
        x = left + column * cell
        y = top + row * cell
        color = colors[index] if index < len(colors) else GRID_BG
        canvas.create_rectangle(x + 0.5, y + 0.5, x + cell - 1.0, y + cell - 1.0, fill=color, outline="#ffffff")
        if color != GRID_BG:
            filled += 1

    caption = f"每个方格 = 1%（共 {filled} 格有色，{cells - filled} 格空）"
    _text(canvas, left, top + rows * cell + 12, anchor="w", text=caption, fill="#606060")

    if beside:
        _text(canvas, 
            legend_x, 4, anchor="w", text=f"共 {cells} 格", fill="#808080"
        )
        draw_legend(
            canvas, series, legend_x, legend_y, legend_w, line_height=px(15),
            max_height=height - 24 - (px(14) if note else 0),
        )
    else:
        draw_legend(
            canvas,
            series,
            legend_x,
            top + rows * cell + px(24),
            legend_w,
            line_height=px(15),
            max_height=max(px(15), height - (top + rows * cell + px(26)) - (px(14) if note else 0)),
        )
    _draw_note(canvas, note, height)


# ---------------------------------------------------------------- 统一入口

#: 三种图形（「表格」由查询页自己画，不在这里）
CHART_VIEWS: tuple[str, ...] = ("饼状图", "柱状图", "方格图")
#: 分类统计里可以选的视图，顺序就是面板摆放顺序
STATS_VIEWS: tuple[str, ...] = ("表格", *CHART_VIEWS)

DRAWERS = {"饼状图": draw_pie, "柱状图": draw_bar, "方格图": draw_waffle}


def draw(view: str, canvas, series: list[dict], note: str = "") -> None:
    """按视图名画图；不认识的视图名什么都不做。"""
    drawer = DRAWERS.get(view)
    if drawer is not None:
        drawer(canvas, series, note)
