"""Perspective plane rasterizers.  A shader receives a Footprint describing the world-space
extent of the pixel on the plane, so thin features (lines, frames) are hit exactly once per
row/column -> crisp, gap-free pixel lines with no anti-aliasing."""
import math
from envlib import VPX, VPY, FOCAL as F, EYE, proj


class FP:
    """Pixel footprint on a plane.  u = along-plane horizontal coord (Z for facades, X for
    frontal/horizontal planes), v = vertical coord (Y) or depth (Z for horizontal planes).
    u0<u1, v0<v1 are the footprint bounds, uc/vc the centre."""
    __slots__ = ('u0', 'u1', 'v0', 'v1', 'uc', 'vc', 'x', 'y', 'k')

    def has_u(self, u):
        return self.u0 < u <= self.u1

    def has_v(self, v):
        return self.v0 < v <= self.v1

    def in_box(self, ua, ub, va, vb):
        return ua <= self.uc < ub and va <= self.vc < vb


def facade(cv, X, Z0, Z1, Y0, Y1, shader):
    """Plane X = const (street-facing facade), Z in [Z0,Z1], Y in [Y0,Y1]."""
    pts = [proj(X, Y0, Z0), proj(X, Y1, Z0), proj(X, Y1, Z1), proj(X, Y0, Z1)]
    xs = [p[0] for p in pts]
    fp = FP()
    for (x, y) in cv.poly_pixels(pts):
        dxa, dxb = x - VPX, x + 1 - VPX
        za = F * X / dxa if dxa != 0 else 1e9
        zb = F * X / dxb if dxb != 0 else 1e9
        zc = F * X / (x + 0.5 - VPX)
        fp.u0, fp.u1 = min(za, zb), max(za, zb)
        fp.uc = zc
        k = F / zc
        fp.k = k
        # Y at top/bottom edge of the pixel (at centre depth)
        ya = EYE - (y - VPY) / k
        yb = EYE - (y + 1 - VPY) / k
        fp.v0, fp.v1 = yb, ya
        fp.vc = EYE - (y + 0.5 - VPY) / k
        fp.x, fp.y = x, y
        c = shader(fp)
        if c is not None:
            cv.px(x, y, c)


def frontal(cv, Z, X0, X1, Y0, Y1, shader):
    """Plane Z = const facing the camera."""
    k = F / Z
    pts = [proj(X0, Y0, Z), proj(X0, Y1, Z), proj(X1, Y1, Z), proj(X1, Y0, Z)]
    fp = FP()
    fp.k = k
    for (x, y) in cv.poly_pixels(pts):
        fp.u0 = (x - VPX) / k; fp.u1 = (x + 1 - VPX) / k; fp.uc = (x + 0.5 - VPX) / k
        fp.v1 = EYE - (y - VPY) / k; fp.v0 = EYE - (y + 1 - VPY) / k; fp.vc = EYE - (y + 0.5 - VPY) / k
        fp.x, fp.y = x, y
        c = shader(fp)
        if c is not None:
            cv.px(x, y, c)


def horizontal(cv, Y, X0, X1, Z0, Z1, shader):
    """Plane Y = const (porch-roof underside, boardwalk top).  u = X, v = Z."""
    pts = [proj(X0, Y, Z0), proj(X1, Y, Z0), proj(X1, Y, Z1), proj(X0, Y, Z1)]
    fp = FP()
    dyv = EYE - Y
    for (x, y) in cv.poly_pixels(pts):
        # depth from row: y - VPY = k*(EYE-Y)  -> k = (y-VPY)/(EYE-Y)
        ka = (y - VPY) / dyv; kb = (y + 1 - VPY) / dyv; kc = (y + 0.5 - VPY) / dyv
        if kc <= 0 or ka <= 0 or kb <= 0:
            continue
        za, zb = F / ka, F / kb
        fp.v0, fp.v1 = min(za, zb), max(za, zb)
        fp.vc = F / kc
        fp.k = kc
        fp.u0 = (x - VPX) / kc; fp.u1 = (x + 1 - VPX) / kc; fp.uc = (x + 0.5 - VPX) / kc
        fp.x, fp.y = x, y
        c = shader(fp)
        if c is not None:
            cv.px(x, y, c)


def post(cv, X, Z, Y0, Y1, width, cols, cap=None, side=-1):
    """Vertical square post at (X,Z).  cols = (lit, mid, dark).  side=-1 left row (lit on the
    right edge, sun from the right), +1 right row."""
    k = F / Z
    xc, yb = proj(X, Y0, Z)
    _, yt = proj(X, Y1, Z)
    w = max(2, int(round(width * k)))
    x0 = int(round(xc - w / 2))
    lit, mid, dark = cols
    cv.rect(x0, int(round(yt)), x0 + w - 1, int(round(yb)) - 1, mid)
    if w >= 3:
        cv.vline(x0 + w - 1, int(round(yt)), int(round(yb)) - 1, lit)
        cv.vline(x0, int(round(yt)), int(round(yb)) - 1, dark)
    else:
        cv.vline(x0 + 1, int(round(yt)), int(round(yb)) - 1, lit)
    return x0, w, int(round(yt)), int(round(yb))


def hline_world(cv, X0, X1, Y, Z, c):
    """Horizontal frontal line (constant Y,Z)."""
    xa, y = proj(X0, Y, Z); xb, _ = proj(X1, Y, Z)
    cv.hline(int(round(min(xa, xb))), int(round(max(xa, xb))) - 1, int(math.floor(y)), c)


def recede_line(cv, X, Y, Z0, Z1, c):
    """Line along depth at constant X,Y (converges to VP); drawn per column (or per row when
    steep) so steps stay regular."""
    xa, ya = proj(X, Y, Z0); xb, yb = proj(X, Y, Z1)
    if abs(xb - xa) >= abs(yb - ya):
        for x in range(int(round(min(xa, xb))), int(round(max(xa, xb)))):
            Z = F * X / (x + 0.5 - VPX) if (x + 0.5 - VPX) != 0 else 1e9
            if Z <= 0:
                continue
            y = VPY + F * (EYE - Y) / Z
            cv.px(x, int(math.floor(y)), c)
    else:
        for y in range(int(round(min(ya, yb))), int(round(max(ya, yb)))):
            dy = (y + 0.5 - VPY)
            if (EYE - Y) == 0:
                continue
            k = dy / (EYE - Y)
            if k <= 0:
                continue
            x = VPX + k * X
            cv.px(int(math.floor(x)), y, c)
