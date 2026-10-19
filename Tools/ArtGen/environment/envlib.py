"""Pixel drawing primitives + one-point perspective helpers for the PAW NOON environment.

Everything works on a Canvas (numpy RGBA, palette colours by name). No anti-aliasing anywhere.
"""
import os, sys
import numpy as np
from PIL import Image

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(_HERE, '..', 'common'))
from pixkit import P, PA, check_palette, upscale, strip, save_png  # noqa: E402

W, H = 360, 640
VPX, VPY = 196, 296          # street vanishing point (canvas px)
HORIZON = 300
FOCAL = 288.0                # px
EYE = 2.3                    # camera height in metres (over-the-shoulder, slightly high)


def proj(X, Y, Z):
    """World metres (X right, Y up, Z depth) -> canvas float px."""
    k = FOCAL / Z
    return VPX + k * X, VPY + k * (EYE - Y)


def kz(Z):
    return FOCAL / Z


def zk(k):
    return FOCAL / k


def col(c):
    if c is None:
        return None
    if isinstance(c, str):
        return PA[c]
    if len(c) == 3:
        return tuple(c) + (255,)
    return tuple(c)


class Canvas:
    def __init__(self, w=W, h=H):
        self.w, self.h = w, h
        self.a = np.zeros((h, w, 4), np.uint8)

    # ---------------------------------------------------------------- basics
    def px(self, x, y, c):
        x, y = int(x), int(y)
        if c is None or not (0 <= x < self.w and 0 <= y < self.h):
            return
        self.a[y, x] = col(c)

    def get(self, x, y):
        x, y = int(x), int(y)
        if not (0 <= x < self.w and 0 <= y < self.h):
            return None
        v = self.a[y, x]
        return None if v[3] == 0 else tuple(int(t) for t in v)

    def opaque(self, x, y):
        return 0 <= x < self.w and 0 <= y < self.h and self.a[int(y), int(x), 3] > 0

    def clear(self, x, y):
        if 0 <= x < self.w and 0 <= y < self.h:
            self.a[int(y), int(x)] = 0

    def rect(self, x0, y0, x1, y1, c):
        """Inclusive rectangle fill."""
        x0, x1 = sorted((int(x0), int(x1)))
        y0, y1 = sorted((int(y0), int(y1)))
        x0c, y0c = max(x0, 0), max(y0, 0)
        x1c, y1c = min(x1, self.w - 1), min(y1, self.h - 1)
        if x0c > x1c or y0c > y1c or c is None:
            return
        self.a[y0c:y1c + 1, x0c:x1c + 1] = col(c)

    def hline(self, x0, x1, y, c):
        self.rect(x0, y, x1, y, c)

    def vline(self, x, y0, y1, c):
        self.rect(x, y0, x, y1, c)

    def frame(self, x0, y0, x1, y1, c):
        self.hline(x0, x1, y0, c); self.hline(x0, x1, y1, c)
        self.vline(x0, y0, y1, c); self.vline(x1, y0, y1, c)

    def line(self, x0, y0, x1, y1, c):
        x0, y0, x1, y1 = int(round(x0)), int(round(y0)), int(round(x1)), int(round(y1))
        dx, dy = abs(x1 - x0), -abs(y1 - y0)
        sx, sy = (1 if x0 < x1 else -1), (1 if y0 < y1 else -1)
        err = dx + dy
        while True:
            self.px(x0, y0, c)
            if x0 == x1 and y0 == y1:
                break
            e2 = 2 * err
            if e2 >= dy:
                err += dy; x0 += sx
            if e2 <= dx:
                err += dx; y0 += sy

    def poly(self, pts, c, mask_fn=None):
        """Scanline polygon fill, pixel-centre sampling (crisp, no AA)."""
        if c is None:
            return
        ys = [p[1] for p in pts]
        ymin, ymax = int(np.floor(min(ys))), int(np.ceil(max(ys)))
        n = len(pts)
        cc = col(c)
        for y in range(max(ymin, 0), min(ymax + 1, self.h)):
            yc = y + 0.5
            xs = []
            for i in range(n):
                xa, ya = pts[i]; xb, yb = pts[(i + 1) % n]
                if (ya <= yc < yb) or (yb <= yc < ya):
                    xs.append(xa + (yc - ya) * (xb - xa) / (yb - ya))
            xs.sort()
            for j in range(0, len(xs) - 1, 2):
                xa = int(np.ceil(xs[j] - 0.5)); xb = int(np.floor(xs[j + 1] - 0.5))
                for x in range(max(xa, 0), min(xb, self.w - 1) + 1):
                    if mask_fn is None or mask_fn(x, y):
                        self.a[y, x] = cc

    def poly_pixels(self, pts):
        """Return list of (x,y) inside polygon (same rule as poly)."""
        out = []
        ys = [p[1] for p in pts]
        ymin, ymax = int(np.floor(min(ys))), int(np.ceil(max(ys)))
        n = len(pts)
        for y in range(max(ymin, 0), min(ymax + 1, self.h)):
            yc = y + 0.5
            xs = []
            for i in range(n):
                xa, ya = pts[i]; xb, yb = pts[(i + 1) % n]
                if (ya <= yc < yb) or (yb <= yc < ya):
                    xs.append(xa + (yc - ya) * (xb - xa) / (yb - ya))
            xs.sort()
            for j in range(0, len(xs) - 1, 2):
                xa = int(np.ceil(xs[j] - 0.5)); xb = int(np.floor(xs[j + 1] - 0.5))
                for x in range(max(xa, 0), min(xb, self.w - 1) + 1):
                    out.append((x, y))
        return out

    def checker(self, x0, y0, x1, y1, c, parity=0):
        """50% ordered 2x2 checker inside inclusive rect."""
        for y in range(max(int(y0), 0), min(int(y1), self.h - 1) + 1):
            for x in range(max(int(x0), 0), min(int(x1), self.w - 1) + 1):
                if (x + y + parity) & 1 == 0:
                    self.a[y, x] = col(c)

    def blit(self, spr, x, y):
        """Alpha-0/255 blit of another Canvas or RGBA array at (x,y)."""
        arr = spr.a if isinstance(spr, Canvas) else spr
        h, w = arr.shape[:2]
        for yy in range(h):
            ty = y + yy
            if not (0 <= ty < self.h):
                continue
            for xx in range(w):
                tx = x + xx
                if 0 <= tx < self.w and arr[yy, xx, 3] > 0:
                    self.a[ty, tx] = arr[yy, xx]

    def replace(self, old, new, region=None):
        o = np.array(col(old)); n = np.array(col(new))
        sub = self.a if region is None else self.a[region[1]:region[3] + 1, region[0]:region[2] + 1]
        m = np.all(sub == o, axis=-1)
        sub[m] = n

    def image(self):
        return Image.fromarray(self.a.copy(), 'RGBA')

    def copy(self):
        c = Canvas(self.w, self.h); c.a = self.a.copy(); return c


def over(dst, src):
    """Alpha composite PIL images (src over dst) -> new image."""
    out = dst.copy(); out.alpha_composite(src); return out


# ------------------------------------------------------------------ tiny sign font
# Hand-made 5px-tall western sign lettering (serif-ish, bold strokes). '#' = ink.
GLYPHS = {
    'A': ["0110", "1001", "1111", "1001", "1001"],
    'B': ["1110", "1001", "1110", "1001", "1110"],
    'C': ["0111", "1000", "1000", "1000", "0111"],
    'D': ["1110", "1001", "1001", "1001", "1110"],
    'E': ["1111", "1000", "1110", "1000", "1111"],
    'G': ["0111", "1000", "1011", "1001", "0111"],
    'H': ["1001", "1001", "1111", "1001", "1001"],
    'I': ["111", "010", "010", "010", "111"],
    'J': ["0011", "0001", "0001", "1001", "0110"],
    'K': ["1001", "1010", "1100", "1010", "1001"],
    'L': ["1000", "1000", "1000", "1000", "1111"],
    'M': ["10001", "11011", "10101", "10001", "10001"],
    'N': ["1001", "1101", "1011", "1001", "1001"],
    'O': ["0110", "1001", "1001", "1001", "0110"],
    'P': ["1110", "1001", "1110", "1000", "1000"],
    'R': ["1110", "1001", "1110", "1010", "1001"],
    'S': ["0111", "1000", "0110", "0001", "1110"],
    'T': ["11111", "00100", "00100", "00100", "00100"],
    'U': ["1001", "1001", "1001", "1001", "0110"],
    'W': ["10001", "10001", "10101", "11011", "10001"],
    'Y': ["10001", "01010", "00100", "00100", "00100"],
    '$': ["0111", "1010", "0110", "0101", "1110"],
    '0': ["0110", "1001", "1001", "1001", "0110"],
    '1': ["010", "110", "010", "010", "111"],
    '5': ["1111", "1000", "1110", "0001", "1110"],
    ',': ["0", "0", "0", "1", "1"],
    ' ': ["00", "00", "00", "00", "00"],
}

# 3x4 micro font for tiny print (poster reward line)
MICRO = {
    '$': ["111", "110", "011", "111"],
    '5': ["111", "110", "001", "110"],
    '0': ["111", "101", "101", "111"],
    '1': ["01", "11", "01", "01"],
    ',': ["0", "0", "1", "1"],
}


def text_width(s, font=GLYPHS, spacing=1):
    return sum(len(font[ch][0]) for ch in s) + spacing * (len(s) - 1)


def draw_text(cv, s, x, y, c, font=GLYPHS, spacing=1, shadow=None):
    cx = x
    for ch in s:
        g = font[ch]
        for gy, row in enumerate(g):
            for gx, v in enumerate(row):
                if v == '1':
                    if shadow is not None:
                        cv.px(cx + gx + 1, y + gy + 1, shadow)
        cx += len(g[0]) + spacing
    cx = x
    for ch in s:
        g = font[ch]
        for gy, row in enumerate(g):
            for gx, v in enumerate(row):
                if v == '1':
                    cv.px(cx + gx, y + gy, c)
        cx += len(g[0]) + spacing
    return cx - spacing


def rng(seed):
    return np.random.RandomState(seed)


def np_outline(cv, c='ink2', only_below=None):
    """Fast 1px 4-neighbour outline drawn into transparent pixels around the shape."""
    a = cv.a[..., 3] > 0
    n = np.zeros_like(a)
    n[1:, :] |= a[:-1, :]; n[:-1, :] |= a[1:, :]; n[:, 1:] |= a[:, :-1]; n[:, :-1] |= a[:, 1:]
    m = n & ~a
    if only_below is not None:
        m[:only_below] = False
    cv.a[m] = col(c)
    return cv
