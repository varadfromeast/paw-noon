"""Shared helpers for the Paw Noon UI / fonts / FX generators.

All drawing happens at 1x on the virtual pixel grid. Masks are numpy bool arrays indexed [y, x].
Colors always come from the palette (pixkit.P / PA).
"""
import json, os, sys
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'common'))
from pixkit import P, PA, PALETTE_SET, check_palette, upscale, strip, save_png  # noqa: E402,F401

PROJECT = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
ROOT = os.path.abspath(os.path.join(PROJECT, '..'))
ART = os.path.join(PROJECT, 'Assets', 'PawNoon', 'Resources', 'Art')
UI_DIR = os.path.join(ART, 'UI')
FONT_DIR = os.path.join(ART, 'Fonts')
FX_DIR = os.path.join(ART, 'FX')
ICON_DIR = os.path.join(PROJECT, 'Assets', 'PawNoon', 'Art', 'AppIcon')
WORK = os.path.join(ROOT, 'work', 'ui')


# --------------------------------------------------------------------------- masks
def blank(w, h):
    return np.zeros((h, w), bool)


def rect(m, x0, y0, x1, y1, val=True):
    """Fill [x0,x1) x [y0,y1)."""
    h, w = m.shape
    m[max(0, y0):min(h, y1), max(0, x0):min(w, x1)] = val
    return m


def rrect_mask(w, h, x0, y0, x1, y1, r=(0, 0, 0, 0), p=2.0):
    """Rounded rect [x0,x1) x [y0,y1) with per-corner radii (tl, tr, br, bl), pixel-centre sampled.
    p = superellipse exponent (2 = circle, >2 squarer)."""
    yy, xx = np.mgrid[0:h, 0:w] + 0.5
    m = (xx >= x0) & (xx < x1) & (yy >= y0) & (yy < y1)
    corners = [(x0 + r[0], y0 + r[0], r[0], -1, -1), (x1 - r[1], y0 + r[1], r[1], 1, -1),
               (x1 - r[2], y1 - r[2], r[2], 1, 1), (x0 + r[3], y1 - r[3], r[3], -1, 1)]
    for cx, cy, rr, sx, sy in corners:
        if rr <= 0:
            continue
        inq = ((xx - cx) * sx > 0) & ((yy - cy) * sy > 0)
        d = (np.abs(xx - cx) / rr) ** p + (np.abs(yy - cy) / rr) ** p
        m &= ~(inq & (d > 1.0))
    return m


def ellipse_mask(w, h, cx, cy, rx, ry, p=2.0):
    yy, xx = np.mgrid[0:h, 0:w] + 0.5
    return (np.abs(xx - cx) / rx) ** p + (np.abs(yy - cy) / ry) ** p <= 1.0


def poly_mask(w, h, pts):
    """Polygon (list of (x, y) in pixel-edge coordinates), filled by pixel-centre test."""
    from matplotlib.path import Path
    yy, xx = np.mgrid[0:h, 0:w] + 0.5
    path = Path(pts)
    return path.contains_points(np.c_[xx.ravel(), yy.ravel()], radius=1e-9).reshape(h, w)


def shift(m, dx, dy):
    h, w = m.shape
    out = np.zeros_like(m)
    ys0, ys1 = max(0, -dy), min(h, h - dy)
    xs0, xs1 = max(0, -dx), min(w, w - dx)
    out[ys0 + dy:ys1 + dy, xs0 + dx:xs1 + dx] = m[ys0:ys1, xs0:xs1]
    return out


def dilate(m, r=1, diag=True):
    out = m.copy()
    for _ in range(r):
        o = out.copy()
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            o |= shift(out, dx, dy)
        if diag:
            for dx, dy in ((1, 1), (-1, 1), (1, -1), (-1, -1)):
                o |= shift(out, dx, dy)
        out = o
    return out


def erode(m, r=1, diag=False):
    return ~dilate(~m, r, diag)


def edge_in(m, dx, dy):
    """Pixels of m whose neighbour in direction (dx,dy) is outside m."""
    return m & ~shift(m, -dx, -dy)


def pad(m, l, t, r, b):
    h, w = m.shape
    out = np.zeros((h + t + b, w + l + r), bool)
    out[t:t + h, l:l + w] = m
    return out


def bbox(m):
    ys, xs = np.nonzero(m)
    return xs.min(), ys.min(), xs.max() + 1, ys.max() + 1


def crop_mask(m):
    x0, y0, x1, y1 = bbox(m)
    return m[y0:y1, x0:x1]


def shear(m, k, anchor_bottom=True):
    """Forward lean: shift row y right by (rows above the bottom) // k."""
    h, w = m.shape
    extra = (h - 1) // k + 1
    out = np.zeros((h, w + extra), bool)
    for y in range(h):
        s = (h - 1 - y) // k if anchor_bottom else y // k
        out[y, s:s + w] = m[y]
    return out


# --------------------------------------------------------------------------- color layers
class Canvas:
    """RGBA canvas you paint masks onto with palette colours."""

    def __init__(self, w, h):
        self.a = np.zeros((h, w, 4), np.uint8)

    @property
    def w(self):
        return self.a.shape[1]

    @property
    def h(self):
        return self.a.shape[0]

    def paint(self, m, col, alpha=255, ox=0, oy=0):
        if isinstance(col, str):
            col = P[col]
        mh, mw = m.shape
        H, W = self.h, self.w
        x0, y0 = max(0, ox), max(0, oy)
        x1, y1 = min(W, ox + mw), min(H, oy + mh)
        if x1 <= x0 or y1 <= y0:
            return self
        sub = m[y0 - oy:y1 - oy, x0 - ox:x1 - ox]
        reg = self.a[y0:y1, x0:x1]
        reg[sub, 0:3] = col
        reg[sub, 3] = alpha
        return self

    def px(self, x, y, col, alpha=255):
        if 0 <= x < self.w and 0 <= y < self.h:
            self.a[y, x, :3] = P[col] if isinstance(col, str) else col
            self.a[y, x, 3] = alpha
        return self

    def blit(self, img, ox, oy):
        """Alpha-over paste of an RGBA PIL image / array (binary alpha honoured, partial kept)."""
        src = np.asarray(img.convert('RGBA')) if isinstance(img, Image.Image) else img
        sh, sw = src.shape[:2]
        H, W = self.h, self.w
        x0, y0 = max(0, ox), max(0, oy)
        x1, y1 = min(W, ox + sw), min(H, oy + sh)
        if x1 <= x0 or y1 <= y0:
            return self
        s = src[y0 - oy:y1 - oy, x0 - ox:x1 - ox].astype(np.float32)
        d = self.a[y0:y1, x0:x1].astype(np.float32)
        sa = s[..., 3:4] / 255.0
        da = d[..., 3:4] / 255.0
        oa = sa + da * (1 - sa)
        rgb = np.where(oa > 0, (s[..., :3] * sa + d[..., :3] * da * (1 - sa)) / np.maximum(oa, 1e-6), 0)
        self.a[y0:y1, x0:x1, :3] = np.round(rgb).astype(np.uint8)
        self.a[y0:y1, x0:x1, 3] = np.round(oa[..., 0] * 255).astype(np.uint8)
        return self

    def mask(self):
        return self.a[..., 3] > 0

    def img(self):
        return Image.fromarray(self.a.copy(), 'RGBA')


def flatten_over(img, bg):
    """Composite RGBA img over an RGB(A) background image of the same size (for mockups)."""
    base = bg.convert('RGBA').copy()
    base.alpha_composite(img.convert('RGBA'))
    return base


# --------------------------------------------------------------------------- bitmap fonts (our own JSON)
_FONT_CACHE = {}


def load_font(name):
    if name not in _FONT_CACHE:
        meta = json.load(open(os.path.join(FONT_DIR, name + '.json')))
        atlas = np.asarray(Image.open(os.path.join(FONT_DIR, name + '.png')).convert('RGBA'))[..., 3] > 0
        _FONT_CACHE[name] = (meta, atlas)
    return _FONT_CACHE[name]


def text_width(text, font, tracking=0):
    meta, _ = load_font(font)
    g = meta['glyphs']
    w = 0
    for i, ch in enumerate(text):
        gl = g.get(ch) or g.get('?')
        w += gl['adv'] + (tracking if i < len(text) - 1 else 0)
    # trailing advance includes 1 px spacing -> visible width is one less
    return w - meta.get('spacing', 1)


def text_mask(text, font, tracking=0):
    """Return a bool mask of the rendered string (height = lineHeight)."""
    meta, atlas = load_font(font)
    g = meta['glyphs']
    W = max(1, text_width(text, font, tracking) + 2)
    m = np.zeros((meta['lineHeight'], W), bool)
    x = 0
    for ch in text:
        gl = g.get(ch) or g.get('?')
        if gl['w'] > 0:
            sub = atlas[gl['y']:gl['y'] + gl['h'], gl['x']:gl['x'] + gl['w']]
            yy, xx = gl['oy'], x + gl['ox']
            m[yy:yy + gl['h'], xx:xx + gl['w']] |= sub
        x += gl['adv'] + tracking
    return m[:, :max(1, text_width(text, font, tracking))]


def draw_text(cv, text, font, x, y, col, outline=None, shadow=None, tracking=0, align='left'):
    """Engine-equivalent bitmap text: optional 1px outline (8-neigh) and 1px drop shadow (down)."""
    m = text_mask(text, font, tracking)
    if align == 'center':
        x = x - m.shape[1] // 2
    elif align == 'right':
        x = x - m.shape[1]
    m2 = pad(m, 2, 2, 2, 2)
    ox, oy = x - 2, y - 2
    if shadow:
        base = dilate(m2, 1) if outline else m2
        cv.paint(shift(base, 0, 1), shadow, ox=ox, oy=oy)
    if outline:
        cv.paint(dilate(m2, 1), outline, ox=ox, oy=oy)
    cv.paint(m2, col, ox=ox, oy=oy)
    return m.shape[1]


def save_checked(img, path, partial_alpha=False):
    check_palette(img, allow_partial_alpha=partial_alpha, name=os.path.basename(path))
    save_png(img, path)
    return img


# --------------------------------------------------------------------------- sprite registry -> ui.json / fx.json
class Registry:
    def __init__(self, out_dir, json_name):
        self.out_dir = out_dir
        self.json_name = json_name
        self.meta = {}

    def add(self, name, img, pivot=None, partial_alpha=False, **extra):
        """Save <name>.png (palette-checked) and record metadata. pivot defaults to the centre."""
        save_checked(img, os.path.join(self.out_dir, name + '.png'), partial_alpha)
        cell = extra.get('cell')
        w, h = (cell if cell else img.size)
        if pivot is None:
            pivot = [w // 2, h // 2]
        entry = {'file': name, 'size': list(img.size), 'pivot': [int(pivot[0]), int(pivot[1])]}
        for k, v in extra.items():
            entry[k] = v
        self.meta[name] = entry
        return img

    def write(self):
        """Flat map: sprite name -> entry (every value has "file")."""
        out = {k: self.meta[k] for k in sorted(self.meta)}
        with open(os.path.join(self.out_dir, self.json_name), 'w') as fp:
            json.dump(out, fp, indent=1)
