"""Paw Noon woodtype: a parametric heavy slab-serif display alphabet (Clarendon / wanted-poster
flavour) built from pixel-exact geometry, plus the layered lettering effect
(bevelled two-tone face, ink outline, red block extrusion, outer outline, drop shadow).

Glyphs: A D E F G I N O P R T U W Y ! 1 2 3 (enough for DRAW!, 3-2-1, PAW NOON, YOU WIN,
DEFEATED, GET READY).
"""
import numpy as np
from uikit import (blank, rect, rrect_mask, ellipse_mask, poly_mask, shift, dilate, edge_in, pad,
                   shear, Canvas, crop_mask)


class Metrics:
    def __init__(self, H, weight=1.0, cond=1.0):
        self.H = H
        self.C = cond
        self.S = max(3, round(H * 0.25 * weight))      # thick stem
        self.s = max(2, round(H * 0.12 * weight))      # thin stroke
        self.T = max(2, round(H * 0.17 * weight))      # slab thickness
        self.E = max(1, round(H * 0.09))               # serif overhang
        self.K = max(1, round(H * 0.06))               # bracket
        self.B = max(2, round(H * 0.15 * weight))      # bowl top/bottom thickness / bars


def _bracket(m, x, y, k, dx, dy):
    """Triangular fillet with the right angle at (x, y) opening toward (dx, dy)."""
    h, w = m.shape
    for i in range(k):
        for j in range(k - i):
            xx = x + (j if dx > 0 else -j - 1)
            yy = y + (i if dy > 0 else -i - 1)
            if 0 <= xx < w and 0 <= yy < h:
                m[yy, xx] = True


def stem_with_serifs(m, M, x, top=True, bottom=True, left=True, right=True, w=None, y0=0, y1=None):
    w = w or M.S
    y1 = M.H if y1 is None else y1
    rect(m, x, y0, x + w, y1)
    for is_top, on in ((True, top), (False, bottom)):
        if not on:
            continue
        ys = (y0, y0 + M.T) if is_top else (y1 - M.T, y1)
        xl = x - (M.E if left else 0)
        xr = x + w + (M.E if right else 0)
        rect(m, xl, ys[0], xr, ys[1])
        yb = ys[1] if is_top else ys[0]
        dy = 1 if is_top else -1
        if left:
            _bracket(m, x, yb, M.K, -1, dy)
        if right:
            _bracket(m, x + w, yb, M.K, 1, dy)


def stroke(w, h, top_c, bot_c, width, y0=0, y1=None):
    y1 = h if y1 is None else y1
    hw = width / 2.0
    return poly_mask(w, h, [(top_c - hw, y0), (top_c + hw, y0), (bot_c + hw, y1), (bot_c - hw, y1)])


# ------------------------------------------------------------------------- glyph builders
def g_D(M):
    H, S, E = M.H, M.S, M.E
    W = round(M.C * H * 0.80)
    R = round(H * 0.42)
    m = rrect_mask(W, H, E, 0, W, H, (0, R, R, 0), p=2.3)
    S2 = S + 1
    r2 = max(1, R - S2 + 1)
    m &= ~rrect_mask(W, H, E + S, M.B, W - S2, H - M.B, (0, r2, r2, 0), p=2.3)
    rect(m, 0, 0, E, M.T)
    rect(m, 0, H - M.T, E, H)
    _bracket(m, E, M.T, M.K, -1, 1)
    _bracket(m, E, H - M.T, M.K, -1, -1)
    return m


def g_P(M, leg=False):
    H, S, E = M.H, M.S, M.E
    W = round(M.C * H * (0.86 if leg else 0.74))
    yb = round(H * 0.58)
    Wb = W - (E if leg else 0)
    R = round(yb * 0.48)
    m = blank(W, H)
    m |= rrect_mask(W, H, E, 0, Wb, yb, (0, R, R, 0), p=2.3)
    S2 = S
    r2 = max(1, R - S2 + 1)
    m &= ~rrect_mask(W, H, E + S, M.B, Wb - S2, yb - M.B, (0, r2, r2, 0), p=2.3)
    stem_with_serifs(m, M, E, top=True, bottom=True, left=True, right=not leg)
    # top-left overhang only for the top slab (bowl covers the right side)
    if leg:
        lt = yb - M.B
        top_c = E + S + S / 2.0
        bot_c = max(W - E - S / 2.0, top_c + H * 0.18)
        m |= stroke(W, H, top_c, bot_c, S, y0=lt, y1=H)
        rect(m, int(round(bot_c - S / 2.0 - M.E)), H - M.T, W, H)
        # keep the inner corner between stem and leg open
    return m


def g_R(M):
    return g_P(M, leg=True)


def g_A(M):
    H, S, s, E, T = M.H, M.S, M.s, M.E, M.T
    W = round(M.C * H * 0.98)
    c = W / 2.0
    a = max(1.5, H * 0.05)
    xl_bot, xr_bot = E + 0.5, W - E - 0.5
    m = poly_mask(W, H, [(c - a, 0), (c + a, 0), (xr_bot, H), (xl_bot, H)])
    # counter between the legs: left inner = left outer + s, right inner = right outer - S
    def lerp(x0, x1, t):
        return x0 + (x1 - x0) * t
    yc0 = round(H * 0.62)
    yc1 = yc0 + max(2, M.B - 1)
    pts = []
    # inner left line from (c - a + s, 0) to (xl_bot + s, H); inner right from (c + a - S, 0) to (xr_bot - S, H)
    L0, L1 = (c - a + s), (xl_bot + s)
    R0, R1 = (c + a - S), (xr_bot - S)
    # intersection y of the two inner lines
    # x = L0 + (L1-L0) t ; x = R0 + (R1-R0) t
    t = (R0 - L0) / ((L1 - L0) - (R1 - R0))
    yi = t * H
    hole = poly_mask(W, H, [(lerp(L0, L1, t), yi), (R1, H), (L1, H)])
    hole[yc0:yc1, :] = False
    m &= ~hole
    # foot serifs
    lx0, lx1 = xl_bot - E, xl_bot + s + E
    rx0, rx1 = xr_bot - S - E, xr_bot + E
    rect(m, int(round(lx0)), H - T, int(round(lx1)), H)
    rect(m, int(round(rx0)), H - T, int(round(rx1)), H)
    return m


def g_W(M):
    H, S, s, E, T = M.H, M.S, M.s, M.E, M.T
    dx = round(M.C * H * 0.21)
    xa = E + S / 2.0
    xv1 = xa + dx
    xm = xv1 + dx
    xv2 = xm + dx
    xd = xv2 + dx
    W = int(round(xd + S / 2.0 + E))
    m = blank(W, H)
    m |= stroke(W, H, xa, xv1, S)
    m |= stroke(W, H, xm, xv1, s)
    m |= stroke(W, H, xm, xv2, S * 0.9)
    m |= stroke(W, H, xd, xv2, s)
    for cx, sw in ((xa, S), (xd, s)):
        rect(m, int(round(cx - sw / 2.0 - E)), 0, int(round(cx + sw / 2.0 + E)), T)
    rect(m, int(round(xm - S * 0.45 - E * 0.5)), 0, int(round(xm + S * 0.45 + E * 0.5)), max(2, T - 1))
    return m


def g_N(M):
    H, S, s, E, T = M.H, M.S, M.s, M.E, M.T
    W = round(M.C * H * 0.86)
    m = blank(W, H)
    xl = E
    xr = W - E - s
    rect(m, xl, 0, xl + s, H)
    rect(m, xr, 0, xr + s, H)
    m |= stroke(W, H, xl + S / 2.0, xr + s - S / 2.0, S, y0=0, y1=H)
    rect(m, 0, 0, xl + S, T)              # top-left slab (merges with diagonal)
    rect(m, xr - E, 0, W, T)              # top-right serif
    rect(m, 0, H - T, xl + s + E, H)      # bottom-left serif
    m[H - T:, xr + s:] = False
    return m


def g_O(M, W=None):
    H, S = M.H, M.S
    W = W or round(M.C * H * 0.84)
    outer = ellipse_mask(W, H, W / 2.0, H / 2.0, W / 2.0, H / 2.0, p=2.6)
    inner = ellipse_mask(W, H, W / 2.0, H / 2.0, W / 2.0 - S, H / 2.0 - M.B, p=2.6)
    return outer & ~inner


def g_U(M):
    H, S, s, E, T = M.H, M.S, M.s, M.E, M.T
    W = round(M.C * H * 0.86)
    R = round(W * 0.42)
    m = rrect_mask(W, H, E, 0, W - E, H, (0, 0, R, R), p=2.4)
    r2 = max(1, R - S)
    m &= ~rrect_mask(W, H, E + S, -1, W - E - s, H - M.B, (0, 0, r2, r2), p=2.4)
    rect(m, 0, 0, E + S + E, T)
    rect(m, W - E - s - E, 0, W, T)
    return m


def g_Y(M):
    H, S, s, E, T = M.H, M.S, M.s, M.E, M.T
    W = round(M.C * H * 0.92)
    c = W / 2.0
    ym = round(H * 0.52)
    m = blank(W, H)
    m |= stroke(W, H, E + S / 2.0, c, S, y0=0, y1=ym + 1)
    m |= stroke(W, H, W - E - s / 2.0, c, s, y0=0, y1=ym + 1)
    sx = int(round(c - S / 2.0))
    stem_with_serifs(m, M, sx, top=False, bottom=True, y0=ym - 1)
    rect(m, 0, 0, int(E + S + E), T)
    rect(m, int(W - E - s - E), 0, W, T)
    return m


def g_I(M):
    H, S, E = M.H, M.S, M.E
    W = S + 2 * E
    m = blank(W, H)
    stem_with_serifs(m, M, E)
    return m


def g_T(M):
    H, S, E, T = M.H, M.S, M.E, M.T
    W = round(M.C * H * 0.80)
    m = blank(W, H)
    rect(m, 0, 0, W, T)
    sx = (W - S) // 2
    stem_with_serifs(m, M, sx, top=False)
    # beak serifs
    bk = max(2, round(H * 0.14))
    rect(m, 0, T, max(1, round(T * 0.7)), T + bk)
    rect(m, W - max(1, round(T * 0.7)), T, W, T + bk)
    return m


def g_E(M, F=False):
    H, S, E, T = M.H, M.S, M.E, M.T
    W = round(M.C * H * 0.70)
    m = blank(W, H)
    stem_with_serifs(m, M, E, right=False, bottom=True)
    rect(m, 0, 0, W, T)
    bk = max(2, round(H * 0.13))
    bw = max(1, round(T * 0.75))
    rect(m, W - bw, T, W, T + bk)
    ym = round(H * 0.46)
    mb = max(2, M.B - 1)
    rect(m, E, ym, W - round(H * 0.14), ym + mb)
    rect(m, W - round(H * 0.14) - bw, ym - max(1, bk // 2), W - round(H * 0.14), ym + mb + max(1, bk // 2))
    if not F:
        rect(m, 0, H - T, W, H)
        rect(m, W - bw, H - T - bk, W, H - T)
    else:
        rect(m, 0, H - T, E + S + E + 1, H)
    return m


def g_F(M):
    return g_E(M, F=True)


def g_G(M):
    H, S, s, E = M.H, M.S, M.s, M.E
    W = round(M.C * H * 0.84)
    ring = g_O(M, W)
    # open the upper right
    ring[round(H * 0.24):round(H * 0.52), W // 2:] = False
    # beak at top-right terminal
    bk = max(2, round(H * 0.16))
    rect(ring, W - s - 1, round(H * 0.12), W - 1, round(H * 0.12) + bk)
    # crossbar + spur
    yb = round(H * 0.52)
    rect(ring, W // 2 - 1, yb, W, yb + max(2, M.B - 1))
    rect(ring, W - S, yb, W - 1, H - round(H * 0.18))
    return ring


def g_excl(M):
    H, S = M.H, M.S
    W = S + 2
    c = W / 2.0
    yt = round(H * 0.68)
    m = poly_mask(W, H, [(c - S * 0.58, 0), (c + S * 0.58, 0), (c + S * 0.26, yt), (c - S * 0.26, yt)])
    d = round(S * 0.95)
    m |= rrect_mask(W, H, c - d / 2.0, H - d, c + d / 2.0, H, (2, 2, 2, 2) if d > 6 else (1, 1, 1, 1))
    return m


def g_1(M):
    H, S, E, T = M.H, M.S, M.E, M.T
    W = round(M.C * H * 0.62)
    m = blank(W, H)
    sx = (W - S) // 2 + 1
    rect(m, sx, 0, sx + S, H)
    # flag: diagonal from top of stem to the left
    fl = round(H * 0.26)
    m |= poly_mask(W, H, [(sx, 0), (sx + 1, 0), (sx + 1, fl), (1, fl + round(H * 0.06)), (1, fl - round(H * 0.08))])
    # foot slab
    rect(m, sx - round(H * 0.16), H - T, sx + S + round(H * 0.16), H)
    _bracket(m, sx, H - T, M.K, -1, -1)
    _bracket(m, sx + S, H - T, M.K, 1, -1)
    return m


def _ball(W, H, cx, cy, r):
    return ellipse_mask(W, H, cx, cy, r, r, p=2.0)


def g_2(M):
    H, S, s, E, T = M.H, M.S, M.s, M.E, M.T
    W = round(M.C * H * 0.70)
    yb = round(H * 0.55)
    R = W // 2
    ring = rrect_mask(W, H, 0, 0, W, yb + R // 2, (R, R, R, R), p=2.2)
    ring &= ~rrect_mask(W, H, s + 1, M.B, W - S, yb + R // 2 - M.B, (R - S, R - S, R - S, R - S), p=2.2)
    ring[round(H * 0.30):, :W // 2 + 1] = False      # open the left/bottom-left
    ring[yb + 1:, :] = False
    m = ring
    # ball terminal on the upper left
    br = S * 0.62
    m |= _ball(W, H, br + 0.5, round(H * 0.30), br)
    # diagonal from right of the bowl down to bottom-left
    m |= poly_mask(W, H, [(W - S - 1, yb - round(H * 0.16)), (W, yb - round(H * 0.14)),
                          (S + 1, H - T + 1), (0, H - T + 1), (0, H - T - 1)])
    rect(m, 0, H - T, W, H)
    rect(m, W - max(2, round(T * 0.7)), H - T - round(H * 0.12), W, H - T)
    return m


def g_3(M):
    H, S, s, E, T = M.H, M.S, M.s, M.E, M.T
    W = round(M.C * H * 0.70)
    ym = round(H * 0.46)
    R1 = round(ym * 0.5)
    top = rrect_mask(W, H, round(W * 0.10), 0, W - 1, ym + M.B // 2 + 1, (R1, R1, R1, R1), p=2.2)
    top &= ~rrect_mask(W, H, round(W * 0.10) + s, M.B, W - 1 - S + 1, ym - M.B // 2 + 1,
                       (max(1, R1 - S),) * 4, p=2.2)
    R2 = round((H - ym) * 0.5)
    bot = rrect_mask(W, H, 0, ym - M.B // 2, W, H, (R2, R2, R2, R2), p=2.2)
    bot &= ~rrect_mask(W, H, s, ym + M.B - M.B // 2, W - S, H - M.B, (max(1, R2 - S),) * 4, p=2.2)
    m = top | bot
    # open the left side between terminals
    m[round(H * 0.24):ym - M.B // 2, :round(W * 0.5)] = False
    m[ym + M.B - M.B // 2:H - round(H * 0.22), :round(W * 0.42)] = False
    m[ym - M.B // 2:ym + M.B - M.B // 2, :round(W * 0.36)] = False
    br = S * 0.58
    m |= _ball(W, H, round(W * 0.10) + br, round(H * 0.24), br)
    m |= _ball(W, H, br + 0.5, H - round(H * 0.22), br)
    return m


BUILDERS = {'A': g_A, 'D': g_D, 'E': g_E, 'F': g_F, 'G': g_G, 'I': g_I, 'N': g_N, 'O': g_O,
            'P': g_P, 'R': g_R, 'T': g_T, 'U': g_U, 'W': g_W, 'Y': g_Y, '!': g_excl,
            '1': g_1, '2': g_2, '3': g_3}


def glyph(ch, H, weight=1.0, cond=1.0):
    M = Metrics(H, weight, cond)
    m = BUILDERS[ch](M)
    # trim empty columns
    cols = np.nonzero(m.any(0))[0]
    return m[:, cols.min():cols.max() + 1]


def word(text, H, gap=None, weight=1.0, space=None, kern=None, cond=1.0):
    gap = max(1, round(H * 0.06)) if gap is None else gap
    space = round(H * 0.32) if space is None else space
    kern = kern or {}
    parts = []
    for i, ch in enumerate(text):
        if ch == ' ':
            parts.append(np.zeros((H, space), bool))
            continue
        parts.append(glyph(ch, H, weight, cond))
        if i < len(text) - 1 and text[i + 1] != ' ':
            k = gap + kern.get(text[i:i + 2], 0)
            if k > 0:
                parts.append(np.zeros((H, k), bool))
            elif k < 0:
                parts.append(-k)   # marker: overlap
    W = sum(p.shape[1] for p in parts if isinstance(p, np.ndarray)) + 4
    m = np.zeros((H, W), bool)
    x = 0
    for p in parts:
        if isinstance(p, (int, np.integer)):
            x -= int(p)
            continue
        m[:, x:x + p.shape[1]] |= p
        x += p.shape[1]
    return crop_mask(m) if m.any() else m


# ------------------------------------------------------------------------- the lettering effect
SCHEMES = {
    # face top..bottom, highlight, face shade, outline, extrusion (light, dark), outer outline, shadow
    'brass': dict(face=['cream0', 'brass0', 'brass1'], hi='white', lo='brass2', line='ink2',
                  ext=['red1', 'red2', 'red3'], outer='ink', shadow='ink2'),
    'cream': dict(face=['cream0', 'cream1', 'cream2'], hi='white', lo='paintCreamD', line='ink2',
                  ext=['red1', 'red2', 'red3'], outer='ink', shadow='ink2'),
    'steel': dict(face=['steel0', 'steel1', 'char0'], hi='white', lo='steel2', line='ink2',
                  ext=['char2', 'char3', 'ink2'], outer='ink', shadow='ink2'),
    'gold': dict(face=['sun1', 'brass0', 'brass1'], hi='sun0', lo='brass2', line='ink2',
                 ext=['red1', 'red2', 'red3'], outer='ink', shadow='ink2'),
}


def letter_fx(m, scheme='brass', depth=4, ext_dx=1, lean=0, outline2=True, shadow=(0, 2), bands=None,
              inline=False, margin=2):
    """Render a bool letter mask into a coloured RGBA image.
    depth   = rows of block extrusion (down / down-right by ext_dx per step... integer slope)
    lean    = shear factor k (1 px per k rows) for an italic forward lean, 0 = upright
    """
    S = SCHEMES[scheme]
    if lean:
        m = shear(m, lean)
    H0, W0 = m.shape
    P_ = depth + 4 + margin
    face = pad(m, P_, P_, P_ + depth, P_ + depth)
    h, w = face.shape
    line = dilate(face, 1) & ~face
    body = face | line
    ext = np.zeros_like(face)
    steps = []
    for i in range(1, depth + 1):
        dxi = (i * ext_dx) if ext_dx else 0
        st = shift(body, dxi, i)
        steps.append(st)
        ext |= st
    ext &= ~body
    allm = body | ext
    outer = dilate(allm, 1) & ~allm if outline2 else np.zeros_like(allm)
    full = allm | outer
    cv = Canvas(w, h)
    if shadow:
        sh = shift(full, shadow[0], shadow[1]) & ~full
        cv.paint(sh, S['shadow'])
    cv.paint(outer, S['outer'])
    # extrusion shading: the face-adjacent rim (first step) lighter, deeper steps darker,
    # vertical side walls (where the extrusion is reached horizontally) darkest
    e_light = ext & shift(body, 0, 1)
    e_side = ext & ~shift(allm, 0, -1) | (ext & ~e_light & shift(ext, -1, 0) & ~shift(ext, 0, 1))
    cv.paint(ext, S['ext'][1])
    cv.paint(ext & ~e_side, S['ext'][1])
    cv.paint(e_light & ~shift(body, 0, 2) | (ext & shift(body, 0, 1)), S['ext'][0])
    cv.paint(ext & ~shift(ext, 0, -1) & ~shift(body, 0, -1), S['ext'][2])   # bottom-most row of the block
    cv.paint(line, S['line'])
    # two-tone face in horizontal bands
    rows = np.nonzero(face.any(1))[0]
    y0, y1 = rows.min(), rows.max() + 1
    fh = y1 - y0
    bands = bands or [0.0, 0.46, 0.72]
    for bi, col in enumerate(S['face']):
        b0 = y0 + int(round(fh * bands[bi]))
        b1 = y0 + int(round(fh * bands[bi + 1])) if bi + 1 < len(bands) else y1
        bandm = face.copy()
        bandm[:b0] = False
        bandm[b1:] = False
        cv.paint(bandm, col)
    # bevel: 1px highlight on top/left inner edge, 1px shade on bottom/right inner edge
    lo = (edge_in(face, 0, 1) | edge_in(face, 1, 0)) & ~(edge_in(face, 0, -1))
    hi = edge_in(face, 0, -1) | (edge_in(face, -1, 0) & ~edge_in(face, 0, 1))
    upper = np.zeros_like(face)
    upper[:y0 + int(round(fh * 0.56))] = True
    cv.paint(lo, S['lo'])
    cv.paint(hi & upper, S['hi'])
    cv.paint(hi & ~upper, S['face'][1] if scheme != 'steel' else 'steel0')
    if inline:
        inl = edge_in(face, 0, -1)
        inl = shift(inl, 0, 2) & face & ~edge_in(face, 0, 1)
    img = cv.img()
    bb = img.getbbox()
    return img.crop(bb)
