"""Draws the Glance app icon: an isometric cube in the brand palette."""
import math
import sys
from PIL import Image, ImageDraw

COBALT = (0, 71, 171, 255)
COBALT_DEEP = (0, 46, 118, 255)
AMBER = (255, 193, 7, 255)


def cube(size: int) -> Image.Image:
    ss = 8
    n = size * ss
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx, cy = n / 2, n / 2
    r = n * 0.47
    gap = n * 0.022 if size >= 32 else 0.0
    pts = [(cx + r * math.cos(math.radians(a)), cy + r * math.sin(math.radians(a)))
           for a in (-90, -30, 30, 90, 150, 210)]
    top, tr, br, bot, bl, tl = pts
    c = (cx, cy)

    def shrink(poly):
        mx = sum(p[0] for p in poly) / len(poly)
        my = sum(p[1] for p in poly) / len(poly)
        out = []
        for x, y in poly:
            dx, dy = x - mx, y - my
            ln = math.hypot(dx, dy) or 1
            out.append((x - dx / ln * gap, y - dy / ln * gap))
        return out

    d.polygon(shrink([top, tr, c, tl]), fill=AMBER)
    d.polygon(shrink([tl, c, bot, bl]), fill=COBALT)
    d.polygon(shrink([c, tr, br, bot]), fill=COBALT_DEEP)
    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    out = sys.argv[1]
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    big = cube(256)
    big.save(out, format="ICO", sizes=[(s, s) for s in sizes],
             append_images=[cube(s) for s in sizes[:-1]])
    if len(sys.argv) > 2:
        big.save(sys.argv[2])
