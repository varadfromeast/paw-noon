"""Blackpaw rig: variants of parts + a pose -> frame composer.
A pose is a dict; every key is optional and falls back to the idle base.
"""
from bkit import Part, compose, pixels, PA
import parts_base as B

# =============================================================== expressions (head rows y24..y30)
# Head part spans x22..42 (21 cols); rows index = y - 20.
EXPR = {
    'squint': {},
    'blink1': {4: "..444kk4443444kk422..", 5: "..44kkkkk333kkkkk22.."},
    'blink2': {4: "..44444444434444442..", 5: "..44kkkkk333kkkkk22..", 6: "..4433333333333322..."},
    'narrow': {4: "..44kkkk43444kkkk22..", 5: "..444ekkk333kkke422.."},
    'focus':  {4: "..44kkkk43444kkkk22..", 5: "..444wkkk333kkkw422.."},
    'shock':  {3: "..4kkk4444444444kkk2..", 4: "..4ewk4443444kwe22..", 5: "..4eek3333333kee122..",
               6: "..4433333333333322...",
               9: ".43333322kkk22322222.", 10: "..44333322kpk233222..."},
    'wince':  {4: "..4kk44444344444kk2..", 5: "..444kkk333kkk4422..", 6: "..44k333333333k322...",
               9: ".4333332kkkkk2322222.", 10: "..44333322jj2233222..."},
    'grit':   {4: "..44kkkk43444kkkk22..", 5: "..444ekkk333kkke422..",
               9: ".43333kkkkkkk222222.", 10: "..44333j2j2j2332222..."},
    'smug':   {4: "..4444444434444kkk2..", 5: "..44kkkk3333kkkee22..",
               9: ".4333332k22kkk322222.", 10: "..4433332222j233222..."},
    'blow':   {4: "..4444444434444kkk2..", 5: "..44kkkk3333kkkee22..",
               8: "443333222p22223222222", 9: ".4333332222k22322222.", 10: "..44333322222333222..."},
    'worry':  {3: "..4k4444444444444k42..", 4: "..44kk4443444kk422..", 5: "..44ewk333333kwe22..",
               9: ".4333332k2k2k2322222.", 10: "..44333322222233222..."},
    'dazed':  {4: "..44k4k4434444k4k22..", 5: "..444k4433334444k422..", 6: "..44k4k333333k3k22...",
               9: ".4333332kkkkk2322222.", 10: "..44333322nn2233222..."},
}


def head_part(expr='squint'):
    rows = list(B.HEAD_ROWS)
    for i, r in EXPR[expr].items():
        rows[i] = (r + '.' * 21)[:21]
    return Part(rows, B.HEAD.x, B.HEAD.y, name='head_' + expr)


# =============================================================== ears
EARS = {
    'up': (B.EAR_L, B.EAR_R),
    # flattened: rotated outward / down, hand drawn
    'flat': (Part("""
3.....
33m...
.333m3
..3333
""", x=20, y=17, name='earLflat'), Part("""
.....2
...m22
2m3332
33332.
""", x=37, y=17, name='earRflat')),
    # one ear twitch (right ear flicks outward)
    'twitch': (B.EAR_L, Part("""
....2
...m2
..mm2
.3mm2
3m32.
3332.
""", x=37, y=14, name='earRtw')),
}

# =============================================================== hats
HAT = B.HAT
HAT_TILT = Part("""
.........KKp.Kp4.......
........KKKKppp44......
........KKKKppo4p......
.......KKKKKpppp4......
..Kp...NBBBBbbbby......
..KKKKppppppppppp3334pK
KKKKKKKKKKKKKKKKKKKKK3p
.KKKK...........KKKKK..
""", x=20, y=14, name='hat_tilt')

# hat seen tumbling (for hit pop / down)
HAT_SPIN = [
    HAT,
    Part("""
.....KKp4...
...KKKpppp4.
..NKKKpoppp.
.KNBKKpppp..
KKKBbKpp4...
KKKKybp.....
.KKKKKK.....
..KKKK......
""", x=0, y=0, name='hat_spin1'),
    Part("""
.KKKKKKKKKKKKKKKKKKKKK.
KKKKpppppppppppp333334p
Kp.....NBBBbbbby.....pK
.......KKKKpppp4.......
.......KKKKppo4p.......
.......KKKKppp44.......
........KKp.Kp4........
""", x=0, y=0, name='hat_upside'),
    Part("""
...KKp......
..KKpp4.....
.KKKKpp4....
.KKBKpp4....
.KKNbbpp....
KKKKKyyp....
KKKKKKK.....
.KKK........
""", x=0, y=0, name='hat_side'),
]


# =============================================================== tail (procedural spine brush)
def tail_part(spine, widths, tip_light=True, name='tail'):
    """spine: list of (x,y) along the tail from base to tip. widths: per point (1..3).
    Each point paints a short run perpendicular to the local direction. Shading from neighbours:
    lit on right/top, shadow on left/bottom."""
    pts = set()
    n = len(spine)
    for i, (x, y) in enumerate(spine):
        a = spine[max(0, i - 1)]
        b = spine[min(n - 1, i + 1)]
        dx, dy = b[0] - a[0], b[1] - a[1]
        w = widths[i]
        if abs(dx) > abs(dy):           # horizontal-ish: thickness downward
            for k in range(w):
                pts.add((x, y + k))
        else:                            # vertical-ish: thickness rightward
            for k in range(w):
                pts.add((x + k, y))
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
    x0, y0 = min(xs), min(ys)
    wdt, hgt = max(xs) - x0 + 1, max(ys) - y0 + 1
    rows = [['.'] * wdt for _ in range(hgt)]
    tip = spine[-1]
    for (x, y) in pts:
        r, l = (x + 1, y) not in pts, (x - 1, y) not in pts
        t, b = (x, y - 1) not in pts, (x, y + 1) not in pts
        if r and not l:
            c = '2'
        elif l and not r:
            c = '4'
        elif t and not b:
            c = '2'
        elif b and not t:
            c = '4'
        else:
            c = '3'
        rows[y - y0][x - x0] = c
    if tip_light:
        rows[tip[1] - y0][tip[0] - x0] = '2'
    return Part([''.join(r) for r in rows], x0, y0, name=name)


def _line(pts):
    """expand a coarse polyline of (x,y) into a 8-connected pixel path."""
    out = [pts[0]]
    for (x1, y1) in pts[1:]:
        x0, y0 = out[-1]
        while (x0, y0) != (x1, y1):
            x0 += (x1 > x0) - (x1 < x0)
            y0 += (y1 > y0) - (y1 < y0)
            out.append((x0, y0))
    return out


def _tail(poly, name):
    sp = _line(poly)
    n = len(sp)
    widths = [3 if i < n * 0.35 else 2 for i in range(n)]
    widths[-1] = 2
    return tail_part(sp, widths, name=name)


TAILS = {
    'c': _tail([(40, 52), (44, 52), (47, 49), (48, 47), (48, 44), (47, 42), (46, 41)], 'tail_c'),
    'r': _tail([(40, 52), (45, 52), (49, 49), (50, 47), (50, 44), (49, 42), (48, 41)], 'tail_r'),
    'rr': _tail([(40, 52), (45, 52), (49, 50), (51, 48), (52, 45), (52, 43), (51, 41)], 'tail_rr'),
    'l': _tail([(40, 52), (43, 51), (45, 49), (46, 47), (46, 44), (46, 42), (47, 41), (48, 41)], 'tail_l'),
    'low': _tail([(40, 52), (46, 52), (49, 51), (51, 49), (52, 47), (52, 46)], 'tail_low'),
    'up': _tail([(40, 51), (43, 50), (45, 47), (46, 44), (46, 40), (45, 38), (44, 37)], 'tail_up'),
    'bristle': _tail([(40, 52), (44, 51), (46, 49), (47, 46), (47, 42), (47, 39)], 'tail_br'),
}

# =============================================================== bandana tips (wind to the right)
TIPS = [
    B.BANDANA_TIPS,
    Part("""
RRrr..
dRRRrr
.ddd.R
......
""", x=40, y=30, name='tips1'),
    Part("""
RRr..r
dRRrrR
.ddRd.
......
""", x=40, y=30, name='tips2'),
    Part("""
RRr...
dRRr..
.dRRr.
..ddRr
""", x=40, y=30, name='tips3'),
]

# =============================================================== coat skirt flutter (bottom rows)
def _skirt(base, bottoms, name):
    rows = list(base.rows)
    k = len(bottoms)
    w = max(len(r) for r in rows + bottoms)
    rows = rows[:len(rows) - k] + bottoms
    return Part([r.ljust(w, '.') for r in rows], base.x, base.y, name=name)


SKIRT_L = [
    B.SKIRT_L,
    _skirt(B.SKIRT_L, [".zLLLLLLD.", ".zLLLLLD..", "zLL.LLLD.."], 'skL1'),
    _skirt(B.SKIRT_L, [".zLLLLLLD.", "..zLLLLLD.", "..zL.LLD.."], 'skL2'),
    _skirt(B.SKIRT_L, [".zLLLLLLD.", ".zLLLLLD..", ".zL..LLD.."], 'skL3'),
]
SKIRT_R = [
    B.SKIRT_R,
    _skirt(B.SKIRT_R, [".DllllCllCc", "..DlllClCCl", "..Dll...ClC"], 'skR1'),
    _skirt(B.SKIRT_R, [".DllllCllCC", "..DlllClCCCc", "..Dll....CCl"], 'skR2'),
    _skirt(B.SKIRT_R, [".DllllCllCc", "..DlllClCCl", "..Dll..CCl."], 'skR3'),
]

# =============================================================== composer
def frame(pose=None):
    p = dict(pose or {})
    bx, by = p.get('body', (0, 0))          # whole upper body incl. belt/holster/skirt
    cx, cy = p.get('chest', (0, 0))         # extra for torso + arms (+ head group)
    hx, hy = p.get('head', (0, 0))          # extra for head group
    lx, ly = p.get('legs_off', (0, 0))
    ux, uy = bx + cx, by + cy
    gx, gy = ux + hx, uy + hy
    items = []
    # behind everything
    if p.get('tail', 'c'):
        t = p.get('tail', 'c')
        tp = TAILS[t] if isinstance(t, str) else t
        tx, ty = p.get('tail_off', (0, 0))
        items.append((tp, bx + tx, by + ty))
    for extra in p.get('back', []):
        items.append(extra)
    tips = p.get('tips', 0)
    if tips is not None:
        items.append((TIPS[tips] if isinstance(tips, int) else tips, gx, gy))
    # legs and boots
    items.append((p.get('legs', B.LEGS), lx, ly))
    bl = p.get('boot_l', (0, 0)); br = p.get('boot_r', (0, 0))
    items.append((p.get('bootL', B.BOOT_L), *bl))
    items.append((p.get('bootR', B.BOOT_R), *br))
    if p.get('spurs', True):
        items.append(pixels([(23 + bl[0], 58 + bl[1], 'steel1'), (22 + bl[0], 57 + bl[1], 'steel2'),
                             (22 + bl[0], 59 + bl[1], 'steel2'), (21 + bl[0], 58 + bl[1], 'steel1'),
                             (40 + br[0], 58 + br[1], 'steel1'), (41 + br[0], 57 + br[1], 'steel2'),
                             (41 + br[0], 59 + br[1], 'steel2'), (42 + br[0], 58 + br[1], 'steel1')]))
    # coat skirt
    sl = p.get('skirt_l', 0); sr = p.get('skirt_r', 0)
    items.append((SKIRT_L[sl] if isinstance(sl, int) else sl, bx, by))
    items.append((SKIRT_R[sr] if isinstance(sr, int) else sr, bx, by))
    # torso
    items.append((p.get('torso', B.TORSO), ux, uy))
    items.append((B.BELT, bx, by))
    if p.get('holster', 'full') == 'full':
        items.append((B.HOLSTER, bx, by))
    elif p.get('holster') == 'empty':
        items.append((HOLSTER_EMPTY, bx, by))
    for extra in p.get('mid', []):
        items.append(extra)
    # arms (behind-head layer)
    arm_l = p.get('armL', ARMS_L['hang'])
    arm_r = p.get('armR', ARMS_R['hang'])
    al = p.get('armL_off', (0, 0)); ar = p.get('armR_off', (0, 0))
    front_r = p.get('armR_front', False)
    items.append((arm_l, ux + al[0], uy + al[1]))
    if not front_r:
        items.append((arm_r, ux + ar[0], uy + ar[1]))
    # head group
    el, er = EARS[p.get('ears', 'up')]
    items.append((el, gx, gy)); items.append((er, gx, gy))
    items.append((head_part(p.get('expr', 'squint')), gx, gy))
    if p.get('scar', True):
        items.append(_shift(pixels(B.SCAR), gx, gy, p.get('expr', 'squint')))
    if p.get('whiskers', True):
        items.append(_shift(pixels(B.WHISK), gx, gy))
    items.append((p.get('bandana', B.BANDANA), ux + p.get('bandana_off', (0, 0))[0],
                  uy + p.get('bandana_off', (0, 0))[1]))
    hat = p.get('hat', HAT)
    if hat is not None:
        hdx, hdy = p.get('hat_off', (0, 0))
        if p.get('hat_abs'):
            items.append((hat, hdx, hdy))
        else:
            items.append((hat, gx + hdx, gy + hdy))
    if front_r:
        items.append((arm_r, ux + ar[0], uy + ar[1]))
    for extra in p.get('front', []):
        items.append(extra)
    return compose(items)


def _shift(painter, dx, dy, expr=None):
    def f(cv):
        sub = []
        orig = cv.px
        def px(x, y, c):
            orig(x + dx, y + dy, c)
        cv.px = px
        painter(cv)
        cv.px = orig
    return f


# =============================================================== arms
ARMS_R = {'hang': B.ARM_R}
ARMS_L = {'hang': B.ARM_L}
HOLSTER_EMPTY = Part("""
......
......
......
cCCCl.
cKKCl.
.cCCl.
.cCCl.
.cCl..
.cCl..
..l...
""", x=21, y=42, name='holster_empty')

# ---------------- gun-arm (his right, screen left) variants
ARMS_R.update({
    'hover0': Part("""
.....LLl.
....LLll.
...LLll..
..LLlcC..
...LcC43.
....43332
....3.3..
""", x=15, y=35, name='hover0'),
    'hover1': Part("""
.....LLl.
....LLll.
...LLll..
..LLlcC..
...LcC43.
....43332
.....3.3.
""", x=15, y=35, name='hover1'),
    'grab': Part("""
....LLl.
...LLll.
...LLll.
..LLll..
..LLll..
...Llc..
....cC4.
....4332
....433.
""", x=16, y=35, name='grab'),
    'swing': Part("""
....LLl....
...LLll....
...LLll....
...LLlc....
....LcC43..
.....G4332.
....GsG33..
....sks....
.....g.....
""", x=16, y=35, name='swing'),
    # gun pointed at the camera (front layer): big foreshortened revolver, bore at (27,36)
    'point': Part("""
Ll..........
Lll....g....
LLll..GgG...
.LLl.GskSG..
.LLl..GsG...
..LlsGgGgGs.
..LLGkGkGkG.
..LLsGsGsGS.
...Ll32223..
...L432223..
....cCCCCc..
""", x=20, y=33, name='point'),
    # recoil: barrel kicked up, muzzle at (27,29)
    'recoil': Part("""
.......g....
......GgG...
......GsS...
......GsS...
Ll...sGgGs..
Lll.GkGkGkG.
LLl.sGsGsGS.
.LLl322223..
.LLl432234..
..LLcCCCc...
""", x=20, y=29, name='recoil'),
    # twirl: arm out low to the side, paw at (15..18, 41..42)
    'side': Part("""
......LLl
.....LLll
....LLll.
...LLll..
..LLll...
.cCCc....
.4333....
.4332....
""", x=14, y=35, name='side'),
    # blow smoke: gun held up beside the face, muzzle at (24,26)
    'blow': Part("""
.......g..
.......Gs.
.......Gs.
.......Gs.
......GGsG
......sGks
.....43332
.....33332
....LcCC..
...LLll...
..LLll....
..LLl.....
...Ll.....
""", x=17, y=26, name='blow'),
    'flail': Part("""
.43.....
4332....
.cCc....
.LLl....
..LLl...
..LLl...
...LLl..
....LLl.
.....LLl
""", x=13, y=27, name='flailR'),
    'limp': Part("""
....LLl
...LLll
...LLll
...LLl.
...LLl.
...LLl.
...LLl.
...cCc.
...433.
...432.
""", x=17, y=36, name='limpR'),
})
ARMS_L.update({
    'out': Part("""
llC....
llCc...
.llCc..
..llCc.
..lCCc.
..cCCc.
...2223
...2232
....2..
""", x=40, y=35, name='outL'),
    'flail': Part("""
.....23.
....2232
....cCc.
....lCC.
...lCC..
...lCC..
..lCC...
.lCC....
lCC.....
""", x=40, y=27, name='flailL'),
    'fist': Part("""
llC...
llCc..
llCc..
.llCc.
.llCc.
.lCCc.
.cCCc.
.2232.
.2222.
""", x=40, y=35, name='fistL'),
    'limp': Part("""
llC..
llC..
llCc.
.llC.
.llC.
.llC.
.llC.
.cCc.
.222.
.232.
""", x=40, y=36, name='limpL'),
})

# small revolver in profile for the twirl, 4 orientations, pivot = trigger finger
GUNS_SIDE = {
    'down': Part("""
iI.
GsG
.G.
.s.
.G.
.g.
""", name='gd'),
    'left': Part("""
gGsGGs.
...SGsi
.....iI
""", name='gl'),
    'up': Part("""
.g.
.G.
.s.
.G.
GsG
.Ii
""", name='gu'),
    'right': Part("""
.sGGsGg
iGsS...
Ii.....
""", name='gr'),
}
