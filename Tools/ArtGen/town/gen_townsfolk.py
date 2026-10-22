"""Townsfolk (SPEC_V2 §8.3): two 16-px onlookers who peek over cover during a duel.

    python3 Tools/ArtGen/town/gen_townsfolk.py            # writes Art/FX/town_*.png + fx.json entries
    python3 Tools/ArtGen/town/gen_townsfolk.py --preview  # also writes previews next to this script

Sheets (all registered in Resources/Art/FX/fx.json, sizes / pivots / canvas anchors there):
  town_bonnet      3 frames 16x18: peek, ducked, blink   ginger tabby in a cream sunbonnet (left barrel)
  town_bowler      3 frames 16x18: peek, ducked, blink   grey tom, no hat (right balcony)
  town_bowler_hat  1 frame  12x6                         his bowler, a separate sprite so it can fly on LIGHTNING
  town_cover_*     plate pixels cut in front of each onlooker (noon + dawn plate), drawn over them so
                   they sit *behind* the barrel / railing of the painted plate.
Every onlooker pixel is from the palette (palette.json + palette_ext_*.json); covers are the plate's own pixels.
"""
import json
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'common'))
from pixkit import PA, check_palette, strip, upscale, save_png  # noqa: E402

ROOT = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
ART = os.path.join(ROOT, 'Assets', 'PawNoon', 'Resources', 'Art')
FX = os.path.join(ART, 'FX')
ENV = os.path.join(ART, 'Environment')

CELL = (16, 18)
PIVOT = (8, 17)          # bottom centre: the anchor sits behind the cover, below its top edge
HAT_CELL = (12, 6)
HAT_PIVOT = (6, 5)       # brim centre

# --------------------------------------------------------------------------------------------- art
# One char per pixel. Bonnet cat: ginger tabby, cream sunbonnet (two ear bumps), red ribbon, teal dress.
BONNET_KEY = {
    '.': None,
    'k': 'wood5',        # outline (warm, mid-distance: never pure ink)
    'B': 'paintCream', 'b': 'paintCreamD', 'c': 'cream2',
    'r': 'paintRed', 'R': 'paintRedD',
    'f': 'tabby1', 'F': 'tabby2', 'd': 'tabby3', 'D': 'tabby4', 'm': 'tabby0', 'w': 'cream0',
    'e': 'eyeGreen', 'i': 'ink2', 'n': 'red0',
    't': 'paintTeal', 'T': 'paintTealD',
}
BONNET_PEEK = [
    "...kk......kk...",
    "..kBBk....kBBk..",
    ".kBBBBkkkkBBBBk.",
    ".kBBBBBBBBBBBBk.",
    "kBBbbbbbbbbbbBBk",
    "kBbcFdffffdFcbBk",
    "kBbFffffffffFbBk",
    "kBbfeiffffieFbBk",
    "kBbfiiffffiifbBk",
    "kBbFfmwnnwmfFbBk",
    ".kbbFmwwwwmFbbk.",
    "..kbkdFmmFdkbk..",
    "...kkrrRRrrkk...",
    "..kTtkrRRrktTk..",
    ".kTttTkkkkTttTk.",
    ".kTtttttttttttk.",
    "kTTttttttttttTTk",
    "kTTttttttttttTTk",
]


def blink(rows, eye_rows, lid='d'):
    """Eyes shut: the two eye rows become fur with one dark lid line."""
    out = [list(r) for r in rows]
    top, bot = eye_rows
    for x, ch in enumerate(out[top]):
        if ch in 'ei':
            out[top][x] = out[top][x - 1] if out[top][x - 1] not in 'ei' else 'f'
    for x, ch in enumerate(out[bot]):
        if ch in 'ei':
            out[bot][x] = lid
    return [''.join(r) for r in out]


# Grey tom with a cream muzzle, white collar and a red bow tie; the bowler sits on top (separate sprite).
BOWLER_KEY = {
    '.': None,
    'k': 'char3',
    'g': 'char0', 'G': 'char1', 'H': 'char2',
    'm': 'cream1', 'w': 'cream0',
    'e': 'eyeAmber', 'i': 'ink2', 'n': 'red0', 'p': 'paintRedD',
    'r': 'paintRed', 'R': 'paintRedD',
    'v': 'leath1', 'V': 'leath2', 'W': 'cream2',
}
BOWLER_PEEK = [
    "................",
    "................",
    "................",
    "..kk........kk..",
    ".kGgk......kgGk.",
    ".kGHgkkkkkkgHGk.",
    ".kggggggggggggk.",
    "kGgggggggggggggk",
    "kGgeiggggggiegGk",
    "kGgiiggggggiigGk",
    "kHGgmmwnnwmmgGHk",
    ".kHGmwwwwwwmGHk.",
    "..kkHHmmmmHHkk..",
    "..kWWrRkkRrWWk..",
    ".kvVkWrRRrWkVvk.",
    ".kvvVWwwwwWVvvk.",
    "kVvvvVWwwWVvvvVk",
    "kVvvvvVwwVvvvvVk",
]
HAT_KEY = {'.': None, 'k': 'ink', 'h': 'ink2', 'H': 'shadowPurp', 'b': 'leath2', 'l': 'char2'}
HAT = [
    "...kkkkkk...",
    "..kHHhhhhk..",
    "..kHhhhhhk..",
    "..kbbbbbbk..",
    "kkhhhhhhhhkk",
    ".kkkkkkkkkk.",
]
# the hat's pivot (brim centre, bottom) sits this far from the body pivot (canvas px, y down) in the peek frame
HAT_ON_BODY = (0, -11)
DUCK_DROP = 8            # ducked frame = the figure pressed this many px lower (only the hat / bonnet top shows)

# --------------------------------------------------------------------------------------------- placement
# Canvas px (360x640, y down) on the v3 plate. The pivot (cell 8,17) sits at "at"; the cover is a patch of the
# plate whose top edge is the far rim of the barrel / the top rail of the balcony, drawn over the onlooker.
#   left : the planter barrel on the saloon porch (x 141..163, far rim y 316). Head shows above the rim.
#   right: the balcony of the right-hand building (rail top ~y 237). On phones the right-hand street barrels sit
#          behind Pawtruth (he spans x 239..325) or outside the 270-px core (x > 315), so the right onlooker
#          watches from upstairs instead: never over a duelist, never over the holster.
PLACES = {
    'left':  {'sheet': 'town_bonnet', 'at': (152, 321), 'cover': 'town_cover_left',
              'rect': (140, 316, 166, 342), 'top': lambda x: 316},
    'right': {'sheet': 'town_bowler', 'at': (309, 238), 'cover': 'town_cover_right',
              'rect': (300, 226, 322, 252), 'top': lambda x: int(round(243 - 0.85 * (x - 301)))},
}


def render(rows, key, size):
    w, h = size
    assert len(rows) == h, (len(rows), h)
    im = Image.new('RGBA', size, (0, 0, 0, 0))
    px = im.load()
    for y, row in enumerate(rows):
        assert len(row) == w, (y, row, len(row))
        for x, ch in enumerate(row):
            name = key[ch]
            if name is not None:
                px[x, y] = PA[name]
    return im


def ducked(rows, fixes):
    """Ducked: ears / bonnet bumps pressed flat (fixes: row -> new row), then the figure drops DUCK_DROP px."""
    out = list(rows)
    for y, fix in fixes.items():
        out[y] = fix
    blank = '.' * len(rows[0])
    out = [blank] * DUCK_DROP + out
    return out[:len(rows)]


def bonnet_frames():
    peek = BONNET_PEEK
    duck = ducked(peek, {0: "................", 1: "...kk......kk...", 2: ".kkBBkkkkkkBBkk."})
    return [render(peek, BONNET_KEY, CELL), render(duck, BONNET_KEY, CELL), render(blink(peek, (7, 8)), BONNET_KEY, CELL)]


def bowler_frames():
    peek = BOWLER_PEEK
    duck = ducked(peek, {3: "................", 4: "................", 5: ".kkkkkkkkkkkkkk."})
    duck = [r for r in duck]
    return [render(peek, BOWLER_KEY, CELL), render(duck, BOWLER_KEY, CELL), render(blink(peek, (8, 9), 'H'), BOWLER_KEY, CELL)]


def hat_frame():
    return render(HAT, HAT_KEY, HAT_CELL)


def cut_cover(plate, place):
    x0, y0, x1, y1 = place['rect']
    a = np.asarray(plate.convert('RGBA'))[y0:y1, x0:x1].copy()
    for cx in range(x1 - x0):
        top = place['top'](x0 + cx)
        a[:max(0, top - y0), cx, 3] = 0
    return Image.fromarray(a, 'RGBA')


def compose(plate, frame, place, lift=0, hat=None, hat_dy=0):
    """Preview: the plate, the onlooker at its anchor (+lift px up), the cover on top."""
    im = plate.copy().convert('RGBA')
    ax, ay = place['at']
    im.alpha_composite(frame, (ax - PIVOT[0], ay - PIVOT[1] - lift))
    if hat is not None:
        im.alpha_composite(hat, (ax + HAT_ON_BODY[0] - HAT_PIVOT[0], ay + HAT_ON_BODY[1] - HAT_PIVOT[1] - lift + hat_dy))
    cov = cut_cover(plate, place)
    im.alpha_composite(cov, place['rect'][:2])
    return im


def register(fx, name, entry):
    e = fx.get(name, {})
    e.update(entry)
    fx[name] = e


def build(preview=False):
    plate = Image.open(os.path.join(ENV, 'plate.png')).convert('RGBA')
    dawn = Image.open(os.path.join(ENV, 'plate_dawn.png')).convert('RGBA')
    bon, bow, hat = bonnet_frames(), bowler_frames(), hat_frame()
    for i, f in enumerate(bon + bow + [hat]):
        check_palette(f, name='town frame %d' % i)
    save_png(strip(bon), os.path.join(FX, 'town_bonnet.png'))
    save_png(strip(bow), os.path.join(FX, 'town_bowler.png'))
    save_png(hat, os.path.join(FX, 'town_bowler_hat.png'))
    covers = {}
    for side, pl in PLACES.items():
        noon, dusk = cut_cover(plate, pl), cut_cover(dawn, pl)
        covers[side] = (noon, dusk)
        save_png(strip([noon, dusk]), os.path.join(FX, pl['cover'] + '.png'))

    path = os.path.join(FX, 'fx.json')
    fx = json.load(open(path))
    note = 'townsfolk (SPEC_V2 §8.3, Tools/ArtGen/town/gen_townsfolk.py): '
    for side, sheet, frames in (('left', 'town_bonnet', bon), ('right', 'town_bowler', bow)):
        pl = PLACES[side]
        e = {'file': sheet, 'size': [CELL[0] * 3, CELL[1]], 'cell': list(CELL), 'frames': 3, 'fps': 0, 'loop': False,
             'pivot': list(PIVOT), 'at': list(pl['at']), 'duckDrop': DUCK_DROP, 'cover': pl['cover'],
             'note': note + 'frame 0 peek, 1 ducked, 2 blink; pivot sits on canvas "at"; ' + side + ' onlooker'}
        if sheet == 'town_bowler':
            e['hat'] = 'town_bowler_hat'
            e['hatOffset'] = list(HAT_ON_BODY)
        register(fx, sheet, e)
    register(fx, 'town_bowler_hat', {'file': 'town_bowler_hat', 'size': list(HAT_CELL), 'cell': list(HAT_CELL), 'frames': 1,
                                     'fps': 0, 'loop': False, 'pivot': list(HAT_PIVOT),
                                     'note': note + 'the bowler; pivot = brim centre; flies 4 px on LIGHTNING'})
    for side, pl in PLACES.items():
        x0, y0, x1, y1 = pl['rect']
        register(fx, pl['cover'], {'file': pl['cover'], 'size': [(x1 - x0) * 2, y1 - y0], 'cell': [x1 - x0, y1 - y0],
                                   'frames': 2, 'fps': 0, 'loop': False, 'pivot': [0, 0], 'at': [x0, y0],
                                   'note': note + 'plate pixels in front of the ' + side + ' onlooker; frame 0 noon plate, '
                                           '1 dawn plate (crossfaded with the street); top-left at canvas "at"'})
    with open(path, 'w') as f:
        json.dump(fx, f, indent=1)

    if preview:
        pv = os.path.join(HERE, 'preview')
        os.makedirs(pv, exist_ok=True)
        upscale(strip(bon + bow), 10).save(os.path.join(pv, 'frames_10x.png'))
        shots = []
        for f_b, f_w, lift, hdy in ((0, 0, 0, 0), (2, 2, 0, 0), (1, 1, 0, 0), (0, 0, 2, -4)):
            im = compose(plate, bon[f_b], PLACES['left'], lift)
            im = compose(im, bow[f_w], PLACES['right'], lift, hat, hdy + (DUCK_DROP if f_w == 1 else 0))
            shots.append(im)
        # in context, with both cats (ready frame 0) and a tight crop around each onlooker
        A = ART
        b = Image.open(os.path.join(A, 'Characters/Bandit/bandit_ready.png')).convert('RGBA').crop((0, 0, 117, 120))
        h = Image.open(os.path.join(A, 'Characters/Hero/hero_ready.png')).convert('RGBA').crop((0, 0, 322, 275))
        full = []
        for im in shots:
            im = im.copy()
            im.alpha_composite(b, (278 - 60, 437 - 119))
            im.alpha_composite(h, (133 - 130, 622 - 274))
            full.append(im)
        full[0].crop((45, 0, 315, 640)).save(os.path.join(pv, 'phone_1x.png'))
        upscale(full[0].crop((45, 160, 315, 480)), 3).save(os.path.join(pv, 'phone_3x.png'))
        row = Image.new('RGBA', (4 * 90 * 6, 2 * 60 * 6))
        for i, im in enumerate(full):
            row.paste(upscale(im.crop((122, 286, 212, 346)), 6), (i * 540, 0))
            row.paste(upscale(im.crop((262, 210, 352, 270)), 6), (i * 540, 360))
        row.save(os.path.join(pv, 'states_6x.png'))
        dz = compose(dawn, bon[0], PLACES['left'])
        dz = compose(dz, bow[0], PLACES['right'], 0, hat)
        upscale(dz.crop((45, 160, 315, 480)), 3).save(os.path.join(pv, 'dawn_3x.png'))


if __name__ == '__main__':
    build(preview='--preview' in sys.argv)
