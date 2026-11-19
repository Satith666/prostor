"""Рисует значок Prostor: кольцо диска на графитовой плитке.

Занятая часть кольца светлая, свободная — синяя четверть. Каждый размер
рисуется отдельно (с запасом по разрешению и уменьшением), для мелких
размеров кольцо крупнее и толще, а зазоры не уже пикселя. Пишет .ico и PNG.
Использование: python3 mkicon.py out.ico out256.png out_small.png [out_mark.svg]
"""
import math
import struct
import sys
from io import BytesIO

from PIL import Image, ImageDraw

TILE = (27, 29, 35, 255)      # #1b1d23
EDGE = (48, 51, 59, 255)      # #30333b — граница плитки, видна на тёмной панели задач
USED = (236, 238, 241, 255)   # #eceef1
FREE = (123, 147, 255, 255)   # #7b93ff
SS = 8

# Кольцо в сетке 64×64 (как в макете): центр 32, радиус по оси 15.5, толщина 9.
# Занято — от 12 часов по часовой стрелке до 9 часов, свободно — от 9 до 12.
USED_ARC = (-90, 180)
FREE_ARC = (180, 270)


def geometry(s):
    """Размеры кольца в пикселях для значка s×s."""
    if s <= 16:
        return 6.6, 2.8, 1.0     # внешний радиус, толщина, зазор
    if s <= 24:
        return s * 0.40, s * 0.17, 1.1
    k = s / 64
    return 20 * k, 9 * k, 2.2 * k


def draw(s):
    big = s * SS
    im = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    if s >= 32:
        m = round(s / 64) * SS
        edge = max(1, round(s * 0.019)) * SS
        d.rounded_rectangle((m, m, big - m - 1, big - m - 1), radius=s * 0.22 * SS, fill=EDGE)
        d.rounded_rectangle((m + edge, m + edge, big - m - edge - 1, big - m - edge - 1),
                            radius=(s * 0.22 - edge / SS) * SS, fill=TILE)
    else:
        d.rounded_rectangle((0, 0, big - 1, big - 1), radius=s * 0.2 * SS, fill=TILE)

    ro, w, gap = geometry(s)
    c = big / 2
    ro_b, ri_b = ro * SS, (ro - w) * SS
    rmid = (ro - w / 2)
    g = math.degrees(gap / rmid) / 2  # половина зазора в градусах
    box = (c - ro_b, c - ro_b, c + ro_b, c + ro_b)
    d.pieslice(box, USED_ARC[0] + g, USED_ARC[1] - g, fill=USED)
    d.pieslice(box, FREE_ARC[0] + g, FREE_ARC[1] - g, fill=FREE)
    d.ellipse((c - ri_b, c - ri_b, c + ri_b, c + ri_b), fill=TILE)
    return im.resize((s, s), Image.LANCZOS)


def dib(im):
    w, h = im.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    rows = []
    px = im.load()
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = px[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    mask_row = ((w + 31) // 32) * 4
    return header + b"".join(rows) + bytes(mask_row * h)


def main(out_ico, out_png, out_png_small):
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    images = []
    for s in sizes:
        im = draw(s)
        if s >= 256:
            b = BytesIO()
            im.save(b, "PNG")
            data = b.getvalue()
        else:
            data = dib(im)
        images.append((s, data))
    with open(out_ico, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(images)))
        offset = 6 + 16 * len(images)
        for s, data in images:
            f.write(struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset))
            offset += len(data)
        for _, data in images:
            f.write(data)
    draw(256).save(out_png)
    draw(64).save(out_png_small)


if __name__ == "__main__":
    main(*sys.argv[1:4])
