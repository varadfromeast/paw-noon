"""FX strips for Art/FX + fx.json.  Run: python3 gen_fx.py"""
import math, os
import numpy as np
from uikit import Canvas, Registry, FX_DIR, ellipse_mask, poly_mask, dilate, shift, edge_in, strip


def grid(w, h):
    yy, xx = np.mgrid[0:h, 0:w] + 0.5
    return xx, yy


def star_poly(cx, cy, rays, r_out, r_in, rot=0.0, stretch=None):
    pts = []
    n = len(r_out) if isinstance(r_out, (list, tuple)) else rays
    for i in range(2 * rays):
        a = rot + i * math.pi / rays
        r = (r_out[i // 2] if isinstance(r_out, (list, tuple)) else r_out) if i % 2 == 0 else r_in
        x, y = r * math.cos(a), r * math.sin(a)
        if stretch:
            # stretch along direction stretch=(ux,uy,k)
            ux, uy, k = stretch
            d = x * ux + y * uy
            if d > 0:
                x += ux * d * (k - 1)
                y += uy * d * (k - 1)
        pts.append((cx + x, cy + y))
    return pts


def layered(cv, W, H, pts_fn, layers):
    """layers: list of (scale, colour) from outside in; pts_fn(scale) -> polygon."""
    for sc, col in layers:
        cv.paint(poly_mask(W, H, pts_fn(sc)), col)


# ---------------------------------------------------------------- muzzle flashes (opaque, Metal Slug snap)
def muzzle_side():
    """Hero's flash pointing up-right; pivot = muzzle end at (3, 20)."""
    W = H = 24
    px, py = 3, 20
    ux, uy = math.cos(-math.radians(40)), math.sin(-math.radians(40))
    frames = []
    specs = [  # (length, width, star spikes radius)
        (21, 7.5, 6.5), (17, 5.0, 4.0), (10, 2.6, 0)]
    for fi, (L, Wd, sr) in enumerate(specs):
        cv = Canvas(W, H)
        def cone(sc):
            l, w = L * sc, Wd * sc
            nx, ny = -uy, ux
            return [(px - ux * 1.5 + nx * w * 0.35, py - uy * 1.5 + ny * w * 0.35),
                    (px + ux * l * 0.45 + nx * w, py + uy * l * 0.45 + ny * w),
                    (px + ux * l, py + uy * l),
                    (px + ux * l * 0.45 - nx * w, py + uy * l * 0.45 - ny * w),
                    (px - ux * 1.5 - nx * w * 0.35, py - uy * 1.5 - ny * w * 0.35)]
        m_out = poly_mask(W, H, cone(1.0))
        if sr:
            # side spikes perpendicular (cross flare) for the first two frames
            nx, ny = -uy, ux
            for s in (1, -1):
                m_out |= poly_mask(W, H, [(px + ux * 3 - ux, py + uy * 3 - uy), (px + ux * 3 + nx * s * sr * 1.4, py + uy * 3 + ny * s * sr * 1.4),
                                          (px + ux * 5, py + uy * 5)])
        cv.paint(dilate(m_out, 1, diag=False), 'red1' if fi < 2 else 'sigOrange')
        cv.paint(m_out, 'sigOrange')
        cv.paint(poly_mask(W, H, cone(0.75)) | (m_out & ~dilate(~m_out, 2, diag=False)), 'sun2')
        cv.paint(poly_mask(W, H, cone(0.5)), 'sun1')
        if fi < 2:
            cv.paint(poly_mask(W, H, cone(0.32)), 'white')
        frames.append(cv.img())
    return strip(frames), [px, py]


def muzzle_front():
    """Bandit's flash toward the camera: radial star, centre pivot."""
    W = H = 32
    frames = []
    for fi, (R, ri, rays, rot) in enumerate([(15.5, 6.0, 8, 0.2), (12, 5.0, 6, 0.6), (7, 3.0, 5, 0.1)]):
        cv = Canvas(W, H)
        rr = [R if i % 2 == 0 else R * 0.68 for i in range(rays)]
        outer = poly_mask(W, H, star_poly(16, 16, rays, rr, ri, rot))
        cv.paint(dilate(outer, 1, diag=False), 'red1')
        cv.paint(outer, 'sigOrange')
        cv.paint(poly_mask(W, H, star_poly(16, 16, rays, [r * 0.72 for r in rr], ri * 0.8, rot)), 'sun2')
        cv.paint(ellipse_mask(W, H, 16, 16, ri + 1.5, ri + 1.5), 'sun1')
        if fi < 2:
            cv.paint(ellipse_mask(W, H, 16, 16, ri * 0.75, ri * 0.75), 'white')
        frames.append(cv.img())
    return strip(frames)


# ---------------------------------------------------------------- smoke / dust
def blobs(W, H, specs):
    m = np.zeros((H, W), bool)
    for (cx, cy, r) in specs:
        if r > 0.3:
            m |= ellipse_mask(W, H, cx, cy, r, r)
    return m


def smoke_puff():
    W = H = 16
    frames = []
    seq = [  # (blobs, alpha)
        ([(8, 9, 3.2), (6, 8, 2.2), (10, 7.5, 2.4)], 255),
        ([(8, 9, 4.2), (5.5, 7.5, 3.0), (10.5, 6.5, 3.0)], 255),
        ([(8, 8.5, 4.8), (5, 6.5, 3.4), (11, 5.8, 3.4), (8, 4.5, 2.6)], 191),
        ([(8, 8, 5.2), (4.5, 5.8, 3.4), (11.5, 5.0, 3.4), (8, 3.5, 2.8)], 128),
        ([(8, 7, 5.0), (4.2, 4.6, 3.0), (11.8, 4.0, 3.0)], 128),
        ([(8, 6, 4.0), (4.5, 3.6, 2.2), (11.5, 3.2, 2.2)], 64),
    ]
    for bl, a in seq:
        cv = Canvas(W, H)
        m = blobs(W, H, bl)
        cv.paint(m, 'cloud2', alpha=a)
        top = m & shift(m, 1, 1) & shift(m, 0, 1)
        cv.paint(top & ~shift(m, -1, -1) | (m & ~shift(m, 0, -1) & False), 'cloud2', alpha=a)
        core = m & shift(m, -1, -1) & shift(m, -2, -2)
        cv.paint(m & ~shift(m, 0, -1), 'cloud3', alpha=a)
        cv.paint(m & ~shift(m, 1, 1) & shift(m, 0, -1), 'cloud1', alpha=a)
        cv.paint(m & shift(m, 0, -2) & shift(m, 0, 2) & ~shift(m, 2, 2), 'cloud1', alpha=a)
        frames.append(cv.img())
    return strip(frames)


def dust_puff():
    W, H = 24, 12
    frames = []
    seq = [
        [(12, 10, 2.5), (9, 10.5, 1.8), (15, 10.5, 1.8)],
        [(12, 9, 3.4), (7.5, 9.8, 2.6), (16.5, 9.8, 2.6)],
        [(12, 8, 3.8), (6.5, 9, 3.0), (17.5, 9, 3.0), (3.5, 10.5, 1.6), (20.5, 10.5, 1.6)],
        [(12, 7.2, 3.6), (6, 8.4, 3.0), (18, 8.4, 3.0), (2.8, 10, 1.6), (21.2, 10, 1.6)],
        [(12, 6.6, 2.8), (5.5, 8.0, 2.4), (18.5, 8.0, 2.4)],
        [(5, 7.6, 1.6), (19, 7.6, 1.6), (12, 6.0, 1.8)],
    ]
    for bl in seq:
        cv = Canvas(W, H)
        m = blobs(W, H, bl)
        cv.paint(m, 'sand2')
        cv.paint(m & ~shift(m, 0, -1), 'sand3')
        cv.paint(m & ~shift(m, 0, 1), 'sand0')
        cv.paint(m & ~shift(m, 0, 1) & shift(m, 0, -1) & False, 'sand0')
        frames.append(cv.img())
    return strip(frames)


# ---------------------------------------------------------------- hit puff: fur tufts + stars
def small_star(cv, x, y, big=True):
    pts = [(0, -2), (0, -1), (-1, 0), (0, 0), (1, 0), (-2, 0), (2, 0), (0, 1), (0, 2)] if big else \
          [(0, -1), (-1, 0), (0, 0), (1, 0), (0, 1)]
    ring = set()
    for dx, dy in pts:
        for ax, ay in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ring.add((dx + ax, dy + ay))
    for (dx, dy) in ring - set(pts):
        cv.px(x + dx, y + dy, 'ink2')
    for dx, dy in pts:
        cv.px(x + dx, y + dy, 'sun1')
    cv.px(x, y, 'white')


def hit_puff():
    W = H = 32
    frames = []
    rng_tufts = [(-1, -0.6), (0.9, -0.8), (1.0, 0.5), (-0.9, 0.7), (0.1, 1.0), (-0.2, -1.0)]
    for fi in range(5):
        cv = Canvas(W, H)
        if fi == 0:   # impact pop: white star burst
            m = poly_mask(W, H, star_poly(16, 16, 6, 9, 3.5, 0.3))
            cv.paint(dilate(m, 1, diag=False), 'ink2')
            cv.paint(m, 'cream0')
            cv.paint(ellipse_mask(W, H, 16, 16, 3, 3), 'white')
        else:
            spread = 4 + fi * 3.0
            rr = max(1.2, 4.2 - fi * 0.7)
            m = np.zeros((H, W), bool)
            for (dx, dy) in rng_tufts:
                cx, cy = 16 + dx * spread, 16 + dy * spread + fi * 0.6
                m |= ellipse_mask(W, H, cx, cy, rr * 1.2, rr)
                # tuft wisp
                m |= poly_mask(W, H, [(cx - rr * 0.6, cy), (cx + dx * rr * 2.2, cy + dy * rr * 2.2 - 0.5),
                                      (cx + rr * 0.6, cy)])
            if fi <= 2:
                m |= ellipse_mask(W, H, 16, 16, 5 - fi, 5 - fi)
            cv.paint(dilate(m, 1, diag=False), 'ink2')
            cv.paint(m, 'char0')
            cv.paint(m & ~shift(m, 0, 1), 'cream1')
            cv.paint(m & ~shift(m, 0, -1), 'char1')
            n = 4 if fi < 4 else 2
            for k in range(n):
                a = k * 2 * math.pi / n + fi * 0.5
                rad = 8 + fi * 2.2
                small_star(cv, int(16 + rad * math.cos(a)), int(16 + rad * math.sin(a)), big=fi < 3)
        frames.append(cv.img())
    return strip(frames)


def spark():
    frames = []
    for fi in range(4):
        cv = Canvas(8, 8)
        L = [3, 3, 2, 1][fi]
        cols = ['white', 'sun1', 'sun2', 'sigOrange'][fi]
        for i in range(-L, L + 1):
            cv.px(4 + i, 4, cols if abs(i) > 0 else 'white')
            cv.px(4, 4 + i, cols if abs(i) > 0 else 'white')
        if fi < 2:
            for d in (1, -1):
                cv.px(4 + d * 2, 4 + d * 2, 'sun2')
                cv.px(4 + d * 2, 4 - d * 2, 'sun2')
        frames.append(cv.img())
    return strip(frames)


def shell():
    pats = [["....", "#Bb.", "#Bb.", "...."],
            [".#B.", ".BBb", "..b.", "...."],
            [".#..", ".B..", ".B..", ".b.."],
            ["..#.", ".bB.", "bBB.", "...."]]
    pats = [[".##.", ".BB.", ".bb.", "...."],
            ["...#", "..Bb", ".Bb.", ".b.."],
            ["....", "#BBb", "....", "...."],
            [".#..", ".bB.", "..Bb", "...."]]
    cmap = {'#': 'brass0', 'B': 'brass1', 'b': 'brass2'}
    frames = []
    for p in pats:
        cv = Canvas(4, 4)
        for y, r in enumerate(p):
            for x, c in enumerate(r):
                if c in cmap:
                    cv.px(x, y, cmap[c])
        frames.append(cv.img())
    return strip(frames)


def shadow_bandit():
    W, H = 28, 6
    cv = Canvas(W, H)
    cv.paint(ellipse_mask(W, H, 14, 3, 14, 3), 'shadowPurp', alpha=128)
    cv.paint(ellipse_mask(W, H, 14, 3, 9, 2), 'shadowPurp', alpha=191)
    return cv.img()


def flash_ring():
    W = H = 48
    xx, yy = grid(W, H)
    r = np.hypot(xx - 24, yy - 24)
    frames = []
    for fi, (R, th, a_core) in enumerate([(7, 4, 255), (13, 3, 255), (19, 2, 191), (23, 1.5, 128)]):
        cv = Canvas(W, H)
        ring = (r < R) & (r >= R - th)
        if fi == 0:
            cv.paint(r < R, 'sun1', alpha=191)
        cv.paint(ring, 'white', alpha=a_core)
        cv.paint((r < R - th) & (r >= R - th - 1.5), 'sun1', alpha=64 if fi else 128)
        frames.append(cv.img())
    return strip(frames)


def sweat_drop():
    frames = []
    drop = ["..#...", "..#...", ".###..", ".#W#..", "#WWB#.", "#WBB#.", ".###..", "......"]
    for fi in range(3):
        cv = Canvas(6, 8)
        oy = [0, 1, 2][fi]
        rows = drop if fi < 2 else ["......", "......", "..#...", ".#W#..", "#WBB#.", ".###..", "......", "......"]
        for y, rr in enumerate(rows):
            yy = y + (oy if fi < 2 else 0)
            for x, c in enumerate(rr):
                if c == '#':
                    cv.px(x, yy, 'ink2')
                elif c == 'W':
                    cv.px(x, yy, 'white')
                elif c == 'B':
                    cv.px(x, yy, 'sky4')
        frames.append(cv.img())
    return strip(frames)


def dust_mote():
    cv0 = Canvas(2, 2); cv0.px(0, 0, 'sand0')
    cv1 = Canvas(2, 2)
    for p in ((0, 0), (1, 0), (0, 1), (1, 1)):
        cv1.px(*p, 'cream0')
    return strip([cv0.img(), cv1.img()])


def main():
    os.makedirs(FX_DIR, exist_ok=True)
    R = Registry(FX_DIR, 'fx.json')
    img, piv = muzzle_side()
    R.add('muzzle_side', img, pivot=piv, cell=[24, 24], frames=3, fps=30, loop=False)
    R.add('muzzle_front', muzzle_front(), pivot=[16, 16], cell=[32, 32], frames=3, fps=30, loop=False)
    R.add('smoke_puff', smoke_puff(), pivot=[8, 9], cell=[16, 16], frames=6, fps=12, loop=False, partial_alpha=True)
    R.add('dust_puff', dust_puff(), pivot=[12, 12], cell=[24, 12], frames=6, fps=14, loop=False)
    R.add('hit_puff', hit_puff(), pivot=[16, 16], cell=[32, 32], frames=5, fps=16, loop=False)
    R.add('spark', spark(), pivot=[4, 4], cell=[8, 8], frames=4, fps=20, loop=False)
    R.add('shell', shell(), pivot=[2, 2], cell=[4, 4], frames=4, fps=16, loop=True)
    R.add('shadow_bandit', shadow_bandit(), pivot=[14, 3], partial_alpha=True)
    R.add('flash_ring', flash_ring(), pivot=[24, 24], cell=[48, 48], frames=4, fps=24, loop=False, partial_alpha=True)
    R.add('sweat_drop', sweat_drop(), pivot=[3, 0], cell=[6, 8], frames=3, fps=8, loop=False)
    R.add('dust_mote', dust_mote(), pivot=[0, 0], cell=[2, 2], frames=2, fps=0, loop=False,
          note='frame 0 = 1px sand0, frame 1 = 2x2 cream0; pick per particle, not an animation')
    R.write()
    print(len(R.meta), 'FX')


def muzzle_side():  # noqa: F811  (v2: stretched star = sharper Metal-Slug snap)
    W = H = 24
    px, py = 3, 20
    ang = -math.radians(40)
    ux, uy = math.cos(ang), math.sin(ang)
    frames = []
    for fi, (R, ri, k, off) in enumerate([(7.5, 2.6, 2.3, 6.5), (6.0, 2.2, 2.0, 5.5), (3.6, 1.5, 1.8, 3.5)]):
        cv = Canvas(W, H)
        cx, cy = px + ux * off, py + uy * off
        rot = ang
        pts = lambda sc: star_poly(cx, cy, 6, [R * sc, R * 0.62 * sc, R * 0.62 * sc] * 2, ri * sc, rot, (ux, uy, k))
        outer = poly_mask(W, H, pts(1.0))
        cv.paint(dilate(outer, 1, diag=False), 'red1')
        cv.paint(outer, 'sigOrange')
        cv.paint(poly_mask(W, H, pts(0.72)), 'sun2')
        cv.paint(poly_mask(W, H, pts(0.48)) | ellipse_mask(W, H, cx - ux, cy - uy, ri + 0.6, ri + 0.6), 'sun1')
        if fi < 2:
            cv.paint(ellipse_mask(W, H, cx - ux, cy - uy, ri * 0.7, ri * 0.7), 'white')
        frames.append(cv.img())
    return strip(frames), [px, py]


if __name__ == '__main__':
    main()
