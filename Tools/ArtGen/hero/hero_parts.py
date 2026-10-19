"""Body parts of the Kid (orange tabby gunslinger, 3/4 rear over-the-shoulder view).
All coordinates are cell coordinates (144 x 216, top-left origin) for the neutral pose.
Every function paints a fresh Part for the given pose dict `p`."""
import math
import numpy as np
from herokit import Part, catmull, poly_mask, shift, edge, SCALE, CW, CH

W, H = CW, CH
YY, XX = (np.mgrid[0:H, 0:W] + .5) / SCALE - .5     # design-unit coordinates of each pixel


def T(pts, dx=0, dy=0):
    return [(x + dx, y + dy) for x, y in pts]


def lerp_pts(a, b, t):
    return [(p[0] * (1 - t) + q[0] * t, p[1] * (1 - t) + q[1] * t) for p, q in zip(a, b)]


def dist_inside(mask, maxd=12):
    d = np.full(mask.shape, maxd, np.int16)
    cur = mask.copy()
    for k in range(maxd):
        er = cur & shift(cur, 1, 0) & shift(cur, -1, 0) & shift(cur, 0, 1) & shift(cur, 0, -1)
        d[cur & ~er] = k
        cur = er
    return d


def thick_curve(pts, r0, r1, n=10):
    """Mask of a tapered stroke along a Catmull-Rom curve (radius r0 -> r1)."""
    dense = catmull(pts, n)
    m = np.zeros((H, W), bool)
    L = len(dense)
    for i, (x, y) in enumerate(dense):
        t = i / max(1, L - 1)
        r = r0 + (r1 - r0) * t
        m |= (XX - x) ** 2 + (YY - y) ** 2 <= r * r
    return m, dense


def arc_y(x0, x1, y_end, sag):
    """f(x): an elliptic arc through (x0,y_end),(x1,y_end) sagging down by `sag` in the middle."""
    c, r = (x0 + x1) / 2, (x1 - x0) / 2
    return lambda x: y_end + sag * np.sqrt(np.clip(1 - ((x - c) / r) ** 2, 0, 1))


# =====================================================================================  PANTS
def pants(p):
    pt = Part('pants', 'pants')
    bx, by = p.get('bx', 0), p.get('by', 0)
    X, Y = XX - bx, YY - by
    m = pt.poly(T([(8, 158), (30, 152), (64, 150), (100, 148), (116, 150), (122, 160), (124, 216),
                   (6, 216)], bx, by), 'pants', 2)
    pt.addlum(m & (X > 92), 1)
    pt.addlum(m & edge(m, 1, 0), 1)
    pt.addlum(m & (X < 20), -1)
    # leg split (rear view)
    pt.poly(T([(63, 182), (66, 182), (70, 216), (59, 216)], bx, by), 'pants', 0)
    pt.line(T([(64, 160), (64, 181)], bx, by), 'pants', 1)
    # seat creases
    pt.line(T([(42, 178), (52, 183), (60, 185)], bx, by), 'pants', 1)
    pt.line(T([(80, 183), (90, 178), (100, 176)], bx, by), 'pants', 1)
    pt.line(T([(81, 184), (91, 179)], bx, by), 'pants', 3)
    return pt


# =====================================================================================  BELT
BELT_TOP = [(6, 156), (30, 152), (60, 151), (90, 149), (112, 146), (122, 146)]
BELT_BOT = [(122, 153), (112, 154), (90, 157), (60, 159), (30, 160), (6, 164)]


def belt(p):
    pt = Part('belt', 'belt')
    bx, by = p.get('bx', 0), p.get('by', 0)
    m = pt.poly(T(BELT_TOP + BELT_BOT, bx, by), 'leath', 2)
    pt.addlum(edge(m, 0, -1), 1)
    pt.addlum(edge(m, 0, 1), -1)
    xs = [q[0] + bx for q in BELT_TOP]; ys = [q[1] + by for q in BELT_TOP]
    for x0 in range(18, 98, 4):
        x = x0 + bx
        yt = int(round(np.interp(x, xs, ys)))
        pt.px([(x, yt + 1)], 'brass', 4)
        pt.px([(x + 1, yt + 1)], 'brass', 3)
        pt.px([(x, yt + 2), (x + 1, yt + 2)], 'brass', 2)
        pt.px([(x, yt + 3), (x + 1, yt + 3)], 'leath', 3)
        pt.px([(x + 2, yt + 2), (x + 2, yt + 3)], 'leath', 0)
    return pt


# =====================================================================================  HOLSTER
HOLSTER = [(101, 151), (114, 149), (118, 156), (119, 170), (117, 182), (112, 188), (107, 185),
           (104, 172), (102, 160)]
# grip of the holstered revolver, butt tilted back toward camera (hand-pixeled)
HOLSTER_GUN = [
    "..kkkk...",
    ".kGGgdk..",
    "kGGggddk.",
    "kGgggbdk.",
    "kGggggdk.",
    ".kgggddk.",
    ".kkgdddkk",
    "kSskkkksk",
    "kssssssdk",
    ".kdddddk.",
]


def holster(p, with_gun=True):
    bx, by = p.get('bx', 0), p.get('by', 0)
    pt = Part('holster', 'holster')
    m = pt.poly(T(HOLSTER, bx, by), 'leath', 2)
    pt.addlum(m & (XX >= 114 + bx), 1)
    pt.addlum(m & (XX <= 105 + bx), -1)
    pt.addlum(edge(m, 0, -1), 1)
    for y in range(157, 182, 3):
        pt.px([(115 + bx, y + by)], 'leath', 4)
    pt.line(T([(105, 160), (108, 181)], bx, by), 'leath', 1)
    pt.line(T([(102, 152), (114, 150)], bx, by), 'leath', 0)
    out = [pt]
    if with_gun:
        g = Part('holster_gun', 'holster_gun')
        legend = {'k': 'ink', 'G': ('grip', 4), 'g': ('grip', 3), 'd': ('grip', 2),
                  'b': ('brass', 4), 's': ('steel', 3), 'S': ('steel', 4)}
        g.bitmap(HOLSTER_GUN, legend, 103 + bx, 141 + by)
        out.append(g)
    return out


# =====================================================================================  TAIL
TAIL_BASE = [(50, 158), (47, 172), (42, 184), (33, 190), (23, 188), (17, 180), (17, 171), (22, 166)]


def tail(p):
    """J-curl tail hanging from under the poncho, tip curling up on the lower left."""
    pt = Part('tail', 'tail')
    bx, by = p.get('bx', 0), p.get('by', 0)
    ph = p.get('tail', 0.0)
    amp = p.get('tail_amp', 1.0)
    spine = []
    n = len(TAIL_BASE)
    for i, (x, y) in enumerate(TAIL_BASE):
        t = i / (n - 1)
        a = ph * 2 * math.pi - t * 2.4
        spine.append((x + bx + math.sin(a) * 3.0 * amp * t ** 1.3,
                      y + by - math.cos(a) * 1.8 * amp * t ** 1.6 + p.get('tail_dy', 0) * t))
    m, dense = thick_curve(spine, 5.6, 3.1, 10)
    pt.fill(m, 'fur', 2)
    pt.addlum(edge(m, 0, -1) | edge(m, 1, 0), 1)
    pt.addlum(edge(m, 0, 1) | edge(m, -1, 0), -1)
    L = len(dense)
    for t0, w0 in ((0.20, 2.4), (0.37, 2.2), (0.53, 2.0), (0.68, 1.8)):
        i = int(t0 * (L - 1))
        x, y = dense[i]
        x2, y2 = dense[min(L - 1, i + 1)]
        tx, ty = x2 - x, y2 - y
        ln = math.hypot(tx, ty) or 1
        tx, ty = tx / ln, ty / ln
        d = (XX - x) * tx + (YY - y) * ty
        pt.fill(m & (np.abs(d) <= w0 / 2 + 0.2), 'furS')
    tipm = np.zeros((H, W), bool)
    for i in range(int(L * 0.85), L):
        x, y = dense[i]
        tipm |= (XX - x) ** 2 + (YY - y) ** 2 <= 3.6 ** 2
    pt.fill(tipm & m, 'furS')
    return pt


# =====================================================================================  PONCHO
COLLAR = [(38, 71), (46, 68), (56, 67), (66, 68), (74, 71)]
R_REST = [(84, 73), (94, 77), (102, 82), (108, 88), (111, 96), (112, 108), (113, 122), (114, 134),
          (113, 141), (110, 144)]
R_HALF = [(85, 72), (96, 74), (105, 77), (111, 82), (114, 90), (114, 102), (113, 116), (111, 128),
          (109, 138), (106, 144)]
R_LIFT = [(86, 71), (98, 70), (107, 71), (114, 75), (116, 84), (114, 96), (110, 110), (106, 124),
          (102, 136), (99, 143)]
HEM = [(104, 146), (97, 148), (94, 146), (90, 149), (80, 152), (72, 154), (68, 152), (62, 155),
       (54, 157), (50, 155), (44, 159), (36, 160), (34, 158), (28, 162), (20, 163), (18, 161),
       (12, 164), (6, 164)]
LEFT = [(3, 158), (2, 146), (3, 132), (4, 118), (6, 106), (10, 96), (16, 87), (24, 80), (31, 75)]
FOLDS = [((14, 98), (18, 161), 4), ((32, 90), (34, 158), 3), ((50, 92), (50, 155), 4),
         ((68, 90), (68, 152), 3), ((92, 92), (94, 146), 4)]
FOLDS_LIFT = [((14, 98), (18, 161), 4), ((32, 90), (34, 158), 3), ((50, 92), (50, 155), 4),
              ((72, 80), (68, 152), 3), ((86, 76), (94, 146), 4), ((100, 76), (102, 132), 3)]


def poncho_outline(p):
    br = p.get('breath', 0)
    lift = p.get('lift', 0.0)
    bx, by = p.get('bx', 0), p.get('by', 0)
    if lift <= 0.5:
        right = lerp_pts(R_REST, R_HALF, lift * 2)
    else:
        right = lerp_pts(R_HALF, R_LIFT, (lift - 0.5) * 2)
    col = [(x, y - br) for x, y in COLLAR]
    right = [(x, y - (br if y < 100 else 0)) for x, y in right]
    left = [(x, y - (br if y < 100 else 0)) for x, y in LEFT]
    hem = [(x, y - lift * max(0.0, x - 60) * 0.10) for x, y in HEM]
    hem = [(x + p.get('hem_dx', 0) * (1 - (y - 100) / 80.0) * 0, y) for x, y in hem]
    pts = col + right + hem + left
    return T(pts, bx, by), T(hem, bx, by), T(col, bx, by)


def poncho(p):
    pt = Part('poncho', 'poncho')
    pts, hem, col = poncho_outline(p)
    bx, by = p.get('bx', 0), p.get('by', 0)
    lift = p.get('lift', 0.0)
    br = p.get('breath', 0)
    m = pt.poly(pts, 'poncho', 2)
    d = dist_inside(m, 10)
    X, Y = XX - bx, YY - by
    # ---- big masses: back turns away on the left, right side toward the sun
    pt.addlum(m & (X < 26), -1)
    pt.addlum(m & (X < 8) & (Y > 104), -1)
    # shoulder caps lit; terminator shadow below the right cap
    cap = m & (d <= 3) & (Y < 98 - br)
    pt.addlum(cap & (X > 56), 1)
    pt.addlum(m & (d <= 1) & (Y < 94) & (X > 64), 1)
    pt.addlum(cap & (X <= 56) & (X > 12) & (d <= 1), 1)
    term = m & (d >= 6) & (d <= 7) & (Y < 104) & (X > 78)
    pt.addlum(term, -1)
    # right rim light down the side
    pt.addlum(m & (d <= 1) & (X > 100) & (Y >= 94), 1)
    # head shadow on upper back (falls back-left of the neck)
    pt.addlum(m & poly_mask(T([(32, 70), (68, 70), (64, 77), (54, 84), (42, 88), (30, 82)], bx, by - br), W, H), -1)
    # ---- folds: tapered valley wedges with a lit ridge on their right
    for (x0, y0), (x1, y1), wv in (FOLDS_LIFT if lift > 0.4 else FOLDS):
        x0 += bx; x1 += bx; y0 += by; y1 += by + 3
        if SCALE > 1:
            wv = wv * 1.7
        val = poly_mask([(x0, y0), (x1 + wv / 2, y1), (x1 - wv / 2, y1)], W, H)
        core = poly_mask([(x0 + (x1 - x0) * .45, y0 + (y1 - y0) * .45), (x1 + wv / 4, y1),
                          (x1 - wv / 4, y1)], W, H)
        rid = poly_mask([(x0 + 1, y0 + (y1 - y0) * .15), (x1 + wv / 2 + 2.2 + (1.2 if SCALE > 1 else 0), y1),
                         (x1 + wv / 2 + 0.6, y1)], W, H) & ~val
        pt.addlum(m & val, -1)
        pt.addlum(m & core, -1)
        pt.addlum(m & rid, 1, hi=3)
    # collar roll: lit top, shadow under
    cy = np.interp(XX, [q[0] for q in col], [q[1] for q in col])
    dc = YY - cy
    zone = m & (XX > 32 + bx) & (XX < 82 + bx)
    pt.fill(zone & (dc >= 0) & (dc < 2), lum=3)
    pt.fill(zone & (dc >= 2) & (dc < 3) & (XX > 50 + bx), lum=1)
    # ---- woven pattern following the hem line
    hx = [q[0] for q in hem][::-1]; hy = [q[1] for q in hem][::-1]
    hem_y = np.interp(XX, hx, hy)
    dh = hem_y - YY
    def band(a, b):
        return m & (dh >= a) & (dh < b)
    pt.fill(band(1, 3), 'ponB')
    pt.fill(band(13, 14), 'ponK')
    pt.fill(band(15, 24), 'ponC')
    pt.fill(band(24, 25), 'ponK')
    pt.fill(band(27, 29), 'ponB')
    zz = 16 + np.abs(((XX - bx + 2) % 10) - 5) * 1.4
    pt.fill(band(15, 24) & (np.abs(dh - zz) < 1.0), 'ponB')
    pt.fill(band(27, 29) & (((XX - bx) % 10) == 7), 'ponC')
    pt.fill(zone & (dc >= 5) & (dc < 6), 'ponC')
    pt.fill(zone & (dc >= 6) & (dc < 7), 'ponB')
    pt.addlum(m & (dh < 1), -1)
    if SCALE > 1:
        py, px_ = np.mgrid[0:H, 0:W]
        MIDk = __import__('herokit').MID
        # chunky woven zig-zag band (v2): cream ground, thick red zig-zag, brown diamonds
        zb = m & (dh >= 13) & (dh < 29)
        pt.fill(zb, 'ponC')
        pt.fill(m & ((dh >= 12) & (dh < 13) | (dh >= 29) & (dh < 30)), 'ponK')
        per = 14.0
        u = ((XX - bx + 3) % per) / per
        zz2 = 14.5 + np.abs(u - 0.5) * 2 * 12.5
        pt.fill(zb & (np.abs(dh - zz2) < 2.0), 'poncho')
        dia = zb & (np.abs(((XX - bx + 3 + per / 2) % per) - per / 2) + np.abs(dh - 21) * .9 < 2.2)
        pt.fill(dia & (np.abs(dh - zz2) >= 2.0), 'ponB')
        # yarn texture on the cream: every 3rd row slightly darker, brick offset
        yarn = ((py % 3) == 0) & (((px_ + (py // 3) * 2) % 5) < 2)
        pt.addlum(zb & (pt.mat == MIDk['ponC']) & yarn, -1)
        # upper woven stripe set
        pt.fill(m & (dh >= 42) & (dh < 45), 'ponB')
        pt.fill(m & (dh >= 45) & (dh < 47), 'ponC')
        pt.fill(m & (dh >= 45) & (dh < 47) & ((px_ % 8) < 4), 'ponK')
        pt.fill(m & (dh >= 47) & (dh < 49), 'ponB')
        # broader lit shoulder planes + brim cast shadow over the left shoulder / collar
        pt.addlum(m & (d <= 6) & (Y < 96) & (X > 62) & (pt.mat == MIDk['poncho']), 1, hi=4)
        brim_sh = ((XX - bx - 44) / 34.0) ** 2 + ((YY - by - 74) / 8.0) ** 2 <= 1
        pt.addlum(m & brim_sh, -1)
        pt.addlum(m & brim_sh & (((XX - bx - 40) / 24.0) ** 2 + ((YY - by - 73) / 5.0) ** 2 <= 1), -1)
    return pt


def fringe(p):
    pt = Part('fringe', 'fringe', outline=False)
    _, hem, _ = poncho_outline(p)
    ph = p.get('fringe', 0.0)
    amp = p.get('fringe_amp', 1.4)
    hx = [q[0] for q in hem][::-1]; hy = [q[1] for q in hem][::-1]
    x = hx[0] + 2
    i = 0
    while x < hx[-1] - 2:
        y0 = float(np.interp(x, hx, hy)) + 1
        ln = 4 + ((i * 7) % 3)
        sway = math.sin(ph * 2 * math.pi - x * 0.10) * amp
        if i % 3 == 1:
            mat, l0 = 'ponC', 1
        else:
            mat, l0 = 'poncho', 1
        tip = (x + sway, y0 + ln)
        midp = (x + sway * 0.4, y0 + ln * 0.55)
        pt.line([(x, y0), midp, tip], mat, l0)
        pt.line([(x + 1, y0), (x + 1 + sway * 0.4, y0 + ln * 0.55)], mat, l0 + 1)
        pt.px([(int(round(x)), int(round(y0))), (int(round(x)) + 1, int(round(y0)))], 'ponB', 1)
        x += 3
        i += 1
    pt.noline[:] = True
    return pt


# =====================================================================================  HEAD
def head(p):
    pt = Part('head', 'head')
    hx = p.get('hdx', 0) + p.get('bx', 0)
    hy = p.get('hdy', 0) + p.get('by', 0)
    X, Y = XX - hx, YY - hy
    pt.ellipse(55 + hx, 51 + hy, 24, 18, 'fur', 2)
    pt.poly(T([(42, 60), (70, 60), (69, 78), (43, 78)], hx, hy), 'fur', 2)
    # right cheek ruff: profile sliver with tufts
    pt.poly(T([(70, 44), (79, 45), (84, 48), (86, 51), (84, 52), (88, 55), (85, 57), (87, 60),
               (83, 61), (84, 64), (79, 64), (76, 68), (70, 66)], hx, hy), 'fur', 3)
    # left side tufts
    pt.poly(T([(33, 52), (29, 55), (32, 57), (30, 61), (34, 62), (38, 60)], hx, hy), 'fur', 1)
    m = pt.mask
    # deep brim shade: whole back of head in shadow, nape gets some bounce light
    pt.addlum(m & (Y < 58), -1)
    pt.addlum(m & (Y < 51) & (X < 70), -1)
    pt.addlum(m & (X < 40), -1)
    # right side / cheek faces the sun
    pt.addlum(m & (X > 72) & (Y >= 50), 1)
    pt.addlum(edge(m, 1, 0) & (X > 74) & (Y >= 50), 1)
    # whisker pad: small cream patch
    pt.poly(T([(81, 54), (85, 55), (84, 58), (81, 59), (79, 57)], hx, hy), 'furL', 2)
    pt.px(T([(84, 55)], hx, hy), 'furL', 3)
    pt.px(T([(86, 51), (87, 51)], hx, hy), 'pink', 1)
    # tabby stripes: broken bands down the back of the head + rings on the nape
    strokes = [
        ([(41, 52), (42, 56), (45, 59)], 1.3, 0.5),
        ([(49, 51), (50, 55)], 1.4, 0.6),
        ([(60, 51), (59, 55)], 1.4, 0.6),
        ([(68, 51), (66, 55), (64, 57)], 1.2, 0.5),
    ]
    for pts, r0, r1 in strokes:
        sm, _ = thick_curve(T(pts, hx, hy), r0, r1, 6)
        pt.fill(sm & m, 'furS')
    for (x0, yb, x1, r0) in ((44, 62, 68, 1.5), (45, 68, 67, 1.3)):
        sm, _ = thick_curve(T([(x0, yb + 1), ((x0 + x1) / 2 - 3, yb), ((x0 + x1) / 2 + 4, yb + 1),
                               (x1, yb)], hx, hy), r0, 0.6, 8)
        pt.fill(sm & m, 'furS')
    sm, _ = thick_curve(T([(74, 51), (79, 52), (82, 52)], hx, hy), 1.0, 0.5, 6)
    pt.fill(sm & m, 'furS')
    return pt


def whiskers(p):
    pt = Part('whisk', 'whisk', outline=False)
    hx = p.get('hdx', 0) + p.get('bx', 0)
    hy = p.get('hdy', 0) + p.get('by', 0)
    wk = p.get('whisk', 0)
    for (a, b) in [((86, 55), (97, 52 + wk)), ((87, 57), (99, 57 + wk)), ((86, 59), (96, 63 + wk))]:
        pt.line([(a[0] + hx, a[1] + hy), (b[0] + hx, b[1] + hy)], 'whisk', 2)
    pt.noline[:] = True
    return pt


# =====================================================================================  HAT
HAT_C = (55, 41)


def hat_tr(p):
    hx = p.get('hatdx', p.get('hdx', 0)) + p.get('bx', 0)
    hy = p.get('hatdy', p.get('hdy', 0)) + p.get('by', 0)
    tilt = p.get('tilt', 0.0)
    cx, cy = HAT_C
    c, s = math.cos(tilt), math.sin(tilt)
    def tr(x, y):
        dx, dy = x - cx, y - cy
        return (cx + dx * c - dy * s + hx, cy + dx * s + dy * c + hy)
    return tr


def hat(p):
    pt = Part('hat', 'hat')
    tr = hat_tr(p)
    TP = lambda pts: [tr(*q) for q in pts]
    MP = lambda pts: poly_mask(TP(pts), W, H)
    # ---- brim: ellipse with curled-up sides, right side a bit longer
    rim = []
    for k in range(120):
        t = 2 * math.pi * k / 120
        c, s = math.cos(t), math.sin(t)
        rx = (47 if c > 0 else 45) + (4 if SCALE > 1 else 0)
        x = 55 + rx * c
        ry = 10.0 if SCALE > 1 else 8.0
        y = 42 + ry * s - 7.0 * abs(c) ** 4 + (1.5 * s if s > 0 else 0)
        rim.append((x, y))
    brim = pt.poly(TP(rim), 'hat', 3)
    pt.addlum(brim & MP([(58, 20), (110, 20), (110, 60), (58, 60)]), 1)
    pt.addlum(brim & MP([(6, 38), (34, 40), (36, 53), (6, 50)]), -1)
    pt.addlum(brim & MP([(0, 20), (33, 20), (33, 39), (0, 39)]), -1)
    be = edge(brim, 0, 1)
    pt.addlum(be, -2)
    pt.addlum(shift(be, 0, -1) & brim & ~be, -1)
    if SCALE > 1:   # thick dark underside lip, 3 px
        pt.addlum(shift(be, 0, -2) & brim & ~be, -2)
        pt.addlum(shift(be, 0, -3) & brim & ~be, -1)
    top = edge(brim, 0, -1)
    if SCALE > 1:
        pt.addlum(shift(top, 0, 1) & brim & (XX > tr(60, 0)[0]), 2, hi=5)
    pt.addlum(top & (XX > tr(66, 0)[0]), 2, hi=5)
    pt.addlum(top & (XX < tr(30, 0)[0]), 1, hi=4)
    # ---- crown: tapered, slightly pinched, centre crease; base and band follow the ellipse
    base = arc_y(33, 77, 40, 3.0)
    crown_pts = [(33, 40), (33, 33), (35, 25), (39, 19), (45, 15), (51, 15), (55, 17), (59, 15),
                 (65, 15), (71, 19), (75, 25), (77, 33), (77, 40)]
    crown_pts += [(x, float(base(x))) for x in range(76, 33, -2)]
    if SCALE > 1:   # taller crown with a deeper centre dent
        crown_pts = [(x, y - 5 * max(0, (34 - y)) / 19.0) if y < 34 else (x, y) for x, y in crown_pts]
        crown_pts = [(x, y + (2 if x == 55 else 0)) for x, y in crown_pts]
    crown = pt.poly(TP(crown_pts), 'hat', 2)
    pt.addlum(crown & MP([(31, 46), (31, 30), (36, 20), (43, 16), (40, 26), (39, 46)]), -1)
    pt.addlum(crown & MP([(63, 14), (72, 18), (79, 32), (79, 46), (71, 46), (68, 26)]), 1)
    pt.addlum(crown & MP([(73, 22), (79, 32), (79, 46), (75, 46)]), 1, hi=4)
    ct = edge(crown, 0, -1)
    pt.addlum(ct, 2, hi=5)
    pt.addlum(shift(ct, 0, 1) & crown & ~ct & (XX > tr(46, 0)[0]), 1, hi=4)
    # crease
    pt.line(TP([(55, 18), (55, 22), (54, 27)]), 'hat', 1)
    pt.line(TP([(56, 19), (56, 23)]), 'hat', 3)
    # ---- band, curving with the crown base
    bt = arc_y(33, 77, 32 if SCALE > 1 else 34, 3.0)
    bb = arc_y(33, 77, 38, 3.0)
    bpts = [(x, float(bt(x))) for x in range(31, 80, 2)] + [(x, float(bb(x))) for x in range(79, 30, -2)]
    band = MP(bpts) & crown
    pt.fill(band, 'band', 2)
    pt.fill(band & MP([(64, 28), (82, 28), (82, 48), (64, 48)]), lum=3)
    pt.fill(edge(band, 0, -1) & (XX > tr(58, 0)[0]), 'band', 4)
    pt.fill(edge(band, 0, -1) & (XX <= tr(58, 0)[0]), 'band', 3)
    cx_, cy_ = [int(round(v)) for v in tr(70, 38)]
    pt.px([(cx_, cy_), (cx_ + 1, cy_)], 'brass', 4)
    pt.px([(cx_, cy_ + 1), (cx_ + 1, cy_ + 1)], 'brass', 2)
    # worn patch with stitches
    patch = MP([(40, 24), (46, 23), (47, 29), (41, 30)]) & crown
    pt.fill(patch, 'hat', 3)
    pt.fill(edge(patch, 0, 1) | edge(patch, -1, 0), 'hat', 0)
    # notch in brim edge
    pt.erase(MP([(22, 49), (25, 48), (24, 51)]) & brim)
    return pt


# =====================================================================================  EARS
EAR_R = {
    'up':     [(63, 22), (66, 13), (70, 6), (74, 2), (76, 3), (77, 12), (76, 22)],
    'twitch': [(63, 22), (67, 14), (73, 7), (79, 4), (80, 8), (78, 15), (76, 22)],
    'back':   [(63, 22), (66, 14), (69, 8), (72, 5), (74, 6), (76, 13), (76, 22)],
    'flat':   [(63, 23), (70, 19), (79, 16), (86, 16), (85, 19), (78, 22), (76, 23)],
    'perk':   [(63, 22), (65, 12), (68, 4), (71, 0), (74, 1), (76, 10), (76, 22)],
}
EAR_L = {
    'up':     [(37, 25), (36, 16), (37, 8), (39, 5), (43, 9), (48, 17), (49, 21)],
    'twitch': [(37, 25), (35, 17), (34, 9), (36, 6), (41, 10), (48, 17), (49, 21)],
    'back':   [(37, 25), (37, 17), (39, 10), (41, 8), (44, 11), (48, 17), (49, 21)],
    'flat':   [(37, 25), (31, 22), (25, 21), (26, 18), (34, 18), (46, 18), (49, 21)],
    'perk':   [(37, 25), (37, 15), (39, 6), (41, 3), (45, 8), (48, 16), (49, 21)],
}


def ears(p):
    if p.get('ears_head'):          # ears stay on the head while the hat is knocked about
        q = dict(p); q['hatdx'] = p.get('hdx', 0); q['hatdy'] = p.get('hdy', 0); q['tilt'] = p.get('tilt', 0) * .3
        tr = hat_tr(q)
    else:
        tr = hat_tr(p)
    out = []
    for side, table in (('R', EAR_R), ('L', EAR_L)):
        st = p.get('ear' + side, 'up')
        pt = Part('ear' + side, 'ear' + side)
        raw = table[st]
        if SCALE > 1:
            bx_ = (raw[0][0] + raw[-1][0]) / 2; by_ = (raw[0][1] + raw[-1][1]) / 2
            raw = [(bx_ + (x - bx_) * .62, by_ + (y - by_) * .62 - 4) for x, y in raw]
        pts = [tr(*q) for q in raw]
        m = pt.poly(pts, 'fur', 2 if side == 'R' else 1)
        pt.addlum(edge(m, 1, 0) | edge(m, 0, -1), 1)
        xs = [q[0] for q in pts]; ys = [q[1] for q in pts]
        top, bot = min(ys), max(ys)
        hgt = bot - top
        if st != 'flat':
            for f in (0.40, 0.68):
                yb = top + hgt * f
                pt.fill(m & (np.abs(YY - yb + (XX - xs[0]) * 0.25) < 0.9), 'furS')
            pt.fill(m & (YY < top + 3), 'furS')
            if side == 'R':
                pt.fill(m & edge(m, -1, 0) & (YY > top + 4) & (YY < bot - 3), 'furL', 1)
        else:
            pt.fill(m & (XX > max(xs) - 4) if side == 'R' else m & (XX < min(xs) + 4), 'furS')
        hole = Part('hole' + side, 'hat', outline=False)
        b0, b1 = pts[0], pts[-1]
        midx, midy = (b0[0] + b1[0]) / 2, (b0[1] + b1[1]) / 2
        hole.line([(b0[0] - 1, b0[1]), (midx, midy + 2), (b1[0] + 1, b1[1])], 'ink2', 0)
        hole.line([(b0[0] - 1, b0[1] + 1), (midx, midy + 3), (b1[0] + 1, b1[1] + 1)], 'hat', 4)
        out += [hole, pt]
    return out
