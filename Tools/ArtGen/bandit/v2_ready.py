#!/usr/bin/env python3
"""ART DIRECTION v2 style test: Blackpaw READY pose, cell 128x128, pivot [64,124].
Parts are aliased polygon / brush masks (no AA). Each part is shaded with one-sided directional
light from the upper right (stepped ramp, lit rim, shadow side), fur gets structured zig-zag
clump borders, every part gets a 1px ink outline (sticker order), and the face, folds, stitching
and texture are hand-placed pixels on top.  Output: work/bandit/v2_ready.png (1x, transparent).
"""
import os, sys, math
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'common'))
import numpy as np
from PIL import Image, ImageDraw
from scipy.ndimage import distance_transform_edt
from pixkit import P, PA, check_palette, upscale, save_png

N = 128
WORK = os.path.abspath(os.path.join(HERE, '..', '..', '..', '..', 'work', 'bandit'))
_CUR = ['']
LXY = np.array([0.72, -0.69])        # light direction in image space (right, up)

R = {
    'fur':   ['bk_fur0', 'bk_fur1', 'bk_fur2', 'bk_fur3', 'bk_fur4', 'bk_fur5'],
    'coat':  ['bk_coat0', 'bk_coat1', 'bk_coat2', 'bk_coat3', 'bk_coat4'],
    'hat':   ['bk_hat0', 'bk_hat1', 'bk_hat2', 'shadowPurp'],
    'red':   ['bk_redDeep', 'red3', 'red2', 'red1', 'red0'],
    'teal':  ['bk_teal0', 'paintTealD', 'paintTeal', 'bk_tealHi'],
    'pants': ['bk_pants0', 'bk_pants1', 'bk_pants2'],
    'boot':  ['leath3', 'leath2', 'leath1', 'leath0'],
    'tan':   ['leath2', 'leath1', 'leath0'],
    'ivory': ['cream2', 'cream1', 'cream0'],
    'brass': ['brass3', 'brass2', 'brass1', 'brass0'],
    'steel': ['steel3', 'steel2', 'steel1', 'steel0'],
    'ear':   ['shadowPurp', 'rockFar3'],
}
RIM = {'fur': 'bk_furRim', 'coat': 'bk_coatRim', 'hat': 'bk_hatRim', 'red': 'bk_redRim',
       'pants': 'bk_pantsRim', 'boot': 'bk_leathRim', 'tan': 'bk_leathRim', 'teal': 'bk_tealHi'}


class Img:
    def __init__(self):
        self.c = np.zeros((N, N, 4), np.uint8)

    def put(self, x, y, name):
        if 0 <= x < N and 0 <= y < N:
            self.c[y, x] = PA[name] if name else (0, 0, 0, 0)

    def get(self, x, y):
        return self.c[y, x]


def mask_poly(pts):
    im = Image.new('1', (N, N), 0)
    ImageDraw.Draw(im).polygon(pts, fill=1)
    return np.array(im, bool)


def mask_brush(pts, r0, r1=None):
    """thick polyline: discs along the path, radius lerped r0 -> r1."""
    r1 = r0 if r1 is None else r1
    im = Image.new('1', (N, N), 0)
    d = ImageDraw.Draw(im)
    segs = list(zip(pts[:-1], pts[1:]))
    tot = sum(math.dist(a, b) for a, b in segs) or 1
    acc = 0
    for a, b in segs:
        L = math.dist(a, b)
        steps = max(1, int(L * 2))
        for s in range(steps + 1):
            t = s / steps
            x = a[0] + (b[0] - a[0]) * t; y = a[1] + (b[1] - a[1]) * t
            r = r0 + (r1 - r0) * ((acc + L * t) / tot)
            d.ellipse([x - r, y - r, x + r, y + r], fill=1)
        acc += L
    return np.array(im, bool)


def mask_ellipse(cx, cy, rx, ry):
    im = Image.new('1', (N, N), 0)
    ImageDraw.Draw(im).ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=1)
    return np.array(im, bool)


YY, XX = np.mgrid[0:N, 0:N]


def shade(m, ramp, base=0.5, gx=0.35, gy=-0.25, rim=2, rim_s=0.35, sh=3, sh_s=0.3, zig=0.0,
          rimc=True, extra=None):
    """Return dict (y,x)->palette name for mask m."""
    names = R[ramp]
    n = len(names)
    ys, xs = np.nonzero(m)
    if len(xs) == 0:
        return {}
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    w = max(1, x1 - x0); h = max(1, y1 - y0)
    d, (iy, ix) = distance_transform_edt(m, return_indices=True)
    out = {}
    _CUR[0] = ramp
    for y, x in zip(ys, xs):
        v = base + gx * ((x - x0) / w - 0.5) + gy * ((y - y0) / h - 0.5) * -1
        nx, ny = ix[y, x] - x, iy[y, x] - y
        L = math.hypot(nx, ny) or 1
        s = (nx * LXY[0] + ny * LXY[1]) / L
        dd = d[y, x]
        if dd <= rim and s > 0.3:
            v += rim_s * (1.2 - dd / (rim + 1))
        if dd <= sh and s < -0.25:
            v -= sh_s * (1.2 - dd / (sh + 1))
        if zig:
            v += zig * (abs(((x + 2 * y) % 6) - 3) / 3 - 0.5)
        if extra is not None:
            v += extra[y, x]
        k = int(np.clip(v * n, 0, n - 1))
        c = names[k]
        if rimc and ramp in RIM and dd <= 1 and s > 0.62 and v > 0.78:
            c = RIM[ramp]
        out[(y, x)] = c
    return out


class Scene:
    def __init__(self):
        self.img = Img()
        self.mat = np.full((N, N), '', object)

    def part(self, m, colors, outline='ink'):
        self._cur = _CUR[0]
        if outline:
            ring = np.zeros_like(m)
            ring[1:, :] |= m[:-1, :]; ring[:-1, :] |= m[1:, :]
            ring[:, 1:] |= m[:, :-1]; ring[:, :-1] |= m[:, 1:]
            ring &= ~m
            for y, x in zip(*np.nonzero(ring)):
                self.img.put(x, y, outline)
        for (y, x), c in colors.items():
            self.img.put(x, y, c)
            self.mat[y, x] = getattr(self, '_cur', '')

    def px(self, pts, c):
        for x, y in pts:
            self.img.put(x, y, c)

    def line(self, a, b, c):
        x0, y0 = a; x1, y1 = b
        n = max(abs(x1 - x0), abs(y1 - y0))
        for i in range(n + 1):
            t = i / n if n else 0
            self.img.put(int(round(x0 + (x1 - x0) * t)), int(round(y0 + (y1 - y0) * t)), c)


def build():
    S = Scene()

    def weave(mask, tone, dark, k=3):
        """sparse ordered checker in one mid tone: fabric/leather texture, no noise."""
        for y, x in zip(*np.nonzero(mask)):
            if (x + y) % 2 == 0 and (x // 2 + y // 2) % k == 0 and tuple(S.img.get(x, y)[:3]) == P[tone]:
                S.img.put(x, y, dark)

    def folds(lines):
        for a, b, c in lines:
            S.line(a, b, c)

    # ---------------------------------------------------------------- tail (behind, bristled up)
    tail = mask_brush([(80, 102), (92, 100), (100, 92), (103, 82), (102, 72), (98, 65), (94, 62)], 3.2, 2.0)
    S.part(tail, shade(tail, 'fur', base=0.45, zig=0.22, gx=0.5))
    for (x, y) in [(107, 82), (107, 78), (106, 89), (105, 72)]:
        S.px([(x, y)], 'ink'); S.px([(x - 1, y)], 'bk_fur4')

    # ---------------------------------------------------------------- bandana tips (behind neck)
    tips = mask_poly([(77, 54), (88, 51), (96, 48), (93, 53), (98, 55), (89, 58), (79, 60)])
    S.part(tips, shade(tips, 'red', base=0.55))
    S.line((84, 54), (94, 50), 'red2')

    # ---------------------------------------------------------------- legs (stance apart)
    legL = mask_poly([(47, 84), (61, 84), (60, 92), (54, 106), (42, 106), (43, 94)])
    legR = mask_poly([(67, 84), (81, 84), (85, 94), (86, 106), (74, 106), (68, 92)])
    S.part(legL, shade(legL, 'pants', base=0.36, gx=0.5))
    S.part(legR, shade(legR, 'pants', base=0.5, gx=0.5))
    folds([((50, 93), (54, 97), 'bk_pants0'), ((76, 93), (80, 96), 'bk_pants0'),
           ((47, 99), (51, 101), 'bk_pants0'), ((77, 99), (81, 101), 'bk_pants0'),
           ((56, 88), (58, 90), 'bk_pants0'), ((79, 95), (81, 96), 'bk_pantsRim')])

    # ---------------------------------------------------------------- spurs (behind boots)
    for cx in (37, 91):
        S.px([(cx, 116), (cx - 2, 116), (cx + 2, 116), (cx, 114), (cx, 118),
              (cx - 1, 115), (cx + 1, 117), (cx + 1, 115), (cx - 1, 117)], 'ink')
        S.px([(cx, 116)], 'steel0'); S.px([(cx - 1, 116), (cx + 1, 116)], 'steel1')
        S.px([(cx, 115), (cx, 117)], 'steel1')

    # ---------------------------------------------------------------- boots
    for side in (0, 1):
        if side == 0:
            shaft = mask_poly([(41, 104), (55, 104), (54, 116), (41, 116)])
            foot = mask_poly([(39, 113), (54, 113), (55, 121), (54, 123), (32, 123), (31, 120), (35, 116)])
            x0, sx0, sx1, tx = 41, 32, 55, 36
        else:
            shaft = mask_poly([(73, 104), (87, 104), (87, 116), (74, 116)])
            foot = mask_poly([(74, 113), (89, 113), (93, 116), (96, 120), (95, 123), (73, 123), (72, 121)])
            x0, sx0, sx1, tx = 73, 73, 96, 89
        m = shaft | foot
        S.part(m, shade(m, 'boot', base=0.42 + 0.12 * side, gx=0.45))
        S.line((x0, 107), (x0 + 14, 107), 'ink')
        for x in range(x0, x0 + 15):
            S.img.put(x, 104, 'leath0' if x > x0 + 6 else 'leath1')
            S.img.put(x, 105, 'leath1' if x > x0 + 3 else 'leath2')
        S.line((x0 + 3, 110), (x0 + 6, 112), 'leath3'); S.line((x0 + 9, 111), (x0 + 12, 109), 'leath3')
        S.line((sx0 + 1, 122), (sx1 - 1, 122), 'leath3'); S.line((sx0 + 1, 123), (sx1 - 1, 123), 'leath3')
        S.line((x0, 116), (x0 + 13, 116), 'brass3'); S.line((x0, 115), (x0 + 13, 115), 'brass2')
        S.px([(x0 + 10, 115), (x0 + 11, 115)], 'brass0')
        S.px([(tx, 117), (tx + 1, 117), (tx + 2, 118)], 'bk_leathRim' if side else 'leath0')

    # ---------------------------------------------------------------- long coat skirt panels
    skL = mask_poly([(42, 79), (55, 81), (53, 96), (52, 112), (46, 114), (40, 112), (34, 115), (28, 113), (32, 100), (37, 88)])
    skR = mask_poly([(73, 81), (86, 79), (91, 88), (96, 100), (100, 110), (102, 115), (95, 113), (89, 115), (82, 113), (77, 114), (76, 98)])
    S.part(skL, shade(skL, 'coat', base=0.3, gx=0.4))
    S.part(skR, shade(skR, 'coat', base=0.48, gx=0.45))
    weave(skL, 'bk_coat2', 'bk_coat1'); weave(skR, 'bk_coat3', 'bk_coat2')
    for lm, base in (([(53, 81), (55, 81), (53, 96), (52, 112), (51, 112), (52, 96)], 0.05),
                     ([(73, 81), (75, 81), (77, 98), (78, 113), (77, 113), (76, 98)], 0.25)):
        ln = mask_poly(lm)
        S.part(ln, shade(ln, 'red', base=base, gx=0, gy=0, rim=0, sh=0, rimc=False), outline=None)
    for (a, b) in [((45, 88), (37, 111)), ((49, 90), (45, 112)), ((41, 86), (32, 108)),
                   ((80, 88), (84, 112)), ((85, 86), (92, 110)), ((89, 90), (98, 110)), ((79, 96), (80, 112))]:
        S.line(a, b, 'bk_coat0')
        S.line((a[0] + 1, a[1] + 1), (b[0] + 1, b[1]), 'bk_coat4' if a[0] > 64 else 'bk_coat3')
    S.px([(33, 114), (34, 113), (99, 112), (100, 111), (88, 114)], None)
    S.px([(33, 113), (99, 111), (88, 113)], 'ink')

    # ---------------------------------------------------------------- torso (coat), vest, lapels
    torso = mask_poly([(48, 56), (56, 57), (72, 57), (80, 56), (87, 60), (88, 68), (85, 81), (43, 81), (40, 68), (41, 60)])
    S.part(torso, shade(torso, 'coat', base=0.38, gx=0.5))
    weave(torso, 'bk_coat2', 'bk_coat1')
    vest = mask_poly([(55, 57), (73, 57), (73, 82), (68, 80), (64, 83), (60, 80), (55, 82)])
    S.part(vest, shade(vest, 'teal', base=0.45, gx=0.6, rim=1))
    weave(vest, 'paintTealD', 'bk_teal0')
    S.line((64, 70), (64, 82), 'bk_teal0')
    for y in (72, 76, 80):
        S.px([(66, y)], 'brass1'); S.px([(66, y + 1)], 'brass3')
    for i, (x, y) in enumerate([(67, 76), (68, 77), (69, 78), (70, 78), (71, 79)]):
        S.px([(x, y)], 'brass1' if i % 2 else 'brass2')
    S.px([(59, 74), (60, 74), (61, 74)], 'bk_teal0')
    lapL = mask_poly([(47, 56), (55, 57), (54, 81), (52, 81), (49, 66)])
    lapR = mask_poly([(81, 56), (73, 57), (74, 81), (76, 81), (79, 66)])
    S.part(lapL, shade(lapL, 'coat', base=0.42, gx=0.6))
    S.part(lapR, shade(lapR, 'coat', base=0.66, gx=0.6))
    folds([((44, 62), (42, 77), 'bk_coat0'), ((84, 62), (86, 76), 'bk_coat1'), ((45, 70), (47, 74), 'bk_coat1'),
           ((83, 70), (81, 74), 'bk_coat2'), ((85, 61), (87, 66), 'bk_coat4')])
    S.px([(77, 63), (78, 63)], 'brass2')

    # ---------------------------------------------------------------- belt + cartridges + buckle
    belt = mask_poly([(42, 80), (86, 80), (86, 85), (42, 85)])
    S.part(belt, shade(belt, 'boot', base=0.18, gx=0.3, rimc=False))
    for x in range(45, 85, 4):
        if 57 <= x <= 70:
            continue
        S.px([(x, 82), (x, 83)], 'brass1'); S.px([(x + 1, 82), (x + 1, 83)], 'brass3'); S.px([(x, 81)], 'brass0')
    buckle = mask_poly([(59, 79), (69, 79), (69, 86), (59, 86)])
    S.part(buckle, shade(buckle, 'brass', base=0.55, gx=0.5))
    S.line((62, 81), (66, 81), 'ink'); S.line((62, 84), (66, 84), 'ink')
    S.px([(62, 82), (62, 83), (66, 82), (66, 83)], 'ink'); S.px([(64, 82), (64, 83)], 'brass3')

    # ---------------------------------------------------------------- holster + ivory grip
    hol = mask_poly([(29, 87), (43, 87), (42, 98), (39, 107), (34, 108), (31, 98)])
    S.part(hol, shade(hol, 'tan', base=0.42, gx=0.55))
    for y in range(90, 106, 2):
        S.px([(32 + (y - 90) // 6, y), (41 - (y - 90) // 5, y)], 'leath2')
    S.line((30, 90), (42, 90), 'leath2'); S.line((30, 89), (42, 89), 'leath0')
    S.px([(36, 96), (37, 96), (36, 97), (37, 97)], 'brass1'); S.px([(37, 97)], 'brass3')
    S.line((34, 108), (34, 112), 'leath2')
    S.line((43, 85), (39, 87), 'ink'); S.line((44, 85), (41, 87), 'leath3')
    grip = mask_poly([(28, 79), (32, 76), (36, 78), (41, 87), (35, 88)])
    S.part(grip, shade(grip, 'ivory', base=0.5, gx=0.6))
    S.px([(33, 81), (34, 83), (35, 85)], 'cream2')
    S.px([(39, 82), (40, 83), (40, 84)], 'steel1'); S.px([(39, 81)], 'steel0')

    def paw(cx, cy, fingers, base):
        pm = mask_ellipse(cx, cy, 4.5, 3.4)
        S.part(pm, shade(pm, 'fur', base=base, gx=0.6, zig=0.08, sh=1, sh_s=0.15))
        for x, y in fingers:
            S.px([(x, y - 1)], 'bk_fur3')
            S.px([(x, y)], 'ink')
            S.px([(x, y + 1)], 'cream0')
            S.px([(x, y + 2)], 'cream2')
        S.px([(int(cx) + 1, int(cy) - 2), (int(cx) + 2, int(cy) - 2)], 'bk_fur6')

    # ---------------------------------------------------------------- arm L (screen right): bent, out
    armL = mask_brush([(84, 58), (94, 68), (95, 77)], 4.6, 3.6)
    S.part(armL, shade(armL, 'coat', base=0.5, gx=0.6))
    folds([((88, 62), (92, 67), 'bk_coat1'), ((92, 70), (93, 74), 'bk_coat1'), ((97, 66), (99, 72), 'bk_coat4')])
    cuffL = mask_poly([(90, 76), (99, 76), (99, 80), (90, 80)])
    S.part(cuffL, shade(cuffL, 'coat', base=0.72, gx=0.5))
    paw(94.5, 83.5, [(91, 87), (94, 88), (97, 87)], 1.0)

    # ---------------------------------------------------------------- arm R (gun arm): elbow out, paw over grip
    armR = mask_brush([(44, 58), (29, 66), (33, 70)], 4.6, 3.6)
    S.part(armR, shade(armR, 'coat', base=0.36, gx=0.55))
    folds([((39, 60), (33, 64), 'bk_coat0'), ((27, 64), (26, 68), 'bk_coat0'), ((40, 63), (35, 66), 'bk_coat3')])
    cuffR = mask_poly([(30, 67), (36, 69), (35, 73), (29, 71)])
    S.part(cuffR, shade(cuffR, 'coat', base=0.66, gx=0.5))
    paw(37.5, 72, [(34, 75), (37, 76), (40, 76), (43, 75)], 0.95)

    # ---------------------------------------------------------------- ears (behind hat)
    for pts, inner, base in [([(45, 40), (46, 22), (57, 34)], [(48, 36), (48, 27), (54, 33)], 0.38),
                             ([(71, 34), (82, 22), (83, 40)], [(74, 33), (80, 27), (80, 36)], 0.62)]:
        e = mask_poly(pts)
        S.part(e, shade(e, 'fur', base=base, zig=0.15))
        ie = mask_poly(inner)
        S.part(ie, shade(ie, 'ear', base=0.5, rimc=False, gx=0.6), outline=None)
    S.px([(83, 28), (83, 29), (82, 29)], None); S.px([(82, 28), (82, 30), (81, 29)], 'ink')
    S.px([(49, 33), (50, 31), (77, 33), (78, 31)], 'bk_fur5')

    # ---------------------------------------------------------------- head (smaller)
    head = mask_poly([(48, 36), (80, 36), (83, 40), (85, 44), (88, 47), (85, 49), (87, 51), (82, 53),
                      (77, 56), (64, 57), (51, 56), (46, 53), (41, 51), (43, 49), (40, 47), (43, 44), (45, 40)])
    cast = np.zeros((N, N))
    cast[(YY < 44 + ((XX - 64) / 12.0) ** 2 / 3)] -= 0.32
    S.part(head, shade(head, 'fur', base=0.48, gx=0.55, zig=0.16, extra=cast))
    for cx in (59.5, 68.5):
        mp = mask_ellipse(cx, 52.5, 5, 3) & head
        cols = shade(mp, 'fur', base=0.72 + (0.1 if cx > 64 else 0), gx=0.3, zig=0.12, rim=0, sh=0)
        for (y, x), c in cols.items():
            S.img.put(x, y, c)
    for a, b in [((42, 47), (45, 50)), ((45, 50), (43, 52)), ((85, 47), (82, 50)), ((82, 50), (85, 52))]:
        S.line(a, b, 'bk_fur1')
    S.px([(83, 46), (84, 45), (86, 49)], 'bk_fur5')
    S.line((48, 42), (57, 45), 'bk_fur4'); S.line((80, 42), (71, 45), 'bk_fur5')
    for sx in (1, -1):
        def X(x):
            return x if sx == 1 else 128 - x
        S.line((X(49), 43), (X(57), 46), 'ink')
        S.line((X(50), 43), (X(56), 45), 'ink')
        for (x, y) in [(50, 44), (51, 44), (52, 45), (53, 45), (51, 45), (52, 46), (53, 46), (54, 46), (55, 46)]:
            S.img.put(X(x), y, 'eyeAmber' if y < 46 else 'brass2')
        S.px([(X(53), 45), (X(53), 46)], 'ink')
        S.px([(X(51), 44) if sx == 1 else (X(55), 46)], 'cream0')
        S.line((X(51), 47), (X(56), 47), 'bk_fur1')
    for (x, y) in [(80, 39), (79, 40), (79, 41), (78, 42), (76, 48), (75, 49), (75, 50)]:
        S.img.put(x, y, 'bk_scar')
    S.line((61, 48), (67, 48), 'rockFar2'); S.line((62, 49), (66, 49), 'shadowPurp'); S.px([(63, 50), (64, 50), (65, 50)], 'shadowPurp')
    S.px([(64, 51)], 'ink')
    S.px([(63, 52), (62, 53), (61, 53), (60, 52)], 'ink')
    S.px([(65, 52), (66, 53), (67, 53), (68, 52), (69, 51)], 'ink')
    S.px([(67, 54), (67, 55)], 'cream0'); S.px([(68, 54)], 'cream2')

    # ---------------------------------------------------------------- bandana (smaller, more vest)
    ban = mask_poly([(51, 56), (77, 56), (75, 59), (67, 68), (64, 71), (61, 68), (53, 59)])
    S.part(ban, shade(ban, 'red', base=0.55, gx=0.55, sh=4))
    for a, b in [((58, 59), (62, 66)), ((70, 59), (66, 67)), ((54, 57), (74, 57))]:
        S.line(a, b, 'red2')
    for (x, y) in [(57, 59), (62, 61), (68, 60), (72, 58), (64, 66)]:
        S.px([(x, y)], 'cream1')
    S.line((58, 56), (70, 56), 'bk_redDeep')

    # ---------------------------------------------------------------- hat
    brim = mask_poly([(37, 31), (41, 35), (50, 36), (64, 37), (78, 36), (87, 35), (91, 31), (93, 34),
                      (88, 39), (78, 41), (64, 42), (50, 41), (40, 39), (35, 34)])
    crown = mask_poly([(54, 37), (54, 29), (56, 25), (60, 23), (64, 26), (68, 23), (72, 25), (74, 29), (74, 37)])
    S.part(crown, shade(crown, 'hat', base=0.42, gx=0.7, rim=2, rim_s=0.45))
    S.part(brim, shade(brim, 'hat', base=0.45, gx=0.55, gy=-0.6, rim=1))
    for x in range(36, 93):
        for y in range(38, 42):
            if brim[y, x] and not brim[min(N - 1, y + 2), x]:
                S.img.put(x, y, 'bk_hat0')
    band = mask_poly([(54, 33), (74, 33), (74, 36), (54, 36)])
    S.part(band, shade(band, 'brass', base=0.3, gx=0.7, rim=0, sh=0), outline=None)
    S.line((54, 36), (74, 36), 'brass3'); S.px([(57, 34), (58, 34)], 'brass3')
    S.line((68, 26), (71, 31), 'shadowPurp'); S.line((58, 26), (56, 31), 'bk_hat0')
    S.px([(69, 28), (70, 28), (69, 29), (70, 29)], None)
    S.px([(68, 27), (71, 28), (68, 30), (71, 29), (69, 27), (70, 30)], 'bk_hatRim')

    # ---------------------------------------------------------------- whiskers
    for a, b in [((40, 49), (32, 47)), ((41, 51), (31, 52)), ((88, 49), (97, 47)), ((87, 51), (98, 52))]:
        S.line(a, b, 'bk_fur6')
    # rim light: outer-silhouette pixels facing the sun (up-right) get the warm rim tone
    a = S.img.c
    alpha = a[..., 3] > 0
    ink = np.all(a[..., :3] == P['ink'], -1) & alpha
    rim = {}
    for y in range(1, N - 2):
        for x in range(1, N - 2):
            m = S.mat[y, x]
            if not alpha[y, x] or ink[y, x] or m not in RIM:
                continue
            for dx, dy in ((1, 0), (0, -1)):
                if ink[y + dy, x + dx] and not alpha[y + 2 * dy, x + 2 * dx]:
                    if (x > 60 or dy == -1) and S.mat[y, x] == m:
                        rim[(x, y)] = RIM[m]
    for (x, y), c in rim.items():
        S.img.put(x, y, c)
    # colour budget (<= 48 per sprite): fold rarely-used tones into neighbours
    MERGE = {'steel2': 'steel1', 'rockFar1': 'rockFar2', 'bk_tealHi': 'paintTeal', 'red0': 'red1',
             'bk_pants2': 'bk_pants1', 'ink2': 'leath3', 'sun0': 'cream0', 'leath0': 'bk_leathRim'}
    a = S.img.c
    for src, dst in MERGE.items():
        hit = np.all(a[..., :3] == P[src], -1) & (a[..., 3] > 0)
        a[hit] = PA[dst]
    img = Image.fromarray(S.img.c, 'RGBA')
    return img


if __name__ == '__main__':
    im = build()
    check_palette(im, name='v2_ready')
    cols = {tuple(c) for c in np.asarray(im).reshape(-1, 4) if c[3]}
    bb = im.getbbox()
    from collections import Counter
    inv = {v: k for k, v in PA.items()}
    cnt = Counter(inv[tuple(c)] for c in np.asarray(im).reshape(-1, 4) if c[3])
    print(sorted(cnt.items(), key=lambda t: t[1]))
    print('colours', len(cols), 'bbox', bb, 'height', bb[3] - bb[1])
    save_png(im, os.path.join(WORK, 'v2_ready.png'))
    bg = Image.new('RGBA', im.size, PA['sand0']); bg.alpha_composite(im)
    big = Image.new('RGBA', (128 * 5 + 10 + 128, 128 * 5), PA['sand1'])
    big.paste(upscale(bg, 5), (0, 0)); big.paste(bg, (128 * 5 + 10, 0))
    big.save(os.path.join(WORK, 'v2_ready_preview.png'))
