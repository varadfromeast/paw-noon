"""Right arm, paw and revolver of the Kid. The revolver is a small vector model in profile that is
re-drawn (rasterized at 1x, no AA) for every pose angle -- never a rotated bitmap."""
import math
import numpy as np
from herokit import Part, poly_mask, edge, shift
from hero_parts import W, H, XX, YY, thick_curve, T

# revolver in profile, pointing +x, origin = grip point held by the paw
GUN = {
    'barrel':  ([(4, -7), (17, -7), (17, -4), (4, -4)], 'steel', 3),
    'rod':     ([(6, -4), (13, -4), (13, -3), (6, -3)], 'steel', 2),
    'cyl':     ([(0, -9), (6, -9), (7, -7), (7, -3), (6, -2), (0, -2)], 'steel', 2),
    'frame':   ([(-4, -8), (0, -8), (0, -2), (-3, -2)], 'steel', 2),
    'hammer':  ([(-6, -11), (-4, -11), (-2, -8), (-4, -8)], 'steel', 1),
    'grip':    ([(-4, -2), (1, -2), (0, 3), (-2, 6), (-5, 6), (-6, 3)], 'grip', 3),
    'sight':   ([(15, -8), (16, -8), (16, -7), (15, -7)], 'steel', 3),
}
MUZZLE = (17.5, -5.5)


GS = 1.35   # model scale (re-rasterized, not a bitmap scale)


def rot(pts, ang, ox, oy):
    c, s = math.cos(ang) * GS, math.sin(ang) * GS
    return [(ox + x * c - y * s, oy + x * s + y * c) for x, y in pts]


def gun(ox, oy, deg, jam=False, flip=False):
    """Revolver with grip point at (ox,oy), barrel direction `deg` (screen, 0=right, -90=up)."""
    ang = math.radians(deg)
    fy = -1 if flip else 1
    g = Part('gun', 'gun')
    for name in ('grip', 'frame', 'cyl', 'barrel', 'rod', 'sight', 'hammer'):
        pts, mat, lum = GUN[name]
        pts = [(x, y * fy) for x, y in pts]
        if name == 'hammer' and jam:
            pts = [(x + 1, y + 1) for x, y in pts]
        g.poly(rot(pts, ang, ox, oy), mat, lum)
    m = g.mask
    # light from above / right in screen space
    up = edge(m, 0, -1) | edge(m, 1, 0)
    g.addlum(up & (g.mat >= 0), 1, hi=5)
    g.addlum(edge(m, 0, 1), -1)
    # cylinder flute + barrel shine line
    for t in (2.0, 4.0):
        a = rot([(t, -8 * fy)], ang, ox, oy)[0]
        b = rot([(t, -3 * fy)], ang, ox, oy)[0]
        g.line([a, b], 'steel', 1, only=m)
    a, b = rot([(5, -6 * fy), (16, -6 * fy)], ang, ox, oy)
    g.line([a, b], 'steel', 4, only=m)
    mx, my = rot([(MUZZLE[0], MUZZLE[1] * fy)], ang, ox, oy)[0]
    return g, (int(round(mx)), int(round(my)))


def paw(x, y, kind='grip', deg=0.0):
    """Orange paw. kind: grip (fist around a grip), open (spread, hovering), relax, twirl."""
    pt = Part('paw', 'paw')
    if kind == 'open':
        m = pt.ellipse(x, y, 4.6, 3.6, 'fur', 2)
        for dx in (-3, -1, 1, 3):     # splayed toes hanging down
            pt.poly([(x + dx - 0.6, y + 2), (x + dx + 0.6, y + 2), (x + dx * 1.3 + .5, y + 6),
                     (x + dx * 1.3 - .5, y + 6)], 'fur', 2)
        pt.px([(int(round(x + dx * 1.3)), int(round(y + 6))) for dx in (-3, -1, 1, 3)], 'furL', 3)
    elif kind == 'relax':
        m = pt.ellipse(x, y, 4.0, 4.6, 'fur', 2)
        pt.line([(x - 1.5, y + 2), (x - 1.5, y + 4)], 'fur', 1)
        pt.line([(x + 1.5, y + 2), (x + 1.5, y + 4)], 'fur', 1)
    else:
        m = pt.ellipse(x, y, 5.0, 4.6, 'fur', 2)
        a = math.radians(deg)
        # knuckle lines across the fist, perpendicular to the barrel direction
        for k in (-1.5, 1.5):
            p0 = (x + math.cos(a) * k - math.sin(a) * -3, y + math.sin(a) * k + math.cos(a) * -3)
            p1 = (x + math.cos(a) * k - math.sin(a) * 1, y + math.sin(a) * k + math.cos(a) * 1)
            pt.line([p0, p1], 'fur', 1, only=pt.mask)
        if kind == 'twirl':
            pt.px([(int(x) + 3, int(y) - 4), (int(x) + 4, int(y) - 5)], 'fur', 3)
    mm = pt.mask
    pt.addlum(edge(mm, 0, -1) | edge(mm, 1, 0), 1)
    pt.addlum(edge(mm, 0, 1), -1)
    return pt


def sleeve(pts, r0=5.2, r1=4.2):
    pt = Part('arm', 'arm')
    m, dense = thick_curve(pts, r0, r1, 8)
    pt.fill(m, 'sleeve', 2)
    pt.addlum(edge(m, 1, 0) | edge(m, 0, -1), 1)
    pt.addlum(edge(m, -1, 0), -1)
    # rolled cuff near the wrist
    wx, wy = dense[-1]
    px_, py_ = dense[-4]
    tx, ty = wx - px_, wy - py_
    ln = math.hypot(tx, ty) or 1
    d = ((XX - wx) * tx + (YY - wy) * ty) / ln
    pt.fill(m & (d > -3.5) & (d <= -1.2), 'sleeve', 4)
    pt.fill(m & (d > -1.2), 'sleeve', 3)
    # fold crease on the sleeve
    mx, my = dense[len(dense) // 2]
    pt.line([(mx - 2, my), (mx + 1, my - 1)], 'sleeve', 1, only=m)
    return pt


def smear(arc, mat='whisk', lum=1):
    pt = Part('smear', 'smear', outline=False)
    pt.line(arc, mat, lum, smooth=6)
    pt.noline[:] = True
    return pt


def puff(cx, cy, r, phase=0):
    pt = Part('puff', 'puff', outline=False)
    for dx, dy, rr in ((0, 0, r), (-r * .8, r * .3, r * .7), (r * .8, r * .2, r * .75), (r * .2, -r * .7, r * .65)):
        pt.ellipse(cx + dx, cy + dy, rr, rr * .85, 'smoke', 3)
    m = pt.mask
    pt.addlum(edge(m, 0, 1) | edge(m, -1, 0), -2)
    pt.addlum(edge(m, 0, -1) | edge(m, 1, 0), 1)
    pt.noline[:] = True
    return pt


# ------------------------------------------------------------------------------ arm poses
# S shoulder (under poncho), E elbow, Wr wrist/paw centre, gun angle (deg) or None
ARM = {
    'rest':    dict(S=(112, 124), E=(117, 134), Wr=(118, 145), paw='relax', gun=None, holster=True),
    'hover':   dict(S=(112, 110), E=(121, 123), Wr=(110, 136), paw='open', gun=None, holster=True),
    'hover2':  dict(S=(112, 110), E=(121, 124), Wr=(111, 137), paw='open', gun=None, holster=True),
    'grab':    dict(S=(106, 104), E=(117, 124), Wr=(108, 141), paw='grip', gun=None, holster=True),
    'pull':    dict(S=(108, 104), E=(122, 118), Wr=(120, 131), paw='grip', gun=95),
    'swing':   dict(S=(108, 94), E=(125, 100), Wr=(124, 84), paw='grip', gun=-30),
    'aim':     dict(S=(108, 90), E=(119, 80), Wr=(116, 64), paw='grip', gun=-63),
    'aim2':    dict(S=(108, 90), E=(119, 81), Wr=(116, 65), paw='grip', gun=-63),
    'recoil':  dict(S=(108, 90), E=(118, 77), Wr=(114, 58), paw='grip', gun=-82),
    'recover': dict(S=(108, 90), E=(119, 79), Wr=(115, 62), paw='grip', gun=-70),
    'droop':   dict(S=(108, 92), E=(121, 85), Wr=(119, 74), paw='grip', gun=-15),
    'look':    dict(S=(108, 92), E=(119, 88), Wr=(110, 76), paw='grip', gun=-150),
    'tw0':     dict(S=(108, 96), E=(123, 106), Wr=(122, 90), paw='twirl', gun=0),
    'tw1':     dict(S=(108, 96), E=(123, 106), Wr=(122, 90), paw='twirl', gun=90),
    'tw2':     dict(S=(108, 96), E=(123, 106), Wr=(122, 90), paw='twirl', gun=180),
    'tw3':     dict(S=(108, 96), E=(123, 106), Wr=(122, 90), paw='twirl', gun=270),
    'holst':   dict(S=(108, 102), E=(122, 120), Wr=(116, 132), paw='grip', gun=95),
    'tip':     dict(S=(111, 84), E=(121, 62), Wr=(103, 37), paw='grip', gun=None, holster=True),
    'tip2':    dict(S=(111, 84), E=(121, 64), Wr=(104, 39), paw='grip', gun=None, holster=True),
}


def arm(p):
    """Returns (parts_behind_poncho, parts_front, muzzle or None, holster_has_gun)."""
    key = p.get('arm', 'rest')
    if key in BMP_ARM:
        return bmp_arm(p)
    a = ARM[key]
    ax, ay = p.get('adx', 0) + p.get('bx', 0), p.get('ady', 0) + p.get('by', 0)
    S, E, Wr = [(x + ax, y + ay) for x, y in (a['S'], a['E'], a['Wr'])]
    sl = sleeve([S, E, Wr])
    parts = [sl]
    muzzle = None
    gdeg = a['gun']
    pw = paw(Wr[0], Wr[1], a['paw'], gdeg or 0)
    if gdeg is not None:
        flip = 90 < (gdeg % 360) < 270
        g, muzzle = gun(Wr[0], Wr[1], gdeg, jam=p.get('jam', False), flip=flip)
        parts += [g, pw]
    else:
        parts += [pw]
    return parts, muzzle, a.get('holster', False)


# ============================================================ hand-pixeled revolver + paw (aim)
# Barrel points right toward the street, cylinder / hammer / ejector rod readable. 24 x 15.
GUN_AIM = [
    ".kk.....................",
    "kSdk....................",
    ".kSdkkkkkkk.........kk..",
    "..kdkSSSSSSkkkkkkkkkSSk.",
    "..kdksssdssSSSSSSSSSSSSk",
    "..kdkssdsssswwwwwwwwwwsk",
    "..kdksdsdssdddddddddddk.",
    ".kkdkddddddkkkkkkkkkkk..",
    "kfFkkddddddksssssssk....",
    "kFFFfkkkkkkkkkkkkkk.....",
    "kfFFffkdk.k.............",
    "kffoffok.kk.............",
    "koffoffk................",
    ".kooooOk................",
    "..kkkkk.................",
]
GUN_LEG = {'k': 'ink', 'S': ('steel', 3), 's': ('steel', 2), 'd': ('steel', 1), 'w': ('steel', 4),
           'f': ('fur', 2), 'F': ('fur', 3), 'o': ('fur', 1), 'O': ('fur', 0), 'b': ('brass', 3)}
AIM_MUZZLE = (23, 5)     # barrel tip in bitmap coords
AIM_WRIST = (2, 12)      # where the sleeve meets the paw


def kick(rows, step=4, x0=11):
    """Recoil variant: barrel columns lifted 1px per `step` columns (clean 1:4 line steps)."""
    h = len(rows); w = len(rows[0])
    grid = [list(r) for r in rows]
    out = [['.'] * w for _ in range(h + 4)]
    for y in range(h):
        for x in range(w):
            up = max(0, (x - x0) // step + 1) if x >= x0 else 0
            if grid[y][x] != '.':
                out[y + 4 - up][x] = grid[y][x]
    return [''.join(r) for r in out], 4


def rot90(rows):
    """Exact quarter turn clockwise (lossless pixel transpose) -> barrel pointing down."""
    h, w = len(rows), len(rows[0])
    return [''.join(rows[h - 1 - y][x] for y in range(h)) for x in range(w)]


def bmp_gun(kind, ox, oy):
    pt = Part('gunbmp', 'gun')
    if kind == 'aim':
        rows, mz, yo = GUN_AIM, AIM_MUZZLE, 0
    elif kind == 'kick':
        rows, yo = kick(GUN_AIM)
        mz = (23, 5 + yo - ((23 - 11) // 4 + 1))
    else:   # 'down': gun hanging barrel-down while being pulled from the holster
        rows = rot90(GUN_AIM); yo = 0
        mz = (len(GUN_AIM) - 1 - AIM_MUZZLE[1], AIM_MUZZLE[0])
    pt.bitmap(rows, GUN_LEG, ox, oy - yo)
    return pt, (ox + mz[0], oy - yo + mz[1])


# bitmap arm poses: S, E, wrist, bitmap kind, bitmap origin (top-left)
BMP_ARM = {
    'b_pull':  dict(S=(110, 104), E=(123, 116), Wr=(119, 124), kind='down', o=(113, 120)),
    'b_swing': dict(S=(110, 96), E=(125, 98), Wr=(118, 90), kind='aim', o=(116, 78)),
    'b_aim':   dict(S=(108, 86), E=(112, 72), Wr=(107, 58), kind='aim', o=(105, 40)),
    'b_aim2':  dict(S=(108, 86), E=(112, 73), Wr=(107, 59), kind='aim', o=(105, 41)),
    'b_kick':  dict(S=(108, 86), E=(111, 70), Wr=(105, 55), kind='kick', o=(103, 37)),
    'b_recov': dict(S=(108, 86), E=(112, 71), Wr=(106, 57), kind='aim', o=(104, 39)),
}


def bmp_arm(p):
    a = BMP_ARM[p['arm']]
    ax, ay = p.get('adx', 0) + p.get('bx', 0), p.get('ady', 0) + p.get('by', 0)
    S, E, Wr = [(x + ax, y + ay) for x, y in (a['S'], a['E'], a['Wr'])]
    g, muzzle = bmp_gun(a['kind'], a['o'][0] + ax, a['o'][1] + ay)
    return [sleeve([S, E, Wr]), g], muzzle, False


def fur_puff(cx, cy, r, stage=0):
    """Fur tufts + little stars for an incoming hit (no outline, soft)."""
    pt = Part('furpuff', 'furpuff', outline=False)
    for i in range(7):
        a = i * 2 * math.pi / 7 + stage * .4
        d = r * (0.55 + 0.25 * (i % 2))
        x, y = cx + math.cos(a) * d, cy + math.sin(a) * d * .8
        pt.ellipse(x, y, r * .33, r * .28, 'fur', 4 if i % 2 else 3)
    pt.ellipse(cx, cy, r * .45, r * .4, 'furL', 3)
    m = pt.mask
    pt.addlum(edge(m, 0, 1) | edge(m, -1, 0), -2)
    for i in range(3):
        a = i * 2.1 + stage
        sx, sy = int(cx + math.cos(a) * r * 1.3), int(cy + math.sin(a) * r * 1.1)
        pt.px([(sx, sy), (sx - 1, sy), (sx + 1, sy), (sx, sy - 1), (sx, sy + 1)], 'brass', 4)
        pt.px([(sx, sy)], 'brass', 5)
    pt.noline[:] = True
    return pt
