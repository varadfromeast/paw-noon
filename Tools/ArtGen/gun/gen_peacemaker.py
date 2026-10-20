#!/usr/bin/env python3
"""Peacemaker revolver art (SPEC_V2 §3 A4, §6; M7). Pixel art, palette only, whole pixels, magenta-free RGBA.

Writes into Assets/PawNoon/Resources/Art:
  UI/holster_peacemaker.png   2-frame strip, cell 64x40, pivot [32,40]: same footprint and layout as
                              holster_zone (frame 0 idle, frame 1 grip glowing) so DuelHud swaps it 1:1.
                              Black tooled leather, silver star concho, ivory grip, blued frame, long
                              Colt hammer; the glow is blue-white instead of the standard orange.
  UI/peacemaker_card.png      64x30 side view of the revolver for the shop card (muzzle right).
  UI/ico_shop.png             12x12 shop icon (a coin with a star) for the title's SHOP button.
  FX/muzzle_side_peacemaker.png  3 frames, cell 72x72, pivot [9,36]: the hero's flash, bigger than
                              muzzle_side (56) and blue-white (sigBlue / sky / cream core).
and registers them in UI/ui.json and FX/fx.json the way the existing sprites are.

    python3 Tools/ArtGen/gun/gen_peacemaker.py          (writes the PNGs, json entries, preview)
"""
import json, math, os, sys
import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'common'))
from pixkit import PA, check_palette, strip, upscale  # noqa: E402

ART = os.path.normpath(os.path.join(HERE, '..', '..', '..', 'Assets', 'PawNoon', 'Resources', 'Art'))
UI, FX = os.path.join(ART, 'UI'), os.path.join(ART, 'FX')
T = (0, 0, 0, 0)

# one letter per palette colour, for the hand-placed parts
KEY = {
    '#': 'ink', 'i': 'ink2', 'K': 'char3', 'k': 'char2', 'j': 'char1', 'b': 'sky1', 'B': 'sky2', 'w': 'sky4',
    'G': 'sigBlue', 'X': 'steel0', 'S': 'steel1', 's': 'steel2', 't': 'steel3', 'V': 'cream0', 'v': 'cream1',
    'u': 'cream2', 'q': 'paintCreamD', 'y': 'brass1', 'Y': 'brass0', 'n': 'brass2', 'W': 'white',
    'L': 'leath2', 'D': 'leath3', 'l': 'leath1', 'h': 'leath0', 'c': 'steel1',
}


def grid_to_img(rows):
    h, w = len(rows), max(len(r) for r in rows)
    im = Image.new('RGBA', (w, h), T)
    for y, r in enumerate(rows):
        for x, ch in enumerate(r):
            if ch != '.':
                im.putpixel((x, y), PA[KEY[ch]])
    return im


# ------------------------------------------------------------------------------------------------ card gun
# Colt Single Action Army side view, muzzle right, light from the top right. Blued steel reads as
# char3/char2 with a thin sky1/sky2 sheen and sigBlue glints; brass trigger guard and backstrap; ivory grip.
# Built from material masks (shapes), shaded per material, then a 1 px selective ink outline.

class Canvas:
    def __init__(self, w, h):
        self.w, self.h = w, h
        self.m = np.full((h, w), '', dtype=object)     # material per pixel
        self.c = np.full((h, w), '', dtype=object)     # colour key per pixel (after shading)

    def rect(self, x0, y0, x1, y1, mat):
        for y in range(max(0, y0), min(self.h, y1 + 1)):
            for x in range(max(0, x0), min(self.w, x1 + 1)):
                self.m[y, x] = mat

    def poly(self, pts, mat):
        im = Image.new('L', (self.w, self.h), 0)
        ImageDraw.Draw(im).polygon(pts, fill=1)
        a = np.asarray(im)
        self.m[a > 0] = mat

    def px(self, x, y, mat):
        if 0 <= x < self.w and 0 <= y < self.h:
            self.m[y, x] = mat

    def clear(self, x, y):
        self.m[y, x] = ''

    def solid(self):
        return self.m != ''

    def to_img(self, outline='ink'):
        im = Image.new('RGBA', (self.w, self.h), T)
        sol = self.solid()
        for y in range(self.h):
            for x in range(self.w):
                if self.c[y, x]:
                    im.putpixel((x, y), PA[KEY[self.c[y, x]]])
        # 1 px outline outside the silhouette (4-neighbourhood) where nothing else is drawn
        for y in range(self.h):
            for x in range(self.w):
                if sol[y, x]:
                    continue
                n = [(x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)]
                if any(0 <= a < self.w and 0 <= b < self.h and sol[b, a] for a, b in n):
                    im.putpixel((x, y), PA[outline])
        return im


def shade(cv, mat, rows):
    """rows: list of (y_from_top_of_material_column, key) applied per column, top-down; last repeats."""
    for x in range(cv.w):
        ys = [y for y in range(cv.h) if cv.m[y, x] == mat]
        for i, y in enumerate(ys):
            k = rows[min(i, len(rows) - 1)]
            # bottom pixel of each column is the dark core
            if i == len(ys) - 1 and len(ys) > 2:
                k = rows[-1] if rows[-1] != rows[0] else k
            cv.c[y, x] = k


def card_gun():
    W, H = 80, 34
    cv = Canvas(W, H)
    # barrel + front sight + ejector housing
    cv.rect(35, 8, 76, 11, 'barrel')
    cv.rect(73, 6, 74, 7, 'barrel')
    cv.rect(35, 12, 55, 14, 'ejector')
    cv.rect(56, 13, 57, 13, 'rod')
    # cylinder (rounded), frame with top strap, recoil shield
    cv.rect(13, 4, 37, 5, 'frame')        # top strap over the cylinder
    cv.rect(12, 4, 22, 17, 'frame')       # frame body
    cv.rect(36, 6, 37, 14, 'frame')       # frame front (barrel shank)
    cv.rect(22, 16, 37, 16, 'frame')      # frame under the cylinder
    cv.rect(23, 6, 35, 15, 'cyl')         # the drum, seated in the frame window
    # hammer: spur up and back
    cv.poly([(12, 4), (16, 4), (11, 0), (8, 0), (8, 1)], 'hammer')
    cv.rect(13, 2, 15, 4, 'hammer')
    # brass trigger guard (loop) and backstrap
    cv.poly([(17, 17), (29, 17), (29, 20), (26, 24), (19, 24), (16, 21)], 'guard')
    cv.poly([(19, 18), (27, 18), (27, 20), (25, 22), (20, 22), (19, 20)], '')
    cv.rect(21, 18, 22, 20, 'trigger')
    cv.px(22, 21, 'trigger')
    # ivory grip, plum-shaped, sweeping down and back from the frame
    cv.poly([(12, 14), (18, 17), (16, 22), (13, 29), (10, 33), (4, 33), (1, 30), (2, 25), (6, 19), (9, 15)], 'grip')
    cv.poly([(10, 14), (12, 14), (7, 20), (4, 26), (2, 26), (5, 19), (8, 15)], 'strap')
    # --- shading
    for x in range(W):
        ys = [y for y in range(H) if cv.m[y, x] == 'barrel']
        for i, y in enumerate(ys):
            cv.c[y, x] = 'b' if i == 0 else ('k' if i < len(ys) - 1 else 'K')
        ys = [y for y in range(H) if cv.m[y, x] == 'ejector']
        for i, y in enumerate(ys):
            cv.c[y, x] = 'k' if i == 0 else 'K'
        ys = [y for y in range(H) if cv.m[y, x] == 'frame']
        for i, y in enumerate(ys):
            cv.c[y, x] = 'B' if i == 0 else 'K'
        ys = [y for y in range(H) if cv.m[y, x] == 'guard']
        for i, y in enumerate(ys):
            cv.c[y, x] = 'Y' if i == 0 else ('y' if i < len(ys) - 1 else 'n')
        ys = [y for y in range(H) if cv.m[y, x] == 'strap']
        for i, y in enumerate(ys):
            cv.c[y, x] = 'y' if i < len(ys) - 1 else 'n'
    for y in range(H):
        xs = [x for x in range(W) if cv.m[y, x] == 'cyl']
        for x in xs:
            # flutes: dark grooves every 3 px, a lit band on top
            rel = x - xs[0]
            if rel == 0 or x == xs[-1]:
                cv.c[y, x] = 'K'                       # drum ends read as seams against the frame
            elif y == 6:
                cv.c[y, x] = 'j'
            elif rel % 3 == 0 and 7 <= y <= 14:
                cv.c[y, x] = 'K'                       # flutes
            elif y == 15:
                cv.c[y, x] = 'K'
            else:
                cv.c[y, x] = 'j' if (rel % 3 == 1 and y <= 8) else 'k'
        xs = [x for x in range(W) if cv.m[y, x] == 'grip']
        for i, x in enumerate(xs):
            # ivory: lit edge at the right, cool shadow at the bottom-left
            r = len(xs) - 1 - i
            k = 'V' if r <= 1 else ('v' if r <= 3 else 'u')
            if y >= 29 and i <= 2:
                k = 'q'
            cv.c[y, x] = k
        for x in range(W):
            if cv.m[y, x] == 'hammer':
                cv.c[y, x] = ('S' if (x + y) % 2 == 0 else 's') if y <= 1 else ('S' if y == 2 else 's')
            if cv.m[y, x] == 'trigger':
                cv.c[y, x] = 's'
            if cv.m[y, x] == 'rod':
                cv.c[y, x] = 'S'
    # details: hammer checkering highlight, glints, screws, grip screw, muzzle crown
    for (x, y, k) in [(9, 0, 'X'), (10, 1, 'X'), (13, 2, 'S'), (14, 2, 'X'),
                      (27, 6, 'G'), (28, 6, 'G'), (44, 8, 'G'), (45, 8, 'G'), (46, 8, 'w'), (70, 8, 'G'),
                      (14, 7, 'y'), (19, 9, 'y'), (8, 24, 'y'),
                      (76, 9, 'K'), (76, 10, 'K'), (36, 8, 'j'), (36, 9, 'j')]:
        if cv.m[y, x]:
            cv.c[y, x] = k
    return cv.to_img()


# ------------------------------------------------------------------------------------------------ holster
# Derived from the standard holster_zone strip so the silhouette, the belt and the swipe target stay
# identical (DuelHud lays both out the same way); only materials change. The grip region (rows 0..14 around
# the gun) maps wood -> ivory, the steel backstrap -> brass, the frame -> blued steel; the leather maps one
# step darker (black tooled leather), the stitching goes silver and the brass star becomes a silver concho.
# The glow (frame 1) maps orange/yellow -> sigBlue/sky5 (blue-white).

def rgb(name):
    return PA[name][:3]


GRIP_MAP = {   # standard colour -> Peacemaker colour, inside the gun region
    'wood0': 'cream0', 'wood1': 'cream1', 'wood2': 'cream2', 'wood3': 'cream2', 'wood4': 'paintCreamD',
    'steel0': 'brass0', 'steel1': 'brass1', 'steel2': 'brass2', 'steel3': 'char3',
    'sun1': 'cream0', 'sun2': 'sky5', 'sigOrange': 'sigBlue',
}
HAMMER_MAP = {'steel0': 'steel0', 'steel1': 'steel1', 'steel2': 'steel2'}   # the hammer stays bright steel
LEATHER_MAP = {
    'leath0': 'leath1', 'leath1': 'leath2', 'leath2': 'leath3', 'leath3': 'ink2', 'cream2': 'steel1',
    'sun2': 'sky5', 'sigOrange': 'sigBlue',
}
STAR_MAP = {'brass0': 'white', 'brass1': 'steel1', 'brass2': 'steel2'}


def holster():
    src = Image.open(os.path.join(UI, 'holster_zone.png')).convert('RGBA')
    by_rgb = {v[:3]: k for k, v in PA.items()}
    out = Image.new('RGBA', src.size, T)
    for f in range(2):
        for y in range(40):
            for x in range(64):
                px = src.getpixel((f * 64 + x, y))
                if px[3] == 0:
                    continue
                name = by_rgb.get(px[:3])
                if name is None:
                    raise ValueError(f'holster_zone has an off-palette pixel at {x},{y}')
                gun = y <= 13 or (y == 14 and 24 <= x <= 40)
                hammer = gun and x <= 29 and y >= 7 and name.startswith('steel')
                star = 28 <= x <= 35 and 23 <= y <= 28 and name.startswith('brass')
                if hammer:
                    m = HAMMER_MAP
                elif star:
                    m = STAR_MAP
                elif gun:
                    m = GRIP_MAP
                else:
                    m = LEATHER_MAP
                if y == 14 and 28 <= x <= 35 and name == 'wood4':
                    name, m = 'blued', {'blued': 'char3'}     # the frame showing at the holster mouth
                out.putpixel((f * 64 + x, y), PA[m.get(name, name)])
        # blued sheen on the frame and a glint on the concho
        out.putpixel((f * 64 + 29, 14), PA['sky1'])
        out.putpixel((f * 64 + 30, 14), PA['sky1'])
        out.putpixel((f * 64 + 32, 25), PA['sigBlue'])
    return out


# ------------------------------------------------------------------------------------------------ shop icon

ICO = [
    "...####.....",
    "..#YYyy#....",
    ".#YyYYyn#...",
    "#YyYWYyyn#..",
    "#yYWWWYyn#..",
    "#yyYWYyyn#..",
    "#yyWyWyyn#..",
    ".#nyyyynn#..",
    "..#nnnnn#...",
    "...#####....",
    "............",
    "............",
]


def shop_icon():
    # a brass coin with a star, 10 px, centred in the 12x12 cell (the other icons leave the same margin)
    im = grid_to_img(ICO)
    out = Image.new('RGBA', (12, 12), T)
    out.alpha_composite(im, (1, 1))
    return out


# ------------------------------------------------------------------------------------------------ rupee glyph
# The bitmap fonts have no "₹". This is the sign in font_medium's hand (5x7, 2 px strokes), pre-coloured like
# BitmapText draws the shop price: sun1 fill, 1 px ink outline, ink2 shadow 2 px below. Cell 7x11, the fill's
# top-left at (1,1), so it sits exactly where a glyph drawn at the same pen position would.
RUPEE = [
    "#####",
    "..###",
    "#####",
    "###..",
    ".##..",
    "..##.",
    "...##",
]


def rupee_glyph():
    w, h = 7, 11
    fill = np.zeros((h, w), bool)
    for y, r in enumerate(RUPEE):
        for x, ch in enumerate(r):
            if ch == '#':
                fill[y + 1, x + 1] = True
    outline = np.zeros_like(fill)
    for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
        outline |= np.roll(np.roll(fill, dy, 0), dx, 1)
    outline &= ~fill
    body = fill | outline
    shadow = np.zeros_like(fill)
    for dx in (-1, 0, 1):
        shadow |= np.roll(np.roll(fill, 2, 0), dx, 1)
    shadow &= ~body
    im = Image.new('RGBA', (w, h), T)
    for y in range(h):
        for x in range(w):
            if fill[y, x]:
                im.putpixel((x, y), PA['sun1'])
            elif outline[y, x]:
                im.putpixel((x, y), PA['ink'])
            elif shadow[y, x]:
                im.putpixel((x, y), PA['ink2'])
    return im


# ------------------------------------------------------------------------------------------------ muzzle flash
# Same construction as fx_v3's muzzle_side (stacked stars, 1 px rim) but 72 px and blue-white: outer
# sigBlue, middle sky5, cream core, white heart; rim sky1 then sky0 as it dies. One extra spike layer.

def star(d, cx, cy, r_out, r_in, n, col, rot=0.0):
    pts = []
    for i in range(n * 2):
        r = r_out if i % 2 == 0 else r_in
        a = rot + math.pi * i / n
        pts.append((cx + math.cos(a) * r, cy + math.sin(a) * r))
    d.polygon(pts, fill=col)


def muzzle_peacemaker(cell=72, frames=3):
    out = []
    for i in range(frames):
        im = Image.new('RGBA', (cell, cell), T)
        d = ImageDraw.Draw(im)
        c = cell / 2
        k = [1.0, 0.84, 0.58][i]
        R = cell * 0.46 * k
        ox = cell * 0.125
        star(d, ox + R * 0.58, c, R, R * 0.40, 9, PA['sigBlue'], rot=0.27 * i)
        star(d, ox + R * 0.52, c, R * 0.78, R * 0.30, 9, PA['sky5'], rot=0.27 * i + 0.17)
        star(d, ox + R * 0.48, c, R * 0.50, R * 0.20, 7, PA['cream0'], rot=0.1)
        d.ellipse((ox + R * 0.30 - R * 0.14, c - R * 0.14, ox + R * 0.30 + R * 0.14, c + R * 0.14), fill=PA['white'])
        d.ellipse((ox - 3, c - 4, ox + 5, c + 4), fill=PA['white'])
        a = np.asarray(im).copy()
        al = a[..., 3] > 0
        rim = al & ~(np.roll(al, 1, 0) & np.roll(al, -1, 0) & np.roll(al, 1, 1) & np.roll(al, -1, 1))
        a[rim] = PA['sky1'] if i < 2 else PA['sky0']
        out.append(Image.fromarray(a))
    return out


# ------------------------------------------------------------------------------------------------ write

def register(json_path, name, entry):
    d = json.load(open(json_path))
    e = d.get(name, {})
    e.update(entry)
    d[name] = e
    with open(json_path, 'w') as f:
        json.dump(d, f, indent=1)


# ------------------------------------------------------------------------------------------------ preview

def preview(items, path, k=6):
    pad = 4
    w = sum(i.width for i in items) + pad * (len(items) + 1)
    h = max(i.height for i in items) + pad * 2
    for bg in ((92, 158, 224, 255), (59, 36, 25, 255)):
        pass
    sheet = Image.new('RGBA', (w, h * 2), (92, 158, 224, 255))
    ImageDraw.Draw(sheet).rectangle((0, h, w, 2 * h), fill=(110, 68, 40, 255))
    x = pad
    for i in items:
        sheet.alpha_composite(i, (x, pad))
        sheet.alpha_composite(i, (x, h + pad))
        x += i.width + pad
    upscale(sheet, k).save(path)


if __name__ == '__main__':
    card = card_gun()
    hol = holster()
    ico = shop_icon()
    mz = muzzle_peacemaker()
    mzs = strip(mz)
    rup = rupee_glyph()
    check_palette(rup, name='glyph_rupee')
    rup.save(os.path.join(UI, 'glyph_rupee.png'))
    for img, n in ((card, 'peacemaker_card'), (hol, 'holster_peacemaker'), (ico, 'ico_shop'), (mzs, 'muzzle_side_peacemaker')):
        check_palette(img, name=n)
        a = np.asarray(img)
        assert not ((a[..., 0] == 255) & (a[..., 1] == 0) & (a[..., 2] == 255) & (a[..., 3] > 0)).any(), n + ': magenta'
    card.save(os.path.join(UI, 'peacemaker_card.png'))
    hol.save(os.path.join(UI, 'holster_peacemaker.png'))
    ico.save(os.path.join(UI, 'ico_shop.png'))
    mzs.save(os.path.join(FX, 'muzzle_side_peacemaker.png'))
    ui_json = os.path.join(UI, 'ui.json')
    register(ui_json, 'peacemaker_card', {'file': 'peacemaker_card', 'size': list(card.size), 'pivot': [card.width // 2, card.height // 2]})
    register(ui_json, 'holster_peacemaker', {'file': 'holster_peacemaker', 'size': [128, 40], 'pivot': [32, 40], 'cell': [64, 40],
                                             'frames': 2, 'fps': 0, 'loop': False,
                                             'note': 'Peacemaker loadout; same layout as holster_zone (frame 0 idle, frame 1 grip glowing)'})
    register(ui_json, 'glyph_rupee', {'file': 'glyph_rupee', 'size': [7, 11], 'pivot': [0, 0],
                                      'note': 'the shop price sign; fill at (1,1), drawn like font_medium sun1 + outline + shadow'})
    register(ui_json, 'ico_shop', {'file': 'ico_shop', 'size': [12, 12], 'pivot': [6, 6]})
    register(os.path.join(FX, 'fx.json'), 'muzzle_side_peacemaker', {'file': 'muzzle_side_peacemaker', 'size': [72 * 3, 72], 'pivot': [9, 36],
                                                                    'cell': [72, 72], 'frames': 3, 'fps': 24, 'loop': False})
    std = Image.open(os.path.join(UI, 'holster_zone.png')).convert('RGBA')
    std_mz = Image.open(os.path.join(FX, 'muzzle_side.png')).convert('RGBA')
    preview([card, ico, rup, std, hol], os.path.join(HERE, 'preview.png'))
    preview([std_mz, mzs], os.path.join(HERE, 'preview_muzzle.png'), k=3)
    print('ok')
