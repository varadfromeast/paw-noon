"""Outcome-moment tags for the round card (Docs/FEEL.md "Outcome moments") -> Art/UI + ui.json entries.

  tag_streak  brass pennant (nail hole left, swallowtail right) behind the "x2" / "x3" streak text, 9-slice in x
  tag_best    SigYellow ribbon with folded sigOrange tails behind the "NEW BEST!" text, 9-slice in x

Procedural (no source image): a shape mask per part, a 1 px ink outline from the mask's own edge, row-ramped fills
and a 1 px ink2 drop shadow. House palette only, alpha 0/255 only (pixkit.check_palette). The text is drawn by the
engine (BitmapText, ink), so both sprites are blank and stretch horizontally: the centre columns are identical.
Re-runnable: python3 Tools/ArtGen/ui_v3/build_outcome_tags.py
"""
import json
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'common'))
import pixkit  # noqa: E402

OUT = os.path.normpath(os.path.join(HERE, '..', '..', '..', 'Assets', 'PawNoon', 'Resources', 'Art', 'UI'))
PAL = json.load(open(os.path.join(HERE, '..', 'palette.json')))


def c(name):
    h = PAL[name].lstrip('#')
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), 255)


def edge(mask, solid=None):
    """Mask pixels with a 4-neighbour outside `solid` (default: the mask itself) or on the image border."""
    p = np.pad(mask if solid is None else (solid | mask), 1, constant_values=False)
    inner = p[1:-1, 1:-1] & p[:-2, 1:-1] & p[2:, 1:-1] & p[1:-1, :-2] & p[1:-1, 2:]
    return mask & ~inner


def paint(img, mask, fill_rows, outline, solid=None):
    """fill_rows(x, y) -> colour for interior pixels; outline colour on the edge (seams against `solid` stay open)."""
    h, w = mask.shape
    e = edge(mask, solid)
    for y in range(h):
        for x in range(w):
            if not mask[y, x]:
                continue
            img[y, x] = outline if e[y, x] else fill_rows(x, y)


def shadow(img, union):
    """1 px ink2 drop shadow under the lowest pixel of each column (never inside a notch)."""
    h, w = union.shape
    for x in range(w):
        ys = np.where(union[:, x])[0]
        if len(ys) and ys[-1] + 1 < h:
            img[ys[-1] + 1, x] = c('ink2')


def to_image(img):
    return Image.fromarray(img, 'RGBA')


# ------------------------------------------------------------------ pennant (streak)
PEN_H = 13          # shape rows; +1 shadow row
PEN_TAIL = 6        # swallowtail columns
PEN_L, PEN_R = 8, 7  # 9-slice borders (nail hole inside the left one, the tail inside the right one)


def pennant(width=17):
    h = PEN_H + 1
    m = np.zeros((h, width), bool)
    m[0:PEN_H, :] = True
    m[0, 0] = m[PEN_H - 1, 0] = False                     # rounded left corners
    for x in range(width - PEN_TAIL, width):              # swallowtail: a V cut growing 1 px per column
        k = x - (width - PEN_TAIL)
        n = k - 1
        mid = PEN_H // 2
        if n >= 0:
            m[mid - n:mid + n + 1, x] = False
    img = np.zeros((h, width, 4), np.uint8)

    def fill(x, y):
        if y <= 1:
            return c('brass0')                              # top light
        if y >= PEN_H - 3:
            return c('brass2')                              # underside
        return c('brass1')
    paint(img, m, fill, c('ink'))
    # rivet (2x2 dome, lit from the top-left) inside the left border
    img[5, 3] = c('brass0')
    img[5, 4] = c('brass2')
    img[6, 3] = c('brass2')
    img[6, 4] = c('brass3')
    shadow(img, m)
    return to_image(img)


# ------------------------------------------------------------------ ribbon (new best)
RIB_BAND = 11       # band rows (outline + 9 + outline)
RIB_DROP = 4        # tails hang this far below the band
RIB_OUT = 6         # tail columns outside the band
RIB_FOLD = 3        # tail columns tucked under each band end (seen below it)
RIB_L = RIB_R = RIB_OUT + RIB_FOLD + 1   # 9-slice borders


def ribbon(width=RIB_L + RIB_R + 2):
    h = RIB_BAND + RIB_DROP + 1
    img = np.zeros((h, width, 4), np.uint8)
    band = np.zeros((h, width), bool)
    band[0:RIB_BAND, RIB_OUT:width - RIB_OUT] = True
    tails = np.zeros((h, width), bool)
    mid = RIB_DROP + RIB_BAND // 2
    for side in (0, 1):
        for k in range(RIB_OUT + RIB_FOLD):        # k = 0 at the outer end
            x = k if side == 0 else width - 1 - k
            tails[RIB_DROP:RIB_DROP + RIB_BAND, x] = True
            notch = 1 - k                          # V notch on the outer end: 3 rows, then 1 row deep
            if notch >= 0:
                tails[mid - notch:mid + notch + 1, x] = False
    tails &= ~band                                 # behind the band

    def tail_fill(x, y):
        under = RIB_OUT <= x < width - RIB_OUT
        if under or y >= RIB_DROP + RIB_BAND - 2:
            return c('brass2')                     # the fold tucked under the band / the tail's underside
        return c('sigOrange')
    paint(img, tails, tail_fill, c('ink'), band)   # the band's own outline is the seam

    def band_fill(x, y):
        if y == 1:
            return c('sun1')                       # top light
        if y >= RIB_BAND - 2:
            return c('brass1')                     # underside
        return c('sigYellow')
    paint(img, band, band_fill, c('ink'))
    shadow(img, band | tails)
    return to_image(img)


def main():
    ui_path = os.path.join(OUT, 'ui.json')
    ui = json.load(open(ui_path))
    pen = pennant()
    rib = ribbon()
    out = {
        # pivot top-left (kit.Panel / UiKit.SetRect); border l,t,r,b. The height never stretches.
        'tag_streak': (pen, [PEN_L, 4, PEN_R, 4]),
        'tag_best': (rib, [RIB_L, 4, RIB_R, 4]),
    }
    for name, (im, border) in out.items():
        pixkit.check_palette(im, name=name)
        im.save(os.path.join(OUT, name + '.png'))
        ui[name] = {'file': name, 'size': [im.width, im.height], 'pivot': [0, 0], 'border': border}
        print(name, im.size, 'border', border)
    with open(ui_path, 'w') as f:
        json.dump(ui, f, indent=1)
        f.write('\n')
    # preview: the blank tags, then stretched like the engine does (centre columns repeated) at 4x
    sheet = Image.new('RGBA', (120, 60), c('paintCream'))
    sheet.alpha_composite(pen, (4, 4))
    sheet.alpha_composite(stretch(pen, PEN_L, PEN_R, 33), (30, 4))
    sheet.alpha_composite(rib, (4, 26))
    sheet.alpha_composite(stretch(rib, RIB_L, RIB_R, 62), (40, 26))
    pixkit.upscale(sheet, 4).save(os.path.join(HERE, 'preview_outcome_tags.png'))


def stretch(im, l, r, w):
    """Horizontal 9-slice stretch (centre columns repeated), as SpriteRenderer Sliced draws it."""
    a = np.asarray(im)
    mid = a[:, l:a.shape[1] - r]
    reps = [mid[:, i % mid.shape[1]:i % mid.shape[1] + 1] for i in range(w - l - r)]
    return Image.fromarray(np.concatenate([a[:, :l]] + reps + [a[:, a.shape[1] - r:]], axis=1), 'RGBA')


if __name__ == '__main__':
    main()
