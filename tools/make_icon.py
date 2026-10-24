"""Generate Assets/app.ico (app icon) using Pillow."""
from __future__ import annotations

import os

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "AxisTeller", "Assets", "app.ico")

BG = (14, 16, 19, 255)
ACCENT = (245, 233, 0, 255)
GREY = (74, 82, 95, 255)
SIZE = 256


def make_image() -> Image.Image:
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((8, 8, SIZE - 8, SIZE - 8), radius=56, fill=BG, outline=(38, 43, 51, 255), width=4)

    # 左侧荧光黄竖条
    d.rounded_rectangle((40, 58, 62, 198), radius=11, fill=ACCENT)

    # “轴”字
    font = None
    for candidate in (r"C:\Windows\Fonts\msyhbd.ttc", r"C:\Windows\Fonts\msyh.ttc", r"C:\Windows\Fonts\simhei.ttf"):
        if os.path.exists(candidate):
            try:
                font = ImageFont.truetype(candidate, 132)
                break
            except Exception:
                continue
    if font is not None:
        d.text((150, 128), "轴", font=font, fill=ACCENT, anchor="mm")
    else:
        d.text((96, 60), "EF", fill=ACCENT)

    # 底部时间轴点
    for i in range(3):
        x = 84 + i * 34
        color = ACCENT if i == 0 else GREY
        d.rounded_rectangle((x, 206, x + 24, 216), radius=5, fill=color)
    return img


def main() -> None:
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    img = make_image()
    img.save(OUT, format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])
    png = os.path.join(os.path.dirname(OUT), "app.png")
    img.save(png, format="PNG")
    print("wrote", OUT, "and", png)


if __name__ == "__main__":
    main()
