"""herokit: tiny 1x pixel 'painter' used by the hero generator.

Parts are painted at 1x into frame-sized arrays as (material, light level) pairs using aliased
polygons / lines / hand-written pixel maps.  A frame composites parts back-to-front, then an
outline pass adds a 1px selective outline (ink on shadow sides, darkest ramp tone on lit sides)
and inner contour lines where a front part overlaps a different part group.  Nothing is ever
rotated or scaled as a bitmap.
"""
import os, sys
import numpy as np
from PIL import Image, ImageDraw

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(_HERE, '..', 'common'))
from pixkit import P, PA, check_palette, upscale, strip, save_png  # noqa: E402

V2 = os.environ.get('PAWNOON_V2') == '1'
SCALE = 200 / 144 if V2 else 1.0          # v2: same design, re-rasterized on the denser grid
CW, CH = (200, 300) if V2 else (144, 216)
PAL = list(P.keys())
IDX = {n: i for i, n in enumerate(PAL)}
RGBA = np.array([PA[n] for n in PAL], np.uint8)

# ---------------------------------------------------------------- materials (light 0 = deepest)
MATS = {
    'fur':     ['tabby4', 'tabby3', 'tabby2', 'tabby1', 'tabby0', 'cream0'],
    'furS':    ['rock4', 'tabby4', 'tabby3', 'tabby2', 'tabby1', 'tabby1'],   # tabby stripe
    'furL':    ['sand3', 'cream2', 'cream1', 'cream0', 'white', 'white'],     # light fur
    'whisk':   ['cream2', 'cream1', 'cream0', 'white', 'white', 'white'],
    'hat':     ['wood5', 'wood4', 'wood3', 'wood2', 'wood1', 'wood0'],
    'band':    ['ink', 'ink2', 'leath3', 'leath2', 'leath1', 'leath0'],
    'poncho':  ['red3', 'paintRedD', 'red2', 'red1', 'red0', 'rock1'],
    'ponB':    ['wood5', 'wood4', 'rock4', 'rock3', 'rock2', 'rock1'],        # brown weave
    'ponC':    ['sand4', 'paintCreamD', 'cream2', 'cream1', 'cream0', 'cream0'],  # cream band
    'ponK':    ['ink', 'ink2', 'shadowPurp', 'wood4', 'wood3', 'wood2'],      # dark weave
    'sleeve':  ['ink2', 'char3', 'char2', 'char1', 'char0', 'cream1'],
    'leath':   ['leath3', 'leath3', 'leath2', 'leath1', 'leath0', 'wood0'],
    'pants':   ['ink', 'ink2', 'char3', 'char2', 'char1', 'char0'],
    'brass':   ['brass3', 'brass3', 'brass2', 'brass1', 'brass0', 'sun0'],
    'steel':   ['steel3', 'steel3', 'steel2', 'steel1', 'steel0', 'white'],
    'grip':    ['wood5', 'wood4', 'wood3', 'wood2', 'wood1', 'wood0'],
    'ink':     ['ink'] * 6,
    'ink2':    ['ink2'] * 6,
    'smoke':   ['char1', 'char0', 'cream2', 'cream1', 'cream0', 'white'],
    'eye':     ['ink', 'ink', 'green2', 'eyeGreen', 'eyeGreen', 'white'],
    'pink':    ['rock3', 'rock2', 'rock1', 'rock0', 'rock0', 'rock0'],
}
if V2:
    MATS.update({
        'fur':    ['h_tabby5', 'tabby4', 'tabby3', 'tabby2', 'tabby1', 'h_tabbyRim'],
        'furS':   ['h_tabby5', 'h_tabby5', 'tabby4', 'tabby3', 'tabby2', 'tabby1'],
        'poncho': ['h_red4', 'red3', 'red2', 'red1', 'red0', 'h_redRim'],
        'ponB':   ['h_hat6', 'wood5', 'rock4', 'rock3', 'rock2', 'rock1'],
        'ponC':   ['sand5', 'sand4', 'paintCreamD', 'cream1', 'cream0', 'h_creamRim'],
        'hat':    ['h_hat6', 'wood5', 'wood4', 'wood3', 'wood2', 'h_hatRim'],
        'pants':  ['ink', 'h_navy', 'char3', 'char2', 'char1', 'char0'],
        'sleeve': ['ink', 'h_navy', 'char3', 'char2', 'char1', 'char0'],
        'leath':  ['h_hat6', 'leath3', 'leath2', 'leath1', 'leath0', 'wood0'],
        'steel':  ['steel3', 'steel3', 'steel2', 'steel1', 'steel0', 'h_steelRim'],
    })
MAT_NAMES = list(MATS.keys())
MID = {n: i for i, n in enumerate(MAT_NAMES)}
LUT = np.array([[IDX[c] for c in MATS[m]] for m in MAT_NAMES], np.int16)
# outline colour on a lit (top/right facing) edge: darkest tone of the ramp
LIT_LINE = {m: MATS[m][0] for m in MAT_NAMES}
LIT_LINE.update({'fur': 'tabby4', 'furL': 'sand4', 'whisk': 'cream2', 'poncho': 'red3',
                 'ponC': 'sand4', 'ponB': 'wood5', 'hat': 'wood5', 'steel': 'steel3',
                 'brass': 'brass3', 'sleeve': 'char3', 'leath': 'leath3', 'smoke': 'char1'})
NO_LINE_MATS = {'whisk', 'smoke'}          # never outlined (thin strands / soft puffs)


# ---------------------------------------------------------------- helpers
def catmull(pts, n=8, closed=False):
    """Catmull-Rom spline through control points -> dense float polyline."""
    pts = [np.array(p, float) for p in pts]
    if closed:
        pts = pts[-1:] + pts + pts[:2]
    else:
        pts = [pts[0]] + pts + [pts[-1]]
    out = []
    for i in range(1, len(pts) - 2):
        p0, p1, p2, p3 = pts[i - 1], pts[i], pts[i + 1], pts[i + 2]
        for k in range(n):
            t = k / n
            t2, t3 = t * t, t * t * t
            q = 0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2
                       + (-p0 + 3 * p1 - 3 * p2 + p3) * t3)
            out.append(tuple(q))
    out.append(tuple(pts[-2]))
    return out


def poly_mask(pts, w, h):
    im = Image.new('L', (w, h), 0)
    pts = [((x + .5) * SCALE - .5, (y + .5) * SCALE - .5) for x, y in pts]
    if len(pts) >= 3:
        ImageDraw.Draw(im).polygon([(float(x), float(y)) for x, y in pts], fill=1, outline=1)
    return np.asarray(im, np.uint8).astype(bool)


def line_pts(p0, p1):
    """Bresenham integer points from p0 to p1 inclusive."""
    x0, y0 = int(round(p0[0])), int(round(p0[1]))
    x1, y1 = int(round(p1[0])), int(round(p1[1]))
    dx, dy = abs(x1 - x0), -abs(y1 - y0)
    sx, sy = (1 if x0 < x1 else -1), (1 if y0 < y1 else -1)
    err = dx + dy
    out = []
    while True:
        out.append((x0, y0))
        if x0 == x1 and y0 == y1:
            break
        e2 = 2 * err
        if e2 >= dy:
            err += dy; x0 += sx
        if e2 <= dx:
            err += dx; y0 += sy
    return out


def polyline_pts(pts):
    out = []
    for a, b in zip(pts[:-1], pts[1:]):
        seg = line_pts(a, b)
        if out and seg and out[-1] == seg[0]:
            seg = seg[1:]
        out.extend(seg)
    # pixel-perfect: drop L-corner pixels
    clean = []
    for p in out:
        clean.append(p)
        if len(clean) >= 3:
            a, b, c = clean[-3], clean[-2], clean[-1]
            if abs(a[0] - c[0]) == 1 and abs(a[1] - c[1]) == 1 and (b[0] == a[0] or b[1] == a[1]):
                clean.pop(-2)
    return clean


def shift(m, dx, dy, fill=False):
    """Shift a bool mask by integer (dx,dy)."""
    h, w = m.shape
    out = np.full_like(m, fill)
    xs0, xs1 = max(0, -dx), min(w, w - dx)
    ys0, ys1 = max(0, -dy), min(h, h - dy)
    if xs1 > xs0 and ys1 > ys0:
        out[ys0 + dy:ys1 + dy, xs0 + dx:xs1 + dx] = m[ys0:ys1, xs0:xs1]
    return out


def edge(m, dx, dy):
    """Pixels of m whose neighbour at (dx,dy) is outside m."""
    return m & ~shift(m, -dx, -dy)


# ---------------------------------------------------------------- parts
class Part:
    """A part painted in frame space (W x H) as material / light arrays."""

    def __init__(self, name, group=None, w=None, h=None, outline=True):
        w = w or CW; h = h or CH
        self.name, self.group = name, group or name
        self.w, self.h = w, h
        self.mat = np.full((h, w), -1, np.int16)
        self.lum = np.zeros((h, w), np.int8)
        self.fix = np.full((h, w), -1, np.int16)    # explicit palette index (hand pixel maps)
        self.noline = np.zeros((h, w), bool)        # pixels exempt from receiving an outline
        self.outline = outline

    # -- basic fills -------------------------------------------------------------
    @property
    def mask(self):
        return self.mat >= 0

    def fill(self, m, mat=None, lum=None, only=None):
        if only is not None:
            m = m & only
        if mat is not None:
            self.mat[m] = MID[mat]
        if lum is not None:
            self.lum[m] = lum
        return m

    def poly(self, pts, mat=None, lum=None, smooth=0, only=None, closed=True):
        if smooth:
            pts = catmull(pts, smooth, closed=closed)
        m = poly_mask(pts, self.w, self.h)
        return self.fill(m, mat, lum, only)

    def line(self, pts, mat=None, lum=None, smooth=0, only=None):
        if smooth:
            pts = catmull(pts, smooth)
        m = np.zeros((self.h, self.w), bool)
        pts = [((x + .5) * SCALE - .5, (y + .5) * SCALE - .5) for x, y in pts]
        for x, y in polyline_pts(pts):
            if 0 <= x < self.w and 0 <= y < self.h:
                m[y, x] = True
        return self.fill(m, mat, lum, only)

    def px(self, pts, mat=None, lum=None):
        m = np.zeros((self.h, self.w), bool)
        pts = [(int(round((x + .5) * SCALE - .5)), int(round((y + .5) * SCALE - .5))) for x, y in pts]
        for x, y in pts:
            if 0 <= x < self.w and 0 <= y < self.h:
                m[y, x] = True
        return self.fill(m, mat, lum)

    def ellipse(self, cx, cy, rx, ry, mat=None, lum=None, only=None):
        yy, xx = (np.mgrid[0:self.h, 0:self.w] + .5) / SCALE - .5
        m = ((xx - cx) / max(rx, .01)) ** 2 + ((yy - cy) / max(ry, .01)) ** 2 <= 1.0
        return self.fill(m, mat, lum, only)

    def addlum(self, m, d, lo=0, hi=5):
        m = m & self.mask
        self.lum[m] = np.clip(self.lum[m] + d, lo, hi)

    def bitmap(self, rows, legend, ox, oy, flipx=False, pixel=False):
        """Paste a hand-written pixel map. legend: char -> (mat, lum) | palette name | None."""
        if not pixel:
            ox, oy = int(round(ox * SCALE)), int(round(oy * SCALE))
        for j, row in enumerate(rows):
            if flipx:
                row = row[::-1]
            for i, ch in enumerate(row):
                if ch in ' .':
                    continue
                v = legend[ch]
                x, y = ox + i, oy + j
                if not (0 <= x < self.w and 0 <= y < self.h):
                    continue
                if isinstance(v, str):
                    self.fix[y, x] = IDX[v]
                    self.mat[y, x] = MID['ink']
                else:
                    self.mat[y, x] = MID[v[0]]
                    self.lum[y, x] = v[1]
                    self.fix[y, x] = -1

    def erase(self, m):
        self.mat[m] = -1
        self.fix[m] = -1

    def moved(self, dx, dy):
        q = Part(self.name, self.group, self.w, self.h, self.outline)
        for a in ('mat', 'lum', 'fix', 'noline'):
            src = getattr(self, a)
            dst = getattr(q, a)
            h, w = src.shape
            xs0, xs1 = max(0, -dx), min(w, w - dx)
            ys0, ys1 = max(0, -dy), min(h, h - dy)
            if xs1 > xs0 and ys1 > ys0:
                dst[ys0 + dy:ys1 + dy, xs0 + dx:xs1 + dx] = src[ys0:ys1, xs0:xs1]
        return q


class Frame:
    def __init__(self, w=None, h=None):
        self.w, self.h = w or CW, h or CH
        self.parts = []

    def add(self, part, dx=0, dy=0):
        if part is None:
            return
        self.parts.append(part.moved(dx, dy) if (dx or dy) else part)

    def render(self, open_edges=('left', 'bottom'), line_pairs_skip=()):
        h, w = self.h, self.w
        mat = np.full((h, w), -1, np.int16)
        lum = np.zeros((h, w), np.int8)
        fix = np.full((h, w), -1, np.int16)
        gid = np.full((h, w), -1, np.int16)
        zid = np.full((h, w), -1, np.int16)
        nol = np.zeros((h, w), bool)
        outl = np.zeros((h, w), bool)
        groups = {}
        for z, p in enumerate(self.parts):
            m = p.mask
            g = groups.setdefault(p.group, len(groups))
            mat[m] = p.mat[m]; lum[m] = p.lum[m]; fix[m] = p.fix[m]
            gid[m] = g; zid[m] = z; nol[m] = p.noline[m]
            outl[m] = p.outline
        sil = mat >= 0
        if V2 and self.w > 64:
            # dramatic light: warm rim on top/right inner edge, cool core shadow bottom/left
            def nbo(dx, dy):
                return sil & ~shift(sil, -dx, -dy, False)
            b_up, b_rt = nbo(0, -1), nbo(1, 0)
            b_dn = sil & ~shift(sil, 0, 1, 'bottom' in open_edges)
            b_lf = sil & ~shift(sil, -1, 0, 'left' in open_edges)
            bd = b_up | b_rt | b_dn | b_lf
            solid = outl & ~nol
            rim = (shift(b_up, 0, 1) | shift(b_rt, -1, 0) | shift(b_up & b_rt, -1, 1)) & sil & ~bd & solid
            rim &= ~(shift(b_dn | b_lf, 0, -1) | shift(b_lf, 1, 0))
            lum = np.where(rim, np.maximum(lum, 5), lum).astype(np.int8)
            core = (shift(b_dn, 0, -1) | shift(b_lf, 1, 0) | shift(b_lf, 2, 0) | shift(b_dn, 0, -2)) & sil & ~bd & solid & ~rim
            lum = np.where(core, np.clip(lum - 1, 0, 5), lum).astype(np.int8)
        col = LUT[np.clip(mat, 0, None), np.clip(lum, 0, 5)]
        col = np.where(fix >= 0, fix, col)
        col[~sil] = -1

        # ---- exterior outline (inner border of the silhouette)
        def nb(a, dx, dy, fill):
            return shift(a, -dx, -dy, fill)
        out_up = sil & ~nb(sil, 0, -1, False)
        out_dn = sil & ~nb(sil, 0, 1, 'bottom' in open_edges)
        out_lf = sil & ~nb(sil, -1, 0, 'left' in open_edges)
        out_rt = sil & ~nb(sil, 1, 0, False)
        border = out_up | out_dn | out_lf | out_rt
        lit = (out_up | out_rt) & ~(out_dn | out_lf)
        eligible = border & outl & ~nol
        for mname in MAT_NAMES:
            mm = eligible & (mat == MID[mname])
            if mname in NO_LINE_MATS:
                continue
            col[mm & lit] = IDX['ink'] if V2 else IDX[LIT_LINE[mname]]
            col[mm & ~lit] = IDX['ink']
        # ---- inner contour: front part pixels touching a different group behind them
        inner = np.zeros((h, w), bool)
        for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
            g2 = shift(gid, -dx, -dy, -1)
            z2 = shift(zid, -dx, -dy, -1)
            inner |= sil & (g2 >= 0) & (g2 != gid) & (z2 < zid)
        inner &= outl & ~nol & ~border
        skip = np.isin(mat, [MID[m] for m in NO_LINE_MATS])
        inner &= ~skip
        for mname in MAT_NAMES:
            mm = inner & (mat == MID[mname])
            col[mm] = IDX[LIT_LINE[mname]] if mname in ('fur', 'furL', 'brass', 'steel', 'ponC') else IDX['ink2']
        img = np.zeros((h, w, 4), np.uint8)
        img[sil] = RGBA[col[sil]]
        return Image.fromarray(img, 'RGBA')


def blank(w=None, h=None):
    w = w or CW; h = h or CH
    return Image.new('RGBA', (w, h), (0, 0, 0, 0))
