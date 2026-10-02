# -*- coding: utf-8 -*-
"""
ImeTip 图标生成脚本。

用法（在项目根目录执行）：

    python tools/make-icon.py

产出：

    icon.ico                    程序图标（exe 用）。资源管理器、任务栏、标题栏都显示它。
                                8 档尺寸：16/20/24/32/48/64/128/256
    assets/tray-zh.ico          托盘图标：中文状态（青绿「中」）
    assets/tray-en.ico          托盘图标：英文状态（琥珀「E」）
    assets/tray-unknown.ico     托盘图标：读不到状态（灰「?」）
                                各 5 档尺寸：16/20/24/32/48
    icon.svg                    矢量源文件（程序图标那份）
    tools/icon-preview.png      各尺寸 / 各状态的效果预览，给人看的，程序不需要

--------------------------------------------------------------------------
为什么有两套图标
--------------------------------------------------------------------------
**程序图标（exe）是静态的**：它写在 PE 文件里，Windows 只在文件本身变化时
才重新读取，运行时没有任何办法改它。

**托盘图标是动态的**：程序可以随时给 NotifyIcon 换一个 Icon。
所以"图标跟随输入法状态"这件事，只有托盘图标做得到。

于是分工：
    exe 图标   = 品牌标识，静态，用"中英并列"体现它管的是中英两种状态
    托盘图标   = 状态指示，跟着中/英实时切换

--------------------------------------------------------------------------
设计方案
--------------------------------------------------------------------------
统一为"深灰圆角方块 + 彩色字"，配色取自程序自己的 Theme.cs：

    青绿 #4EC9B0 = 中文（也正好是深色主题下「中」字的颜色）
    琥珀 #E0A458 = 英文（深色主题下「英」字的颜色）
    灰色 #888888 = 读不到状态

exe 图标（中英并列）
    深灰底 + 内描边 + 中间一条竖线 + 左边青绿「中」+ 右边琥珀「英」。
    中间那竖线的作用是给两个字一个明确的分界，否则在深色底上会糊成一片。

    代价说清楚：左下角小尺寸（16～32）下这两个字会糊成一团 —— 半块只有
    8~16 像素宽，塞不下一个汉字。这是"一个图标里塞两个字"的必然结果。

托盘图标（三态）
    只有方块的底和一个字，不画描边和状态点 —— 16 像素下它们会糊成脏点。

    英文档用的是拉丁字母「E」而不是汉字「英」：实测 20px 下「英」有 9 个笔画，
    挤在一起根本认不出；「E」只有 4 笔，同样尺寸下清晰得多。颜色（琥珀）
    本身已经承担了主要的区分作用，字形只需要让人确认一下。

--------------------------------------------------------------------------
两个容易忽略的细节
--------------------------------------------------------------------------

1）小尺寸要"光学补偿"
   同一套比例直接缩到 16px，「中」字只剩 7 个像素高，四个横画挤成一团。
   所以小尺寸下要把字**相对放大**（见 glyph_size_for）。这是图标设计的
   常规做法——大图标追求留白，小图标追求辨识度，两者比例本来就不同。

2）anchor="mm" 并不真的居中
   Pillow 的 anchor="mm" 是按字体的 ascent/descent 中点对齐的，而汉字墨迹
   并不落在这个点上（实测微软雅黑的「中」会偏下约 4.4%）。直接用 anchor="mm"
   画出来的图标，字是歪的。所以这里对每个字**分别实测**墨迹范围再补偿
   （见 glyph_metrics）—— 注意「中」「英」「E」「?」的偏移量互不相同，不能共用。

--------------------------------------------------------------------------
为什么不装 SVG 渲染器
--------------------------------------------------------------------------
本脚本用 Pillow 直接按每个尺寸**独立渲染**（先在 4 倍尺寸上画，再缩回去），
而不是"渲染一张 256 的大图然后逐级缩小"。

原因：后者在小尺寸下会糊——256 缩到 16 丢掉了 94% 的像素信息。独立渲染则每个
尺寸都由几何信息重新计算，配合 4 倍超采样拿到平滑边缘，小尺寸下清晰得多。
"""

import io
import os
import struct
import sys

from PIL import Image, ImageDraw, ImageFont

# Windows 控制台默认编码是 GBK，直接 print 中文会炸。这里强制走 UTF-8。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


# ===========================================================================
# 设计参数（想调图标长什么样，改这里就够了）
# ===========================================================================

BASE = 256              # 设计基准尺寸。下面所有数值都按这个尺寸给，渲染时等比缩放。

BG_COLOR = "#262628"    # 方块底色（深色主题卡片 #222224 的同族色，略亮一点）
RING_COLOR = "#55555A"  # 内描边、中缝线的颜色。深色底 + 浅线 = 边界清晰
RING_INSET = 4          # 内描边离边缘多远
RING_WIDTH = 8          # 内描边多粗
CORNER_RADIUS = 56      # 圆角半径

# 小到这个尺寸以下，某些细节就画不出来，干脆省略，免得糊成一坨脏点。
MIN_SIZE_FOR_RING = 24
MIN_SIZE_FOR_SPLIT_LINE = 32     # 中缝线：16~24px 下只有零点几像素宽，画了也是灰雾

FONT_PATH = r"C:\Windows\Fonts\msyhbd.ttc"   # 微软雅黑 Bold
FONT_INDEX = 0

SS = 4   # 超采样倍数（在 4 倍尺寸上作画，再缩回去 = 免费抗锯齿）

# ── 程序图标（exe）：静态的中英并列 ──
# 每项：(字, 颜色, 横向位置——占宽度的比例)
EXE_GLYPHS = [
    ("中", "#4EC9B0", 0.26),
    ("英", "#E0A458", 0.74),
]
EXE_GLYPH_SIZE = 96          # 每个字的大小（半块宽度是 128，得留出边距）
SPLIT_LINE_MARGIN = 22       # 中缝线上下各留多少空白
SPLIT_LINE_WIDTH = 4         # 中缝线粗细
EXE_SIZES = [16, 20, 24, 32, 48, 64, 128, 256]

# ── 托盘图标：三态，跟着输入法状态切换 ──
# 每项：(文件名, 显示的字, 字色, 字号倍率)
# 倍率存在的意义：拉丁字母和半角标点天生比汉字矮小，要放大到视觉等重。
TRAY_STATES = [
    ("tray-zh",      "中", "#4EC9B0", 1.00),
    ("tray-en",      "E",  "#E0A458", 1.15),
    ("tray-unknown", "?",  "#888888", 1.45),
]
TRAY_GLYPH_SIZE = 132    # 托盘没描边没点，字可以比 exe 图标更大
TRAY_SIZES = [16, 20, 24, 32, 48]

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS_DIR = os.path.join(ROOT, "assets")

_GLYPH_METRICS_CACHE = {}    # 字形墨迹偏移缓存，按字分开存


# ===========================================================================
# 辅助计算
# ===========================================================================

def glyph_size_for(size: int, base: float) -> float:
    """
    返回该尺寸下的字号（按 BASE 基准给，调用方再乘缩放系数）。

    越小的图标，字相对越大 —— 见文件开头"光学补偿"那段说明。
    """
    if size >= 96:
        return base
    if size >= 64:
        return base * 1.05
    if size >= 48:
        return base * 1.10
    if size >= 32:
        return base * 1.16
    return base * 1.22


def glyph_metrics(glyph: str):
    """
    量一次该字在 anchor='mm' 下的实际墨迹偏移，按字号归一化后缓存。

    返回 (水平偏移, 垂直偏移)，单位是"每 1 点字号偏移多少"。
    ⚠️ 每个字都要单独量：「中」「英」「E」「?」的墨迹在 em 框里的位置各不相同。
    """
    if glyph not in _GLYPH_METRICS_CACHE:
        probe = 200
        font = ImageFont.truetype(FONT_PATH, probe, index=FONT_INDEX)
        canvas = Image.new("L", (probe * 3, probe * 3), 0)
        ImageDraw.Draw(canvas).text(
            (probe * 1.5, probe * 1.5), glyph, font=font, fill=255, anchor="mm")
        box = canvas.getbbox()
        _GLYPH_METRICS_CACHE[glyph] = (
            ((box[0] + box[2]) / 2 - probe * 1.5) / probe,
            ((box[1] + box[3]) / 2 - probe * 1.5) / probe,
        )
    return _GLYPH_METRICS_CACHE[glyph]


def draw_glyph(draw: ImageDraw.ImageDraw, glyph: str, ink: str,
               center_x: float, center_y: float, font_pt: float) -> None:
    """把一个字以"墨迹正中心"对齐到指定坐标画出来。"""

    font = ImageFont.truetype(FONT_PATH, max(1, round(font_pt)), index=FONT_INDEX)
    off_x, off_y = glyph_metrics(glyph)
    draw.text(
        (center_x - off_x * font_pt, center_y - off_y * font_pt),
        glyph, font=font, fill=ink, anchor="mm",
    )


# ===========================================================================
# 渲染
# ===========================================================================

def _new_canvas(size: int):
    s = size * SS
    k = s / BASE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    return img, ImageDraw.Draw(img), s, k


def _draw_card(draw: ImageDraw.ImageDraw, s: float, k: float, size: int) -> None:
    """画底板 + 内描边（exe 和托盘共用的那层壳）。"""

    if not os.path.exists(FONT_PATH):
        raise SystemExit("找不到字体文件：" + FONT_PATH)

    draw.rounded_rectangle([0, 0, s - 1, s - 1], radius=CORNER_RADIUS * k, fill=BG_COLOR)

    if size >= MIN_SIZE_FOR_RING:
        inset = RING_INSET * k
        draw.rounded_rectangle(
            [inset, inset, s - 1 - inset, s - 1 - inset],
            radius=max(1.0, CORNER_RADIUS * k - inset),
            outline=RING_COLOR,
            width=max(1, round(RING_WIDTH * k)),
        )


def render_exe(size: int) -> Image.Image:
    """程序图标：深灰圆角方块 + 中缝线 + 左「中」右「英」。"""

    img, draw, s, k = _new_canvas(size)
    _draw_card(draw, s, k, size)

    # 中缝线：给两个字一个明确的分界
    if size >= MIN_SIZE_FOR_SPLIT_LINE:
        inset = RING_INSET * k
        draw.line(
            [(s / 2, inset + SPLIT_LINE_MARGIN * k),
             (s / 2, s - inset - SPLIT_LINE_MARGIN * k)],
            fill=RING_COLOR,
            width=max(1, round(SPLIT_LINE_WIDTH * k)),
        )

    font_pt = glyph_size_for(size, EXE_GLYPH_SIZE) * k
    for glyph, ink, pos in EXE_GLYPHS:
        draw_glyph(draw, glyph, ink, s * pos, s / 2, font_pt)

    return img.resize((size, size), Image.Resampling.LANCZOS)


def render_tray(size: int, glyph: str, ink: str, scale: float) -> Image.Image:
    """
    托盘图标：只有底色和一个字。

    刻意不画内描边、不画状态点 —— 16 像素下它们会糊成脏点，
    而且"英文"档的字本身就是琥珀色，再加个琥珀点纯属噪点。
    """

    img, draw, s, k = _new_canvas(size)
    draw.rounded_rectangle([0, 0, s - 1, s - 1], radius=CORNER_RADIUS * k, fill=BG_COLOR)

    font_pt = glyph_size_for(size, TRAY_GLYPH_SIZE * scale) * k
    draw_glyph(draw, glyph, ink, s / 2, s / 2, font_pt)

    return img.resize((size, size), Image.Resampling.LANCZOS)


# ===========================================================================
# 产出 SVG（程序图标那一份）
# ===========================================================================

def write_svg(path: str) -> None:
    """
    写出矢量源文件。数值与上面的渲染代码同源，两者不会走样。

    注意：SVG 里的文字用的是 <text>，所以它的字形依赖系统字体（微软雅黑）。
    这只是"设计源文件"，实际给 Windows 用的是 icon.ico —— 后者由 Pillow
    精确渲染，不依赖任何东西。
    """

    texts = "\n".join(
        f'''  <text x="{round(BASE * pos)}" y="{BASE // 2}" text-anchor="middle"
        dominant-baseline="central"
        font-family="Microsoft YaHei, PingFang SC, Noto Sans SC, sans-serif"
        font-size="{EXE_GLYPH_SIZE}" font-weight="700" fill="{ink}">{glyph}</text>'''
        for glyph, ink, pos in EXE_GLYPHS
    )

    y1 = RING_INSET + SPLIT_LINE_MARGIN
    y2 = BASE - RING_INSET - SPLIT_LINE_MARGIN

    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {BASE} {BASE}"
     width="{BASE}" height="{BASE}" role="img">
  <title>ImeTip</title>
  <desc>深色圆角方块内并排的青绿中字与琥珀英字，中间一条竖线分隔。</desc>

  <rect width="{BASE}" height="{BASE}" rx="{CORNER_RADIUS}" fill="{BG_COLOR}"/>
  <rect x="{RING_INSET}" y="{RING_INSET}"
        width="{BASE - RING_INSET * 2}" height="{BASE - RING_INSET * 2}"
        rx="{CORNER_RADIUS - RING_INSET}" fill="none"
        stroke="{RING_COLOR}" stroke-width="{RING_WIDTH}"/>

  <line x1="{BASE // 2}" y1="{y1}" x2="{BASE // 2}" y2="{y2}"
        stroke="{RING_COLOR}" stroke-width="{SPLIT_LINE_WIDTH}"/>

{texts}
</svg>
'''

    with open(path, "w", encoding="utf-8") as f:
        f.write(svg)


# ===========================================================================
# 产出 ICO
# ===========================================================================

def write_ico(path: str, images: list) -> None:
    """
    手写 ICO 容器。

    为什么不直接用 Pillow 的 save(format="ICO")？
        Pillow 那边只能用"一张图 + sizes 列表"，它会自己做缩放——那样小尺寸
        又是从大图缩出来的，白费了我们逐尺寸渲染的功夫。

    ICO 文件结构（Vista 以后）：
        ICONDIR      6 字节：保留字(0) + 类型(1=图标) + 图像数量
        ICONDIRENTRY × n，每条 16 字节：宽 高 调色板数 保留 位面数 位深 数据长度 数据偏移
        图像数据按顺序紧跟着放

    两个要点：
      · 宽或高写 0 表示 256（一个字节放不下 256）。
      · Vista 以后每个条目可以直接塞 PNG 数据，不必用老式的 DIB。
    """

    count = len(images)
    header = struct.pack("<HHH", 0, 1, count)

    entries = bytearray()
    payload = bytearray()
    offset = 6 + 16 * count          # 数据区从目录之后开始

    for img in images:
        buf = io.BytesIO()
        img.save(buf, format="PNG", optimize=True)
        png = buf.getvalue()

        w, h = img.size
        entries += struct.pack(
            "<BBBBHHII",
            0 if w >= 256 else w,    # 宽（0 = 256）
            0 if h >= 256 else h,    # 高（0 = 256）
            0,                       # 调色板颜色数（真彩色填 0）
            0,                       # 保留
            1,                       # 颜色位面数
            32,                      # 位深
            len(png),                # 该条目数据长度
            offset,                  # 该条目数据偏移
        )
        offset += len(png)
        payload += png

    with open(path, "wb") as f:
        f.write(bytes(header) + bytes(entries) + bytes(payload))


# ===========================================================================
# 产出预览图（给自己看效果的，程序不需要）
# ===========================================================================

def write_preview(path: str) -> None:
    exe_sizes = [s for s in EXE_SIZES if s <= 128]
    tray_real = 20          # 托盘的真实尺寸（Windows 默认 DPI 下就是 20）
    tray_zoom = 6           # 放大多少倍来展示细节

    pad, gap = 24, 20
    light_h, top_h = 150, 300        # 上半：exe 图标（浅底一行 / 深底一行）
    tray_h = 240                     # 下半：托盘三态

    big_w = tray_real * tray_zoom
    row_w = pad * 2 + sum(exe_sizes) + gap * (len(exe_sizes) - 1)
    tray_w = pad * 2 + big_w * len(TRAY_STATES) + gap * (len(TRAY_STATES) - 1)
    width = max(row_w, tray_w)

    canvas = Image.new("RGB", (width, top_h + tray_h), "#1C1C1C")
    draw = ImageDraw.Draw(canvas)
    draw.rectangle([0, 0, width, light_h], fill="#F3F3F3")
    draw.rectangle([0, light_h, width, top_h], fill="#252525")

    # ── 上半：exe 图标各尺寸，浅底一行、深底一行 ──
    x = pad
    for size in exe_sizes:
        icon = render_exe(size)
        canvas.paste(icon, (x, light_h // 2 - size // 2), icon)
        canvas.paste(icon, (x, light_h + (top_h - light_h) // 2 - size // 2), icon)
        x += size + gap

    # ── 下半：托盘三态（放大看细节 + 原尺寸看真实观感）──
    x = pad
    for _, glyph, ink, scale in TRAY_STATES:
        big = render_tray(tray_real, glyph, ink, scale).resize(
            (big_w, big_w), Image.Resampling.NEAREST)
        canvas.paste(big, (x, top_h + 20), big)

        small = render_tray(tray_real, glyph, ink, scale)
        canvas.paste(small, (x + big_w // 2 - tray_real // 2, top_h + 20 + big_w + 16), small)
        x += big_w + gap

    canvas.save(path)


# ===========================================================================
# main
# ===========================================================================

def main() -> None:
    os.makedirs(ASSETS_DIR, exist_ok=True)

    svg_path = os.path.join(ROOT, "icon.svg")
    ico_path = os.path.join(ROOT, "icon.ico")
    preview_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icon-preview.png")

    write_svg(svg_path)
    print("已写出 SVG     ：" + svg_path)

    write_ico(ico_path, [render_exe(s) for s in EXE_SIZES])
    print("已写出程序图标 ：%s  (%s)" % (ico_path, "/".join(str(s) for s in EXE_SIZES)))

    for name, glyph, ink, scale in TRAY_STATES:
        tray_path = os.path.join(ASSETS_DIR, name + ".ico")
        write_ico(tray_path, [render_tray(s, glyph, ink, scale) for s in TRAY_SIZES])
        print("已写出托盘图标 ：%s  (%s)" % (tray_path, "/".join(str(s) for s in TRAY_SIZES)))

    write_preview(preview_path)
    print("已写出预览     ：" + preview_path)


if __name__ == "__main__":
    main()
