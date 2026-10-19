"""Display sprites for Art/UI: cue numerals, DRAW!, cue_burst, logo, banners, get_ready,
round_sign and the grade / foul stamps."""
import math
import numpy as np
from PIL import Image
from uikit import (P, Canvas, blank, rect, rrect_mask, ellipse_mask, poly_mask, shift, dilate, edge_in,
                   pad, text_mask, crop_mask, strip)
import woodtype as wt


def ascii_mask(rows):
    rows = [r for r in rows.split()] if isinstance(rows, str) else rows
    return np.array([[c != '.' for c in r] for r in rows], bool)


def ascii_paint(cv, rows, x, y, cmap):
    rows = rows.split() if isinstance(rows, str) else rows
    for j, r in enumerate(rows):
        for i, c in enumerate(r):
            if c != '.' and c in cmap:
                cv.px(x + i, y + j, cmap[c])


# ============================================================================ countdown & DRAW!
def make_cue_numeral(ch):
    m = wt.word(ch, 56)
    return wt.letter_fx(m, 'brass', depth=6, ext_dx=1)


def make_cue_draw():
    m = wt.word('DRAW!', 52, cond=0.86, gap=2, kern={'AW': -4, 'RA': -1, 'W!': 0})
    return wt.letter_fx(m, 'brass', depth=6, ext_dx=1, lean=5)


def make_cue_burst(size=160, frames=4):
    """Sunburst behind DRAW!: 12 long + 12 short rays, rotating a quarter ray-step per frame and
    swapping long/short every frame (seamless 4-frame loop). Alpha steps 25/50/75/100%."""
    N = 12
    c = size / 2.0
    yy, xx = np.mgrid[0:size, 0:size] + 0.5
    dx, dy = xx - c, yy - c
    r = np.hypot(dx, dy)
    th = np.arctan2(dy, dx)
    out = []
    step = 2 * math.pi / N
    for f in range(frames):
        cv = Canvas(size, size)
        rot = f * step / 4.0
        # faint glow disc
        cv.paint(r < 46, 'sun1', alpha=64)
        cv.paint(r < 34, 'sun0', alpha=128)
        for k in range(2 * N):
            ang = rot + k * step / 2.0
            d = np.angle(np.exp(1j * (th - ang)))
            long_ray = (k + f) % 2 == 0
            if k % 2 == 0:
                R = 78 if long_ray else 64
                hw = 0.17 * (1.0 - r / (R + 6))          # tapering half-width (radians)
            else:
                R = 58 if long_ray else 48
                hw = 0.07 * (1.0 - r / (R + 4))
            ray = (np.abs(d) < hw) & (r > 14) & (r < R)
            if k % 2 == 0:
                cv.paint(ray & (r >= R * 0.80), 'sun2', alpha=128)
                cv.paint(ray & (r < R * 0.80), 'sun1', alpha=191)
                cv.paint(ray & (r < R * 0.55), 'sun0', alpha=255)
            else:
                cv.paint(ray & (r >= R * 0.7), 'cloudW', alpha=64)
                cv.paint(ray & (r < R * 0.7), 'cloudW', alpha=128)
        # twinkling 4-point sparkles near the long-ray tips
        for k in range(0, 2 * N, 2):
            if (k // 2 + f) % 3:
                continue
            ang = rot + k * step / 2.0
            R = 70
            sx, sy = int(round(c + R * math.cos(ang))), int(round(c + R * math.sin(ang)))
            for ox, oy in ((0, 0), (1, 0), (-1, 0), (0, 1), (0, -1)):
                cv.px(sx + ox, sy + oy, 'white', 255)
            for ox, oy in ((2, 0), (-2, 0), (0, 2), (0, -2)):
                cv.px(sx + ox, sy + oy, 'sun1', 191)
        out.append(cv.img())
    return strip(out)


# ============================================================================ logo
HAT = """
.......ee..........ee.......
......eEe..........eEe......
......eEEe........eEEe......
.....eEpEe..aaaaa.eEpEe.....
.....eEpEeaaAAAAAaeEpEe.....
.....eEEEaAAAAAAAAaEEEe.....
....aaaaaAAAAAAAAAAaaaaa....
....aAAAAAAAAAAAAAAAAAAa....
...aAAAAAAAAAAAAAAAAAAAAa...
...aAAAAAAAAAAAAAAAAAAAAa...
...abbbbbbbbbbbbbbbbbbbba...
...aBBBBBBBBBBBBBBBBBBBBa...
...accccccccccccccccccccca..
aaaaCCCCCCCCCCCCCCCCCCCCCaaa
aDDDDDDDDDDDDDDDDDDDDDDDDDDa
.aDDDDDDDDDDDDDDDDDDDDDDDDa.
..aaaaaaaaaaaaaaaaaaaaaaaa..
"""


def hat_sprite():
    """Cowboy hat with two cat ears poking through the crown (the Kid's hat)."""
    rows = HAT.split()
    w = max(len(r) for r in rows)
    rows = [r.ljust(w, '.') for r in rows]
    cv = Canvas(w, len(rows))
    cmap = {'a': 'ink2', 'A': 'leath1', 'b': 'brass2', 'B': 'brass1', 'c': 'leath2', 'C': 'leath1',
            'D': 'leath0', 'e': 'ink2', 'E': 'tabby2', 'p': 'tabby0'}
    ascii_paint(cv, rows, 0, 0, cmap)
    a = cv.a
    # crown shading: lighter top-left (sun from above), darker right side
    for y in range(a.shape[0]):
        for x in range(a.shape[1]):
            if tuple(a[y, x, :3]) == P['leath1'] and a[y, x, 3]:
                if y <= 8 and x < 12:
                    a[y, x, :3] = P['leath0']
                elif x > 19:
                    a[y, x, :3] = P['leath2']
    # brim highlight row
    return cv.img()


def paw_print(cv, x, y, col='cream0', line=None):
    """7x7 paw print: three toe beans + pad."""
    rows = """
.#.#.#.
.#.#.#.
.......
..###..
.#####.
.#####.
..#.#..
"""
    rows = """
#.#.#
#.#.#
.....
.###.
#####
#####
"""
    m = ascii_mask(rows)
    if line:
        cv.paint(dilate(pad(m, 1, 1, 1, 1), 1) & ~pad(m, 1, 1, 1, 1), line, ox=x - 1, oy=y - 1)
    cv.paint(m, col, ox=x, oy=y)


def make_logo():
    H = 30
    letters = 'PAW NOON'
    # arched baseline: per-letter vertical offsets
    masks = []
    for ch in letters:
        if ch == ' ':
            masks.append(None)
            continue
        masks.append(wt.glyph(ch, H, cond=0.80))
    gap = 2
    space = 8
    xs = []
    x = 0
    for i, m in enumerate(masks):
        if m is None:
            xs.append(x)
            x += space
            continue
        xs.append(x)
        x += m.shape[1] + gap
    total = x - gap
    arch = 5
    Wm = total + 4
    Hm = H + arch + 2
    word = np.zeros((Hm, Wm), bool)
    cx = total / 2.0
    for m, x0 in zip(masks, xs):
        if m is None:
            continue
        mid = x0 + m.shape[1] / 2.0
        off = int(round(arch * ((mid - cx) / cx) ** 2))
        word[off:off + H, x0:x0 + m.shape[1]] |= m
    face_img = wt.letter_fx(word, 'brass', depth=4, ext_dx=1, margin=0)
    # compose: hat above, wordmark below
    hat = hat_sprite(58)
    W, Hc = 184, 88
    cv = Canvas(W, Hc)
    wx = (W - face_img.width) // 2
    wy = Hc - face_img.height - 6
    # rope flourish under the word (two-tone twisted rope)
    ry = Hc - 5
    for xx in range(14, W - 14):
        cv.px(xx, ry, 'ink2')
        cv.px(xx, ry + 2, 'ink2')
        cv.px(xx, ry + 1, 'wood0' if (xx // 2) % 2 == 0 else 'wood2')
    for ex in (12, 13, W - 14, W - 13):
        for yy in range(ry - 1, ry + 4):
            cv.px(ex, yy, 'ink2')
    cv.blit(face_img, wx, wy)
    hx = (W - hat.width) // 2
    cv.blit(hat, hx, max(0, wy - hat.height + 7))
    # paw prints in the two O counters: find the O glyph positions
    return cv.img(), (wx, wy, face_img)


# ============================================================================ banners
def ribbon(w, h, face='paintRed', hi='red0', dark='paintRedD', line='ink', tail='swallow'):
    """Horizontal ribbon with folded tails. Returns Canvas."""
    tw = 14
    cv = Canvas(w, h + 6)
    # tails (behind), lowered by 4
    for side in (0, 1):
        tm = blank(w, h + 6)
        x0 = 0 if side == 0 else w - tw - 4
        rect(tm, x0, 4, x0 + tw + 4, 4 + h - 2)
        if tail == 'swallow':
            notch = poly_mask(w, h + 6, [(x0 - 1 if side == 0 else x0 + tw + 5, 4),
                                          (x0 + 6 if side == 0 else x0 + tw - 2, 4 + (h - 2) / 2.0),
                                          (x0 - 1 if side == 0 else x0 + tw + 5, 4 + h - 2)])
            tm &= ~notch
        else:   # torn: ragged steps
            for yy in range(4, 4 + h - 2):
                cut = [0, 2, 1, 3, 1, 0, 2, 3, 1, 2, 0, 1][yy % 12]
                if side == 0:
                    tm[yy, x0:x0 + cut] = False
                else:
                    tm[yy, x0 + tw + 4 - cut:x0 + tw + 4] = False
        cv.paint(dilate(tm, 1), line)
        cv.paint(tm, dark)
    # main band
    bm = blank(w, h + 6)
    rect(bm, tw - 2, 0, w - tw + 2, h)
    cv.paint(dilate(bm, 1), line)
    cv.paint(bm, face)
    cv.paint(edge_in(bm, 0, -1) | shift(edge_in(bm, 0, -1), 0, 1) & bm, hi)
    cv.paint(edge_in(bm, 0, 1), dark)
    # fold shadows where band meets tails
    for fx in (tw - 2, w - tw + 1):
        for yy in range(1, h - 1):
            cv.px(fx, yy, dark)
    return cv


def star_mask(R, r_in, n=5, rot=-math.pi / 2, size=None):
    size = size or int(2 * R + 2)
    c = size / 2.0
    pts = []
    for i in range(2 * n):
        rr = R if i % 2 == 0 else r_in
        a = rot + i * math.pi / n
        pts.append((c + rr * math.cos(a), c + rr * math.sin(a)))
    return poly_mask(size, size, pts)


def sheriff_star(cv, cx, cy, R=6, body='brass1', hi='brass0', lo='brass2', line='ink'):
    size = 2 * R + 3
    m = star_mask(R, R * 0.5, size=size)
    ox, oy = cx - size // 2, cy - size // 2
    cv.paint(dilate(m, 1), line, ox=ox, oy=oy)
    cv.paint(m, body, ox=ox, oy=oy)
    upper = m.copy()
    upper[size // 2:] = False
    cv.paint(upper & ~edge_in(m, 0, 1), hi, ox=ox, oy=oy)
    cv.paint(edge_in(m, 0, 1) | edge_in(m, 1, 0), lo, ox=ox, oy=oy)
    # ball tips
    for i in range(5):
        a = -math.pi / 2 + i * 2 * math.pi / 5
        tx, ty = int(round(cx + (R + 0.5) * math.cos(a))), int(round(cy + (R + 0.5) * math.sin(a)))
        cv.px(tx, ty, hi)


def make_banner(text, scheme, rib, tail, deco):
    m = wt.word(text, 30, cond=0.92, gap=2, kern={'AT': -1, 'TE': -1, 'FE': -1, 'EA': 0})
    word = wt.letter_fx(m, scheme, depth=3, ext_dx=1, margin=0)
    W = max(250, word.width + 48)
    rh = 24
    rb = ribbon(W, rh, **rib, tail=tail)
    Hc = word.height + 8
    cv = Canvas(W, Hc)
    ry = (Hc - rh) // 2 + 3
    cv.blit(rb.img(), 0, ry)
    if deco == 'stars':
        sheriff_star(cv, 18, ry + rh // 2 - 1, R=6)
        sheriff_star(cv, W - 19, ry + rh // 2 - 1, R=6)
    cv.blit(word, (W - word.width) // 2, 1)
    return cv.img()


def make_get_ready():
    m = wt.word('GET READY', 20, cond=0.95, gap=1, kern={'ET': -1, 'T ': 0})
    return wt.letter_fx(m, 'cream', depth=2, ext_dx=1, margin=0)


# ============================================================================ round sign
def make_round_sign(w=56, h=18):
    cv = Canvas(w, h)
    m = rrect_mask(w, h, 0, 0, w, h, (2, 2, 2, 2), p=2)
    inner = m & ~dilate(~m, 1)
    cv.paint(m, 'ink2')
    cv.paint(inner, 'wood1')
    # planks: two horizontal boards
    split = h // 2
    for x in range(1, w - 1):
        cv.px(x, split, 'wood3')
    top = inner.copy()
    top[2:] = False
    cv.paint(top & ~edge_in(inner, 0, -1) | edge_in(inner, 0, -1), 'wood0')
    cv.paint(edge_in(inner, 0, 1), 'wood2')
    cv.paint(shift(edge_in(inner, 0, 1), 0, -1) & inner, 'wood2')
    for x in range(1, w - 1):
        cv.px(x, split + 1, 'wood0')
    # grain: short dark dashes at fixed rhythm (not noise)
    for (gx, gy, L) in ((6, 4, 5), (30, 3, 7), (44, 6, 4), (14, 12, 6), (37, 13, 5), (22, 7, 3)):
        for i in range(L):
            cv.px(gx + i, gy, 'wood2')
    # nails
    for nx in (3, w - 4):
        for ny in (4, h - 5):
            cv.px(nx, ny, 'steel1')
    return cv.img()


# ============================================================================ stamps
BOLT = """
...##
..##.
.##..
#####
..##.
.##..
##...
"""
SNAIL = """
..####....
.##..##...
##.##.##..
##.#..##..
.##..##...
#########.
###########
"""
TORTOISE = """
...####.....
..######....
.##.##.##...
#########.##
#.#....#.###
"""
STAR7 = """
...#...
...#...
#######
.#####.
..###..
.##.##.
.#...#.
"""
JAM = """
#....#
.#..#.
..##..
..##..
.#..#.
#....#
"""


def stamp(text, fill, shape, icon=None, icon_cmap=None, h=24, text_col='ink2', line='ink2',
          extra_w=0):
    tm = text_mask(text, 'font_medium')
    tw = tm.shape[1]
    iw = 0
    if icon:
        im_rows = icon.split()
        iw = len(im_rows[0]) + 3
    pad_x = 8 if shape in ('zigzag', 'swallow') else 6
    W = tw + iw + 2 * pad_x + extra_w
    if shape == 'swallow':
        W += 10
    Hh = h
    cv = Canvas(W + 2, Hh + 2)
    plate = blank(W + 2, Hh + 2)
    x0, y0, x1, y1 = 1, 1, W + 1, Hh - 1
    if shape == 'rect':          # GOOD: notched rectangle
        rect(plate, x0, y0, x1, y1)
        for (cx, cy) in ((x0, y0), (x1 - 1, y0), (x0, y1 - 1), (x1 - 1, y1 - 1)):
            plate[cy, cx] = False
        plate[y0, x0 + 1] = plate[y0 + 1, x0] = False
        plate[y0, x1 - 2] = plate[y0 + 1, x1 - 1] = False
        plate[y1 - 1, x0 + 1] = plate[y1 - 2, x0] = False
        plate[y1 - 1, x1 - 2] = plate[y1 - 2, x1 - 1] = False
    elif shape == 'pill':        # GREAT: rounded pill
        plate |= rrect_mask(W + 2, Hh + 2, x0, y0, x1, y1, ((y1 - y0) // 2,) * 4, p=2)
    elif shape == 'zigzag':      # LIGHTNING: saw-tooth top and bottom edge
        rect(plate, x0, y0 + 2, x1, y1 - 2)
        for x in range(x0, x1):
            t = (x - x0) % 6
            hgt = [0, 1, 2, 2, 1, 0][t]
            plate[y0 + 2 - hgt:y0 + 2, x] = True
            plate[y1 - 2:y1 - 2 + hgt, x] = True
    elif shape == 'swallow':     # PERFECT: ribbon with swallowtail ends
        rect(plate, x0, y0, x1, y1)
        mid = (y0 + y1) / 2.0
        plate &= ~poly_mask(W + 2, Hh + 2, [(x0 - 0.5, y0), (x0 + 5, mid), (x0 - 0.5, y1)])
        plate &= ~poly_mask(W + 2, Hh + 2, [(x1 + 0.5, y0), (x1 - 5, mid), (x1 + 0.5, y1)])
    elif shape == 'oval':        # SLOW / SLOWPOKE: ellipse-ish
        plate |= ellipse_mask(W + 2, Hh + 2, (x0 + x1) / 2.0, (y0 + y1) / 2.0, (x1 - x0) / 2.0,
                              (y1 - y0) / 2.0, p=3.2)
    elif shape == 'hazard':      # TOO EARLY: rect with hazard stripes border
        rect(plate, x0, y0, x1, y1)
    elif shape == 'dashed':      # NO SHOT
        rect(plate, x0, y0, x1, y1)
        plate[y0, x0] = plate[y0, x1 - 1] = plate[y1 - 1, x0] = plate[y1 - 1, x1 - 1] = False
    # outline + fill + inner rubber-stamp border line (inset 2)
    full = dilate(plate, 1, diag=False)
    cv.paint(full & ~plate, line)
    cv.paint(plate, fill)
    ring = (plate & ~dilate(~plate, 2, diag=False)) & dilate(~plate, 3, diag=False)
    yy, xx = np.mgrid[0:Hh + 2, 0:W + 2]
    if shape == 'hazard':
        band = plate & dilate(~plate, 3, diag=False)
        cv.paint(band & (((xx + yy) // 3) % 2 == 0), 'ink2')
    elif shape == 'dashed':
        cv.paint(ring & (((xx + yy) // 2) % 2 == 0), line)
    else:
        cv.paint(ring, line)
    # highlight row under the inner line (stamp ink is flat, keep subtle)
    # content
    cx = (W + 2) // 2
    total = tw + iw
    tx = cx - total // 2 + iw
    ty = (Hh + 2) // 2 - 4 if shape != 'zigzag' else (Hh + 2) // 2 - 4
    if icon:
        rows = icon.split()
        ih = len(rows)
        ix = cx - total // 2
        iy = (Hh + 2) // 2 - ih // 2 - (0 if ih % 2 else 1) + 0
        for j, r in enumerate(rows):
            for i, ch in enumerate(r):
                if ch != '.':
                    cv.px(ix + i, iy + j, (icon_cmap or {}).get(ch, text_col))
    cv.paint(tm, text_col, ox=tx, oy=ty)
    return cv.img()


def make_stamps():
    out = {}
    out['grade_lightning'] = stamp('LIGHTNING!', 'sigBlue', 'zigzag', BOLT, h=24)
    out['grade_perfect'] = stamp('PERFECT!', 'sigGreen', 'swallow', STAR7, h=22)
    out['grade_great'] = stamp('GREAT!', 'sigYellow', 'pill', None, h=20, extra_w=4)
    out['grade_good'] = stamp('GOOD', 'sigOrange', 'rect', None, h=20, extra_w=4)
    out['grade_slow'] = stamp('SLOW', 'sigRed', 'oval', SNAIL, h=22, extra_w=6)
    out['grade_slowpoke'] = stamp('SLOWPOKE', 'sigRed', 'oval', TORTOISE, h=22, extra_w=8)
    out['stamp_too_early'] = stamp('TOO EARLY!', 'sigOrange', 'hazard', JAM, h=24)
    out['stamp_no_shot'] = stamp('NO SHOT', 'char0', 'dashed', None, h=22)
    return out


# ============================================================================ v2 overrides (burst, hat, logo)
def make_cue_burst(size=200, frames=4):
    k = size / 160.0
    """Comic-western explosion behind DRAW!: opaque jagged elliptical starburst (sun tones,
    orange/red rim) plus translucent rays. Flattened (ry = 0.66 rx) so it stays above the bandit.
    4-frame loop: spikes alternate phase, rays rotate a quarter step. Alpha 25/50/100%."""
    c = size / 2.0
    yy, xx = np.mgrid[0:size, 0:size] + 0.5
    dx, dy = (xx - c), (yy - c) / 0.66
    r = np.hypot(dx, dy)
    th = np.arctan2(dy, dx)
    N = 18
    out = []
    for f in range(frames):
        cv = Canvas(size, size)
        rot = f * (2 * math.pi / 24) / 4.0
        for k_ in range(24):
            ang = rot + k_ * 2 * math.pi / 24
            d = np.angle(np.exp(1j * (th - ang)))
            R = (80 if (k_ + f) % 2 == 0 else 70) * k
            hw = 0.10 * (1.0 - r / (R + 4))
            ray = (np.abs(d) < hw) & (r < R)
            cv.paint(ray & (r >= 66 * k), 'sun1', alpha=64)
            cv.paint(ray & (r < 66 * k), 'sun1', alpha=128)
        ph = (f % 2) * math.pi / N
        amp = 1.0 + 0.05 * (f in (1, 2))
        spike = np.abs(((th - ph) * N / (2 * math.pi)) % 1.0 - 0.5) * 2
        Rp = (48 + 14 * spike) * amp * k
        cv.paint(r < Rp, 'red1')
        cv.paint(r < Rp - 3, 'sigOrange')
        cv.paint(r < Rp - 7, 'sun2')
        cv.paint(r < (Rp - 7) * 0.80, 'sun1')
        cv.paint(r < 32 * amp * k, 'sun0')
        for k_ in range(N):
            if (k_ + f) % 4:
                continue
            ang = ph + (k_ + 0.5) * 2 * math.pi / N
            R = 70 * amp * k
            sx = int(round(c + R * math.cos(ang)))
            sy = int(round(c + 0.66 * R * math.sin(ang)))
            for ox, oy in ((0, 0), (1, 0), (-1, 0), (0, 1), (0, -1)):
                cv.px(sx + ox, sy + oy, 'white', 255)
            for ox, oy in ((2, 0), (-2, 0), (0, 2), (0, -2)):
                cv.px(sx + ox, sy + oy, 'sun1', 191)
        out.append(cv.img())
    return strip(out)


def hat_sprite(W=52):
    """The Kid's cowboy hat with two cat ears poking through the crown."""
    H = 26
    cv = Canvas(W, H)
    c = W / 2.0
    X = np.mgrid[0:H, 0:W][1]
    crown = rrect_mask(W, H, c - 13, 6, c + 13, 21, (5, 5, 0, 0), p=2.2)
    crown &= ~ellipse_mask(W, H, c, 5.5, 3, 2.2)
    brim = ellipse_mask(W, H, c, 20.5, W / 2.0 - 1, 3.6, p=2.2)
    for sx in (-1, 1):
        brim |= ellipse_mask(W, H, c + sx * (W / 2.0 - 4), 17.5, 3.2, 3.6)
    ears = [poly_mask(W, H, [(c + sx * 13.5, 9), (c + sx * 10.5, -0.5), (c + sx * 5.5, 8)]) for sx in (-1, 1)]
    allm = crown | brim | ears[0] | ears[1]
    cv.paint(dilate(allm, 1), 'ink2')
    for e, sx in zip(ears, (-1, 1)):
        cv.paint(e, 'tabby2')
        cv.paint(edge_in(e, sx, 0), 'tabby3')
        inner = poly_mask(W, H, [(c + sx * 11.5, 8), (c + sx * 10.5, 3), (c + sx * 8, 8)])
        cv.paint(inner & e & ~edge_in(e, 0, -1), 'tabby0')
    cv.paint(crown, 'leath1')
    cv.paint(crown & (X < c - 3), 'leath0')
    cv.paint(crown & (X >= c + 8), 'leath2')
    band = crown.copy(); band[:15] = False; band[18:] = False
    cv.paint(band, 'brass1')
    cv.paint(band & ~shift(band, 0, 1), 'brass0')
    cv.paint(band & ~shift(band, 0, -1), 'brass2')
    cv.paint(brim & ~crown, 'leath1')
    cv.paint(edge_in(brim, 0, -1) & ~crown, 'leath0')
    cv.paint(shift(edge_in(brim, 0, 1), 0, -1) & brim & ~crown, 'leath2')
    cv.paint(edge_in(brim, 0, 1), 'leath3')
    return cv.img()


PAW = """
#.#.#
#.#.#
.....
.###.
#####
#####
"""


def make_logo():
    H = 35
    letters = 'PAW NOON'
    masks, xs = [], []
    x = 0
    for ch in letters:
        if ch == ' ':
            masks.append(None); xs.append(x); x += 14
            continue
        m = wt.glyph(ch, H, weight=0.84, cond=0.96 if ch == 'O' else 0.84)
        masks.append(m); xs.append(x); x += m.shape[1] + 1
    total = x - 1
    arch = 4
    word = np.zeros((H + arch, total), bool)
    cx = total / 2.0
    opos = []
    for ch, m, x0 in zip(letters, masks, xs):
        if m is None:
            continue
        off = int(round(arch * ((x0 + m.shape[1] / 2.0 - cx) / cx) ** 2))
        word[off:off + H, x0:x0 + m.shape[1]] |= m
        if ch == 'O':
            opos.append((x0 + m.shape[1] // 2, off + H // 2))
    face = wt.letter_fx(word, 'brass', depth=4, ext_dx=1, margin=0)
    W, Hc = 240, 112
    assert face.width <= W, face.width
    cv = Canvas(W, Hc)
    wx = (W - face.width) // 2
    wy = Hc - face.height - 7
    ry = Hc - 5
    for xx in range(16, W - 16):
        cv.px(xx, ry, 'ink2'); cv.px(xx, ry + 2, 'ink2')
        cv.px(xx, ry + 1, 'wood0' if (xx // 2) % 2 == 0 else 'wood2')
    for ex in (14, 15, W - 16, W - 15):
        for yy in range(ry - 1, ry + 4):
            cv.px(ex, yy, 'ink2')
    cv.blit(face, wx, wy)
    pm = ascii_mask(PAW)
    for (ox, oy) in opos:
        px_, py_ = wx + 2 + ox - 2, wy + 2 + oy - 3
        cv.paint(dilate(pad(pm, 1, 1, 1, 1), 1), 'ink2', ox=px_ - 1, oy=py_ - 1)
        cv.paint(pm, 'cream0', ox=px_, oy=py_)
    hat = hat_sprite(64)
    cv.blit(hat, (W - hat.width) // 2, max(0, wy - hat.height + 9))
    return cv.img(), (wx, wy, face)
