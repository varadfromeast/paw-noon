"""Ground layer: desert floor + street in one-point perspective (VP 150,258)."""
import math
import numpy as np
from envlib import Canvas, W, H, VPX, VPY, FOCAL, EYE, proj, kz, col, rng

GROUND_TOP = 295
POST_L, POST_R = -2.3, 1.9          # boardwalk street edges (porch post lines)
ROW_END_L, ROW_END_R = 12.0, 8.8   # depth where the building rows stop


def row_k(y):
    """px-per-metre at canvas row y (ground plane)."""
    return (y + 0.5 - VPY) / EYE


def ground_xz(x, y):
    k = row_k(y)
    Z = FOCAL / k
    X = (x + 0.5 - VPX) / k
    return X, Z


def make_ground():
    cv = Canvas()
    r = rng(11)
    # ---------------------------------------------------------------- base colours per pixel
    for y in range(GROUND_TOP, H):
        if y + 0.5 - VPY <= 0.3:
            # horizon sliver: hazy far desert
            for x in range(W):
                cv.px(x, y, 'haze' if (x + y) & 1 else 'sand0')
            continue
        k = row_k(y)
        for x in range(W):
            X, Z = ground_xz(x, y)
            c = 'sand1'
            if Z > 70:
                c = 'haze' if (x + y) & 1 == 0 else 'sand0'
            elif Z > 34:
                c = 'sand0'
            else:
                # trodden lighter band down the middle of the street, widening into a light
                # pool where the bandit stands (Z~6)
                pool = ((X - 0.1) / 1.5) ** 2 + ((Z - 5.2) / 2.2) ** 2
                if pool < 1.0:
                    c = 'sand0'
                elif abs(X - 0.0) < 0.9 and Z > 9:
                    c = 'sand0'
                elif abs(X) < 1.1 and Z > 9:
                    c = 'sand0' if (x + y) & 1 == 0 else 'sand1'
                # dusty margins near the boardwalks
                edge = POST_L if X < 0 else POST_R
                endz = ROW_END_L if X < 0 else ROW_END_R
                if abs(X) > abs(edge) - 0.35 and Z < endz + 1:
                    c = 'sand2' if (x + y) & 1 == 0 else 'sand1'
                if abs(X) > abs(edge) - 0.15 and Z < endz + 1:
                    c = 'sand2'
            cv.px(x, y, c)

    # ---------------------------------------------------------------- texture patches
    # soft darker drifts (checker sand2 over sand1) laid out in world space
    patches = [(-1.6, 2.6, 0.7, 0.9), (1.5, 3.4, 0.8, 0.5), (-1.9, 8.5, 0.5, 1.5),
               (1.75, 9.0, 0.35, 1.6), (-0.6, 1.6, 0.6, 0.3), (2.1, 1.7, 0.8, 0.4),
               (1.1, 15.0, 0.6, 3.0), (-1.4, 18.0, 0.6, 3.0), (0.9, 2.2, 0.3, 0.25)]
    for (Xc, Zc, rx, rz) in patches:
        for y in range(GROUND_TOP + 1, H):
            for x in range(W):
                X, Z = ground_xz(x, y)
                d = ((X - Xc) / rx) ** 2 + ((Z - Zc) / rz) ** 2
                if d < 1.0 and (x + y) & 1 == 0:
                    cur = cv.get(x, y)
                    if cur == col('sand1'):
                        cv.px(x, y, 'sand2')
                    elif cur == col('sand0'):
                        cv.px(x, y, 'sand1')

    # ---------------------------------------------------------------- wagon ruts
    def rut(Xr, zmin, zmax, strength):
        for y in range(GROUND_TOP + 1, H):
            k = row_k(y)
            Z = FOCAL / k
            if not (zmin <= Z <= zmax):
                continue
            xc = VPX + k * Xr
            w = max(1, int(round(0.10 * k)))
            x0 = int(round(xc - w / 2))
            dark = 'sand2'
            if strength > 1 and k > 18 and (k < 40 or VPX + k * Xr < 150):
                dark = 'sand3'
            if Z > 30:
                dark = 'sand1'
            quiet = False
            for x in range(x0, x0 + w):
                cur = cv.get(x, y)
                cv.px(x, y, dark if cur != col('sand2') or dark != 'sand2' else 'sand3')
            # lit berm on the right side of the groove
            if k > 9 and not quiet:
                cv.px(x0 + w, y, 'sand0')
                if k > 60:
                    cv.px(x0 + w + 1, y, 'sand0')
            # soft shoulder on the left
            if k > 30:
                cv.px(x0 - 1, y, 'sand2')
    rut(-0.95, 1.0, 60, 2)
    rut(0.62, 1.0, 60, 2)
    rut(-0.30, 1.0, 14, 1)
    rut(1.25, 3.5, 14, 1)

    # ---------------------------------------------------------------- hoof prints
    def hoof(X, Z):
        sx, sy = proj(X, 0, Z)
        k = kz(Z)
        w = max(2, min(7, int(round(0.11 * k))))
        h = max(1, min(3, int(round(0.11 * k * (k * EYE / FOCAL)))))
        x0, y0 = int(round(sx - w / 2)), int(round(sy))
        # U-shape opening toward camera (crescent): dark rim, lit inner lip
        if w <= 3:
            cv.hline(x0, x0 + w - 1, y0, 'sand2')
            return
        for i in range(w):
            cv.px(x0 + i, y0, 'sand3')
        for j in range(1, h):
            cv.px(x0, y0 + j, 'sand3'); cv.px(x0 + w - 1, y0 + j, 'sand3')
        if h >= 2:
            for i in range(1, w - 1):
                cv.px(x0 + i, y0 + 1, 'sand2')
        cv.px(x0 + w, y0 + 1, 'sand0') if h > 1 else None
    z = 2.0
    side = 0
    while z < 15:
        hoof(-0.30 + (0.13 if side else -0.13) + 0.05 * math.sin(z), z)
        side ^= 1
        z *= 1.12 if z < 5 else 1.07

    # ---------------------------------------------------------------- right-row cast shadows
    # porch roofs at Y=3 throw a short band onto the street left of the right boardwalk
    for y in range(GROUND_TOP + 1, H):
        k = row_k(y)
        Z = FOCAL / k
        if Z > ROW_END_R - 0.2 or Z < 1.0:
            continue
        xa = VPX + k * (POST_R - 0.5)
        xb = VPX + k * POST_R
        for x in range(int(math.ceil(xa - 0.5)), int(math.floor(xb - 0.5)) + 1):
            edge = x == int(math.ceil(xa - 0.5))
            cv.px(x, y, 'sand4' if (k > 40 and x > xb - 0.35 * k) else ('sand3'))
    # post shadows (short skewed bars falling to the left of each right-row post)
    for Zp in RIGHT_POSTS:
        for i in range(0, 12):
            t = i / 11.0
            X = POST_R - 0.15 - 0.55 * t
            Zs = Zp + 0.25 * t
            sx, sy = proj(X, 0, Zs)
            k = kz(Zs)
            ww = max(1, int(round(0.18 * k)))
            cv.rect(int(round(sx - ww / 2)), int(round(sy)), int(round(sx + ww / 2)) - 1, int(round(sy)),
                    'sand3')

    # left-row: the boardwalk is in porch shade; on the street only a thin dusty lip
    # ---------------------------------------------------------------- pebbles & stones
    stones = [(-1.7, 2.2, 0.07), (-2.0, 3.1, 0.05), (1.9, 2.0, 0.06), (2.05, 4.8, 0.05),
              (-1.35, 4.6, 0.04), (1.55, 6.9, 0.04), (-1.85, 7.5, 0.05), (2.0, 8.6, 0.05),
              (-0.65, 3.3, 0.035), (0.95, 9.8, 0.04), (-1.2, 11.5, 0.05), (1.4, 13.5, 0.06),
              (0.35, 1.45, 0.05), (2.15, 3.0, 0.08), (1.75, 1.35, 0.09), (-1.95, 1.6, 0.08),
              (-0.9, 7.8, 0.03), (0.6, 12.0, 0.04), (1.85, 3.9, 0.035)]
    rr = rng(31)
    for i in range(60):
        Z = 1.6 + rr.uniform(0, 1) ** 1.5 * 12
        X = rr.uniform(-2.1, 1.75)
        if abs(X - 0.1) < 1.3 and abs(Z - 4.9) < 1.2:
            continue
        stones.append((X, Z, rr.uniform(0.02, 0.05)))
    for (X, Z, s) in stones:
        sx, sy = proj(X, 0, Z)
        k = kz(Z)
        d = s * k
        if d < 1.2:
            cv.px(int(sx), int(sy), 'sand3')
            continue
        w = max(2, int(round(d * 1.4)))
        h = max(1, int(round(d * 0.8)))
        x0, y0 = int(round(sx - w / 2)), int(round(sy - h))
        # cast shadow first (left/down)
        cv.hline(x0 - 1, x0 + w - 2, y0 + h, 'sand4' if k > 50 else 'sand3')
        cv.rect(x0, y0, x0 + w - 1, y0 + h - 1, 'sand3')
        cv.hline(x0 + 1, x0 + w - 1, y0, 'sand1')
        if w >= 3:
            cv.px(x0 + w - 2, y0, 'sand0')
            cv.px(x0, y0 + h - 1, 'sand4')
    # tufts of dry grass in the dusty margins and off-street desert
    tufts = [(-2.2, 2.4), (2.25, 2.6), (-2.25, 5.5), (2.2, 6.3), (-3.3, 14.5), (3.2, 13.5),
             (-4.6, 18), (4.2, 20), (-2.9, 22), (2.8, 24)]
    for (X, Z) in tufts:
        sx, sy = proj(X, 0, Z)
        k = kz(Z)
        hgt = max(1, int(round(0.18 * k)))
        x0, y0 = int(round(sx)), int(round(sy))
        cv.vline(x0, y0 - hgt, y0, 'sand3' if k < 25 else 'sand4')
        if hgt > 1:
            cv.vline(x0 - 1, y0 - hgt + 1, y0, 'sand2')
            cv.vline(x0 + 1, y0 - hgt + 1, y0, 'sand2')
            cv.px(x0 + 2, y0 - hgt + 2, 'sand0')
            cv.px(x0 - 2, y0 - hgt + 2, 'sand3') if hgt > 2 else None
    return cv


# porch post depths of the right row (shared with buildings_right)
RIGHT_POSTS = [3.6, 5.2, 6.9, 8.5]
