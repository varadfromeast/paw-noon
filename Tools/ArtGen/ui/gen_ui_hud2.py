"""Art direction v2 HUD (reference style): ornate portrait frames with name plates, chunky red
health bars with brass/wood end caps, and the centre "HIGH NOON" badge that holds the 3/2/1
numerals (medallion window) and the live timer (bezel window)."""
import math
import numpy as np
from uikit import Canvas, blank, rect, rrect_mask, ellipse_mask, poly_mask, shift, dilate, edge_in, draw_text


def bevel(cv, m, base, hi, lo, hi2=None):
    cv.paint(m, base)
    cv.paint(m & ~shift(m, 0, 1), hi)
    cv.paint(m & ~shift(m, 1, 0) & ~edge_in(m, 0, 1), hi)
    if hi2:
        cv.paint(m & ~shift(m, 0, 2) & ~(m & ~shift(m, 0, 1)) & ~edge_in(m, 0, 1) & ~edge_in(m, -1, 0), hi2)
    cv.paint(m & ~shift(m, 0, -1), lo)
    cv.paint(m & ~shift(m, -1, 0) & ~(m & ~shift(m, 0, 1)), lo)


def rivet(cv, x, y):
    cv.px(x, y, 'brass0'); cv.px(x + 1, y, 'brass1'); cv.px(x, y + 1, 'brass1'); cv.px(x + 1, y + 1, 'brass3')


# ---------------------------------------------------------------- portrait frame 44x44, window 36x36 at (4,4)
def portrait_frame():
    S = 44
    cv = Canvas(S, S)
    outer = rrect_mask(S, S, 0, 0, S, S, (3, 3, 3, 3), p=2)
    win = blank(S, S); win[4:40, 4:40] = True
    cv.paint(outer & ~win, 'ink')
    wood = rrect_mask(S, S, 1, 1, S - 1, S - 1, (2, 2, 2, 2), p=2) & ~dilate(win, 1, diag=False)
    bevel(cv, wood, 'wood2', 'wood0', 'wood4')
    # wood grain dashes
    for (x, y) in ((8, 2), (20, 1), (30, 2), (10, 41), (24, 42), (34, 41)):
        for i in range(3):
            cv.px(x + i, y, 'wood3')
    brass = dilate(win, 1, diag=False) & ~win
    cv.paint(brass, 'brass1')
    cv.paint(brass & ~shift(brass, 0, 1) & (np.mgrid[0:S, 0:S][0] < 6), 'brass0')
    cv.paint(brass & (np.mgrid[0:S, 0:S][0] > 38), 'brass2')
    # corner brass plates with rivets
    for (x, y) in ((0, 0), (S - 7, 0), (0, S - 7), (S - 7, S - 7)):
        m = blank(S, S); m[y:y + 7, x:x + 7] = True
        m &= outer
        cv.paint(m, 'ink')
        inner = m & ~dilate(~m, 1, diag=False)
        bevel(cv, inner, 'brass1', 'brass0', 'brass3')
        rivet(cv, x + 3 - (0 if x == 0 else 1) + (0 if x == 0 else 1), y + 3 - (0 if y == 0 else 1) + (0 if y == 0 else 1))
    return cv.img()


def portrait_backdrop():
    """36x36 sky backdrop for the window (portraits are 28-36 px; centre them on this)."""
    cv = Canvas(36, 36)
    yy = np.mgrid[0:36, 0:36][0]
    for y0, c in ((0, 'sky2'), (12, 'sky3'), (24, 'sky4'), (30, 'rockFar1')):
        cv.paint(yy >= y0, c)
    return cv.img()


def name_plate():
    """9-slice dark plank with brass edge for names; 18x14, border [5,5,5,5]."""
    W, H = 18, 14
    cv = Canvas(W, H)
    m = rrect_mask(W, H, 0, 0, W, H, (2, 2, 2, 2), p=2)
    cv.paint(m, 'ink')
    b = m & ~edge_in(m, 0, 1) & ~edge_in(m, 0, -1) & ~edge_in(m, 1, 0) & ~edge_in(m, -1, 0)
    cv.paint(b, 'brass2')
    cv.paint(b & ~shift(b, 0, 1), 'brass0')
    inner = b & ~dilate(~b, 1, diag=False)
    cv.paint(inner, 'wood4')
    cv.paint(inner & ~shift(inner, 0, 1), 'wood3')
    cv.paint(inner & ~shift(inner, 0, -1), 'uiWoodDeep')
    return cv.img()


# ---------------------------------------------------------------- chunky HP bar 86x18, window 62x8 at (12,5)
HP_W, HP_H = 86, 18
HP_IN = (12, 5, 62, 8)


def hp_frame():
    cv = Canvas(HP_W, HP_H)
    x, y, w, h = HP_IN
    body = blank(HP_W, HP_H); body[2:16, 7:HP_W - 7] = True
    # pointed brass end caps (arrowhead) + wood collar
    capL = poly_mask(HP_W, HP_H, [(0, 9), (6, 2), (12, 2), (12, 16), (6, 16)])
    capR = poly_mask(HP_W, HP_H, [(HP_W, 9), (HP_W - 6, 2), (HP_W - 12, 2), (HP_W - 12, 16), (HP_W - 6, 16)])
    allm = body | capL | capR
    cv.paint(dilate(allm, 1, diag=False), 'ink')
    cv.paint(allm, 'ink')
    rim = allm & ~edge_in(allm, 0, 1) & ~edge_in(allm, 0, -1) & ~edge_in(allm, 1, 0) & ~edge_in(allm, -1, 0)
    bevel(cv, rim & body & ~capL & ~capR, 'wood2', 'wood0', 'wood4')
    for cap in (capL, capR):
        c = cap & rim
        bevel(cv, c, 'brass1', 'brass0', 'brass3')
    # collar bands where caps meet the body
    for cx in (12, HP_W - 13):
        for yy in range(3, 15):
            cv.px(cx, yy, 'brass2' if 4 <= yy <= 13 else 'brass3')
    # diamond studs on caps
    for cx in (6, HP_W - 7):
        for (dx, dy, c) in ((0, -1, 'brass0'), (-1, 0, 'brass0'), (0, 0, 'uiBrassHi'), (1, 0, 'brass2'), (0, 1, 'brass2')):
            cv.px(cx + dx, 9 + dy, c)
    # window (dark red-black, shows when HP drains)
    win = blank(HP_W, HP_H); win[y:y + h, x:x + w] = True
    cv.paint(dilate(win, 1, diag=False), 'ink')
    cv.paint(win, 'uiBarBg')
    cv.paint(win & ~shift(win, 0, -1), 'uiBarBg2')
    for q in (1, 2, 3):
        cv.px(x + round(w * q / 4), y + h - 1, 'ink2')
    # nails on the wood
    for nx in (17, HP_W - 19):
        cv.px(nx, 3, 'steel1')
        cv.px(nx, 14, 'steel2')
    return cv.img()


def hp_strip(body, hi, hi2, lo, lo2):
    w, h = HP_IN[2], HP_IN[3]
    cv = Canvas(w, h)
    rows = [hi, hi2, body, body, body, body, lo2, lo]
    for i, c in enumerate(rows[:h]):
        m = np.zeros((h, w), bool); m[i] = True
        cv.paint(m, c)
    # chunky segment notches every 8 px (bullet-belt rhythm)
    for sx in range(7, w, 8):
        cv.px(sx, 1, lo2); cv.px(sx, 6, lo2)
    return cv.img()


# ---------------------------------------------------------------- the HIGH NOON badge
BADGE_W, BADGE_H = 152, 136
MED = (16, 20, 136, 98)       # medallion outer rect (x0,y0,x1,y1)
NUM_WIN = (24, 27, 104, 64)   # numeral window (x,y,w,h) -> centre (76, 59)
TIMER_WIN = (22, 102, 108, 20)


def scroll_curl(cv, cx, cy, side):
    """Brass C-scroll flourish beside the medallion."""
    W, H = cv.w, cv.h
    r_out = ellipse_mask(W, H, cx, cy, 7, 9)
    r_in = ellipse_mask(W, H, cx + side * 1, cy + 1, 4, 6)
    curl = r_out & ~r_in
    yy, xx = np.mgrid[0:H, 0:W]
    curl &= ~((xx - cx) * side > 2) | (yy > cy + 3)
    ball = ellipse_mask(W, H, cx + side * 1, cy + 1, 2.2, 2.2)
    m = curl | ball
    cv.paint(dilate(m, 1, diag=False), 'ink')
    bevel(cv, m, 'brass1', 'brass0', 'brass3')


def hud_badge():
    W, H = BADGE_W, BADGE_H
    cv = Canvas(W, H)
    x0, y0, x1, y1 = MED
    # side scrolls (behind)
    scroll_curl(cv, 10, 52, -1)
    scroll_curl(cv, W - 11, 52, 1)
    scroll_curl(cv, 12, 80, -1)
    scroll_curl(cv, W - 13, 80, 1)
    # timer bezel (behind medallion bottom)
    tx, ty, tw, th = TIMER_WIN
    bez = rrect_mask(W, H, tx - 4, ty - 6, tx + tw + 4, ty + th + 4, (4, 4, 6, 6), p=2)
    cv.paint(dilate(bez, 1, diag=False), 'ink')
    bez_rim = bez & ~rrect_mask(W, H, tx - 1, ty - 1, tx + tw + 1, ty + th + 1, (2, 2, 2, 2), p=2)
    bevel(cv, bez_rim, 'wood2', 'wood0', 'wood4')
    tm = rrect_mask(W, H, tx, ty, tx + tw, ty + th, (2, 2, 2, 2), p=2)
    cv.paint(dilate(tm, 1, diag=False) & ~tm & bez, 'brass2')
    cv.paint(tm, 'uiBadgeIn')
    cv.paint(tm & ~shift(tm, 0, 2), 'ink2')
    for (rx, ry) in ((tx - 3, ty + th // 2 - 1), (tx + tw + 1, ty + th // 2 - 1)):
        rivet(cv, rx, ry)
    # medallion: ink, brass ring (4px), wood inner ring (2px), dark interior
    med = rrect_mask(W, H, x0, y0, x1, y1, (22, 22, 22, 22), p=2.6)
    cv.paint(dilate(med, 1), 'ink')
    ring = med & ~rrect_mask(W, H, x0 + 4, y0 + 4, x1 - 4, y1 - 4, (18, 18, 18, 18), p=2.6)
    cv.paint(ring, 'brass1')
    yy, xx = np.mgrid[0:H, 0:W]
    cv.paint(ring & (yy < y0 + 12), 'brass0')
    cv.paint(ring & (yy > y1 - 12), 'brass2')
    cv.paint(ring & edge_in(med, 0, -1), 'uiBrassHi')
    cv.paint(ring & edge_in(med, 0, 1), 'brass3')
    inner_edge = med & ~rrect_mask(W, H, x0 + 3, y0 + 3, x1 - 3, y1 - 3, (19, 19, 19, 19), p=2.6) & ~ring | \
        (rrect_mask(W, H, x0 + 4, y0 + 4, x1 - 4, y1 - 4, (18, 18, 18, 18), p=2.6) & ~rrect_mask(W, H, x0 + 5, y0 + 5, x1 - 5, y1 - 5, (17, 17, 17, 17), p=2.6))
    cv.paint(inner_edge, 'ink')
    wood = rrect_mask(W, H, x0 + 5, y0 + 5, x1 - 5, y1 - 5, (17, 17, 17, 17), p=2.6) & ~rrect_mask(W, H, x0 + 7, y0 + 7, x1 - 7, y1 - 7, (15, 15, 15, 15), p=2.6)
    cv.paint(wood, 'wood3')
    cv.paint(wood & (yy < (y0 + y1) // 2), 'wood2')
    interior = rrect_mask(W, H, x0 + 7, y0 + 7, x1 - 7, y1 - 7, (15, 15, 15, 15), p=2.6)
    cv.paint(interior, 'uiBadgeIn')
    cv.paint(interior & (yy < y0 + 7 + 3), 'ink2')
    # stepped lower glow band (warm, the "sun" behind the numbers)
    cv.paint(interior & (yy > y1 - 22) & (((xx + yy) % 2 == 0) | (yy > y1 - 18)), 'uiBadgeIn2')
    # rivets around the ring
    for ang in range(0, 360, 30):
        a = math.radians(ang)
        rx = int(round((x0 + x1) / 2 + ((x1 - x0) / 2 - 2.5) * math.cos(a))) - 1
        ry = int(round((y0 + y1) / 2 + ((y1 - y0) / 2 - 2.5) * math.sin(a))) - 1
        if 60 < ang < 120 or 240 < ang < 300:
            continue
        rivet(cv, rx, ry)
    # top plaque "HIGH NOON"
    pw, ph = 84, 17
    px, py = (W - pw) // 2, 3
    plq = rrect_mask(W, H, px, py, px + pw, py + ph, (3, 3, 3, 3), p=2)
    wings = poly_mask(W, H, [(px - 8, py + ph / 2), (px + 2, py + 1), (px + 2, py + ph - 1)]) | \
        poly_mask(W, H, [(px + pw + 8, py + ph / 2), (px + pw - 2, py + 1), (px + pw - 2, py + ph - 1)])
    cv.paint(dilate(plq | wings, 1, diag=False), 'ink')
    bevel(cv, wings, 'brass1', 'brass0', 'brass3')
    pr = plq & ~rrect_mask(W, H, px + 2, py + 2, px + pw - 2, py + ph - 2, (1, 1, 1, 1), p=2)
    bevel(cv, pr, 'brass1', 'brass0', 'brass3')
    pin = rrect_mask(W, H, px + 2, py + 2, px + pw - 2, py + ph - 2, (1, 1, 1, 1), p=2)
    cv.paint(pin, 'wood4')
    cv.paint(pin & ~shift(pin, 0, 1), 'wood3')
    draw_text(cv, 'HIGH NOON', 'font_medium', W // 2, py + 5, 'brass0', outline='ink', align='center')
    # sheriff star pendant at the bottom
    from gen_ui_display import sheriff_star
    sheriff_star(cv, W // 2, H - 7, R=6)
    return cv.img()
