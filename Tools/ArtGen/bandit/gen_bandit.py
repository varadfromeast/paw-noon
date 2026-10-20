#!/usr/bin/env python3
"""Generate Pawtruth (bandit) sprite strips, portraits and bandit.json.
Re-runnable: python3 gen_bandit.py [--preview]
Outputs -> project/Assets/PawNoon/Resources/Art/Characters/Bandit/
Previews (with --preview) -> work/bandit/
"""
import json, os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from PIL import Image
from bkit import Part, compose, pixels, PA, check_palette, strip, save_png, upscale
from rig import (frame, ARMS_R, ARMS_L, TAILS, HAT, HAT_TILT, HAT_SPIN, GUNS_SIDE, head_part)
import parts_base as B

OUT = os.path.join(os.path.abspath(os.path.join(HERE, '..', '..', '..')),
                   'Assets', 'PawNoon', 'Resources', 'Art', 'Characters', 'Bandit')
WORK = os.path.abspath(os.path.join(HERE, '..', '..', '..', '..', 'work', 'bandit'))

NOGUN = [-1, -1]
POINT_MUZZLE = (27, 38)
RECOIL_MUZZLE = (27, 32)

# ------------------------------------------------------------------ small fx helpers
SWEAT = Part("""
.x.
xWx
vxV
.V.
""", name='sweat')


def smoke(pts):
    return pixels([(x, y, c) for x, y, c in pts])


def tuft(stage):
    s = {
        0: [(36, 38, 'char0'), (37, 37, 'char1'), (35, 37, 'char0'), (38, 38, 'char1')],
        1: [(38, 35, 'char0'), (39, 34, 'char1'), (36, 34, 'char0'), (40, 36, 'char1'), (37, 33, 'char1')],
        2: [(41, 32, 'char1'), (39, 30, 'char0'), (43, 34, 'char1')],
    }[stage]
    return pixels(s)


def dust(stage, cx=32, y=60):
    s = {
        0: [(-14, 0), (-13, -1), (-12, 0), (12, 0), (13, -1), (14, 0), (-10, 0), (10, 0)],
        1: [(-17, 0), (-16, -1), (-15, -2), (-14, -1), (15, -1), (16, -2), (17, -1), (18, 0), (-12, -1), (12, -1)],
        2: [(-19, -1), (-18, -3), (-17, -2), (18, -2), (19, -3), (20, -1)],
    }[stage]
    cols = ['sand1', 'sand0', 'sand2']
    return pixels([(cx + dx, y + dy, cols[i % 3]) for i, (dx, dy) in enumerate(s)])


def mz(base, off):
    return [base[0] + off[0], base[1] + off[1]]


# ------------------------------------------------------------------ animations
def anim_idle():
    chest = [0, 0, 1, 1, 1, 1, 0, 0]
    headx = [0, 0, -1, 0, 0, 0, 1, 0]
    tails = ['c', 'c', 'r', 'rr', 'rr', 'r', 'c', 'l']
    expr = ['squint'] * 8; expr[5] = 'blink1'; expr[6] = 'blink2'
    ears = ['up'] * 8; ears[3] = 'twitch'
    fr = []
    for i in range(8):
        fr.append(frame({'chest': (0, chest[i]), 'head': (0, headx[i]), 'tail': tails[i],
                         'tips': i % 4, 'skirt_r': i % 4, 'skirt_l': (i // 2) % 4,
                         'expr': expr[i], 'ears': ears[i]}))
    return fr, None


def anim_ready():
    fr = []
    tails = ['up', 'bristle', 'up', 'bristle']
    for i in range(4):
        fr.append(frame({'body': (0, 1), 'armR': ARMS_R['hover%d' % (i % 2)], 'armL': ARMS_L['out'],
                         'expr': 'narrow', 'tail': tails[i], 'tips': (i + 1) % 4,
                         'skirt_r': i, 'skirt_l': (i + 2) % 4, 'head': (0, 0)}))
    return fr, None


def _point_pose(extra=None, off=(0, 0)):
    p = {'armR': ARMS_R['point'], 'armR_front': True, 'armR_off': off, 'holster': 'empty',
         'armL': ARMS_L['fist'], 'expr': 'narrow', 'tail': 'c'}
    if extra:
        p.update(extra)
    return p


def anim_draw_fire():
    fr, mzl = [], []
    # 0 anticipation: dip, grab the grip
    fr.append(frame({'body': (0, 2), 'armR': ARMS_R['grab'], 'armL': ARMS_L['out'], 'expr': 'focus',
                     'tail': 'bristle', 'skirt_l': 1, 'skirt_r': 2, 'tips': 1}))
    mzl.append(NOGUN)
    # 1 swing up: gun clears holster, smear
    smear = pixels([(21, 47, 'steel1'), (20, 46, 'steel2'), (22, 48, 'steel2'), (23, 46, 'cloud2')])
    fr.append(frame({'body': (0, 1), 'armR': ARMS_R['swing'], 'holster': 'empty', 'armL': ARMS_L['fist'],
                     'expr': 'focus', 'tail': 'up', 'skirt_l': 2, 'skirt_r': 1, 'tips': 2,
                     'front': [smear]}))
    mzl.append([21, 44])
    # 2 FIRE: pointed straight at camera
    fr.append(frame(_point_pose({'expr': 'grit', 'tail': 'up', 'skirt_l': 3, 'skirt_r': 2, 'tips': 3})))
    mzl.append(list(POINT_MUZZLE))
    # 3 recoil: barrel kicks up, head/hat jolt
    fr.append(frame(_point_pose({'armR': ARMS_R['recoil'], 'head': (0, -1), 'hat_off': (0, -1),
                                 'chest': (0, -1), 'expr': 'grit', 'tail': 'bristle', 'skirt_l': 1,
                                 'skirt_r': 3, 'tips': 0}, off=(0, 1))))
    mzl.append(mz(RECOIL_MUZZLE, (0, 0)))
    # 4 coming down, smoke curl
    s1 = smoke([(27, 33, 'haze'), (28, 32, 'cloud1'), (27, 31, 'haze')])
    fr.append(frame(_point_pose({'tail': 'c', 'skirt_l': 2, 'skirt_r': 0, 'tips': 1,
                                 'front': [s1]}, off=(0, -2))))
    mzl.append(mz(POINT_MUZZLE, (0, -2)))
    # 5 overshoot settle
    s2 = smoke([(28, 33, 'cloud1'), (29, 32, 'haze'), (28, 31, 'cloud2'), (29, 30, 'cloud1')])
    fr.append(frame(_point_pose({'tail': 'r', 'chest': (0, 1), 'skirt_l': 3, 'skirt_r': 1, 'tips': 2,
                                 'front': [s2]}, off=(0, 0))))
    mzl.append(mz(POINT_MUZZLE, (0, 1)))
    # 6 aim hold
    s3 = smoke([(29, 30, 'cloud2'), (30, 29, 'cloud1'), (30, 27, 'cloud2')])
    fr.append(frame(_point_pose({'tail': 'r', 'skirt_l': 0, 'skirt_r': 2, 'tips': 3, 'front': [s3]})))
    mzl.append(list(POINT_MUZZLE))
    return fr, mzl


def anim_aim():
    fr = [frame(_point_pose({'tail': 'c', 'tips': 0, 'skirt_r': 1})),
          frame(_point_pose({'tail': 'r', 'tips': 2, 'skirt_r': 3, 'chest': (0, 1)}))]
    return fr, [list(POINT_MUZZLE), mz(POINT_MUZZLE, (0, 1))]


def anim_hit():
    fr = []
    fl = {'armR': ARMS_R['flail'], 'armL': ARMS_L['flail']}
    fr.append(frame(dict(fl, body=(0, -1), expr='shock', hat_off=(0, -4), tail='bristle',
                         skirt_l=2, skirt_r=1, tips=2, front=[tuft(0)])))
    fr.append(frame(dict(fl, body=(1, -1), expr='shock', hat_off=(1, -7), tail='up',
                         skirt_l=3, skirt_r=2, tips=3, ears='flat', front=[tuft(1)])))
    fr.append(frame({'body': (1, 0), 'expr': 'wince', 'hat_off': (1, -5), 'tail': 'rr', 'ears': 'flat',
                     'armR': ARMS_R['limp'], 'armL': ARMS_L['limp'], 'skirt_l': 1, 'skirt_r': 3,
                     'tips': 0, 'front': [tuft(2)]}))
    fr.append(frame({'body': (0, 1), 'expr': 'wince', 'hat': HAT_TILT, 'hat_off': (-1, -1), 'tail': 'r',
                     'skirt_l': 2, 'skirt_r': 0, 'tips': 1}))
    fr.append(frame({'body': (0, 1), 'expr': 'grit', 'hat': HAT_TILT, 'hat_off': (-1, -1),
                     'armR': ARMS_R['hover0'], 'armL': ARMS_L['out'], 'tail': 'bristle',
                     'skirt_l': 3, 'skirt_r': 1, 'tips': 2}))
    return fr, None


def _lying():
    """Limp body laid on its side (head to screen left). Built from the limp standing pose by an
    exact 90-degree pixel transposition (no resampling): the front-right light then falls on the
    top edge, matching the scene's high sun. Then dropped onto the ground row."""
    limp = frame({'hat': None, 'expr': 'dazed', 'armR': ARMS_R['limp'], 'armL': ARMS_L['limp'],
                  'tail': 'low', 'whiskers': False, 'tips': 3, 'skirt_l': 0, 'skirt_r': 0})
    rot = limp.transpose(Image.ROTATE_90)
    bb = rot.getbbox()
    body = rot.crop(bb)
    cell = Image.new('RGBA', (64, 64), (0, 0, 0, 0))
    x = 32 - body.width // 2
    y = 61 - body.height
    cell.paste(body, (x, y))
    return cell


def _onto(base, items):
    """composite extra parts on top of an existing frame image."""
    top = compose(items)
    out = base.copy(); out.alpha_composite(top)
    return out


def anim_down():
    fr = []
    fr.append(frame({'body': (1, 0), 'expr': 'dazed', 'armR': ARMS_R['flail'], 'armL': ARMS_L['flail'],
                     'hat': HAT_SPIN[1], 'hat_abs': True, 'hat_off': (38, 6), 'tail': 'bristle',
                     'ears': 'flat', 'skirt_l': 2, 'skirt_r': 3, 'tips': 2}))
    fr.append(frame({'body': (0, 3), 'head': (1, 1), 'expr': 'dazed', 'armR': ARMS_R['limp'],
                     'armL': ARMS_L['limp'], 'hat': HAT_SPIN[2], 'hat_abs': True, 'hat_off': (40, 4),
                     'tail': 'rr', 'ears': 'flat', 'skirt_l': 1, 'skirt_r': 1, 'tips': 3}))
    fr.append(frame({'body': (0, 6), 'head': (2, 1), 'expr': 'dazed', 'armR': ARMS_R['limp'],
                     'armL': ARMS_L['limp'], 'legs': KNEEL, 'bootL': KNEEL_BOOT_L, 'bootR': KNEEL_BOOT_R,
                     'hat': HAT_SPIN[3], 'hat_abs': True, 'hat_off': (47, 22), 'tail': 'low',
                     'ears': 'flat', 'skirt_l': 3, 'skirt_r': 2, 'tips': 0}))
    lying = _lying()
    # 3 impact (one row higher = bounce), 4 settle, 5-7 hat rolls to rest beside him
    bounce = Image.new('RGBA', (64, 64), (0, 0, 0, 0)); bounce.paste(lying, (0, -1))
    fr.append(_onto(bounce, [dust(0), (HAT_SPIN[2], 44, 40)]))
    fr.append(_onto(lying, [dust(1), (HAT_SPIN[3], 49, 50)]))
    fr.append(_onto(lying, [dust(2), (HAT_SPIN[1], 51, 51)]))
    hat_rest = Part(HAT_SPIN[0].rows, 0, 0, name='hat_rest')
    bb = lying.getbbox()
    hx, hy = bb[0] + 4, bb[1] + 6          # hat lands over his face
    fr.append(_onto(lying, [(HAT_SPIN[3], hx + 2, hy - 3)]))
    fr.append(_onto(lying, [(HAT_SPIN[3], hx, hy)]))
    return fr, None


KNEEL = Part("""
..4SSSSSSSSSs..
.44SSSSSSSSSss.
4SSSSs...4SSSSs
4SSSs.....4SSs.
""", x=25, y=50, name='kneel')
KNEEL_BOOT_L = Part("""
lCCCl
LLLLL
""", x=23, y=54, name='kbl')
KNEEL_BOOT_R = Part("""
lCCCl
LLLLL
""", x=37, y=54, name='kbr')


def anim_win():
    fr, mzl = [], []
    pivot = (16, 42)
    places = [('down', (15, 42), (16, 47)), ('left', (9, 41), (9, 41)),
              ('up', (15, 35), (16, 35)), ('right', (17, 41), (23, 41))]
    tails = ['c', 'r', 'rr', 'r']
    arcs = [[(13, 45, 'steel1'), (12, 43, 'steel2')], [(12, 38, 'steel1'), (14, 36, 'steel2')],
            [(19, 37, 'steel1'), (21, 38, 'steel2')], [(20, 46, 'steel1'), (18, 47, 'steel2')]]
    for i, (o, pos, muz) in enumerate(places):
        g = GUNS_SIDE[o]
        fr.append(frame({'armR': ARMS_R['side'], 'armL': ARMS_L['fist'], 'holster': 'empty',
                         'expr': 'smug', 'tail': tails[i], 'tips': i, 'skirt_r': i, 'skirt_l': (i + 1) % 4,
                         'front': [(g, pos[0], pos[1]), pixels(arcs[i])]}))
        mzl.append(list(muz))
    bl = {'armR': ARMS_R['blow'], 'armR_front': True, 'armL': ARMS_L['fist'], 'holster': 'empty'}
    fr.append(frame(dict(bl, expr='squint', tail='c', tips=0, skirt_r=0, skirt_l=1)))
    mzl.append([24, 26])
    fr.append(frame(dict(bl, expr='blow', tail='l', tips=1, skirt_r=1, skirt_l=2, head=(-1, 0),
                         front=[smoke([(24, 24, 'haze'), (25, 23, 'cloud1'), (23, 23, 'cloudW'), (24, 22, 'haze')])])))
    mzl.append([24, 26])
    fr.append(frame(dict(bl, expr='blow', tail='c', tips=2, skirt_r=2, skirt_l=3, head=(-1, 0),
                         front=[smoke([(23, 20, 'cloud1'), (24, 19, 'haze'), (22, 18, 'cloud2'),
                                       (25, 21, 'cloud2'), (23, 22, 'haze')])])))
    mzl.append([24, 26])
    fr.append(frame(dict(bl, expr='smug', tail='r', tips=3, skirt_r=3, skirt_l=0,
                         front=[smoke([(21, 16, 'cloud2'), (22, 15, 'cloud1'), (20, 14, 'cloud2')])])))
    mzl.append([24, 26])
    return fr, mzl


def anim_foul():
    fr, mzl = [], []
    fr.append(frame(_point_pose({'expr': 'focus', 'tail': 'up', 'tips': 1, 'skirt_l': 2})))
    mzl.append(list(POINT_MUZZLE))
    click = pixels([(30, 35, 'sun1'), (31, 34, 'white'), (32, 35, 'sun1'), (31, 36, 'sun1'), (31, 33, 'sun1')])
    fr.append(frame(_point_pose({'expr': 'shock', 'tail': 'bristle', 'tips': 2, 'skirt_l': 3,
                                 'front': [click]})))
    mzl.append(list(POINT_MUZZLE))
    look = {'armR': ARMS_R['recoil'], 'armR_front': True, 'armR_off': (2, -1), 'holster': 'empty',
            'armL': ARMS_L['fist'], 'expr': 'worry', 'ears': 'flat'}
    fr.append(frame(dict(look, tail='l', tips=3, skirt_l=0, head=(-1, 0))))
    mzl.append(mz(RECOIL_MUZZLE, (2, -1)))
    fr.append(frame(dict(look, tail='low', tips=0, skirt_l=1, head=(-1, 0), front=[(SWEAT, 42, 20)])))
    mzl.append(mz(RECOIL_MUZZLE, (2, -1)))
    fr.append(frame(dict(look, tail='low', tips=1, skirt_l=2, head=(-1, 1), chest=(0, 1),
                         front=[(SWEAT, 43, 23)])))
    mzl.append(mz(RECOIL_MUZZLE, (2, 0)))
    fr.append(frame(dict(look, tail='low', tips=2, skirt_l=3, head=(-1, 1), chest=(0, 1),
                         front=[(SWEAT, 43, 27)])))
    mzl.append(mz(RECOIL_MUZZLE, (2, 0)))
    return fr, mzl


ANIMS = [
    # name, builder, fps, loop, events
    ('idle', anim_idle, 8, True, None),
    ('ready', anim_ready, 8, True, None),
    ('draw_fire', anim_draw_fire, 20, False, {'fire': 2}),
    ('aim', anim_aim, 4, True, None),
    ('hit', anim_hit, 14, False, None),
    ('down', anim_down, 10, False, None),
    ('win', anim_win, 8, True, None),
    ('foul', anim_foul, 12, False, None),
]

HIT_POINT = [32, 39]


def portraits():
    def bust(pose):
        f = frame(pose)
        return f.crop((18, 11, 46, 39))
    calm = bust({'expr': 'squint', 'tail': None, 'tips': 0})
    hurt = bust({'expr': 'wince', 'hat': HAT_TILT, 'hat_off': (-1, -1), 'ears': 'flat', 'tail': None,
                 'tips': 2})
    return calm, hurt


def build(preview=False):
    os.makedirs(OUT, exist_ok=True)
    meta = {'cell': [64, 64], 'pivot': [32, 61], 'hitPoint': HIT_POINT, 'anims': {},
            'portrait': 'bandit_portrait'}
    allframes = {}
    for name, fn, fps, loop, events in ANIMS:
        frames, muzzle = fn()
        for i, f in enumerate(frames):
            check_palette(f, name=f'{name}[{i}]')
        save_png(strip(frames), os.path.join(OUT, f'bandit_{name}.png'))
        d = {'file': f'bandit_{name}', 'frames': len(frames), 'fps': fps, 'loop': loop}
        if events:
            d['events'] = events
        d['muzzle'] = muzzle if muzzle else [NOGUN] * len(frames)
        assert len(d['muzzle']) == len(frames)
        meta['anims'][name] = d
        allframes[name] = (frames, fps, d['muzzle'])
    calm, hurt = portraits()
    for im, n in ((calm, 'bandit_portrait'), (hurt, 'bandit_portrait_hurt')):
        check_palette(im, name=n)
        assert im.size == (28, 28)
        save_png(im, os.path.join(OUT, n + '.png'))
    with open(os.path.join(OUT, 'bandit.json'), 'w') as fh:
        json.dump(meta, fh, indent=2)
    if preview:
        import preview
        preview.make(allframes, calm, hurt, WORK, OUT)
    return meta


if __name__ == '__main__':
    m = build('--preview' in sys.argv)
    print({k: v['frames'] for k, v in m['anims'].items()})
