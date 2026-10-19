"""Contact sheet of all UI/FX sprites at 4x (work/ui/contact_<dir>_4x.png)."""
import os, sys, json
import numpy as np
from PIL import Image
from uikit import Canvas, upscale, WORK, UI_DIR, FX_DIR

def sheet(d, js, out, skip=()):
    meta = json.load(open(os.path.join(d, js)))
    cv = Canvas(300, 400); cv.paint(np.ones((400, 300), bool), 'sky3')
    x = y = 2; rowh = 0
    for k, e in meta.items():
        if k in skip: continue
        im = Image.open(os.path.join(d, e['file'] + '.png'))
        if x + im.width > 298: x = 2; y += rowh + 3; rowh = 0
        cv.blit(im, x, y); x += im.width + 3; rowh = max(rowh, im.height)
    img = cv.img().crop((0, 0, 300, min(400, y + rowh + 3)))
    upscale(img, 4).save(os.path.join(WORK, out))

if __name__ == '__main__':
    which = sys.argv[1]
    if which == 'ui':
        sheet(UI_DIR, 'ui.json', 'contact_ui_4x.png', skip=('cue_burst', 'logo', 'cue_draw', 'cue_1', 'cue_2', 'cue_3', 'banner_win', 'banner_lose'))
    else:
        sheet(FX_DIR, 'fx.json', 'contact_fx_4x.png')
