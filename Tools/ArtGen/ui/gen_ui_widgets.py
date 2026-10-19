"""HUD widgets, panels, buttons, toggles, icons and the tap hint for Art/UI."""
import math
import numpy as np
from uikit import (Canvas, blank, rect, rrect_mask, ellipse_mask, poly_mask, shift, dilate, edge_in, pad, strip)


def amask(rows):
    rows = rows.split()
    w = max(len(r) for r in rows)
    return np.array([[c != '.' for c in r.ljust(w, '.')] for r in rows], bool)


# ---------------------------------------------------------------- frames & bars
def portrait_frame():
    cv = Canvas(32, 32)
    outer = rrect_mask(32, 32, 0, 0, 32, 32, (2, 2, 2, 2), p=2)
    ring = outer.copy(); ring[2:30, 2:30] = False
    cv.paint(ring, 'ink2')
    band = blank(32, 32); band[1:31, 1:31] = True; band[2:30, 2:30] = False
    band &= outer & ~edge_in(outer, 0, -1) & ~edge_in(outer, 0, 1) & ~edge_in(outer, 1, 0) & ~edge_in(outer, -1, 0)
    cv.paint(band, 'brass1')
    cv.paint(band & (np.mgrid[0:32, 0:32][0] < 2), 'brass0')
    cv.paint(band & (np.mgrid[0:32, 0:32][1] < 2), 'brass0')
    cv.paint(band & (np.mgrid[0:32, 0:32][0] > 29), 'brass2')
    cv.paint(band & (np.mgrid[0:32, 0:32][1] > 29), 'brass2')
    for (x, y) in ((1, 1), (30, 1), (1, 30), (30, 30)):
        cv.px(x, y, 'brass3')
    # window left transparent (28x28 at 2,2); 1px inner shadow line is the portrait's job
    return cv.img()


HP_W, HP_H = 68, 10
HP_IN = (2, 2, 64, 6)


def hp_frame():
    cv = Canvas(HP_W, HP_H)
    m = rrect_mask(HP_W, HP_H, 0, 0, HP_W, HP_H, (2, 2, 2, 2), p=2)
    cv.paint(m, 'ink2')
    bez = m & ~edge_in(m, 0, -1) & ~edge_in(m, 0, 1) & ~edge_in(m, 1, 0) & ~edge_in(m, -1, 0)
    cv.paint(bez, 'wood1')
    cv.paint(bez & ~shift(bez, 0, 1), 'wood0')
    cv.paint(bez & ~shift(bez, 0, -1), 'wood3')
    x, y, w, h = HP_IN
    win = blank(HP_W, HP_H); win[y:y + h, x:x + w] = True
    cv.paint(win, 'ink2')
    cv.paint(win & ~shift(win, 0, 1), 'shadowPurp')   # top inner shade row... visible when empty
    # quarter ticks (only visible over the empty window)
    for q in (1, 2, 3):
        tx = x + round(w * q / 4)
        cv.px(tx, y + h - 1, 'shadowPurp')
    return cv.img()


def hp_strip(body, hi, lo, hi2=None):
    w, h = HP_IN[2], HP_IN[3]
    cv = Canvas(w, h)
    cv.paint(np.ones((h, w), bool), body)
    cv.paint(np.r_[[True], [False] * (h - 1)][:, None] & np.ones((h, w), bool), hi)
    if hi2:
        r = np.zeros((h, w), bool); r[1] = True
        cv.paint(r, hi2)
    r = np.zeros((h, w), bool); r[h - 1] = True
    cv.paint(r, lo)
    return cv.img()


# ---------------------------------------------------------------- panels (9-slice sources)
def panel_wood(S=24):
    cv = Canvas(S, S)
    m = rrect_mask(S, S, 0, 0, S, S, (3, 3, 3, 3), p=2)
    cv.paint(m, 'ink2')
    frame = m & ~edge_in(m, 0, -1) & ~edge_in(m, 0, 1) & ~edge_in(m, 1, 0) & ~edge_in(m, -1, 0)
    cv.paint(frame, 'wood2')
    cv.paint(frame & ~shift(frame, 0, 1), 'wood0')
    cv.paint(frame & ~shift(frame, 1, 0), 'wood1')
    cv.paint(frame & ~shift(frame, 0, -1), 'wood4')
    cv.paint(frame & ~shift(frame, -1, 0), 'wood3')
    inner = blank(S, S); inner[5:S - 5, 5:S - 5] = True
    cv.paint(dilate(inner, 1, diag=False) & ~inner, 'wood4')
    cv.paint(inner, 'wood3')
    for (x, y) in ((2, 2), (S - 3, 2), (2, S - 3), (S - 3, S - 3)):
        cv.px(x, y, 'steel1')
    return cv.img()


def panel_paper(S=24):
    """Wanted-poster parchment. Centre flat paintCream; border 6."""
    cv = Canvas(S, S)
    m = blank(S, S); m[:, :] = True
    # worn corners
    for (x, y) in ((0, 0), (S - 1, 0), (0, S - 1), (S - 1, S - 1), (1, 0), (0, 1), (S - 2, S - 1), (S - 1, S - 2)):
        m[y, x] = False
    cv.paint(m, 'ink2')
    p = m & ~edge_in(m, 0, -1) & ~edge_in(m, 0, 1) & ~edge_in(m, 1, 0) & ~edge_in(m, -1, 0)
    cv.paint(p, 'paintCream')
    rim = p & dilate(~p, 2, diag=False)
    cv.paint(rim, 'cream2')
    cv.paint(p & dilate(~p, 1, diag=False) & ~shift(p, 0, 1) | (p & dilate(~p, 1, diag=False) & ~shift(p, -1, 0)), 'paintCreamD')
    # printed double rule inset 4 (wanted-poster border)
    rule = blank(S, S); rule[4:S - 4, 4:S - 4] = True
    rule[5:S - 5, 5:S - 5] = False
    cv.paint(rule, 'paintCreamD')
    # pins top corners
    for x in (3, S - 4):
        cv.px(x, 2, 'paintRedD')
    return cv.img()


def button(w, h, face, hi, lo, depth_col, pressed=False, D=3):
    """Raised plank button (9-slice). Unpressed: face rows 1..h-D-1 + D px depth. Pressed: face shifted down D-1."""
    cv = Canvas(w, h)
    off = (D - 1) if pressed else 0
    body = rrect_mask(w, h, 0, off, w, h, (3, 3, 3, 3), p=2)
    cv.paint(body, 'ink2')
    dep = rrect_mask(w, h, 1, off + 1, w - 1, h - 1, (2, 2, 2, 2), p=2)
    cv.paint(dep, depth_col)
    fm = rrect_mask(w, h, 1, off + 1, w - 1, h - D - 1 + off if not pressed else h - 2, (2, 2, 2, 2), p=2)
    cv.paint(fm, face)
    cv.paint(fm & ~shift(fm, 0, 1), hi)
    cv.paint(fm & ~shift(fm, 1, 0) & ~(~shift(fm, 0, 1)), hi)
    cv.paint(fm & ~shift(fm, 0, -1), lo)
    if not pressed:
        # nail heads
        for x in (3, w - 4):
            cv.px(x, off + 3, lo)
    return cv.img()


# ---------------------------------------------------------------- icons (12x12, cream on ink outline)
ICONS = {
    'ico_back': """
....#.....
...##.....
..########
.#########
..########
...##.....
....#.....
""",
    'ico_home': """
....##....
...####...
..######..
.########.
##########
.########.
.###..###.
.###..###.
.###..###.
""",
    'ico_sound': """
...#......
..##...#..
####.#..#.
####..#.#.
####..#.#.
####.#..#.
..##...#..
...#......
""",
    'ico_music': """
...#######
...#######
...##...##
...##...##
...##...##
.####.####
#####.####
.###...##.
""",
    'ico_haptic': """
...####...
#.#....#.#
#.#....#.#
..#....#..
#.#....#.#
#.#.##.#.#
...####...
""",
    'ico_motion': """
......###.
.....#####
###..#####
.....#####
#####.###.
..........
###..###..
""",
}


def gear_mask():
    S = 10
    yy, xx = np.mgrid[0:S, 0:S] + 0.5
    r = np.hypot(xx - 5, yy - 5)
    th = np.arctan2(yy - 5, xx - 5)
    teeth = (np.cos(th * 8) > 0.3) & (r < 5.1)
    m = (r < 3.9) | teeth
    m &= r > 1.6
    return m


def retry_mask():
    S = 10
    yy, xx = np.mgrid[0:S, 0:S] + 0.5
    r = np.hypot(xx - 5, yy - 5.5)
    th = np.degrees(np.arctan2(yy - 5.5, xx - 5))
    m = (r < 4.6) & (r > 2.4) & ~((th > -80) & (th < -20))
    arrow = poly_mask(S, S, [(5.5, -0.5), (9.8, 2.8), (5.5, 4.6)])
    return m | arrow


def icon(m10, slash=False):
    cv = Canvas(12, 12)
    m = np.zeros((12, 12), bool)
    h, w = m10.shape
    oy, ox = (12 - h) // 2, (12 - w) // 2
    m[oy:oy + h, ox:ox + w] = m10
    if slash:
        sl = np.zeros((12, 12), bool)
        for i in range(10):
            sl[1 + i, 10 - i] = True
            sl[1 + i, 11 - i if 11 - i < 12 else 11] = True
        cv.paint(dilate(m, 1) | dilate(sl, 1), 'ink2')
        cv.paint(m & ~dilate(sl, 1), 'cream0')
        cv.paint(sl, 'sigRed')
    else:
        cv.paint(dilate(m, 1), 'ink2')
        cv.paint(m, 'cream0')
    return cv.img()


def make_icons():
    out = {}
    out['ico_gear'] = icon(gear_mask())
    out['ico_retry'] = icon(retry_mask())
    out['ico_back'] = icon(amask(ICONS['ico_back']))
    out['ico_home'] = icon(amask(ICONS['ico_home']))
    out['ico_motion'] = icon(amask(ICONS['ico_motion']))
    for base in ('sound', 'music', 'haptic'):
        m = amask(ICONS['ico_' + base])
        out[f'ico_{base}_on'] = icon(m)
        out[f'ico_{base}_off'] = icon(m, slash=True)
    return out


# ---------------------------------------------------------------- toggles (22x12): shape + mark + colour
def toggle(on):
    W, H = 22, 12
    cv = Canvas(W, H)
    track = rrect_mask(W, H, 0, 0, W, H, (6, 6, 6, 6), p=2)
    cv.paint(track, 'ink2')
    inner = rrect_mask(W, H, 1, 1, W - 1, H - 1, (5, 5, 5, 5), p=2)
    cv.paint(inner, 'sigGreen' if on else 'char1')
    cv.paint(inner & ~shift(inner, 0, -1), 'green2' if on else 'char2')
    kx = W - 11 if on else 1
    knob = rrect_mask(W, H, kx, 1, kx + 10, H - 1, (5, 5, 5, 5), p=2)
    cv.paint(dilate(knob, 1, diag=False) & track, 'ink2')
    cv.paint(knob, 'cream0')
    cv.paint(knob & ~shift(knob, 0, -1), 'cream2')
    if on:   # "I" mark on the exposed left part
        for y in range(4, 8):
            cv.px(6, y, 'ink2')
    else:    # "O" mark on the exposed right part
        o = amask("""
.##.
#..#
#..#
.##.
""")
        cv.paint(o, 'cream1', ox=14, oy=4)
    return cv.img()


# ---------------------------------------------------------------- tap hint (pressing paw, 2 frames 16x16)
PAWHINT = """
..##..##..
.####.####
.####.####
..##..##..
##........
##.######.
.#########
.#########
..#######.
...#####..
"""


def tap_hint():
    frames = []
    for f in range(2):
        cv = Canvas(16, 16)
        m = amask(PAWHINT)
        # proper paw: toes row + pad (drawn as a soft blob)
        paw = np.zeros((16, 16), bool)
        oy = 1 if f == 0 else 3
        for (cx, cy) in ((5, 1.8), (8.2, 0.9), (11.4, 1.8), (13.4, 4.6)):
            paw |= ellipse_mask(16, 16, cx + 0.0, cy + oy + 0.6, 1.6, 1.6)
        pad_m = ellipse_mask(16, 16, 8.6, oy + 7.6, 4.4, 3.6)
        arm = blank(16, 16)
        paw |= pad_m
        if f == 1:
            for (x, y) in ((1, 13), (2, 14), (3, 15), (14, 13), (13, 14), (8, 15), (9, 15), (7, 15)):
                cv.px(x, y, 'cream0')
        cv.paint(dilate(paw, 1), 'ink2')
        cv.paint(paw, 'cream0')
        cv.paint(paw & ~shift(paw, 0, -1), 'cream2')
        beans = ellipse_mask(16, 16, 8.6, oy + 8.0, 2.2, 1.6)
        for (cx, cy) in ((5, 1.8), (8.2, 0.9), (11.4, 1.8)):
            beans |= ellipse_mask(16, 16, cx, cy + oy + 0.6, 0.8, 0.8)
        cv.paint(beans & paw, 'tabby1')
        frames.append(cv.img())
    return strip(frames)


PAW_A = """
...##..##.....
..####.####...
..####.####...
...##..##.##..
##........###.
###.#####..#..
.##########...
..##########..
..##########..
...########...
....######....
"""
BEAN_A = """
..............
...##..##.....
...##..##.....
..........##..
.##...........
.##...........
.......##.....
.....######...
.....######...
......####....
..............
"""


def tap_hint():
    """Cat paw seen from below (toe beans), frame 0 hovering, frame 1 pressed with tap ticks."""
    frames = []
    for f in range(2):
        cv = Canvas(16, 16)
        oy = 1 if f == 0 else 3
        m = amask(PAW_A)
        b = amask(BEAN_A)
        cv.paint(dilate(m, 1), 'ink2', ox=1, oy=oy)
        cv.paint(m, 'cream0', ox=1, oy=oy)
        cv.paint(b & m, 'tabby1', ox=1, oy=oy)
        if f == 1:
            for (x, y) in ((0, 12), (1, 13), (15, 12), (14, 13), (7, 15), (8, 15)):
                cv.px(x, y, 'white')
        frames.append(cv.img())
    return strip(frames)
