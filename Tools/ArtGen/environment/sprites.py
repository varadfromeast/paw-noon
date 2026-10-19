"""Foreground layer, clouds and animated strips (lantern, sign_swing, tumbleweed, vulture, flag)."""
import math
import numpy as np
from envlib import Canvas, W, H, col, draw_text, text_width, GLYPHS, strip, np_outline


# ----------------------------------------------------------------------------- outline helper
def outline(cv, c='ink2', lit_c=None):
    return np_outline(cv, c)


def _outline_slow(cv, c='ink2', lit_c=None):
    """Add a 1px outline around opaque pixels (selective: top/right edges use lit_c if given)."""
    a = cv.a[..., 3] > 0
    out = []
    h, w = a.shape
    for y in range(h):
        for x in range(w):
            if a[y, x]:
                continue
            nb = [(x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)]
            hit = [(nx, ny) for nx, ny in nb if 0 <= nx < w and 0 <= ny < h and a[ny, nx]]
            if hit:
                # pixel above/left of the shape (lit side) -> lit_c
                top_side = any(ny == y + 1 for nx, ny in hit) or any(nx == x - 1 for nx, ny in hit)
                out.append((x, y, lit_c if (lit_c and top_side and not any(ny == y - 1 for nx, ny in hit)) else c))
    for x, y, cc in out:
        cv.px(x, y, cc)


# ----------------------------------------------------------------------------- foreground
def _sage(cv, cx, by, rw, rh, seed):
    """Dry sage/tumbleweed bush: clustered twig blobs, lit top-right."""
    r = np.random.RandomState(seed)
    tmp = Canvas(W, H)
    blobs = []
    for i in range(int(rw * rh / 14)):
        a = r.uniform(0, math.pi)
        d = r.uniform(0, 1) ** 0.6
        x = cx + math.cos(a) * rw * d
        y = by - math.sin(a) * rh * d
        blobs.append((x, y, r.uniform(2.0, 3.6)))
    for (x, y, s) in blobs:
        for yy in range(int(y - s), int(y + s) + 1):
            for xx in range(int(x - s), int(x + s) + 1):
                if (xx - x) ** 2 + ((yy - y) * 1.2) ** 2 <= s * s and yy <= by:
                    tmp.px(xx, yy, 'sand3')
    # shading: lit on top-right of each blob, dark bottom-left
    a = tmp.a[..., 3] > 0
    for (x, y, s) in blobs:
        tmp.px(int(x + s * 0.4), int(y - s * 0.5), 'sand2')
        tmp.px(int(x + s * 0.4) + 1, int(y - s * 0.5), 'sand1')
        tmp.px(int(x - s * 0.5), int(y + s * 0.4), 'sand4')
    for y in range(H):
        for x in range(W):
            if a[y, x] and (y > by - 3 or (not a[y - 1, x] and x % 3 == 0)):
                if y > by - 3:
                    tmp.px(x, y, 'sand4' if (x + y) & 1 else 'sand5')
    outline(tmp, 'ink2', 'sand4')
    # twig spikes poking out of the top
    for i in range(int(rw / 3)):
        x = int(cx - rw + 2 + i * 6 + r.randint(0, 3))
        col_top = np.nonzero(tmp.a[:, x, 3])[0] if 0 <= x < W else []
        if len(col_top):
            t = col_top[0]
            tmp.vline(x, t - 2, t - 1, 'sand4')
            tmp.px(x + 1, t - 3, 'sand4')
    cv.blit(tmp, 0, 0)


def _rock(cv, x0, y0, w, h):
    tmp = Canvas(W, H)
    for yy in range(h):
        t = yy / max(1, h - 1)
        inset = int(round((1 - math.sin(min(1, t * 1.4 + 0.15) * math.pi / 2)) * w * 0.35))
        tmp.hline(x0 + inset, x0 + w - 1 - inset // 2, y0 + yy, 'rock3')
    a = tmp.a[..., 3] > 0
    for y in range(y0, y0 + h):
        xs = np.nonzero(a[y])[0]
        if not len(xs):
            continue
        xa, xb = xs[0], xs[-1]
        # lit top / right faces
        for x in range(xa, xb + 1):
            if not a[y - 1, x]:
                tmp.px(x, y, 'rock1'); tmp.px(x, y + 1, 'rock2')
        tmp.px(xb, y, 'rock2')
        tmp.px(xa, y, 'rock4')
    tmp.hline(x0 + w // 4, x0 + w - 2, y0 + h - 1, 'rock4')
    # crack
    tmp.line(x0 + w // 2, y0 + 2, x0 + w // 2 - 2, y0 + h // 2, 'rock4')
    outline(tmp, 'ink2', 'rock3')
    cv.blit(tmp, 0, 0)


def _cactus(cv, x, by, hgt):
    """Small saguaro arm cactus, lit right side, ink outline."""
    tmp = Canvas(W, H)
    tw = 7
    tmp.rect(x, by - hgt, x + tw - 1, by, 'green2')
    tmp.rect(x + 1, by - hgt - 1, x + tw - 2, by - hgt - 1, 'green2')
    # arms
    tmp.rect(x - 5, by - hgt + 12, x - 1, by - hgt + 16, 'green2')
    tmp.rect(x - 5, by - hgt + 4, x - 2, by - hgt + 15, 'green2')
    tmp.rect(x + tw, by - hgt + 18, x + tw + 4, by - hgt + 21, 'green2')
    tmp.rect(x + tw + 1, by - hgt + 9, x + tw + 4, by - hgt + 20, 'green2')
    a = tmp.a[..., 3] > 0
    for yy in range(H):
        xs = np.nonzero(a[yy])[0]
        if not len(xs):
            continue
    # ribs and light
    for yy in range(by - hgt - 1, by + 1):
        if a[yy, x + tw - 2]:
            tmp.px(x + tw - 2, yy, 'green1')
            tmp.px(x + tw - 3, yy, 'green0' if yy < by - hgt + 6 else 'green1')
        tmp.px(x + 2, yy, 'green3') if a[yy, x + 2] else None
        tmp.px(x + 1, yy, 'green3') if a[yy, x + 1] else None
    for yy in range(by - hgt + 4, by - hgt + 16):
        tmp.px(x - 3, yy, 'green1') if a[yy, x - 3] else None
    for yy in range(by - hgt + 9, by - hgt + 21):
        tmp.px(x + tw + 3, yy, 'green1') if a[yy, x + tw + 3] else None
    # spines
    for yy in range(by - hgt + 2, by - 1, 4):
        tmp.px(x + tw, yy, 'cream1'); tmp.px(x - 1, yy + 2, 'cream2')
    tmp.px(x + 3, by - hgt - 2, 'paintRed'); tmp.px(x + 4, by - hgt - 2, 'red0')   # flower
    outline(tmp, 'ink2', 'green3')
    cv.blit(tmp, 0, 0)


def _wheel(cv, cx, cy, R):
    """Wagon wheel, frontal-ish (slightly squashed), lit from the upper right, ink outline."""
    tmp = Canvas(W, H)
    ry = R * 0.94
    for y in range(int(cy - ry - 2), int(cy + ry + 3)):
        for x in range(int(cx - R - 2), int(cx + R + 3)):
            d = math.hypot((x + 0.5 - cx) / R, (y + 0.5 - cy) / ry)
            ang = math.atan2(y + 0.5 - cy, x + 0.5 - cx)
            lit = math.cos(ang + math.pi / 4)      # upper-right = lit
            if 0.86 <= d <= 1.0:
                tmp.px(x, y, 'steel1' if lit > 0.55 else ('steel2' if lit > -0.3 else 'steel3'))
            elif 0.72 <= d < 0.86:
                tmp.px(x, y, 'wood0' if lit > 0.5 else ('wood1' if lit > -0.2 else 'wood3'))
                if abs(d - 0.79) < 0.03 and lit < 0:
                    tmp.px(x, y, 'wood3')
    for i in range(12):
        a = i * math.pi / 6 + 0.13
        for t in np.linspace(0.16, 0.73, 40):
            x = cx + math.cos(a) * R * t; y = cy + math.sin(a) * ry * t
            lit = math.cos(a + math.pi / 4)
            tmp.px(int(x), int(y), 'wood1' if lit > 0.3 else 'wood2')
            tmp.px(int(x) + 1, int(y), 'wood3')
    for y in range(int(cy - 7), int(cy + 8)):
        for x in range(int(cx - 7), int(cx + 8)):
            d = math.hypot(x + 0.5 - cx, y + 0.5 - cy)
            if d <= 6.5:
                tmp.px(x, y, 'wood2' if d > 4.5 else ('steel2' if d > 2.5 else 'steel3'))
    tmp.px(int(cx) + 2, int(cy) - 3, 'steel0'); tmp.px(int(cx) + 3, int(cy) - 2, 'steel1')
    np_outline(tmp, 'ink')
    cv.blit(tmp, 0, 0)


def _fg_barrel(cv, x0, y0, w, h):
    tmp = Canvas(W, H)
    for yy in range(h):
        t = yy / (h - 1)
        bulge = int(round(math.sin(t * math.pi) * (w // 10)))
        xa, xb = x0 + w // 10 - bulge, x0 + w - 1 - w // 10 + bulge
        ww = xb - xa
        for x in range(xa, xb + 1):
            u = (x - xa) / max(1, ww)
            c = 'wood3' if u < 0.12 else ('wood2' if u < 0.45 else ('wood1' if u < 0.8 else ('wood0' if u < 0.92 else 'wood1')))
            tmp.px(x, y0 + yy, c)
        for sx in range(1, 6):
            xs = xa + int(ww * sx / 6)
            if (yy * 7 + sx * 13) % 23 != 0:
                tmp.px(xs, y0 + yy, 'wood3' if sx < 4 else 'wood2')
    for by in (int(h * 0.14), int(h * 0.2), int(h * 0.8), int(h * 0.86)):
        t = by / (h - 1)
        bulge = int(round(math.sin(t * math.pi) * (w // 10)))
        xa, xb = x0 + w // 10 - bulge, x0 + w - 1 - w // 10 + bulge
        tmp.hline(xa, xb, y0 + by, 'steel3')
        tmp.hline(xa + (xb - xa) * 2 // 3, xb - 1, y0 + by, 'steel2')
        tmp.px(xb - 3, y0 + by, 'steel0')
    # top lid ellipse
    xa, xb = x0 + w // 10, x0 + w - 1 - w // 10
    for i in range(4):
        tmp.hline(xa + (3 - i) * 2, xb - (3 - i) * 2, y0 - 4 + i, 'wood1' if i < 3 else 'wood3')
    tmp.hline(xa + 6, xb - 6, y0 - 4, 'envWoodHi')
    tmp.hline(xa + 2, xb - 2, y0 - 2, 'wood0')
    np_outline(tmp, 'ink')
    cv.blit(tmp, 0, 0)


def _fg_crate(cv, x0, y0, w, h):
    tmp = Canvas(W, H)
    tmp.rect(x0, y0, x0 + w - 1, y0 + h - 1, 'wood1')
    for yy in range(y0 + 1, y0 + h - 1):
        if (yy - y0) % 7 == 6:
            tmp.hline(x0, x0 + w - 1, yy, 'wood3')
        elif (yy - y0) % 7 == 0:
            tmp.hline(x0, x0 + w - 1, yy, 'wood0')
    tmp.frame(x0, y0, x0 + w - 1, y0 + h - 1, 'wood2')
    tmp.frame(x0 + 1, y0 + 1, x0 + w - 2, y0 + h - 2, 'wood2')
    tmp.hline(x0, x0 + w - 1, y0, 'envWoodHi')
    tmp.vline(x0 + w - 1, y0, y0 + h - 1, 'wood0')
    for i in range(2, w - 2):
        yy = y0 + 2 + int((i - 2) * (h - 5) / max(1, w - 5))
        tmp.px(x0 + i, yy, 'wood3'); tmp.px(x0 + i, yy + 1, 'wood2')
    for (nx, ny) in ((x0 + 3, y0 + 3), (x0 + w - 4, y0 + 3), (x0 + 3, y0 + h - 4), (x0 + w - 4, y0 + h - 4)):
        tmp.px(nx, ny, 'wood5')
    np_outline(tmp, 'ink')
    cv.blit(tmp, 0, 0)


def make_foreground():
    """Corner clutter. Hero area x 14..214, y 340..640 and the bandit zone stay empty."""
    cv = Canvas()
    # ground shadow pool under the clutter
    for y in range(560, 640):
        for x in range(236, 360):
            if ((x - 330) / 110.0) ** 2 + ((y - 640) / 60.0) ** 2 < 1 and (x + y) & 1 == 0:
                cv.px(x, y, 'envSandShade')
    _wheel(cv, 322, 548, 40)
    _fg_crate(cv, 250, 590, 40, 34)
    _fg_barrel(cv, 302, 566, 46, 66)
    _sage(cv, 248, 639, 30, 26, 5)
    _rock(cv, 222, 618, 26, 20)
    _cactus(cv, 342, 610, 44)
    for (x, y) in ((230, 600), (236, 606), (244, 633), (226, 636)):
        cv.rect(x, y, x + 2, y + 1, 'sand3'); cv.px(x + 2, y, 'sand1'); cv.hline(x - 1, x + 1, y + 2, 'sand4')
    # bottom-left sliver (left of the hero cell)
    _rock(cv, 0, 614, 13, 22)
    _sage(cv, 4, 639, 9, 18, 9)
    cv.a[340:, 14:215] = 0
    return cv


# ----------------------------------------------------------------------------- clouds
def make_cloud(w, h, bumps, seed):
    """Soft cumulus: overlapping circles, flat base, 4-tone shading (top-right lit)."""
    r = np.random.RandomState(seed)
    cv = Canvas(w, h)
    circles = []
    base = h - 2
    for (cx, cy, rad) in bumps:
        circles.append((cx, cy, rad))
    def inside(x, y):
        for (cx, cy, rad) in circles:
            if (x - cx) ** 2 + (y - cy) ** 2 <= rad * rad and y <= base:
                return True
        return False
    for y in range(h):
        for x in range(w):
            if inside(x + 0.5, y + 0.5):
                cv.px(x, y, 'cloud1')
    # light from upper right: cloudW where the sun-side shifted copy is outside
    for y in range(h):
        for x in range(w):
            if not cv.opaque(x, y):
                continue
            # distance to edge toward light
            if not inside(x + 2.5, y - 1.5) or not inside(x + 1.5, y - 2.5):
                cv.px(x, y, 'cloudW')
            # lower shading
            if y >= base - 2:
                cv.px(x, y, 'cloud2')
            elif not inside(x - 2.5, y + 2.5) and y > h * 0.45:
                cv.px(x, y, 'cloud2')
            if y == base and (x + y) & 1:
                cv.px(x, y, 'cloud3')
    # inner bump creases (soft shadow arcs)
    for (cx, cy, rad) in circles[1:]:
        for a in np.linspace(math.pi * 0.6, math.pi * 1.0, 8):
            x = int(cx + math.cos(a) * rad); y = int(cy + math.sin(a) * rad * 0.2 + rad * 0.15)
            if cv.get(x, y) == col('cloud1') and y < base - 2:
                cv.px(x, y, 'cloud2')
    return cv


def make_clouds():
    a = make_cloud(128, 50, [(26, 34, 15), (48, 24, 20), (74, 20, 19), (98, 28, 16), (64, 34, 18),
                             (14, 40, 9), (114, 38, 10), (86, 36, 14)], 1)
    b = make_cloud(92, 38, [(20, 26, 12), (40, 17, 15), (62, 20, 14), (78, 28, 10), (50, 28, 13)], 2)
    c = make_cloud(58, 24, [(14, 16, 8), (28, 11, 10), (42, 13, 9), (50, 18, 6)], 3)
    d = make_cloud(36, 14, [(9, 10, 5), (18, 7, 7), (27, 9, 5)], 4)
    return {'cloud_a': a, 'cloud_b': b, 'cloud_c': c, 'cloud_d': d}


# ----------------------------------------------------------------------------- lantern
def make_lantern():
    """9x15 cell, 4 frames, pivot (4,0) = hanging hook."""
    frames = []
    flames = [
        ["...y.", "..yo.", ".oyw.", ".oyy.", "..o.."],
        ["..y..", "..yo.", ".oyw.", ".oyy.", "..o.."],
        [".y...", ".oy..", ".wyo.", ".oyy.", "..o.."],
        ["..y..", ".oy..", ".oyw.", ".yyo.", "..o.."],
    ]
    glow = ['sun2', 'sun2', 'sigOrange', 'sun2']
    cmap = {'y': 'sigYellow', 'o': 'sigOrange', 'w': 'sun0'}
    for i in range(4):
        cv = Canvas(9, 15)
        cv.vline(4, 0, 1, 'ink'); cv.px(3, 1, 'ink'); cv.px(5, 1, 'ink')
        cv.hline(3, 5, 2, 'brass2'); cv.px(5, 2, 'brass1')
        cv.hline(2, 6, 3, 'brass2'); cv.px(6, 3, 'brass0')
        cv.vline(1, 4, 11, 'brass3'); cv.vline(7, 4, 11, 'brass1')
        cv.rect(2, 4, 6, 11, glow[i])
        for yy, row in enumerate(flames[i]):
            for xx, v in enumerate(row):
                if v in cmap:
                    cv.px(2 + xx, 5 + yy, cmap[v])
        cv.vline(4, 11, 11, 'brass3')
        cv.hline(1, 7, 12, 'brass2'); cv.px(7, 12, 'brass1')
        cv.hline(2, 6, 13, 'brass3')
        np_outline(cv, 'ink')
        frames.append(cv.image())
    return strip(frames)


# ----------------------------------------------------------------------------- sign_swing (GUNS)
def make_sign_swing():
    """Hanging GUNS board on two parallel chains (stays level, swings in a small arc).
    Cell 60x36, 6 frames. Pivot (30,0) = centre of the bracket arm above."""
    cw, ch = 60, 36
    frames = []
    for dx in (0, 1, 2, 1, 0, -1):
        cv = Canvas(cw, ch)
        by0 = 8
        bw, bh = 50, 26
        bx0 = (cw - bw) // 2 + dx
        for hx in (12, 47):
            for yy in range(0, by0):
                x = int(round(hx + dx * yy / (by0 - 1)))
                cv.px(x, yy, 'steel2' if yy % 2 else 'steel3')
                if yy % 2 == 0:
                    cv.px(x + 1, yy, 'ink')
        x0, y0, x1, y1 = bx0, by0, bx0 + bw - 1, by0 + bh - 1
        cv.rect(x0, y0, x1, y1, 'paintCream')
        for yy in range(y0 + 3, y1):
            if (yy - y0) % 6 == 0:
                cv.hline(x0 + 2, x1 - 2, yy, 'paintCreamD')
        cv.frame(x0 + 1, y0 + 1, x1 - 1, y1 - 1, 'paintTeal')
        cv.frame(x0 + 2, y0 + 2, x1 - 2, y1 - 2, 'paintTealD')
        cv.hline(x0 + 1, x1 - 1, y0 + 1, 'envTealHi')
        cv.vline(x1 - 1, y0 + 1, y1 - 1, 'envTealHi')
        # GUNS lettering (7px) via local glyphs
        G = {'G': ["01111", "10000", "10000", "10011", "10001", "10001", "01111"],
             'U': ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
             'N': ["1001", "1101", "1101", "1011", "1011", "1001", "1001"],
             'S': ["01111", "10000", "10000", "01110", "00001", "00001", "11110"]}
        tw = sum(len(G[c][0]) for c in 'GUNS') + 3 * 2
        tx = x0 + (bw - tw) // 2; ty = y0 + 5
        for pass_ in (1, 0):
            cx = tx
            for chh in 'GUNS':
                g = G[chh]
                for gy, row in enumerate(g):
                    for gx, v in enumerate(row):
                        if v == '1':
                            cv.px(cx + gx + pass_, ty + gy + pass_, 'paintCreamD' if pass_ else 'wood5')
                cx += len(g[0]) + 2
        # revolver pictogram
        gx, gy = x0 + 15, y0 + 15
        cv.hline(gx, gx + 15, gy, 'steel3'); cv.hline(gx + 1, gx + 14, gy + 1, 'steel2')
        cv.hline(gx + 5, gx + 11, gy + 2, 'steel3'); cv.px(gx + 15, gy - 1, 'steel3')
        cv.rect(gx + 6, gy + 3, gx + 9, gy + 3, 'steel3')
        cv.rect(gx + 3, gy + 2, gx + 5, gy + 5, 'wood3'); cv.rect(gx + 2, gy + 4, gx + 4, gy + 6, 'wood3')
        cv.px(gx + 3, gy + 3, 'wood1')
        cv.px(gx + 8, gy + 4, 'steel3')
        cv.hline(x0 + 1, x1 - 1, y1 - 1, 'paintCreamD')
        np_outline(cv, 'ink')
        cv.clear(x0 - 1, y0 - 1); cv.clear(x1 + 1, y0 - 1); cv.clear(x0 - 1, y1 + 1); cv.clear(x1 + 1, y1 + 1)
        frames.append(cv.image())
    return strip(frames)


# ----------------------------------------------------------------------------- tumbleweed
def make_tumbleweed():
    """20x20, 8 frames rolling (rotation 45 deg per frame of a 4-fold-ish twig ball)."""
    r = np.random.RandomState(21)
    # twig segments in polar coords (angle, r0, r1)
    twigs = [(r.uniform(0, 2 * math.pi), r.uniform(0, 6), r.uniform(8, 12.4)) for _ in range(30)]
    arcs = [(r.uniform(0, 2 * math.pi), r.uniform(4, 12), r.uniform(0.8, 1.6)) for _ in range(18)]
    frames = []
    cx, cy = 13.5, 13.5
    for f in range(8):
        cv = Canvas(28, 28)
        rot = f * math.pi / 4 / 2 * 2 * 0.5 * 2  # 45 deg per frame -> full turn in 8
        rot = f * (2 * math.pi / 8)
        for (a, r0, r1) in twigs:
            aa = a + rot
            for t in np.linspace(r0, r1, 14):
                x = cx + math.cos(aa) * t; y = cy + math.sin(aa) * t * 0.95
                cv.px(int(x), int(y), 'sand4')
        for (a, rr, span) in arcs:
            for t in np.linspace(a, a + span, 12):
                x = cx + math.cos(t + rot) * rr; y = cy + math.sin(t + rot) * rr * 0.95
                cv.px(int(x), int(y), 'sand3')
        # lighting: pixels in the upper-right half lighter
        for y in range(28):
            for x in range(28):
                c = cv.get(x, y)
                if c is None:
                    continue
                if (x - cx) - (y - cy) > 4:
                    cv.px(x, y, 'sand2' if c == col('sand3') else 'sand3')
                elif (x - cx) - (y - cy) < -6:
                    cv.px(x, y, 'sand5' if c == col('sand4') else 'sand4')
        np_outline(cv, 'wood5')
        frames.append(cv.image())
    return strip(frames)


# ----------------------------------------------------------------------------- vulture
def make_vulture():
    """15x8 cell, 4 frames: glide, wings up, flap down, wings low. Pivot (7,4)."""
    shapes = [
        ["...............",
         "...............",
         "11...........11",
         ".1111.....1111.",
         "...111232111...",
         "......111......",
         "...............",
         "..............."],
        ["1.............1",
         ".1...........1.",
         "..11.......11..",
         "...111...111...",
         "....1112321....",
         "......111......",
         "...............",
         "..............."],
        ["...............",
         "...............",
         "...............",
         "......232......",
         ".11111111111111"[0:15],
         "11...11111...11",
         "...............",
         "..............."],
        ["...............",
         "...............",
         "...............",
         "......232......",
         "...111111111...",
         "..11..111..11..",
         ".11.........11.",
         "1.............1"],
    ]
    frames = []
    cmap = {'1': 'ink2', '2': 'red2', '3': 'shadowPurp'}
    for s in shapes:
        cv = Canvas(15, 8)
        for y, row in enumerate(s):
            for x, v in enumerate(row):
                if v in cmap:
                    cv.px(x, y, cmap[v])
        frames.append(cv.image())
    return strip(frames)


# ----------------------------------------------------------------------------- flag (extra)
def make_flag():
    """Small red/cream pennant waving, 12x8 cell, 4 frames, pivot (0,7) = pole top-left base."""
    frames = []
    for f in range(4):
        cv = Canvas(12, 8)
        cv.vline(0, 0, 7, 'wood4')
        cv.px(0, 0, 'brass1')
        for x in range(1, 12):
            wave = int(round(math.sin((x * 0.7) - f * math.pi / 2) * 1.0))
            length = 5 - (x * 4) // 12
            y0 = 1 + wave + (x * 2) // 12
            for yy in range(y0, y0 + max(1, length)):
                cv.px(x, yy, 'paintRed' if yy - y0 < max(1, length) // 2 + 1 else 'paintCream')
            if wave < 0:
                cv.px(x, y0, 'red0')
            elif wave > 0:
                cv.px(x, y0 + max(1, length) - 1, 'paintRedD' if max(1, length) == 1 else 'paintCreamD')
        frames.append(cv.image())
    return strip(frames)
