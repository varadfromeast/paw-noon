"""Paw Noon v3 art pipeline: user-generated pixel art -> game assets (270x480 core in a 360x640 canvas).
Inputs (this folder): src_plate.png, src_master.png (Blackpaw + portraits sheet), hr_*.png (hero, 2 poses each).
Run: python3 build_v3.py  (writes into Assets/PawNoon/Resources/Art/{Environment,Characters/*})"""
import json, os
import numpy as np
from PIL import Image
from scipy import ndimage
HERE = os.path.dirname(os.path.abspath(__file__))
ART = os.path.normpath(os.path.join(HERE, '..', '..', '..', 'Assets', 'PawNoon', 'Resources', 'Art'))
CW, CH, OX, OY = 360, 640, 45, 27          # canvas; plate placed at (OX, OY): top of the tallest phone view
def P(*a): return os.path.join(HERE, *a)

# ---------------------------------------------------------------- helpers
def key_magenta(c):
    r, g, b = [c[..., i].astype(int) for i in range(3)]
    return ~((r > 150) & (g < 100) & (b > 100) & (r - g > 110) & (b - g > 60))

def resample(c, m, s):
    h, w = m.shape; nw, nh = max(1, round(w * s)), max(1, round(h * s))
    prem = Image.fromarray((c * m[..., None]).astype(np.uint8)).resize((nw, nh), Image.BOX)
    mm = np.asarray(Image.fromarray((m * 255).astype(np.uint8)).resize((nw, nh), Image.BOX)).astype(float) / 255
    col = np.asarray(prem).astype(float) / np.maximum(mm[..., None], 1e-3)
    o = np.zeros((nh, nw, 4), np.uint8); o[..., :3] = np.clip(col, 0, 255); o[..., 3] = (mm > 0.5) * 255
    return Image.fromarray(o)

def clean(m, min_px=300, it=8):
    lab, n = ndimage.label(ndimage.binary_dilation(m, iterations=it))
    sizes = ndimage.sum(m, lab, range(1, n + 1))
    return m & np.isin(lab, [i + 1 for i, s in enumerate(sizes) if s > min_px])

def shared_palette(frames, colors=96):
    ks = list(frames); w = sum(frames[k].width for k in ks); h = max(frames[k].height for k in ks)
    sheet = Image.new('RGB', (w, h)); x = 0
    for k in ks: sheet.paste(frames[k].convert('RGB'), (x, 0)); x += frames[k].width
    pal = sheet.quantize(colors=colors, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    out = {}
    for k in ks:
        q = np.asarray(frames[k].convert('RGB').quantize(palette=pal, dither=Image.Dither.NONE).convert('RGBA')).copy()
        q[..., 3] = np.asarray(frames[k])[..., 3]; im = Image.fromarray(q); out[k] = im.crop(im.getbbox())
    return out

def feet_x(im):
    a = np.asarray(im)[..., 3]; rows = a[int(a.shape[0] * 0.9):]; xs = np.where(rows.any(0))[0]
    return int((xs.min() + xs.max()) // 2)

def breathe(im, cut_frac, dy):
    """Shift everything above cut row down by dy px (fills the seam by repeating the cut row)."""
    if dy == 0: return im.copy()
    a = np.asarray(im).copy(); cut = int(a.shape[0] * cut_frac)
    top = a[:cut].copy(); a[dy:cut + dy] = top; a[:dy] = 0
    return Image.fromarray(a)

def shift(im, dx, dy):
    o = Image.new('RGBA', im.size, (0, 0, 0, 0)); o.paste(im, (dx, dy)); return o

# ---------------------------------------------------------------- environment
K = 585 / 1672                              # plate source px -> game px (plate fills the tallest phone view)
PW, PH = round(941 * K), 585                # 329 x 585
PX, PY = 28, 27                             # plate top-left on the canvas (shifted right so SALOON stays in view)
def to_canvas(core_xy):                     # positions authored against the old 270x480 fit (scale 270/941)
    f = K / (270 / 941); return [round(core_xy[0] * f) + PX, round(core_xy[1] * f) + PY]

def build_env():
    out = os.path.join(ART, 'Environment'); os.makedirs(out, exist_ok=True)
    src = Image.open(P('src_plate.png')).convert('RGB')
    plate_px = src.resize((PW, PH), Image.BOX)
    pal = plate_px.quantize(colors=128, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    c = np.asarray(plate_px.quantize(palette=pal, dither=Image.Dither.NONE).convert('RGB')).astype(float)
    canvas = np.zeros((CH, CW, 3), float)
    canvas[PY:PY + PH, PX:PX + PW] = c
    canvas[:PY, PX:PX + PW] = c[:1] ; canvas[PY + PH:, PX:PX + PW] = c[-1:] * 0.7     # never visible on phones
    canvas[:, :PX] = canvas[:, PX:2 * PX][:, ::-1] * 0.85                             # iPad side margins
    r = CW - PX - PW; canvas[:, PX + PW:] = canvas[:, PX + PW - r:PX + PW][:, ::-1] * 0.85
    plate = Image.fromarray(np.clip(canvas, 0, 255).astype(np.uint8)).quantize(palette=pal, dither=Image.Dither.NONE).convert('RGBA')
    plate.save(os.path.join(out, 'plate.png'))
    band = Image.new('RGBA', (CW, CH), (0, 0, 0, 0))
    x0, y0 = to_canvas((92, 112)); x1, y1 = to_canvas((232, 222))
    band.paste(plate.crop((x0, y0, x1, y1)), (x0, y0)); band.save(os.path.join(out, 'heat_band.png'))
    old = json.load(open(os.path.join(out, 'environment.json'))) if os.path.exists(os.path.join(out, 'environment.json')) else {}
    sheets = {k: v for k, v in old.get('sheets', {}).items() if k in ('tumbleweed', 'vulture')}
    env = {
        'canvas': [CW, CH],
        'layers': [{'name': 'plate', 'file': 'plate', 'order': 0, 'heat': False},
                   {'name': 'heat_band', 'file': 'heat_band', 'order': 2, 'heat': True}],
        'sheets': sheets, 'props': [], 'clouds': [],
        'tumbleweed': {'sheet': 'tumbleweed', 'laneY': [to_canvas((0, 345))[1], to_canvas((0, 395))[1]], 'order': 78},
        'vulture': {'sheet': 'vulture', 'center': to_canvas((150, 40)), 'radius': [52, 12], 'order': 12},
        'anchors': {'banditFeet': to_canvas((205, 336)), 'heroBottomLeft': to_canvas((86, 488)),
                    'vanishingPoint': to_canvas((175, 290)), 'sun': to_canvas((182, 63))},
        'source': 'v3: user-generated plate processed by Tools/ArtGen/v3/build_v3.py'}
    json.dump(env, open(os.path.join(out, 'environment.json'), 'w'), indent=1)
    return env

# ---------------------------------------------------------------- characters
def hero_frames():
    S = 1.38 / 5.8 * (K / (270 / 941))
    jobs = [('hr_ready_draw.png', ['ready', 'draw'], 1.0), ('hr_aim_fire.png', ['aim', 'fire'], 1.26),
            ('hr_hit_down.png', ['hit', 'down'], 1.26), ('hr_spin_tip.png', ['win_spin', 'win_tip'], 1.0)]
    fr = {}
    for f, names, mult in jobs:
        a = np.asarray(Image.open(P(f)).convert('RGB')); m = clean(key_magenta(a))
        cnt = m.sum(0); W = m.shape[1]; lo, hi = int(W * .35), int(W * .65); split = lo + int(np.argmin(cnt[lo:hi]))
        for n, (x0, x1) in zip(names, [(0, split), (split, W)]):
            fr[n] = resample(a[:, x0:x1], m[:, x0:x1].astype(np.float32), S * mult)
    return shared_palette(fr)

REG = {'bandit_ready': (812, 45, 1025, 322), 'bandit_draw': (1050, 45, 1290, 322), 'bandit_aim': (1300, 45, 1525, 322),
       'bandit_fire': (805, 360, 1015, 632), 'bandit_hit': (1015, 360, 1270, 632), 'bandit_down': (1270, 420, 1525, 632),
       'bandit_win_spin': (590, 705, 845, 992), 'bandit_win_tip': (850, 705, 1080, 992),
       'hero_portrait': (1138, 760, 1308, 922), 'bandit_portrait': (1340, 760, 1510, 922)}

def key_portrait(c):
    """Looser magenta key for the portrait boxes: also catches the anti-aliased pink fringe."""
    r, g, b = [c[..., i].astype(int) for i in range(3)]
    return ~((r - g > 70) & (b - g > 45) & (r > 120) & (b > 90))

def portrait_cut(c, charcoal=False):
    """Head-and-shoulders portrait from a framed box on the master sheet: drop the box's dark frame
    lines, key out the magenta fill (+ spill), keep only the cat, resample to fit 36 px."""
    m = key_portrait(c)
    ys, xs = np.where(m); c = c[ys.min():ys.max() + 1, xs.min():xs.max() + 1]; m = m[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    # the frame: dark rows/cols that run almost the whole edge; peel them off
    dark = c.max(axis=2) < 60
    t = 0
    while t < 12 and (dark[t].mean() > 0.6 or dark[:, t].mean() > 0.6 or dark[-1 - t].mean() > 0.6 or dark[:, -1 - t].mean() > 0.6):
        t += 1
    c = c[t:-t or None, t:-t or None]; m = m[t:-t or None, t:-t or None]
    m = clean(m, 400, 3)
    # spill: edge pixels next to the key that still lean magenta get neutralised
    edge = m & ~ndimage.binary_erosion(m, iterations=2)
    c = c.astype(float)
    r, g, b = c[..., 0], c[..., 1], c[..., 2]
    sp = edge & (b > g + 25) & (r > g + 25)
    lim = np.maximum(g, (r + g) / 2 * 0.9)
    c[..., 2] = np.where(sp, np.minimum(b, lim), b)
    c[..., 0] = np.where(sp, np.minimum(r, np.maximum(g + 30, lim)), r)
    if charcoal:
        # Blackpaw's fur reads violet on the sheet: pull the dark, purple-leaning fur to warm charcoal
        # (eyes, bandana, ears and hat band are bright/saturated and stay as painted)
        r, g, b = c[..., 0], c[..., 1], c[..., 2]
        fur = (np.maximum(r, b) < 150) & (b > g + 4) & (r > g + 2)
        lum = 0.3 * r + 0.55 * g + 0.15 * b
        k = 0.8
        for i, off in enumerate((5, 2, -2)):
            c[..., i] = np.where(fur, c[..., i] * (1 - k) + (lum + off) * k, c[..., i])
    return resample(np.clip(c, 0, 255).astype(np.uint8), m.astype(np.float32), 36 / max(c.shape[:2]))

def master_frames():
    a = np.asarray(Image.open(P('src_master.png')).convert('RGB')); fr = {}
    for k, (x0, y0, x1, y1) in REG.items():
        c = a[y0:y1, x0:x1]; m = key_magenta(c)
        if 'portrait' in k:
            fr[k] = portrait_cut(c, charcoal=k.startswith('bandit'))
        else:
            fr[k] = resample(c, clean(m, 150, 6).astype(np.float32), 1 / 3.2 * (K / (270 / 941)) * 1.18)
    b = shared_palette({k[7:]: v for k, v in fr.items() if k.startswith('bandit_') and 'portrait' not in k})
    return b, fr['hero_portrait'], fr['bandit_portrait']

def write_char(name, frames, anims, out):
    os.makedirs(out, exist_ok=True)
    # common cell: align every pose on its feet (feet_x, bottom)
    fx = {k: feet_x(v) for k, v in frames.items()}
    left = max(fx.values()); right = max(v.width - fx[k] for k, v in frames.items()); hh = max(v.height for v in frames.values())
    cw, ch = left + right + 2, hh + 2
    def cell(im_key, dx=0, dy=0, breath=0):
        im = frames[im_key]
        if breath: im = breathe(im, 0.55, breath)
        c = Image.new('RGBA', (cw, ch), (0, 0, 0, 0)); c.paste(im, (left - fx[im_key] + dx, ch - 1 - im.height + dy)); return c
    meta = {'cell': [cw, ch], 'pivot': [left, ch - 1], 'anims': {}}
    for an, spec in anims.items():
        cells = [cell(*f) for f in spec['frames']]
        Image.fromarray(np.concatenate([np.asarray(c) for c in cells], 1)).save(os.path.join(out, f'{name}_{an}.png'))
        e = {'file': f'{name}_{an}', 'frames': len(cells), 'fps': spec['fps'], 'loop': spec['loop']}
        if 'fire' in spec: e['events'] = {'fire': spec['fire']}
        if 'muzzle' in spec: e['muzzle'] = [spec['muzzle'](f) for f in spec['frames']]
        meta['anims'][an] = e
    return meta, left, ch - 1, (cw, ch)

def muzzle_point(frames, key, left, base, ch, side):
    """Gun tip: rightmost (hero) or a point on the gun near the chest (bandit) in cell coords."""
    im = frames[key]; a = np.asarray(im)[..., 3] > 0
    ox, oy = left - feet_x(im), ch - 1 - im.height
    if side == 'right':
        upper = a[: int(a.shape[0] * 0.55)]; ys, xs = np.where(upper); i = np.argmax(xs); return [int(xs[i] + ox), int(ys[i] + oy)]
    return None

def build_chars():
    hf = hero_frames(); bf, hp, bp = master_frames()
    # ---- hero
    hh = os.path.join(ART, 'Characters', 'Hero')
    fxh = {k: feet_x(v) for k, v in hf.items()}; lefth = max(fxh.values())
    chh = max(v.height for v in hf.values()) + 2
    tip = muzzle_point(hf, 'aim', lefth, 0, chh, 'right')
    mz = lambda f: tip if f[0] == 'aim' else [-1, -1]
    hanims = {
        'idle': {'frames': [('ready',), ('ready',), ('ready', 0, 0, 1), ('ready', 0, 0, 1)], 'fps': 3, 'loop': True},
        'ready': {'frames': [('ready',), ('ready', 0, 0, 1)], 'fps': 2, 'loop': True},
        'draw_fire': {'frames': [('draw',), ('aim', 1, 0), ('aim',), ('aim', -2, -2), ('aim', -1, -1), ('aim',)], 'fps': 22, 'loop': False, 'fire': 2, 'muzzle': mz},
        'aim': {'frames': [('aim',), ('aim', 0, 0, 1)], 'fps': 2, 'loop': True, 'muzzle': mz},
        'hit': {'frames': [('hit', -3, 0), ('hit', -1, 0), ('hit',)], 'fps': 14, 'loop': False},
        'down': {'frames': [('hit',), ('hit', -2, 4), ('down',)], 'fps': 7, 'loop': False},
        'win': {'frames': [('win_spin',), ('win_spin', 0, 0, 1), ('win_tip',), ('win_tip', 0, 0, 1)], 'fps': 2, 'loop': True},
        'foul': {'frames': [('draw',), ('draw', 1, 0), ('draw',), ('ready',)], 'fps': 10, 'loop': False},
    }
    meta, left, base, _ = write_char('hero', hf, hanims, hh)
    meta['hitPoint'] = [left + 4, int(base * 0.45)]; meta['portrait'] = 'hero_portrait'; meta['portraitHurt'] = 'hero_portrait_hurt'
    json.dump(meta, open(os.path.join(hh, 'hero.json'), 'w'), indent=1)
    # ---- bandit (aims/fires at the camera: muzzle = gun centre in aim frame, measured by grey steel pixels)
    bb = os.path.join(ART, 'Characters', 'Bandit')
    aim = np.asarray(bf['aim']).astype(int); steel = (aim[..., 3] > 0) & (np.abs(aim[..., 0] - aim[..., 2]) < 25) & (aim[..., 0] > 150)
    ys, xs = np.where(steel); gun = (int(np.median(xs)), int(np.median(ys))) if len(xs) else (bf['aim'].width // 2, bf['aim'].height // 3)
    fxb = {k: feet_x(v) for k, v in bf.items()}; leftb = max(fxb.values()); chb = max(v.height for v in bf.values()) + 2
    btip = [gun[0] + leftb - fxb['aim'], gun[1] + chb - 1 - bf['aim'].height]
    mzb = lambda f: btip if f[0] == 'aim' else [-1, -1]
    banims = {
        'idle': {'frames': [('ready',), ('ready',), ('ready', 0, 0, 1), ('ready', 0, 0, 1)], 'fps': 3, 'loop': True},
        'ready': {'frames': [('ready',), ('ready', 0, 0, 1)], 'fps': 2, 'loop': True},
        'draw_fire': {'frames': [('draw',), ('aim', 0, 1), ('aim',), ('aim', 0, -2), ('aim', 0, -1), ('aim',)], 'fps': 22, 'loop': False, 'fire': 2, 'muzzle': mzb},
        'aim': {'frames': [('aim',), ('aim', 0, 0, 1)], 'fps': 2, 'loop': True, 'muzzle': mzb},
        'hit': {'frames': [('hit', 2, 0), ('hit', 1, 0), ('hit',)], 'fps': 14, 'loop': False},
        'down': {'frames': [('hit',), ('hit', 1, 2), ('down',)], 'fps': 7, 'loop': False},
        'win': {'frames': [('win_spin',), ('win_spin', 0, 0, 1), ('win_tip',), ('win_tip', 0, 0, 1)], 'fps': 2, 'loop': True},
        'foul': {'frames': [('draw',), ('draw', 1, 0), ('draw',), ('ready',)], 'fps': 10, 'loop': False},
    }
    meta, left, base, _ = write_char('bandit', bf, banims, bb)
    meta['hitPoint'] = [left, int(base * 0.45)]; meta['portrait'] = 'bandit_portrait'; meta['portraitHurt'] = 'bandit_portrait_hurt'
    json.dump(meta, open(os.path.join(bb, 'bandit.json'), 'w'), indent=1)
    # ---- portraits (36x36) + hurt variants (red flash tint, 1px knock)
    for name, im, d in (('hero', hp, hh), ('bandit', bp, bb)):
        c = Image.new('RGBA', (36, 36), (0, 0, 0, 0)); c.paste(im, ((36 - im.width) // 2, 36 - im.height)); c.save(os.path.join(d, f'{name}_portrait.png'))
        a = np.asarray(shift(c, 1, 1)).astype(float); a[..., 0] = np.minimum(255, a[..., 0] * 1.15 + 30); a[..., 1] *= 0.75; a[..., 2] *= 0.75
        Image.fromarray(a.astype(np.uint8)).save(os.path.join(d, f'{name}_portrait_hurt.png'))
    return hf, bf

if __name__ == '__main__':
    env = build_env(); build_chars(); print('ok', env['anchors'])
