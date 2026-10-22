#!/usr/bin/env python3
"""
生成应用图标 src/YunX.Desktop/Assets/app.ico。

为什么要用脚本生成而不是放一张图：
  · 图标要在 7 个尺寸下都清晰，手工缩放会出现锯齿 —— 这里统一从 1024 母版重采样；
  · 配色要和应用主题一致，写在代码里改起来方便；
  · 只放 PNG 源图的话，日后想微调就得靠猜。有脚本就是可重复、可审阅的。

依赖： Pillow
    pip install pillow

用法：
    python tools/make-icon.py
输出：
    src/YunX.Desktop/Assets/app.ico（16/24/32/48/64/128/256 七个尺寸）
    tools/icon-preview.png（各尺寸拼接预览，不参与构建）
"""

import pathlib
import sys

try:
    from PIL import Image, ImageDraw, ImageFilter
except ImportError:
    sys.exit("需要 Pillow：pip install pillow")


ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT_ICO = ROOT / "src" / "YunX.Desktop" / "Assets" / "app.ico"
OUT_PREVIEW = ROOT / "tools" / "icon-preview.png"

SIZES = [16, 24, 32, 48, 64, 128, 256]

W = (255, 255, 255, 255)


def lerp(a, b, t):
    t = max(0.0, min(1.0, t))
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(len(a)))


def vgrad(size, stops):
    """竖直多段渐变。stops = [(位置 0~1, RGB), ...]"""
    img = Image.new("RGB", (1, size))
    d = ImageDraw.Draw(img)
    for y in range(size):
        t = y / max(1, size - 1)
        col = stops[-1][1]
        for i in range(len(stops) - 1):
            p0, c0 = stops[i]
            p1, c1 = stops[i + 1]
            if p0 <= t <= p1:
                col = lerp(c0, c1, (t - p0) / (p1 - p0))
                break
        d.point((0, y), tuple(int(v) for v in col))
    return img.resize((size, size), Image.BILINEAR)


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1],
                                        radius=radius, fill=255)
    return m


def blank(size):
    return Image.new("RGBA", (size, size), (0, 0, 0, 0))


def build(size=1024):
    """
    构图：圆角方底板（蓝→青渐变，左上柔光 + 右下压暗）
          + 白色云（上方）
          + 白色下箭头（下方）
    关键：云与箭头**分离**，靠尺寸对比和留白取胜 —— 叠在一起会显得拥挤。
    """
    k = size / 1024.0
    img = blank(size)
    radius = int(232 * k)

    def P(v):
        """100 单位坐标系 → 像素"""
        return v * size / 100.0

    # ---------- 底板 ----------
    base = vgrad(size, [
        (0.00, (104, 172, 255)),
        (0.30, (62, 128, 246)),
        (0.66, (40, 92, 212)),
        (1.00, (26, 64, 160)),
    ]).convert("RGBA")

    # 左上柔光：营造单一光源，避免整块平涂
    glow = blank(size)
    ImageDraw.Draw(glow).ellipse(
        [-size * 0.30, -size * 0.40, size * 0.94, size * 0.58],
        fill=(160, 214, 255, 105))
    base = Image.alpha_composite(base, glow.filter(ImageFilter.GaussianBlur(size * 0.11)))

    # 右下压暗：增加体积感
    shade = blank(size)
    ImageDraw.Draw(shade).ellipse(
        [size * 0.16, size * 0.50, size * 1.32, size * 1.36],
        fill=(10, 38, 108, 100))
    base = Image.alpha_composite(base, shade.filter(ImageFilter.GaussianBlur(size * 0.12)))

    img.alpha_composite(Image.composite(base, blank(size), rounded_mask(size, radius)))

    # ---------- 云（y 16~50）----------
    # 先画投影，让云"浮"在底板上
    sh = blank(size)
    sd = ImageDraw.Draw(sh)
    sd.ellipse([P(26), P(24), P(46), P(44)], fill=(8, 36, 96, 110))
    sd.ellipse([P(37), P(18), P(62), P(43)], fill=(8, 36, 96, 110))
    sd.ellipse([P(55), P(25), P(74), P(45)], fill=(8, 36, 96, 110))
    sd.rounded_rectangle([P(26), P(36), P(74), P(48)], radius=P(11), fill=(8, 36, 96, 110))
    img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(size * 0.019)))

    cl = blank(size)
    cd = ImageDraw.Draw(cl)
    cd.ellipse([P(24), P(26), P(47), P(49)], fill=W)       # 左圆
    cd.ellipse([P(36), P(16), P(64), P(44)], fill=W)       # 中圆（最高）
    cd.ellipse([P(54), P(27), P(76), P(50)], fill=W)       # 右圆
    # 底部圆角矩形要收窄，完全被三个圆包住，否则左缘会露出小凸起
    cd.rounded_rectangle([P(25.5), P(30), P(74.5), P(50)], radius=P(12), fill=W)

    # 云内部轻微渐隐（底部带一点冷调，避免一片死白）
    cgrad = vgrad(size, [
        (0.00, (255, 255, 255)),
        (0.28, (255, 255, 255)),
        (0.46, (243, 249, 255)),
        (0.52, (226, 239, 253)),
    ]).convert("RGBA")
    img.alpha_composite(Image.composite(cgrad, blank(size), cl.split()[3]))

    # ---------- 箭头（y 60~90，与云留 10 单位空隙）----------
    ax = 50.0
    shaft_top, shaft_bot = 60.0, 76.0
    shaft_w, head_w, head_tip = 6.2, 14.0, 90.0

    arrow = blank(size)
    ad = ImageDraw.Draw(arrow)
    ad.rounded_rectangle([P(ax - shaft_w), P(shaft_top), P(ax + shaft_w), P(shaft_bot)],
                         radius=P(3.0), fill=W)
    ad.polygon([(P(ax - head_w), P(shaft_bot - 1)),
                (P(ax + head_w), P(shaft_bot - 1)),
                (P(ax), P(head_tip))], fill=W)

    # 外发光：深蓝底上纯白边缘偏硬，加一层淡光晕柔化
    halo = blank(size)
    hd = ImageDraw.Draw(halo)
    hd.rounded_rectangle([P(ax - shaft_w - 1), P(shaft_top - 1),
                          P(ax + shaft_w + 1), P(shaft_bot)],
                         radius=P(3.2), fill=(190, 226, 255, 120))
    hd.polygon([(P(ax - head_w - 1), P(shaft_bot - 1)),
                (P(ax + head_w + 1), P(shaft_bot - 1)),
                (P(ax), P(head_tip + 1.5))], fill=(190, 226, 255, 120))
    img.alpha_composite(halo.filter(ImageFilter.GaussianBlur(size * 0.014)))

    # 箭身左侧淡蓝内阴影：避免"塑料白"
    inner = blank(size)
    ImageDraw.Draw(inner).rounded_rectangle(
        [P(ax - shaft_w + 0.8), P(shaft_top), P(ax - 1.2), P(shaft_bot)],
        radius=P(1.6), fill=(150, 198, 255, 95))
    img.alpha_composite(Image.composite(inner, blank(size), arrow.split()[3]))
    img.alpha_composite(arrow)

    # ---------- 顶部高光描边（细亮线）----------
    hi = blank(size)
    ImageDraw.Draw(hi).rounded_rectangle(
        [1.2 * k, 1.2 * k, size - 1.2 * k, size - 1.2 * k],
        radius=radius, outline=(255, 255, 255, 52), width=max(2, int(3 * k)))
    img.alpha_composite(Image.composite(hi, blank(size), rounded_mask(size, radius)))
    return img


def main():
    master = build(1024)
    # 每个尺寸都从 1024 母版重采样（而不是从 256 逐级缩），小尺寸才不糊
    imgs = [master.resize((s, s), Image.LANCZOS) for s in SIZES]

    OUT_ICO.parent.mkdir(parents=True, exist_ok=True)
    imgs[-1].save(OUT_ICO, format="ICO",
                  sizes=[(s, s) for s in SIZES],
                  append_images=imgs[:-1])

    # 预览图：模拟任务栏 / 资源管理器里的真实观感
    pad = 18
    board = Image.new("RGBA",
                      (sum(SIZES) + pad * (len(SIZES) + 1), 256 + pad * 2),
                      (26, 28, 34, 255))
    x = pad
    for s, im in zip(SIZES, imgs):
        board.paste(im, (x, pad + (256 - s) // 2), im)
        x += s + pad
    board.save(OUT_PREVIEW)

    print(f"已生成 {OUT_ICO.relative_to(ROOT)}  ({OUT_ICO.stat().st_size} bytes, {len(SIZES)} 个尺寸)")
    print(f"预览   {OUT_PREVIEW.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
