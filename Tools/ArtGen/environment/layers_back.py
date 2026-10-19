"""Back layers (v2 360x640): sky, sun, mesas_far, mesas_near, town_far."""
import math
import numpy as np
from envlib import Canvas, W, H, VPX, VPY, col, rng

SUN = (276, 124)

# ----------------------------------------------------------------------------------- SKY
SKY_BANDS = [('envSky6', 0), ('sky0', 58), ('sky1', 122), ('sky2', 182), ('sky3', 230),
             ('sky4', 262), ('sky5', 282), ('haze', 294)]


def make_sky():
    cv = Canvas()
    for i, (c, y0) in enumerate(SKY_BANDS):
        y1 = SKY_BANDS[i + 1][1] - 1 if i + 1 < len(SKY_BANDS) else H - 1
        cv.rect(0, y0, W - 1, y1, c)
    for i in range(1, len(SKY_BANDS)):
        y = SKY_BANDS[i][1]
        d = 6 if i < 5 else 4
        cv.checker(0, y - d, W - 1, y - d // 2 - 1, SKY_BANDS[i][0], parity=0)   # sparse-ish
        for yy in range(y - d // 2, y):
            for x in range(W):
                if (x + yy) & 1 == 0 or (x % 2 == 0 and yy % 2 == 0):
                    cv.px(x, yy, SKY_BANDS[i][0])
        cv.checker(0, y, W - 1, y + 1, SKY_BANDS[i - 1][0], parity=1)
    return cv


# ----------------------------------------------------------------------------------- SUN
def make_sun():
    cx, cy = SUN
    a = np.zeros((H, W, 4), np.uint8)

    def setp(x, y, c, alpha):
        if 0 <= x < W and 0 <= y < H:
            r, g, b, _ = col(c)
            a[y, x] = (r, g, b, alpha)
    for y in range(cy - 46, cy + 47):
        for x in range(cx - 46, cx + 47):
            d = math.hypot(x - cx, y - cy)
            if d <= 7.6:
                setp(x, y, 'sun0', 255)
            elif d <= 9.0:
                setp(x, y, 'sun1', 255)
            elif d <= 12.5:
                setp(x, y, 'sun0', 191)
            elif d <= 18.0:
                setp(x, y, 'sun1', 128)
            elif d <= 27.0:
                setp(x, y, 'haze', 64)
            elif d <= 40.0 and (x + y) & 1 == 0:
                setp(x, y, 'haze', 64)
    for i in range(13, 24):
        for (x, y) in ((cx + i, cy), (cx - i, cy), (cx, cy + i), (cx, cy - i)):
            setp(x, y, 'sun0', 128 if i < 19 else 64)
    for i in range(9, 15):
        for (x, y) in ((cx + i, cy + i), (cx - i, cy - i), (cx + i, cy - i), (cx - i, cy + i)):
            setp(x, y, 'sun0', 64)
    cv = Canvas(); cv.a = a
    return cv


# ----------------------------------------------------------------------------------- helpers
def _interp(prof, x):
    for i in range(len(prof) - 1):
        (xa, ya), (xb, yb) = prof[i], prof[i + 1]
        if xa <= x <= xb:
            return ya if xb == xa else ya + (x - xa) * (yb - ya) / (xb - xa)
    return None


def _fill(cv, prof, base, c):
    xs = [p[0] for p in prof]
    for x in range(max(0, int(min(xs))), min(W - 1, int(max(xs))) + 1):
        y = _interp(prof, x + 0.5)
        if y is not None:
            cv.rect(x, int(round(y)), x, base, c)


def _top(cv, x):
    nz = np.nonzero(cv.a[:, x, 3])[0]
    return int(nz[0]) if len(nz) else None


def butte(prof, base, ramp, strata_step=7, gullies=(), seed=1, haze_from=None, haze_cols=None):
    """Draw a lit-from-upper-right butte. ramp = (top, lit, mid, shade, deep, deepest)."""
    top_c, lit, mid, shade, deep, deepest = ramp
    cv = Canvas()
    _fill(cv, prof, base, mid)
    r = rng(seed)
    xs = [p[0] for p in prof]
    x0, x1 = max(0, int(min(xs))), min(W - 1, int(max(xs)))
    tops = {x: _top(cv, x) for x in range(x0, x1 + 1)}
    for x in range(x0, x1 + 1):
        t = tops[x]
        if t is None:
            continue
        yl = _interp(prof, x - 0.5); yr = _interp(prof, x + 1.5)
        slope = (yr - yl) if (yl is not None and yr is not None) else 0
        if slope < -0.9:                 # left-facing steep side: shade
            for y in range(t, base + 1):
                cv.px(x, y, shade if y - t < 50 else deep)
            cv.px(x, t, mid)
        elif slope > 0.9:                # right-facing slope: lit
            cv.rect(x, t, x, base, lit)
            cv.px(x, t, top_c)
        else:
            cv.px(x, t, top_c); cv.px(x, t + 1, top_c)
            cv.px(x, t + 2, lit)
    # strata: horizontal bands following the top contour loosely (stepped, clean)
    for x in range(x0, x1 + 1):
        t = tops[x]
        if t is None:
            continue
        for y in range(t + 4, base + 1):
            c = cv.get(x, y)
            band = (y - 0) // strata_step
            if c == col(mid):
                if (y % strata_step) == 0:
                    cv.px(x, y, shade)
                elif (y % strata_step) == 1:
                    cv.px(x, y, lit) if band % 2 == 0 else None
                elif band % 3 == 0:
                    cv.px(x, y, lit)
            elif c == col(lit) and (y % strata_step) == 0:
                cv.px(x, y, mid)
    # vertical gullies (shade line + deep core + lit right lip)
    for (gx, gl, w) in gullies:
        t = tops.get(gx)
        if t is None:
            continue
        for y in range(t + 3, min(base, t + gl)):
            cv.px(gx, y, shade)
            if w > 1:
                cv.px(gx - 1, y, deep if y < t + gl * 0.7 else shade)
            cv.px(gx + 1, y, lit) if (y - t) % 9 else None
        cv.px(gx, t + 2, deep)
    # haze toward the base (atmospheric) using hazy ramp
    if haze_from is not None:
        h1, h2 = haze_cols
        for y in range(haze_from, base + 1):
            for x in range(x0, x1 + 1):
                if not cv.opaque(x, y):
                    continue
                if y >= haze_from + 8:
                    cv.px(x, y, h2 if (x + y) & 1 or y >= haze_from + 12 else h1)
                elif y >= haze_from + 4 and (x + y) & 1 == 0:
                    cv.px(x, y, h1)
    return cv


def make_mesas_far():
    cv = Canvas()
    base = 302
    prof = [(0, 262), (20, 258), (40, 259), (56, 252), (78, 252), (86, 258), (104, 266),
            (130, 268), (150, 262), (160, 256), (196, 255), (204, 260), (216, 264), (228, 258),
            (236, 246), (258, 245), (264, 254), (284, 262), (304, 257), (316, 250), (342, 250),
            (352, 256), (360, 258)]
    ramp = ('rockFar0', 'rockFar0', 'rockFar1', 'rockFar2', 'rockFar3', 'rockFar3')
    b = butte(prof, base, ramp, strata_step=6, gullies=[(170, 10, 1), (186, 12, 1), (244, 14, 1),
                                                         (252, 10, 1), (326, 12, 1), (64, 9, 1)],
              seed=2, haze_from=276, haze_cols=('rockFar0', 'haze'))
    cv.blit(b, 0, 0)
    return cv


def make_mesas_near():
    cv = Canvas()
    base = 300
    ramp = ('rock0', 'rock1', 'rock2', 'rock3', 'rock4', 'envRockDeep')
    # big left butte with a spire on its right shoulder
    profL = [(60, 300), (74, 270), (88, 238), (96, 206), (102, 188), (106, 182), (112, 180),
             (170, 179), (176, 182), (180, 186), (186, 188), (192, 186), (196, 168), (200, 164),
             (204, 164), (206, 170), (208, 190), (212, 214), (220, 240), (234, 262), (252, 284),
             (262, 300)]
    L = butte(profL, base, ramp, strata_step=7,
              gullies=[(116, 40, 2), (126, 56, 1), (139, 30, 2), (150, 62, 1), (161, 44, 2),
                       (172, 34, 1), (184, 24, 1), (202, 46, 1)],
              seed=4, haze_from=272, haze_cols=('rockFar0', 'rockFar0'))
    cv.blit(L, 0, 0)
    # right mesa (stepped table), partly behind the bank
    profR = [(214, 300), (222, 282), (230, 262), (236, 236), (240, 216), (244, 206), (248, 202),
             (284, 201), (290, 206), (300, 208), (318, 207), (326, 200), (360, 198)]
    R = butte(profR, base, ramp, strata_step=7,
              gullies=[(256, 40, 2), (268, 30, 1), (279, 52, 2), (296, 28, 1), (310, 44, 2),
                       (336, 36, 1), (350, 48, 2)],
              seed=5, haze_from=272, haze_cols=('rockFar0', 'rockFar0'))
    cv.blit(R, 0, 0)
    # distant left shoulder (behind the hotel)
    profLL = [(0, 300), (0, 214), (30, 212), (44, 222), (60, 244), (72, 270), (80, 300)]
    LL = butte(profLL, base, ramp, strata_step=7, gullies=[(14, 40, 2), (28, 30, 1)], seed=6,
               haze_from=272, haze_cols=('rockFar0', 'rockFar0'))
    cv.blit(LL, 0, 0)
    return cv


# ----------------------------------------------------------------------------------- TOWN FAR
def make_town_far():
    """Church, water tower and small hazy facades at the street's end (no outlines, hazy ramp)."""
    cv = Canvas()
    g = 300
    # low facades across the street end
    for (x0, x1, top, c, sh, roof) in [(128, 141, 280, 'rockFar0', 'rockFar1', 'flat'),
                                        (176, 189, 285, 'haze', 'cloud2', 'flat'),
                                        (189, 202, 281, 'rockFar0', 'rockFar1', 'step'),
                                        (202, 213, 286, 'cloud1', 'cloud2', 'flat'),
                                        (213, 226, 282, 'rockFar0', 'rockFar1', 'flat'),
                                        (268, 282, 279, 'rockFar0', 'rockFar1', 'step')]:
        cv.rect(x0, top, x1, g, c)
        cv.hline(x0, x1, top, 'cloud1' if c != 'cloud1' else 'haze')
        if roof == 'step':
            cv.rect(x0 + 3, top - 3, x1 - 3, top - 1, c); cv.hline(x0 + 3, x1 - 3, top - 3, 'cloud1')
        cv.rect(x0, top + 1, x0 + 1, g, sh)
        cv.hline(x0, x1, top + 2, sh)
        for wx in range(x0 + 3, x1 - 2, 4):
            cv.rect(wx, top + 5, wx + 1, top + 7, 'rockFar2')
        cv.rect((x0 + x1) // 2, g - 4, (x0 + x1) // 2 + 1, g, 'rockFar3')
        cv.hline(x0, x1, top + 9, 'rockFar1')          # porch line

    # church
    cx = 160
    cv.rect(cx - 11, 270, cx + 11, g, 'cloud1')
    cv.rect(cx - 11, 270, cx - 7, g, 'cloud2')
    for i in range(0, 12):
        cv.hline(cx - 12 + i, cx + 12 - i, 269 - i, 'rockFar1')
        cv.px(cx + 12 - i, 269 - i, 'rockFar0'); cv.px(cx - 12 + i, 269 - i, 'rockFar2')
    for i in range(1, 10):
        cv.hline(cx - 10 + i, cx + 10 - i, 270 - i, 'haze')
    cv.rect(cx - 4, 240, cx + 4, 262, 'haze')
    cv.rect(cx - 4, 240, cx - 2, 262, 'cloud2')
    cv.rect(cx - 1, 245, cx + 1, 250, 'rockFar2'); cv.px(cx, 245, 'rockFar1')
    cv.hline(cx - 5, cx + 5, 240, 'rockFar1')
    for i in range(0, 12):
        w = max(0, 4 - i // 3)
        cv.hline(cx - w, cx + w, 239 - i, 'rockFar1')
        cv.px(cx + w, 239 - i, 'rockFar0')
    cv.vline(cx, 221, 227, 'rockFar2'); cv.hline(cx - 2, cx + 2, 223, 'rockFar2')
    cv.rect(cx - 2, 291, cx + 2, g, 'rockFar2'); cv.hline(cx - 1, cx + 1, 290, 'rockFar2')
    cv.rect(cx - 1, 276, cx + 1, 278, 'rockFar1'); cv.px(cx, 275, 'rockFar1')
    for wx in (cx - 8, cx + 6):
        cv.rect(wx, 280, wx + 1, 284, 'rockFar2' if wx < cx else 'rockFar1')
    # fence in front of the church
    for x in range(cx - 18, cx + 19, 3):
        cv.vline(x, 295, g, 'rockFar1')
    cv.hline(cx - 18, cx + 18, 296, 'rockFar1')

    # water tower
    tx = 248
    for y in range(256, g + 1):
        sp = (y - 256) // 7
        for lx in (tx - 8 - sp, tx + 8 + sp):
            cv.px(lx, y, 'rockFar2'); cv.px(lx + (1 if lx > tx else -1), y, 'rockFar3') if y > 262 else None
    for yb in (266, 280, 294):
        sp = (yb - 256) // 7
        cv.hline(tx - 8 - sp, tx + 8 + sp, yb, 'rockFar2')
    for i in range(12):
        cv.px(tx - 8 + i + 1, 267 + i, 'rockFar1'); cv.px(tx + 8 - i - 1, 267 + i, 'rockFar1')
    cv.rect(tx - 11, 238, tx + 11, 255, 'rockFar1')
    cv.rect(tx - 11, 238, tx - 8, 255, 'rockFar2')
    cv.rect(tx + 6, 239, tx + 10, 254, 'rockFar0')
    for yb in (243, 249, 255):
        cv.hline(tx - 11, tx + 11, yb, 'rockFar2')
    for i in range(8):
        cv.hline(tx - 12 + i, tx + 12 - i, 237 - i, 'rockFar2')
        cv.px(tx + 12 - i, 237 - i, 'rockFar0'); cv.px(tx + 11 - i, 237 - i, 'rockFar0')
    cv.vline(tx, 226, 229, 'rockFar2')
    cv.vline(tx + 3, 256, 300, 'rockFar2')   # pipe
    # telegraph poles
    for (px_, top) in [(296, 262), (288, 270), (282, 277)]:
        cv.vline(px_, top, g, 'rockFar2')
        cv.hline(px_ - 3, px_ + 3, top + 2, 'rockFar2')
    cv.line(282, 279, 296, 264, 'rockFar1')
    # windmill (static) far left
    wx = 118
    cv.vline(wx, 262, g, 'rockFar2'); cv.line(wx - 4, g, wx, 262, 'rockFar2'); cv.line(wx + 4, g, wx, 262, 'rockFar2')
    for (dx, dy) in ((0, -6), (5, 0), (0, 5), (-5, 0)):
        cv.line(wx, 262, wx + dx, 262 + dy, 'rockFar1')
    cv.px(wx, 262, 'rockFar3')
    return cv
