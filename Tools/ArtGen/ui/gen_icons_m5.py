"""M5 icons in the house icon style (12x12, cream0 fill, 1 px ink2 outline from an 8-neighbour dilation, centre
pivot like ico_gear / ico_home): ico_trophy (leaderboard button on the title).
The room-code SHARE button uses feel wave 2's ico_share (Resources/Art/UI), so it is not generated here.
Writes the PNGs into Resources/Art/UI and adds / updates their entries in ui.json (other entries untouched).
    python3 Tools/ArtGen/ui/gen_icons_m5.py
"""
import json
import os
import numpy as np
from PIL import Image
from uikit import P, UI_DIR, save_checked

ICONS = {
    'ico_trophy': [
        "............",
        ".##########.",
        ".#.######.#.",
        ".#.######.#.",
        "..########..",
        "...######...",
        "....####....",
        ".....##.....",
        ".....##.....",
        "....####....",
        "...######...",
        "............",
    ],
}


def icon(rows):
    fill = np.array([[c == '#' for c in r] for r in rows], bool)
    h, w = fill.shape
    pad = np.pad(fill, 1)
    ring = np.zeros_like(fill)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            ring |= pad[1 + dy:1 + dy + h, 1 + dx:1 + dx + w]
    ring &= ~fill
    img = np.zeros((h, w, 4), np.uint8)
    img[fill] = P['cream0'] + (255,)
    img[ring] = P['ink2'] + (255,)
    return Image.fromarray(img, 'RGBA')


def main():
    path = os.path.join(UI_DIR, 'ui.json')
    meta = json.load(open(path))
    for name, rows in ICONS.items():
        img = icon(rows)
        save_checked(img, os.path.join(UI_DIR, name + '.png'))
        meta[name] = {'file': name, 'size': [12, 12], 'pivot': [6, 6]}
        print(name, img.size)
    with open(path, 'w') as fp:
        json.dump(meta, fp, indent=1)   # keep the existing order (new names are appended)


if __name__ == '__main__':
    main()
