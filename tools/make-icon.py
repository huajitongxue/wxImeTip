# -*- coding: utf-8 -*-
"""
ImeTip 程序图标生成脚本。

用法（在项目根目录执行）：

    python tools/make-icon.py

产出三个文件：

    icon.svg               矢量源文件。想改配色或形状，改本脚本顶部的"设计参数"
                           然后重新跑一遍就行。
    icon.ico               给 Windows 用的图标，内含 8 档尺寸（16 / 20 / 24 / 32 / 48 / 64 / 128 / 256）。
                           资源管理器、任务栏、标题栏会自动挑合适的那一档。
    tools/icon-preview.png 预览图：各尺寸在浅色 / 深色底色下的效果。
                           这个文件只是给人看的，程序本身不需要它。

--------------------------------------------------------------------------
设计方案（方案 B）
--------------------------------------------------------------------------
深灰圆角方块 + 青绿「中」字 + 右下角一个琥珀色小圆点。

配色直接取自程序自己的主题表（Theme.cs），所以图标和程序内部、以及托盘图标
是同一套视觉语言：

    青绿 #4EC9B0 = 深色主题下「中」字的颜色
    琥珀 #E0A458 = 深色主题下「英」字的颜色（右下角那个点就是"英"的暗示）

为什么图标自带一块深色底？
    因为程序图标会被放在任意背景上——浅色的文件管理器、深色的桌面、
    各种颜色的壁纸。不自带底色的话，浅色主题下的白色卡片会直接融进白背景里。

--------------------------------------------------------------------------
两个容易忽略的细节
--------------------------------------------------------------------------

1）小尺寸要"光学补偿"
   同一套比例直接缩到 16px，「中」字只剩 7 个像素高，四个横画挤成一团。
   所以小尺寸下要把字**相对放大**一点（见 glyph_size_for）。这是图标设计的
   常规做法——大图标追求留白，小图标追求辨识度，两者比例本来就不同。

2）anchor="mm" 并不真的居中
   Pillow 的 anchor="mm" 是按字体的 ascent/descent 中点对齐的，而汉字墨迹
   并不落在这个点上（实测微软雅黑的「中」会偏下约 4.4%）。直接用 anchor="mm"
   画出来的图标，字是歪的。所以这里实测一次墨迹范围、按比例补偿
   （见 glyph_metrics），做到与字体无关的精确居中。

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

BASE = 256            # 设计基准尺寸。下面所有数值都按这个尺寸给，渲染时等比缩放。

BG_COLOR = "#262628"    # 方块底色（深色主题卡片 #222224 的同族色，略亮一点）
RING_COLOR = "#55555A"  # 内描边。深色底 + 浅描边 = 边缘清晰，不会糊在暗背景里
INK_COLOR = "#4EC9B0"   # 「中」字颜色（主题表里的青绿）
DOT_COLOR = "#E0A458"   # 状态点颜色（主题表里的琥珀）

GLYPH = "中"           # 图标里的文字

CORNER_RADIUS = 56     # 圆角半径
RING_INSET = 4         # 内描边离边缘多远
RING_WIDTH = 8         # 内描边多粗
GLYPH_SIZE = 120       # 「中」字字号（小尺寸会自动放大，见 glyph_size_for）
DOT_CENTER = 216       # 状态点圆心坐标（x、y 相同 = 右下角）
DOT_RADIUS = 16        # 状态点半径

# 小到这个尺寸以下，细节就画不出来了，干脆省略，免得糊成一坨脏点。
MIN_SIZE_FOR_RING = 24   # 小于 24px 不画内描边
MIN_SIZE_FOR_DOT = 24    # 小于 24px 不画状态点

FONT_PATH = r"C:\Windows\Fonts\msyhbd.ttc"   # 微软雅黑 Bold
FONT_INDEX = 0

# 输出的尺寸档位。16 是托盘和列表视图，256 是"超大图标"视图。
ICON_SIZES = [16, 20, 24, 32, 48, 64, 128, 256]

SS = 4   # 超采样倍数（在 4 倍尺寸上作画，再缩回去 = 免费抗锯齿）

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

_GLYPH_METRICS = None    # 缓存：字形墨迹偏移（按字号归一化）


# ===========================================================================
# 辅助计算
# ===========================================================================

def glyph_size_for(size: int) -> float:
    """
    返回该尺寸下「中」字的字号（按 BASE 基准给，调用方再乘缩放系数）。

    越小的图标，字相对越大 —— 见文件开头"光学补偿"那段说明。
    """
    if size >= 96:
        return GLYPH_SIZE
    if size >= 64:
        return GLYPH_SIZE * 1.05
    if size >= 48:
        return GLYPH_SIZE * 1.10
    if size >= 32:
        return GLYPH_SIZE * 1.16
    return GLYPH_SIZE * 1.22


def glyph_metrics():
    """
    量一次「中」字在 anchor='mm' 下的实际墨迹偏移，按字号归一化后缓存。

    返回 (水平偏移, 垂直偏移)，单位是"每 1 点字号偏移多少"。
    见文件开头关于 anchor='mm' 不居中的说明。
    """
    global _GLYPH_METRICS
    if _GLYPH_METRICS is None:
        probe = 200
        font = ImageFont.truetype(FONT_PATH, probe, index=FONT_INDEX)
        canvas = Image.new("L", (probe * 3, probe * 3), 0)
        ImageDraw.Draw(canvas).text(
            (probe * 1.5, probe * 1.5), GLYPH, font=font, fill=255, anchor="mm")
        box = canvas.getbbox()
        _GLYPH_METRICS = (
            ((box[0] + box[2]) / 2 - probe * 1.5) / probe,
            ((box[1] + box[3]) / 2 - probe * 1.5) / probe,
        )
    return _GLYPH_METRICS


# ===========================================================================
# 渲染
# ===========================================================================

def render(size: int) -> Image.Image:
    """渲染指定边长的图标（RGBA，背景透明）。"""

    if not os.path.exists(FONT_PATH):
        raise SystemExit("找不到字体文件：" + FONT_PATH)

    s = size * SS
    k = s / BASE            # 从设计基准到实际画布的缩放系数

    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # ── 1. 圆角方块底 ──
    draw.rounded_rectangle(
        [0, 0, s - 1, s - 1],
        radius=CORNER_RADIUS * k,
        fill=BG_COLOR,
    )

    # ── 2. 内描边 ──
    if size >= MIN_SIZE_FOR_RING:
        inset = RING_INSET * k
        draw.rounded_rectangle(
            [inset, inset, s - 1 - inset, s - 1 - inset],
            radius=max(1.0, CORNER_RADIUS * k - inset),
            outline=RING_COLOR,
            width=max(1, round(RING_WIDTH * k)),
        )

    # ── 3.「中」字 ──
    glyph_pt = glyph_size_for(size) * k          # 实际字号（点）
    font = ImageFont.truetype(FONT_PATH, max(1, round(glyph_pt)), index=FONT_INDEX)

    off_x, off_y = glyph_metrics()               # 补偿 anchor='mm' 的居中误差
    draw.text(
        (s / 2 - off_x * glyph_pt, s / 2 - off_y * glyph_pt),
        GLYPH, font=font, fill=INK_COLOR, anchor="mm",
    )

    # ── 4. 右下角状态点 ──
    if size >= MIN_SIZE_FOR_DOT:
        c = DOT_CENTER * k
        r = DOT_RADIUS * k
        draw.ellipse([c - r, c - r, c + r, c + r], fill=DOT_COLOR)

    # ── 5. 缩回目标尺寸（这一步顺便把 4 倍超采样的锯齿磨平） ──
    return img.resize((size, size), Image.Resampling.LANCZOS)


# ===========================================================================
# 产出 SVG
# ===========================================================================

def write_svg(path: str) -> None:
    """
    写出矢量源文件。数值与上面的渲染代码同源，两者不会走样。

    注意：SVG 里的文字用的是 <text>，所以它的字形依赖系统字体（微软雅黑）。
    这只是"设计源文件"，实际给 Windows 用的是 icon.ico —— 后者由 Pillow
    精确渲染，不依赖任何东西。
    """

    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {BASE} {BASE}"
     width="{BASE}" height="{BASE}" role="img">
  <title>ImeTip</title>
  <desc>深色圆角方块内的青绿中字，右下角一个琥珀色状态点。</desc>

  <rect width="{BASE}" height="{BASE}" rx="{CORNER_RADIUS}" fill="{BG_COLOR}"/>
  <rect x="{RING_INSET}" y="{RING_INSET}"
        width="{BASE - RING_INSET * 2}" height="{BASE - RING_INSET * 2}"
        rx="{CORNER_RADIUS - RING_INSET}" fill="none"
        stroke="{RING_COLOR}" stroke-width="{RING_WIDTH}"/>

  <text x="{BASE // 2}" y="{BASE // 2}" text-anchor="middle" dominant-baseline="central"
        font-family="Microsoft YaHei, PingFang SC, Noto Sans SC, sans-serif"
        font-size="{GLYPH_SIZE}" font-weight="700" fill="{INK_COLOR}">{GLYPH}</text>

  <circle cx="{DOT_CENTER}" cy="{DOT_CENTER}" r="{DOT_RADIUS}" fill="{DOT_COLOR}"/>
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
    sizes = [s for s in ICON_SIZES if s <= 128]

    pad, gap, row_h = 24, 20, 180
    width = pad * 2 + sum(sizes) + gap * (len(sizes) - 1)

    canvas = Image.new("RGB", (width, row_h * 2), "#F3F3F3")
    draw = ImageDraw.Draw(canvas)
    draw.rectangle([0, row_h, width, row_h * 2], fill="#252525")

    x = pad
    for size in sizes:
        icon = render(size)
        canvas.paste(icon, (x, row_h // 2 - size // 2), icon)              # 浅色底
        canvas.paste(icon, (x, row_h + row_h // 2 - size // 2), icon)      # 深色底
        x += size + gap

    canvas.save(path)


# ===========================================================================
# main
# ===========================================================================

def main() -> None:
    svg_path = os.path.join(ROOT, "icon.svg")
    ico_path = os.path.join(ROOT, "icon.ico")
    preview_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icon-preview.png")

    write_svg(svg_path)
    print("已写出 SVG ：" + svg_path)

    images = [render(s) for s in ICON_SIZES]
    write_ico(ico_path, images)
    print("已写出 ICO ：%s  (%s)" % (ico_path, "/".join(str(s) for s in ICON_SIZES)))

    write_preview(preview_path)
    print("已写出预览 ：" + preview_path)


if __name__ == "__main__":
    main()
