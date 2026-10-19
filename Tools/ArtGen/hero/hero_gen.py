#!/usr/bin/env python3
"""Generates every hero ('the Kid') asset: animation strips, portraits and hero.json.
Usage: python3 hero_gen.py          (writes Assets/PawNoon/Resources/Art/Characters/Hero/)
Deterministic: re-running regenerates identical files."""
import os, sys, json, math
_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _HERE)
from herokit import Frame, Part, check_palette, strip, save_png, edge  # noqa: E402
from hero_parts import (pants, belt, holster, tail, head, poncho, fringe, hat, ears,  # noqa: E402
                        whiskers, hat_tr)
from hero_arm import arm, puff, smear, fur_puff, thick_curve
from herokit import Part as _Part  # noqa: E402

ROOT = os.path.abspath(os.path.join(_HERE, '..', '..', '..'))
OUT = os.path.join(ROOT, 'Assets', 'PawNoon', 'Resources', 'Art', 'Characters', 'Hero')
CELL = (144, 216)


def build(p):
    """Compose one frame from a pose dict. Returns (image, muzzle or None)."""
    f = Frame(*CELL)
    arm_parts, muzzle, holstered = arm(p)
    front = p.get('arm_front', False)
    f.add(pants(p)); f.add(belt(p))
    for q in holster(p, with_gun=holstered): f.add(q)
    if not front:
        for q in arm_parts: f.add(q)
    f.add(tail(p))
    f.add(head(p))
    f.add(poncho(p)); f.add(fringe(p))
    f.add(hat(p))
    for q in ears(p): f.add(q)
    f.add(whiskers(p))
    if front:
        for q in arm_parts: f.add(q)
    for q in p.get('fx', []):
        f.add(q)
    img = f.render()
    if p.get('blank'):
        img = Image_blank()
    return img, muzzle


def Image_blank():
    from PIL import Image
    return Image.new('RGBA', CELL, (0, 0, 0, 0))


def mz(m):
    return [int(m[0]), int(m[1])] if m else [-1, -1]


# ------------------------------------------------------------------------------- animations
def anim_idle():
    out = []
    for i in range(8):
        br = 1 if 2 <= i <= 5 else 0
        p = dict(breath=br, hdy=-br, ady=-br, fringe=i / 8, tail=i / 8, arm='rest', arm_front=True)
        if i == 5: p['earR'] = 'twitch'
        if i == 6: p['earR'] = 'back'
        if i in (3, 4): p['whisk'] = -1
        out.append(p)
    return out


def anim_ready():
    out = []
    for i in range(4):
        br = (0, 1, 1, 0)[i]
        out.append(dict(breath=br, hdy=-br + 1, bx=1, by=1, lift=0.5, arm_front=True, arm=('hover', 'hover', 'hover2', 'hover2')[i],
                        adx=(0, 0, 1, 0)[i], earR='perk', earL='perk', fringe=i / 4, fringe_amp=0.9,
                        tail=i / 4, tail_amp=0.5, whisk=1))
    return out


def anim_draw_fire():
    seq = [('b_pull', .45, 1, 1), ('b_swing', .8, 0, 0), ('b_aim', 1, 0, 0), ('b_kick', 1, -1, -1),
           ('b_recov', 1, 0, 0), ('b_aim', 1, 0, 0), ('b_aim2', 1, 0, 0)]
    out = []
    for i, (a, lift, bx, hy) in enumerate(seq):
        p = dict(arm=a, lift=lift, bx=bx, hdy=hy, earR='perk', earL='perk', fringe=0.15 + i * 0.12,
                 fringe_amp=2.2 if 1 <= i <= 4 else 1.4, tail=0.1 + i * 0.1, tail_amp=1.4)
        if a == 'swing':
            p['fx'] = [smear([(120, 134), (128, 116), (130, 98)], 'whisk', 0),
                       smear([(123, 136), (131, 118), (133, 100)], 'whisk', 1)]
        if a == 'recoil':
            p['hatdy'] = -2; p['earR'] = 'back'; p['earL'] = 'back'
        out.append(p)
    return out


def swing_smear():
    pt = _Part('smear', 'smear', outline=False)
    m, _ = thick_curve([(118, 140), (128, 124), (134, 104), (136, 86)], 0.6, 3.2, 10)
    inner, _ = thick_curve([(118, 140), (128, 124), (134, 104), (136, 86)], 0.3, 1.6, 10)
    pt.fill(m, 'whisk', 0); pt.fill(inner, 'whisk', 2)
    pt.noline[:] = True
    return pt


def anim_aim():
    return [dict(arm='b_aim', lift=1, earR='perk', earL='perk', fringe=0.2, tail=0.2),
            dict(arm='b_aim2', lift=1, breath=1, hdy=-1, earR='perk', earL='perk', fringe=0.45, tail=0.45)]


def anim_hit():
    seq = [(-6, 4, 3, -.14, -8, -2), (-5, 3, 2, -.26, -7, -4), (-2, 1, 1, -.24, -3, -3),
           (-1, 0, 0, -.18, 0, -2), (0, 0, 0, -.16, 0, -2)]
    out = []
    for i, (bx, by, hdy, tilt, hatdy, hatdx) in enumerate(seq):
        out.append(dict(arm='hover', adx=-2 if i < 2 else 0, ady=2 if i < 2 else 0, bx=bx, by=by,
                        hdy=hdy, tilt=tilt, hatdy=hatdy + hdy, hatdx=hatdx, ears_head=True, arm_front=True,
                        earR='back' if i < 3 else 'up', earL='back' if i < 3 else 'up',
                        fringe=0.1 + i * .2, fringe_amp=3 - i * .5, tail=i * .2, tail_amp=1.8, lift=.5,
                        whisk=-2 if i < 2 else 0))
    for i, r in ((0, 9), (1, 12), (2, 10)):
        out[i]['fx'] = [fur_puff(84 + out[i]['bx'], 92 + out[i]['by'] - i * 2, r, i)]
    return out


def anim_down():
    # body sinks out of frame; the hat lingers a beat, then follows
    seq = [(0, 2, 2, -.18, -1), (-1, 6, 4, -.24, -3), (-2, 14, 6, -.30, -4), (-2, 28, 7, -.34, -5),
           (-3, 50, 8, -.38, -5), (-3, 80, 8, -.42, -4), (-3, 122, 8, -.46, -3), (-3, 230, 8, -.5, -2)]
    out = []
    for i, (bx, by, hdy, tilt, hat_rel) in enumerate(seq):
        out.append(dict(arm='rest', bx=bx, by=by, hdy=hdy, tilt=tilt, hatdx=-2 - i,
                        hatdy=hdy + hat_rel, ears_head=True, earR='flat', earL='flat', fringe=i * .15,
                        fringe_amp=2, tail=i * .15, tail_dy=i, whisk=2))
    return out


def anim_win():
    seq = ['tw0', 'tw1', 'tw2', 'tw3', 'tw0', 'tw1', 'holst', 'rest', 'tip', 'tip2']
    out = []
    for i, a in enumerate(seq):
        p = dict(arm=a, earR='perk', earL='up', fringe=i / 10, tail=i / 10, tail_amp=1.4,
                 lift=.6 if a.startswith('tw') else (.3 if a == 'holst' else 0))
        if a.startswith('tw'):
            p['breath'] = 1; p['hdy'] = -1
        if a in ('tip', 'tip2'):
            p['arm_front'] = True
            p['hatdy'] = -3 if a == 'tip' else -2
            p['tilt'] = .08
            p['earL'] = 'perk'
        out.append(p)
    return out


def anim_foul():
    out = []
    seq = ['b_swing', 'b_aim', 'b_aim', 'droop', 'look', 'look']
    for i, a in enumerate(seq):
        p = dict(arm=a, lift=.8 if a == 'b_swing' else 1, jam=i >= 1, fringe=i / 6, tail=i / 6,
                 earR='perk' if i < 2 else ('back' if i == 2 else 'flat'),
                 earL='perk' if i < 2 else ('back' if i == 2 else 'flat'))
        if i >= 3:
            p['hatdy'] = 1; p['hdy'] = 1
        out.append(p)
    # puffs anchored to the cylinder of the jammed gun
    puffs = {2: (113, 40, 3.0), 3: (117, 47, 4.5), 4: (114, 40, 5.0), 5: (112, 33, 4.0)}
    for i, (x, y, r) in puffs.items():
        out[i]['fx'] = [puff(x, y, r)]
    return out


ANIMS = [
    # name, builder, fps, loop, extra
    ('idle', anim_idle, 8, True),
    ('ready', anim_ready, 8, True),
    ('draw_fire', anim_draw_fire, 20, False),
    ('aim', anim_aim, 4, True),
    ('hit', anim_hit, 14, False),
    ('down', anim_down, 10, False),
    ('win', anim_win, 10, False),
    ('foul', anim_foul, 12, False),
]


# ------------------------------------------------------------------------------- portraits
def portrait(hurt=False):
    S = 28
    f = Frame(S, S)
    # poncho collar
    pc = Part('pon', 'pon', S, S)
    pc.poly([(2, 27), (5, 22), (10, 21), (18, 21), (23, 22), (26, 27)], 'poncho', 2)
    pc.fill(pc.mask & edge(pc.mask, 0, -1), lum=3)
    import numpy as np
    yy, xx = np.mgrid[0:S, 0:S]
    pc.fill(pc.mask & (yy == 25), 'ponC', 3)
    pc.fill(pc.mask & (yy == 25) & ((xx % 4) == 1), 'ponB', 2)
    pc.fill(pc.mask & (yy >= 26), lum=1)
    f.add(pc)
    # head
    hd = Part('head', 'head', S, S)
    hd.ellipse(14, 16, 9.2, 7.2, 'fur', 2)
    hd.poly([(5, 17), (2, 20), (5, 20), (4, 22), (9, 22)], 'fur', 2)     # cheek ruffs
    hd.poly([(23, 17), (26, 20), (23, 20), (24, 22), (19, 22)], 'fur', 2)
    hd.poly([(9, 21), (19, 21), (17, 24), (11, 24)], 'fur', 2)
    m = hd.mask
    hd.addlum(m & (yy <= 12), -1)
    hd.addlum(m & (xx >= 19) & (yy > 12), 1)
    hd.addlum(m & (xx <= 7), -1)
    # forehead stripes + cheek stripes
    for x0 in (11, 14, 17):
        hd.line([(x0, 12), (x0, 13 if x0 != 14 else 14)], 'furS', 1)
    hd.line([(5, 18), (7, 18)], 'furS', 1); hd.line([(21, 18), (23, 18)], 'furS', 2)
    # muzzle
    hd.ellipse(14, 20, 3.6, 2.4, 'furL', 2)
    hd.px([(15, 19), (16, 19)], 'furL', 3)
    hd.px([(13, 18), (14, 18)], 'pink', 2)
    if hurt:
        hd.px([(12, 21), (13, 22), (14, 21), (15, 22), (16, 21)], 'ink2', 0)   # wavy grimace
        for (x, y) in [(8, 14), (9, 15), (10, 16), (9, 17), (8, 18)]:
            pass
        hd.line([(8, 14), (11, 16), (8, 17)], 'ink', 0)       # >
        hd.line([(20, 14), (17, 16), (20, 17)], 'ink', 0)     # <
    else:
        hd.px([(13, 20), (14, 20)], 'ink2', 0)
        for ex in (8, 17):
            hd.px([(ex, 15), (ex + 1, 15), (ex + 2, 15)], 'ink', 0)
            hd.px([(ex, 16), (ex + 2, 16)], 'eye', 3)
            hd.px([(ex + 1, 16)], 'ink', 0)
            hd.px([(ex, 17), (ex + 1, 17), (ex + 2, 17)], 'eye', 2)
            hd.px([(ex + 2, 16)], 'eye', 5)
    f.add(hd)
    wk = Part('wk', 'wk', S, S, outline=False)
    for a, b in (((4, 19), (1, 18)), ((4, 21), (1, 22)), ((24, 19), (27, 18)), ((24, 21), (27, 22))):
        wk.line([a, b], 'whisk', 2)
    wk.noline[:] = True
    f.add(wk)
    # hat (tilted when hurt)
    tilt = -0.07 if hurt else 0.0
    c, s = math.cos(tilt), math.sin(tilt)
    tr = lambda x, y: (14 + (x - 14) * c - (y - 9) * s, 9 + (x - 14) * s + (y - 9) * c - (1 if hurt else 0))
    ht = Part('hat', 'hat', S, S)
    br = ht.poly([tr(14 + 13.5 * math.cos(t), 10.5 + 2.6 * math.sin(t) - 2.2 * abs(math.cos(t)) ** 4)
                  for t in np.linspace(0, 2 * math.pi, 48)], 'hat', 3)
    ht.addlum(br & (xx > 14), 1)
    ht.addlum(edge(br, 0, 1), -2)
    cr = ht.poly([tr(*q) for q in [(7, 10), (7, 6), (9, 3), (13, 2), (14, 3), (15, 2), (19, 3), (21, 6), (21, 10)]], 'hat', 2)
    ht.addlum(cr & (xx >= 17), 1)
    ht.addlum(cr & (xx <= 9), -1)
    ht.addlum(edge(cr, 0, -1), 2)
    ht.fill(cr & ht.mask & (np.abs(yy - tr(14, 8)[1]) < 1.0), 'band', 2)
    f.add(ht)
    for side in (-1, 1):
        e = Part('ear%d' % side, 'ear%d' % side, S, S)
        bx_ = 14 + side * 5
        if True:
            pts = [tr(bx_ - 2.5, 4), tr(bx_ + side * 1.5, -1), tr(bx_ + 2.5, 4)]
        em = e.poly(pts, 'fur', 2 if side > 0 else 1)
        e.fill(em & (yy == int(round(tr(0, 2)[1]))), 'furS')
        e.addlum(edge(em, 0, -1), 1)
        f.add(e)
    if hurt:
        sw = Part('sweat', 'sweat', S, S)
        sw.px([(24, 9), (24, 10), (25, 10)], 'sleeve', 5)
        f.add(sw)
    return f.render(open_edges=('bottom',))


# ------------------------------------------------------------------------------- main
def generate(out=OUT, keep_frames=None):
    os.makedirs(out, exist_ok=True)
    meta = {'cell': list(CELL), 'pivot': [0, 216], 'hitPoint': [84, 96], 'anims': {},
            'portrait': 'hero_portrait', 'portraitHurt': 'hero_portrait_hurt'}
    allframes = {}
    for name, fn, fps, loop in ANIMS:
        poses = fn()
        frames, muz = [], []
        for p in poses:
            img, m = build(p)
            check_palette(img, name=f'hero_{name}')
            frames.append(img); muz.append(mz(m))
        save_png(strip(frames), os.path.join(out, f'hero_{name}.png'))
        a = {'file': f'hero_{name}', 'frames': len(frames), 'fps': fps, 'loop': loop}
        if name == 'draw_fire':
            a['events'] = {'fire': 2}
        if name in ('draw_fire', 'aim', 'foul'):
            a['muzzle'] = muz
        meta['anims'][name] = a
        allframes[name] = (frames, muz, fps, loop)
    for hurt, fn in ((False, 'hero_portrait'), (True, 'hero_portrait_hurt')):
        img = portrait(hurt)
        check_palette(img, name=fn)
        save_png(img, os.path.join(out, fn + '.png'))
    with open(os.path.join(out, 'hero.json'), 'w') as fh:
        json.dump(meta, fh, indent=1)
    return meta, allframes


if __name__ == '__main__':
    meta, _ = generate()
    print(json.dumps({k: (v['frames'], v.get('muzzle')) for k, v in meta['anims'].items()}))
