"""buildings_left (SALOON + HOTEL) and buildings_right (GUNS + BANK) in one-point perspective.

World: X right, Y up, Z depth (m).  Left facades X=-3.6 (lit by the high right sun), right
facades X=+3.6 (in shade).  Porch posts / boardwalk street edge at X=+-2.4, porch roofs at 3 m.
Frontal side walls (Z=const) that rise above a lower neighbour carry painted ghost signs.
"""
import math
import numpy as np
from envlib import np_outline, Canvas, W, H, VPX, VPY, FOCAL as F, EYE, proj, kz, col, rng, draw_text, \
    text_width, GLYPHS
from planes import facade, frontal, horizontal, post, recede_line, hline_world, FP

XFL, XPL = 3.4, 2.3     # left facade / post line offsets
XFR, XPR = 2.9, 1.9     # right facade / post line offsets
XF, XP = XFR, XPR       # (right row uses XF/XP below)
BW = 0.32       # boardwalk height
PR = 3.3        # porch roof height

LEFT_POSTS = [3.4, 5.0, 6.9, 8.6, 10.3, 11.9]
RIGHT_POSTS = [3.6, 5.2, 6.9, 8.5]

# 7px-tall ghost-sign lettering (painted on frontal side walls)
BIG = {
    'H': ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
    'O': ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
    'T': ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    'E': ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
    'L': ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
    'B': ["1110", "1001", "1001", "1110", "1001", "1001", "1110"],
    'A': ["0110", "1001", "1001", "1111", "1001", "1001", "1001"],
    'N': ["1001", "1101", "1101", "1011", "1011", "1001", "1001"],
    'K': ["1001", "1010", "1100", "1100", "1010", "1001", "1001"],
    'S': ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
    'G': ["01111", "10000", "10000", "10011", "10001", "10001", "01111"],
    'U': ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
    'W': ["10001", "10001", "10001", "10101", "10101", "11011", "10001"],
    'D': ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
    '$': ["00100", "01111", "10100", "01110", "00101", "11110", "00100"],
    '5': ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
    '0': ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
}


def big_text(cv, s, x, y, c, shadow=None):
    cx = x
    for ch in s:
        g = BIG[ch]
        for gy, row in enumerate(g):
            for gx, v in enumerate(row):
                if v == '1':
                    if shadow:
                        cv.px(cx + gx + 1, y + gy + 1, shadow)
        cx += len(g[0]) + 1
    cx = x
    for ch in s:
        g = BIG[ch]
        for gy, row in enumerate(g):
            for gx, v in enumerate(row):
                if v == '1':
                    cv.px(cx + gx, y + gy, c)
        cx += len(g[0]) + 1
    return cx - 1


def big_w(s):
    return sum(len(BIG[c][0]) for c in s) + len(s) - 1


def wood_grain(fp, step, seed, base, line, hi, dark):
    """Clapboard texture with butt joints, nails, grain streaks and weathered planks."""
    k = fp.k
    if boundary(fp, step) and k * step > 2.2:
        return line
    n = math.floor(fp.vc / step)
    jo = 2.3 * H01(n, seed)
    if boundary_u(fp, 2.3, -jo) and k > 16:
        return line
    seg = math.floor((fp.uc + jo) / 2.3)
    frac = (fp.vc / step) - n
    # lit lip under the joint line above
    if k * step > 6 and frac > 1 - 1.2 / (k * step):
        return hi
    t = H01(n, seg, seed + 7)
    # grain streak at mid-board along part of the plank
    if k * step >= 7 and abs(frac - (0.35 + 0.3 * t)) < 0.5 / (k * step):
        zz = (fp.uc + jo) / 2.3 - seg
        if 0.15 + 0.3 * t < zz < 0.55 + 0.4 * t:
            return dark
    # nail heads just after butt joints
    if k > 28 and k * step >= 8 and abs(frac - 0.5) < 0.5 / (k * step):
        if boundary_u(fp, 2.3, -jo + 0.12) or boundary_u(fp, 2.3, -jo - 0.12):
            return 'wood5'
    if t < 0.16:
        return hi
    if t > 0.88:
        return dark
    return base


def H01(*a):
    """Deterministic hash -> [0,1)."""
    h = 2166136261
    for v in a:
        h = ((h ^ (int(v) & 0xffffffff)) * 16777619) & 0xffffffff
    return (h % 10007) / 10007.0


def boundary(fp, step, off=0.0):
    """True if a multiple of step (+off) lies inside the pixel's vertical footprint."""
    return math.floor((fp.v1 - off) / step) != math.floor((fp.v0 - off) / step)


def boundary_u(fp, step, off=0.0):
    return math.floor((fp.u1 - off) / step) != math.floor((fp.u0 - off) / step)


def window(fp, za, zb, ya, yb, frame, glass, refl, bar, fw=0.09, cross=True, sill=None):
    """Return colour if fp is inside a window (frame + dark glass + 1px sky reflection)."""
    if not (za - fw <= fp.uc < zb + fw and ya - fw <= fp.vc < yb + fw):
        if sill and za - 2 * fw <= fp.uc < zb + 2 * fw and ya - 2.2 * fw <= fp.vc < ya - fw:
            return sill
        return None
    if not (za <= fp.uc < zb and ya <= fp.vc < yb):
        return frame
    # glass
    if fp.has_v(yb - 1e-6) or (fp.v1 >= yb - 1.0 / fp.k and fp.vc > yb - 1.0 / fp.k):
        return refl                       # top row: sky reflection
    if cross:
        zm = (za + zb) / 2; ym = (ya + yb) / 2 + 0.05
        if fp.has_u(zm) or fp.has_v(ym):
            return bar
    return glass


# =========================================================================================
#                                       LEFT  ROW
# =========================================================================================
def _saloon(cv):
    X = -XFL
    Z0, Z1, TOP = 3.0, 8.4, 5.8

    def sh(fp):
        z, yv, k = fp.uc, fp.vc, fp.k
        if yv >= TOP - 0.12:
            return 'envRim'
        if yv >= TOP - 0.2:
            return 'sand0'
        if yv >= TOP - 0.46:
            if yv < TOP - 0.32 and boundary_u(fp, 0.3):
                return 'wood3'
            return 'paintCream'
        if yv >= TOP - 0.56:
            return 'wood3'
        if yv < PR:
            # batwing doorway
            if 6.55 <= z < 7.55 and yv < 2.6:
                if yv >= 2.48 or z < 6.63 or z >= 7.47:
                    return 'wood2' if yv < 2.55 else 'wood1'
                if 0.9 <= yv < 2.05:
                    if 6.63 <= z < 7.03 or 7.07 <= z < 7.47:
                        if yv >= 2.05 - 1.0 / k:
                            return 'wood0'
                        if yv < 0.9 + 1.0 / k:
                            return 'wood4'
                        if boundary_u(fp, 0.1, 0.02):
                            return 'wood4'
                        return 'wood2'
                if yv > 2.05 and (fp.x + fp.y) % 2 == 0 and yv < 2.3:
                    return 'envShadeDeep'
                return 'ink2'
            for (za, zb) in ((5.25, 6.2), (7.8, 8.25)):
                w = window(fp, za, zb, 1.05, 2.4, 'wood2', 'ink2', 'sky2', 'wood3', sill='wood3')
                if w:
                    return w
            if yv < 0.6:
                return 'wood4' if not boundary_u(fp, 0.6) else 'wood5'
            if yv > PR - 0.5:
                return 'envShadeDeep' if yv > PR - 0.25 or (fp.x + fp.y) & 1 else 'shadowPurp'
            return wood_grain(fp, 0.28, 1, 'wood3', 'wood4', 'wood2', 'wood4')
        for (za, zb) in ((5.5, 6.3), (7.1, 7.9)):
            w = window(fp, za, zb, 3.75, 5.0, 'paintCream', 'ink2', 'sky4', 'paintCreamD', sill='wood2')
            if w:
                if w == 'ink2' and fp.vc < 4.25 and fp.uc < (za + zb) / 2:
                    return 'paintRedD'                      # curtain
                return w
            if (za - 0.4 <= z < za - 0.12 or zb + 0.12 <= z < zb + 0.4) and 3.7 <= yv < 5.05:
                if boundary(fp, 0.16, 0.05):
                    return 'paintTealD'
                return 'envTealHi' if fp.has_u(zb + 0.4 - 1e-4) or (z > zb and z >= zb + 0.36) else 'paintTeal'
        return wood_grain(fp, 0.28, 2, 'wood1', 'wood3', 'wood0', 'wood2')

    facade(cv, X, Z0, Z1, BW, TOP, sh)
    facade(cv, X, Z1 - 0.16, Z1, BW, TOP - 0.46, lambda fp: 'paintCreamD' if fp.vc < PR else 'paintCream')


def _hotel(cv):
    X = -XFL
    Z0, Z1, TOP = 8.7, 12.0, 8.4

    def sh(fp):
        z, yv, k = fp.uc, fp.vc, fp.k
        if yv >= TOP - 0.16:
            return 'envRim'
        if yv >= TOP - 0.55:
            if yv < TOP - 0.4 and boundary_u(fp, 0.4):
                return 'paintRedD'
            return 'paintCream'
        if yv < PR:
            if 9.9 <= z < 10.7 and yv < 2.6:
                if yv >= 2.5 or z < 9.98 or z >= 10.62:
                    return 'paintCreamD'
                if 1.5 <= yv < 2.35 and 10.05 <= z < 10.55:
                    return 'sky0'
                return 'wood4' if boundary_u(fp, 0.22, 0.05) else 'wood3'
            for (za, zb) in ((8.95, 9.6), (11.0, 11.65)):
                w = window(fp, za, zb, 1.05, 2.4, 'paintCreamD', 'ink2', 'sky2', 'paintRedD')
                if w:
                    return w
            if yv < 0.6:
                return 'red3'
            if yv > PR - 0.45:
                return 'envShadeDeep' if yv > PR - 0.22 or (fp.x + fp.y) & 1 else 'red3'
            return wood_grain(fp, 0.28, 3, 'paintRedD', 'red3', 'paintRed', 'red3')
        for (ya, yb) in ((3.8, 5.0), (6.05, 7.25)):
            for (za, zb) in ((9.0, 9.6), (10.05, 10.65), (11.1, 11.7)):
                w = window(fp, za, zb, ya, yb, 'paintCream', 'ink2', 'sky4', 'paintCreamD',
                           sill='paintCreamD', cross=k > 13)
                if w:
                    return w
        if 5.45 <= yv < 5.62:
            return 'paintCreamD'
        return wood_grain(fp, 0.28, 4, 'paintRed', 'paintRedD', 'envRedHi', 'paintRedD')

    facade(cv, X, Z0, Z1, BW, TOP, sh)
    facade(cv, X, Z1 - 0.12, Z1, BW, TOP - 0.55, lambda fp: 'paintCreamD' if fp.vc < PR else 'paintCream')


def _hotel_sidewall(cv):
    Z = 8.7
    k = F / Z
    def sh(fp):
        yv = fp.vc
        if yv >= 8.4 - 0.16:
            return 'paintCream'
        if yv >= 8.4 - 0.55:
            return 'paintCreamD'
        if boundary(fp, 0.28):
            return 'red3'
        n = math.floor(yv / 0.28)
        if boundary_u(fp, 2.1, 0.9 * H01(n, 5)):
            return 'red3'
        return 'paintRedD' if H01(n, math.floor(fp.uc / 2.1), 2) > 0.12 else 'wood3'
    frontal(cv, Z, -14.0, -XFL, 0.0, 8.4, sh)
    s = 'HOTEL'
    tw = big_w(s)
    x0 = 52; y0 = 101
    cv.rect(x0 - 3, y0 - 3, x0 + tw + 2, y0 + 9, 'paintRedD')
    cv.hline(x0 - 3, x0 + tw + 2, y0 - 3, 'paintCreamD'); cv.hline(x0 - 3, x0 + tw + 2, y0 + 9, 'paintCreamD')
    big_text(cv, s, x0, y0, 'paintCream', shadow='red3')
    for (dx, dy) in ((3, 6), (14, 1), (22, 4), (8, 3)):
        if cv.get(x0 + dx, y0 + dy) == col('paintCream'):
            cv.px(x0 + dx, y0 + dy, 'paintCreamD')


def _porch_left(cv, Z0, Z1, balcony=False):
    """Porch roof underside, fascia, boardwalk and posts for the left row segment."""
    X0, X1 = -XFL, -XPL - 0.12
    # boardwalk top (planks across X -> horizontal seams at fixed Z)
    def bw(fp):
        if fp.uc > -XPL - 0.12:
            return 'wood2'                       # lit nosing
        if fp.v1 - fp.v0 < 0.5 and boundary(fp, 0.24):
            return 'wood4'
        return 'wood3'
    horizontal(cv, BW, X0, -XPL + 0.02, Z0, Z1, bw)
    # boardwalk front face (faces the street = lit)
    def bwf(fp):
        if fp.vc > BW - 0.07:
            return 'wood0'
        if boundary_u(fp, 1.2, 0.3) and fp.k > 16:
            return 'wood3'
        return 'wood1' if fp.vc > 0.12 else 'wood2'
    facade(cv, -XPL + 0.02, Z0, Z1, 0.0, BW, bwf)
    # porch roof underside (in deep shade) with joists running across
    def under(fp):
        if fp.v1 - fp.v0 < 0.6 and boundary(fp, 0.55, 0.1):
            return 'wood5'
        if fp.uc < -XFL + 0.25:
            return 'ink2'
        return 'wood4'
    horizontal(cv, PR, X0, X1 + 0.12, Z0, Z1, under)
    # fascia board (lit, faces street)
    def fas(fp):
        if fp.vc > PR + 0.22:
            return 'sand0'
        if fp.vc < PR + 0.05:
            return 'wood2'
        return 'wood0' if not boundary_u(fp, 1.8, 0.4) else 'wood1'
    facade(cv, -XPL + 0.02, Z0, Z1, PR - 0.02, PR + 0.3, fas)
    if balcony:
        # balcony railing on top of the porch roof
        TOPR = PR + 1.05
        def rail(fp):
            yv = fp.vc
            if yv >= TOPR - 0.1:
                return 'sand0' if yv >= TOPR - 0.04 else 'paintCream'
            if PR + 0.3 <= yv < PR + 0.42:
                return 'paintCreamD'
            if yv >= PR + 0.42 and boundary_u(fp, 0.28, 0.1) and fp.k > 10:
                return 'paintCreamD'
            if yv >= PR + 0.42 and fp.k > 18 and boundary_u(fp, 0.28, 0.1 + 0.06):
                return 'paintCream'
            return None
        facade(cv, -XPL + 0.04, Z0, Z1, PR + 0.3, TOPR, rail)


def _post_shadows_left(cv, zs):
    """Posts' short shadows on the boardwalk (skewed left)."""
    for Z in zs:
        for i in range(8):
            t = i / 7.0
            X = -XPL - 0.12 - 0.5 * t
            Zs = Z + 0.15 * t
            x, y = proj(X, BW, Zs)
            k = F / Zs
            w = max(1, int(round(0.15 * k)))
            cv.rect(int(round(x - w / 2)), int(round(y)), int(round(x + w / 2)) - 1, int(round(y)), 'wood4')


def _posts_left(cv, zs):
    for Z in zs:
        x0, w, yt, yb = post(cv, -XPL + 0.04, Z, BW, PR, 0.17, ('wood0', 'wood1', 'wood2'))
        # lit capital block + bracket
        cv.rect(x0 - 1, yt, x0 + w, yt + max(1, w // 3), 'wood1')
        cv.hline(x0 - 1, x0 + w, yt, 'sand0')
        # dark base shadow
        cv.hline(x0, x0 + w - 1, yb - 1, 'wood3')


def _barrel(cv, X, Z, lit_side=1, Ybase=BW, hgt=0.85, wid=0.6, banded=True):
    """Frontal barrel: 1px ink outline (selective), bands, lit edge toward the sun (right)."""
    k = F / Z
    xc, yb = proj(X, Ybase, Z)
    w = max(5, int(round(wid * k))) | 1
    h = max(6, int(round(hgt * k)))
    x0 = int(round(xc - w / 2)); y0 = int(round(yb)) - h
    for yy in range(h):
        t = yy / (h - 1)
        bulge = int(round(math.sin(t * math.pi) * max(1, w // 8)))
        xa, xb = x0 + (w // 8) - bulge, x0 + w - 1 - (w // 8) + bulge
        cv.hline(xa, xb, y0 + yy, 'wood2')
        # shading columns
        cv.px(xa, y0 + yy, 'ink2'); cv.px(xb, y0 + yy, 'wood4')
        cv.px(xa + 1, y0 + yy, 'wood3')
        cv.px(xb - 1, y0 + yy, 'wood1')
        if w > 9:
            cv.px(xb - 2, y0 + yy, 'wood1')
            cv.px(xa + 2, y0 + yy, 'wood3') if yy % 2 else None
            # stave seams
            for sx in range(xa + 4, xb - 3, 4):
                if (yy + sx) % 7 != 0:
                    cv.px(sx, y0 + yy, 'wood3')
    # top rim (we're above it -> see a thin ellipse top)
    cv.hline(x0 + w // 8 + 1, x0 + w - 2 - w // 8, y0 - 1, 'wood1')
    cv.hline(x0 + w // 8 + 1, x0 + w - 2 - w // 8, y0, 'wood3')
    cv.px(x0 + w - 2 - w // 8, y0 - 1, 'wood0')
    cv.hline(x0 + w // 8 + 1, x0 + w - 2 - w // 8, y0 - 2, 'ink2')
    if banded:
        for by in (int(h * 0.18), int(h * 0.78)):
            yy = y0 + by
            t = by / (h - 1)
            bulge = int(round(math.sin(t * math.pi) * max(1, w // 8)))
            xa, xb = x0 + (w // 8) - bulge, x0 + w - 1 - (w // 8) + bulge
            cv.hline(xa + 1, xb - 1, yy, 'steel3')
            cv.px(xb - 1, yy, 'steel2')
    # bottom outline / contact shadow
    cv.hline(x0 + w // 8, x0 + w - 1 - w // 8, y0 + h - 1, 'ink2')
    return x0, y0, w, h


def _crate(cv, X, Z, Ybase=BW, s=0.55, shade=False):
    k = F / Z
    xc, yb = proj(X, Ybase, Z)
    w = max(5, int(round(s * k))); h = w
    x0 = int(round(xc - w / 2)); y0 = int(round(yb)) - h
    base, lit, dk = (('wood2', 'wood1', 'wood3') if shade else ('wood1', 'wood0', 'wood2'))
    cv.rect(x0, y0, x0 + w - 1, y0 + h - 1, base)
    cv.frame(x0, y0, x0 + w - 1, y0 + h - 1, dk)
    cv.hline(x0 + 1, x0 + w - 2, y0 + 1, lit)
    # X brace
    for i in range(1, w - 1):
        yy = y0 + 1 + int((i - 1) * (h - 3) / max(1, w - 3))
        cv.px(x0 + i, yy, dk)
        cv.px(x0 + w - 1 - i, yy, dk)
    # outline
    cv.hline(x0, x0 + w - 1, y0 - 1, 'ink2' if shade else 'wood3')
    cv.vline(x0 - 1, y0, y0 + h - 1, 'ink2')
    cv.hline(x0, x0 + w - 1, y0 + h, 'ink2')
    cv.vline(x0 + w, y0, y0 + h - 1, 'ink2' if shade else 'wood3')
    cv.hline(x0, x0 + w - 1, y0, 'sand0' if not shade else 'wood1')
    return x0, y0, w, h


def _saloon_sign(cv):
    """Big frontal SALOON board standing on the balcony rail."""
    Z = 7.6
    xa, ya = proj(-XFL + 0.1, PR + 2.05, Z)
    xb, yb = proj(-1.85, PR + 1.2, Z)
    x0, y0, x1, y1 = int(round(xa)), int(round(ya)), int(round(xb)), int(round(yb))
    for px_ in (x0 + 7, x1 - 8):
        cv.rect(px_, y1, px_ + 2, y1 + 8, 'wood3'); cv.vline(px_ + 2, y1, y1 + 8, 'wood1')
        cv.vline(px_ - 1, y1, y1 + 8, 'ink2'); cv.vline(px_ + 3, y1, y1 + 8, 'ink2')
    cv.rect(x0, y0, x1, y1, 'paintCream')
    for yy in range(y0 + 2, y1 - 1):
        if (yy - y0) % 6 == 0:
            cv.hline(x0 + 1, x1 - 1, yy, 'paintCreamD')
    cv.frame(x0 + 1, y0 + 1, x1 - 1, y1 - 1, 'paintRed')
    cv.frame(x0 + 2, y0 + 2, x1 - 2, y1 - 2, 'paintRedD')
    cv.hline(x0 + 1, x1 - 1, y0 + 1, 'envRedHi')
    cv.frame(x0 - 1, y0 - 1, x1 + 1, y1 + 1, 'ink')
    cv.hline(x0, x1, y0 - 1, 'ink2')
    s = 'SALOON'
    tw = big_w(s)
    tx = (x0 + x1 + 1) // 2 - tw // 2
    ty = (y0 + y1 + 1) // 2 - 3
    big_text(cv, s, tx, ty, 'paintRedD', shadow='paintCreamD')
    for (cx_, cy_) in ((x0 - 1, y0 - 1), (x1 + 1, y0 - 1), (x0 - 1, y1 + 1), (x1 + 1, y1 + 1)):
        cv.clear(cx_, cy_)


def _hitch_and_trough(cv):
    Xa, Xb, Za, Zb, Ht = -2.25, -1.8, 6.2, 7.4, 0.6
    def side(fp):
        if fp.vc > Ht - 0.08:
            return 'wood0'
        if boundary(fp, 0.2):
            return 'wood3'
        if boundary_u(fp, 0.6, 0.2):
            return 'wood3'
        return 'wood1'
    facade(cv, Xb, Za, Zb, 0.0, Ht, side)
    def end(fp):
        if fp.vc > Ht - 0.08:
            return 'wood1'
        return 'wood3' if boundary(fp, 0.2) else 'wood2'
    frontal(cv, Za, Xa, Xb, 0.0, Ht, end)
    def water(fp):
        if fp.uc > Xb - 0.06 or fp.uc < Xa + 0.06 or fp.vc < Za + 0.08 or fp.vc > Zb - 0.08:
            return 'wood1' if fp.uc > Xb - 0.06 else 'wood3'
        if fp.uc < Xa + 0.14:
            return 'sky0'
        return 'sky3' if (fp.x * 3 + fp.y * 5) % 11 == 0 else 'sky1'
    horizontal(cv, Ht, Xa, Xb, Za, Zb, water)
    xa, ya = proj(Xa, Ht, Za); xb_, yb_ = proj(Xa, 0, Za)
    cv.vline(int(round(xa)) - 1, int(round(ya)), int(round(yb_)) - 1, 'ink2')
    recede_line(cv, Xa - 0.05, 0.0, Za, Zb, 'sand4')
    for Z in (7.8, 9.0):
        post(cv, -2.0, Z, 0.0, 1.1, 0.13, ('wood1', 'wood2', 'wood4'))
    facade(cv, -2.0 + 0.07, 7.8, 9.0, 0.92, 1.1, lambda fp: 'wood0' if fp.vc > 1.03 else 'wood2')


def make_buildings_left():
    cv = Canvas()
    _hotel_sidewall(cv)
    _hotel(cv)
    # alley gap between saloon and hotel (dark slot)
    facade(cv, -XFL - 0.02, 8.4, 8.7, BW, 7.6, lambda fp: 'ink2' if fp.vc < 5.5 else 'envShadeDeep')
    _saloon(cv)
    _porch_left(cv, 3.0, 8.4, balcony=True)
    _porch_left(cv, 8.7, 12.0)
    _post_shadows_left(cv, LEFT_POSTS)
    _posts_left(cv, LEFT_POSTS)
    _saloon_sign(cv)
    # props on the left boardwalk
    _barrel(cv, -2.95, 8.25)
    _barrel(cv, -2.6, 8.05, hgt=0.8)
    _crate(cv, -3.0, 9.6)
    _crate(cv, -3.05, 9.95, s=0.4)
    _barrel(cv, -2.9, 5.6, hgt=0.9, wid=0.62)
    _hitch_and_trough(cv)
    np_outline(cv, 'ink')
    return cv


# =========================================================================================
#                                       RIGHT  ROW
# =========================================================================================
GUNS_BRACKET_Z = 6.7
GUNS_BRACKET_Y = 4.7


def _guns(cv):
    X = XF
    Z0, Z1, TOP = 3.0, 7.2, 5.0

    def sh(fp):
        z, yv, k = fp.uc, fp.vc, fp.k
        if yv >= TOP - 0.12:
            return 'envTealHi'
        if yv >= TOP - 0.4:
            if yv < TOP - 0.28 and boundary_u(fp, 0.3):
                return 'envShadeDeep'
            return 'paintCreamD'
        if yv < PR:
            if 5.4 <= z < 6.15 and yv < 2.55:
                if yv >= 2.45 or z < 5.47 or z >= 6.08:
                    return 'paintTealD'
                if 1.4 <= yv < 2.3 and 5.55 <= z < 6.0:
                    return 'sky0' if yv > 2.15 else 'ink2'
                return 'wood4' if not boundary_u(fp, 0.25, 0.05) else 'wood5'
            for (za, zb) in ((3.9, 5.0), (6.4, 7.0)):
                w = window(fp, za, zb, 0.9, 2.45, 'paintTealD', 'ink2', 'sky1', 'envShadeDeep')
                if w:
                    return w
            if yv < 0.6:
                return 'ink2'
            if yv > PR - 0.45:
                return 'ink2' if yv > PR - 0.22 or (fp.x + fp.y) & 1 else 'envShadeDeep'
            return wood_grain(fp, 0.28, 5, 'envShadeDeep', 'ink2', 'shadowPurp', 'ink2')
        if PR + 0.45 <= yv < PR + 0.65:
            return 'paintTeal' if not boundary_u(fp, 0.5) else 'paintTealD'
        return wood_grain(fp, 0.28, 6, 'paintTealD', 'envShadeDeep', 'paintTeal', 'shadowPurp')

    facade(cv, X, Z0, Z1, BW, TOP, sh)
    facade(cv, X, Z1 - 0.14, Z1, BW, TOP - 0.4, lambda fp: 'paintCreamD')
    # iron bracket for the hanging GUNS sign
    Zs = GUNS_BRACKET_Z
    xa, ya = proj(XF, GUNS_BRACKET_Y, Zs); xb, _ = proj(0.85, GUNS_BRACKET_Y, Zs)
    y = int(round(ya))
    cv.hline(int(round(xb)), int(round(xa)) - 1, y, 'ink')
    cv.hline(int(round(xb)), int(round(xa)) - 1, y - 1, 'steel3')
    cv.px(int(round(xb)) - 1, y, 'ink'); cv.px(int(round(xb)) - 1, y - 1, 'ink')
    xd, yd = proj(XF, GUNS_BRACKET_Y - 0.9, Zs)
    for i in range(14):
        cv.px(int(round(xa)) - 1 - i, int(round(yd)) - i * (int(round(yd)) - y) // 14, 'ink')
    # scroll curl
    cv.px(int(round(xa)) - 8, y + 2, 'steel3'); cv.px(int(round(xa)) - 9, y + 3, 'ink')


def _bank(cv):
    X = XF
    Z0, Z1, TOP = 7.5, 8.8, 6.6

    def sh(fp):
        z, yv, k = fp.uc, fp.vc, fp.k
        if yv >= TOP - 0.16:
            return 'paintCream'
        if yv >= TOP - 0.55:
            if yv < TOP - 0.4 and boundary_u(fp, 0.4):
                return 'wood3'
            return 'paintCreamD'
        if yv < PR:
            if 7.9 <= z < 8.5 and yv < 2.6:
                if yv >= 2.5 or z < 7.97 or z >= 8.43:
                    return 'paintCreamD'
                return 'wood4' if not boundary_u(fp, 0.2, 0.05) else 'wood5'
            if yv < 0.6:
                return 'ink2'
            if yv > PR - 0.45:
                return 'ink2' if yv > PR - 0.22 or (fp.x + fp.y) & 1 else 'envShadeDeep'
            if boundary(fp, 0.38):
                return 'ink2'
            return 'envShadeWarm' if not boundary_u(fp, 0.7, 0.35 * (math.floor(yv / 0.38) & 1)) else 'ink2'
        w = window(fp, 7.8, 8.45, 3.85, 5.15, 'paintCreamD', 'ink2', 'sky2', 'wood3')
        if w:
            return w
        if 5.5 <= yv < 5.68:
            return 'paintCreamD'
        if boundary(fp, 0.38):
            return 'wood2'
        n = math.floor(yv / 0.38)
        if boundary_u(fp, 0.7, 0.35 * (n & 1)):
            return 'wood2'
        return 'wood1'

    facade(cv, X, Z0, Z1, BW, TOP, sh)
    facade(cv, X, Z0, Z0 + 0.14, BW, TOP - 0.55, lambda fp: 'paintCreamD')


def _bank_sidewall(cv):
    Z = 7.5
    def sh(fp):
        yv = fp.vc
        if yv >= 6.6 - 0.16:
            return 'envRim'
        if yv >= 6.6 - 0.55:
            return 'paintCream' if not boundary_u(fp, 0.45) else 'paintCreamD'
        if boundary(fp, 0.38):
            return 'paintCreamD'
        n = math.floor(yv / 0.38)
        if boundary_u(fp, 0.7, 0.35 * (n & 1)):
            return 'paintCreamD'
        return 'paintCream'
    frontal(cv, Z, XF, 14.0, 0.0, 6.6, sh)
    s = 'BANK'
    x0, y0 = 318, 138
    tw = big_w(s)
    cv.rect(x0 - 3, y0 - 3, x0 + tw + 2, y0 + 9, 'paintCream')
    big_text(cv, s, x0, y0, 'wood4', shadow='paintCreamD')
    cv.hline(x0 - 3, x0 + tw + 2, y0 - 4, 'paintCreamD'); cv.hline(x0 - 3, x0 + tw + 2, y0 + 10, 'paintCreamD')


def _porch_right(cv, Z0, Z1):
    X0, X1 = XP + 0.12, XF
    def bw(fp):
        if fp.uc < XP + 0.12:
            return 'wood3'
        if fp.v1 - fp.v0 < 0.5 and boundary(fp, 0.24):
            return 'wood5'
        return 'wood4'
    horizontal(cv, BW, XP - 0.02, X1, Z0, Z1, bw)
    def bwf(fp):
        if fp.vc > BW - 0.07:
            return 'wood2'
        if boundary_u(fp, 1.2, 0.3) and fp.k > 16:
            return 'wood5'
        return 'wood3' if fp.vc > 0.12 else 'wood4'
    facade(cv, XP - 0.02, Z0, Z1, 0.0, BW, bwf)
    def under(fp):
        if fp.v1 - fp.v0 < 0.6 and boundary(fp, 0.5, 0.1):
            return 'ink'
        if fp.uc > XF - 0.25:
            return 'ink'
        return 'ink2'
    horizontal(cv, PR, X0 - 0.12, X1, Z0, Z1, under)
    def fas(fp):
        if fp.vc > PR + 0.24:
            return 'envRim'
        if fp.vc > PR + 0.18:
            return 'wood1'
        if fp.vc < PR + 0.05:
            return 'wood4'
        return 'wood3' if not boundary_u(fp, 1.8, 0.4) else 'wood4'
    facade(cv, XP - 0.02, Z0, Z1, PR - 0.02, PR + 0.3, fas)


def _posts_right(cv, zs):
    for Z in zs:
        x0, w, yt, yb = post(cv, XP - 0.04, Z, BW, PR, 0.17, ('wood2', 'wood3', 'wood4'))
        # the right row's posts: lit sliver on the right, sunlit cap
        cv.vline(x0 + w - 1, yt, yb - 1, 'wood1' if w >= 3 else 'wood2')
        cv.vline(x0, yt, yb - 1, 'wood5') if w >= 4 else None
        cv.rect(x0 - 1, yt, x0 + w, yt + max(1, w // 3), 'wood3')
        cv.hline(x0 - 1, x0 + w, yt, 'wood1')
        cv.hline(x0, x0 + w - 1, yb - 1, 'ink2')


def _wanted_board(cv):
    """Frontal notice board on legs at the right boardwalk edge, with a big WANTED poster."""
    Z = 4.95
    xa, ya = proj(1.12, 2.45, Z)
    xb, yb = proj(2.06, 0.95, Z)
    x0, y0, x1, y1 = int(round(xa)), int(round(ya)), int(round(xb)), int(round(yb))
    _, yg = proj(0, 0, Z)
    yg = int(round(yg))
    for lx in (x0 + 5, x1 - 7):
        cv.rect(lx, y1, lx + 2, yg - 1, 'wood3')
        cv.vline(lx + 2, y1 + 1, yg - 1, 'wood1')
        cv.vline(lx - 1, y1 + 1, yg - 1, 'ink'); cv.vline(lx + 3, y1 + 1, yg - 1, 'ink')
        cv.hline(lx - 3, lx + 2, yg, 'sand4'); cv.hline(lx - 2, lx + 1, yg + 1, 'sand3')
    cv.rect(x0, y0, x1, y1, 'wood2')
    for yy in range(y0, y1 + 1):
        if (yy - y0) % 8 == 7:
            cv.hline(x0, x1, yy, 'wood4')
        elif (yy - y0) % 8 == 0:
            cv.hline(x0, x1, yy, 'wood1')
    for yy in range(y0 + 3, y1, 8):
        cv.px(x0 + 2, yy, 'wood5'); cv.px(x1 - 2, yy, 'wood5')
    cv.vline(x1, y0, y1, 'wood1')
    cv.frame(x0 - 1, y0 - 1, x1 + 1, y1 + 1, 'ink')
    cv.rect(x0 - 3, y0 - 5, x1 + 3, y0 - 2, 'wood2')
    cv.hline(x0 - 3, x1 + 3, y0 - 5, 'envRim'); cv.hline(x0 - 3, x1 + 3, y0 - 4, 'wood0')
    cv.frame(x0 - 4, y0 - 6, x1 + 4, y0 - 1, 'ink')
    # small torn note
    cv.rect(x0 + 3, y1 - 13, x0 + 9, y1 - 4, 'paintCreamD')
    for yy in (y1 - 11, y1 - 9, y1 - 7):
        cv.hline(x0 + 4, x0 + 7, yy, 'wood3')
    poster = wanted_poster()
    pw, ph = poster.w, poster.h
    cv.blit(poster, (x0 + x1) // 2 - pw // 2 + 4, y0 + 3)
    return (x0, y0, x1, y1)


def wanted_poster(pw=40, ph=52):
    p = Canvas(pw, ph)
    p.rect(0, 0, pw - 1, ph - 1, 'paintCream')
    p.frame(0, 0, pw - 1, ph - 1, 'paintCreamD')
    for (x, y) in ((0, 0), (pw - 1, ph - 1), (pw - 1, 0)):
        p.clear(x, y)
    p.px(pw - 2, ph - 2, 'cream2'); p.px(1, ph - 1, 'cream2'); p.px(pw - 1, ph - 3, 'cream2')
    p.px(pw // 2, 1, 'steel2'); p.px(pw // 2, 2, 'steel3')
    big_text(p, 'WANTED', 2, 4, 'wood5')
    p.hline(2, pw - 3, 12, 'paintCreamD')
    # cat face: dark charcoal cat with hat, scar, amber eyes
    fx, fy = 7, 14
    face = [
        "........111111111.........",
        ".......11222222211........",
        "......1122222222211.......",
        "...1111111111111111111....",
        "..11777777777777777777....",
        "....1..1.........1..1.....",
        "....11.21.......12.11.....",
        "....121221111111221211....",
        "....12222222222222221.....",
        "...1222222222222222221....",
        "...1229922222222992221....",
        "...1229122222222192221....",
        "...1222222224222222221....",
        "....122222244422222221....",
        "....122225522255222221....",
        ".....1222222222222221.....",
        "......11222222222211......",
        "........1111111111........",
        "........3333333333........",
        ".......333333333333.......",
    ]
    cmap = {'1': 'wood5', '2': 'wood3', '4': 'wood5', '5': 'cream2', '7': 'brass2', '9': 'brass1', '3': 'paintRed'}
    for yy, row in enumerate(face):
        for xx, v in enumerate(row):
            if v in cmap:
                p.px(fx + xx - 2, fy + yy, cmap[v])
    # scar
    p.px(fx + 4, fy + 9, 'paintCream'); p.px(fx + 5, fy + 10, 'paintCream'); p.px(fx + 6, fy + 12, 'paintCream')
    big_text(p, '$500', 7, ph - 10, 'paintRedD')
    return p


def make_buildings_right():
    cv = Canvas()
    _bank_sidewall(cv)
    _bank(cv)
    facade(cv, XF + 0.02, 7.2, 7.5, BW, 5.6, lambda fp: 'ink2')
    _guns(cv)
    _porch_right(cv, 3.0, 7.2)
    _porch_right(cv, 7.5, 8.8)
    _posts_right(cv, RIGHT_POSTS)
    _barrel(cv, 2.35, 7.6, hgt=0.8)
    _crate(cv, 2.45, 8.4, shade=True)
    _barrel(cv, 2.45, 4.2, hgt=0.85)
    _crate(cv, 2.5, 3.6, s=0.6, shade=True)
    _wanted_board(cv)
    np_outline(cv, 'ink')
    return cv, None
