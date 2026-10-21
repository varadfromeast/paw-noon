"""Holster + swipe-hint art from the painted sheet src_holster.png (SPEC_V2 / feel: "YANK IT UP!").

Sheet layout (magenta key): row 1 = holster idle / grip glowing / drawn (empty, dust);
row 2 = paw yanking the gun up, 4 frames; bottom = the hanging YANK IT UP! sign.

Outputs (Resources/Art/UI, registered in ui.json):
  holster_zone.png   3 frames 64x56, pivot bottom-centre: idle, glow, drawn.  Belt 60 px wide.
The painted paw-yank demo (holster_demo) and the YANK IT UP! sign (hint_sign) were retired on 20 Oct 2026
(the SwipeCue thumb + plain wood sign replaced them; cleanup pass 21 Oct removed the PNGs). They are no longer
written; an old ui.json entry for either is dropped.
Run: python3 build_holster.py
"""
import json, os, sys
import numpy as np
from PIL import Image
from scipy import ndimage
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'v3'))
from build_v3 import key_portrait, resample, ART  # noqa: E402

UI = os.path.join(ART, 'UI')
BELT_PX = 60                      # belt width on the 270 grid
CELL_W = 64

def elements(a, m):
    lab, n = ndimage.label(ndimage.binary_dilation(m, iterations=6))
    objs = ndimage.find_objects(lab)
    boxes = [(s[1].start, s[0].start, s[1].stop, s[0].stop) for s in objs if m[s].sum() > 2000]
    rows = sorted(boxes, key=lambda b: (b[1] // 300, b[0]))
    return rows[0:3], rows[3:7], rows[7]

def belt_rows(mm):
    widths = np.array([(np.where(r)[0].max() - np.where(r)[0].min() + 1) if r.any() else 0 for r in mm])
    i = int(np.argmax(widths))
    rows = np.where(widths > 0.85 * widths[i])[0]
    # the glow frame widens the whole thing: keep the contiguous band around the widest row
    lo = i
    while lo - 1 >= 0 and widths[lo - 1] > 0.85 * widths[i]: lo -= 1
    hi = i
    while hi + 1 < len(widths) and widths[hi + 1] > 0.85 * widths[i]: hi += 1
    xs = np.where(mm[i])[0]
    return lo, hi, int(xs.min()), int(widths[i])

def cut(a, m, box, scale_from, erase_below_belt=False):
    x0, y0, x1, y1 = box
    c = a[y0:y1, x0:x1]; mm = m[y0:y1, x0:x1].copy()
    lo, hi, bx, bw = belt_rows(mm)
    if erase_below_belt:
        mm[hi + 1:, :] = False
    # drop crumbs that leaked in from a neighbouring element (dust, ropes): anything under 150 source px
    lab, n = ndimage.label(mm)
    sizes = ndimage.sum(mm, lab, range(1, n + 1))
    mm = mm & np.isin(lab, [i + 1 for i, sz in enumerate(sizes) if sz >= 150])
    s = BELT_PX / scale_from
    im = resample(c, mm.astype(np.float32), s)
    # belt top-left in the resampled frame
    ys, xs = np.where(np.asarray(im)[..., 3] > 0)
    belt_top = int(round(lo * s)); belt_left = int(round(bx * s))
    return im, belt_top, belt_left

def compose(frames, cell_w, cell_h, belt_top_cell):
    """frames: list of (im, belt_top, belt_left). The belt is centred in the cell with its top at belt_top_cell."""
    out = Image.new('RGBA', (cell_w * len(frames), cell_h), (0, 0, 0, 0))
    belt_left_cell = (cell_w - BELT_PX) // 2
    for i, (im, bt, bl) in enumerate(frames):
        dx = belt_left_cell - bl
        dy = belt_top_cell - bt
        if dx < 0 or dy < 0 or dx + im.width > cell_w or dy + im.height > cell_h:
            raise SystemExit(f'frame {i} does not fit the {cell_w}x{cell_h} cell (dx={dx}, dy={dy}, {im.size})')
        out.alpha_composite(im, (i * cell_w + dx, dy))
    return out

def main():
    a = np.asarray(Image.open(os.path.join(HERE, 'src_holster.png')).convert('RGB'))
    m = key_portrait(a)
    hol, _paws, _sign = elements(a, m)
    # row-1 belt width (idle frame) sets the scale
    w1 = belt_rows(m[hol[0][1]:hol[0][3], hol[0][0]:hol[0][2]])[3]
    hf = [cut(a, m, b, w1) for b in hol]
    # holster cell: belt top sits so the longest holster fits below it
    below = max(im.height - bt for im, bt, bl in hf); above = max(bt for im, bt, bl in hf)
    cell_h = above + below
    holster = compose(hf, CELL_W, cell_h, above)
    holster.save(os.path.join(UI, 'holster_zone.png'))

    uij = os.path.join(UI, 'ui.json'); ui = json.load(open(uij))
    ui['holster_zone'] = {'file': 'holster_zone', 'size': [holster.width, holster.height], 'pivot': [CELL_W // 2, cell_h],
                          'cell': [CELL_W, cell_h], 'frames': 3, 'fps': 0, 'loop': False,
                          'note': 'frame 0 idle, 1 grip glowing (thumb resting), 2 drawn (empty, dust); select by state'}
    ui.pop('holster_demo', None)
    ui.pop('hint_sign', None)
    json.dump(ui, open(uij, 'w'), indent=1); open(uij, 'a').write('\n')
    print('holster', holster.size, 'belt top', above)

if __name__ == '__main__':
    main()
