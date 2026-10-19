"""Preview helpers for the hero (writes only to work/hero/)."""
import os, sys
import numpy as np
from PIL import Image, ImageDraw

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _HERE)
from herokit import P, upscale  # noqa: E402

ROOT = os.path.abspath(os.path.join(_HERE, '..', '..', '..'))
WORK = os.path.join(os.path.dirname(ROOT), 'work', 'hero')
SCENE = os.path.join(ROOT, 'Assets', 'PawNoon', 'Resources', 'Art', 'Environment', 'preview_scene.png')
BANDIT = os.path.join(ROOT, 'Assets', 'PawNoon', 'Resources', 'Art', 'Characters', 'Bandit')


def placeholder_scene():
    im = Image.new('RGBA', (288, 512), P['sky3'] + (255,))
    d = ImageDraw.Draw(im)
    for y in range(0, 262):
        c = P['sky1'] if y < 120 else P['sky2'] if y < 200 else P['sky4']
        d.line([(0, y), (287, y)], fill=c + (255,))
    d.polygon([(0, 262), (288, 262), (288, 512), (0, 512)], fill=P['sand1'] + (255,))
    d.polygon([(110, 262), (190, 262), (288, 512), (0, 512)], fill=P['sand0'] + (255,))
    d.polygon([(0, 110), (100, 220), (100, 300), (0, 400)], fill=P['wood2'] + (255,))
    d.polygon([(288, 110), (200, 220), (200, 300), (288, 400)], fill=P['wood3'] + (255,))
    d.ellipse([209, 145, 219, 155], fill=P['sun0'] + (255,))
    return im


def bandit_stub():
    """Use the bandit artist's idle frame 0 if present, else a dark silhouette for scale."""
    f = os.path.join(BANDIT, 'bandit_ready.png')
    if os.path.exists(f):
        s = Image.open(f).convert('RGBA')
        return s.crop((0, 0, 64, 64)), (32, 61)
    im = Image.new('RGBA', (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    c = P['char3'] + (255,)
    d.ellipse([24, 14, 40, 28], fill=c)
    d.polygon([(18, 16), (46, 16), (42, 12), (22, 12)], fill=P['ink'] + (255,))
    d.polygon([(22, 28), (42, 28), (44, 50), (20, 50)], fill=c)
    d.polygon([(23, 50), (30, 50), (30, 61), (23, 61)], fill=c)
    d.polygon([(34, 50), (41, 50), (41, 61), (34, 61)], fill=c)
    return im, (32, 61)


def scene_base():
    if os.path.exists(SCENE):
        return Image.open(SCENE).convert('RGBA'), True
    return placeholder_scene(), False


def composite(frame, extra=None):
    sc, real = scene_base()
    sc = sc.copy()
    if not real or not os.path.exists(os.path.join(BANDIT, 'bandit_ready.png')):
        b, piv = bandit_stub()
        sc.alpha_composite(b, (152 - piv[0], 300 - piv[1]))
    sc.alpha_composite(frame, (24, 512 - 216))
    if extra:
        for img, (x, y) in extra:
            sc.alpha_composite(img, (x, y))
    return sc


def iphone_crop(img):
    return img.crop((36, 22, 252, 490))


def ipad_crop(img):
    return img.crop((0, 64, 288, 448))


def contact(frames, k=3, bg=(70, 60, 80, 255), cols=None):
    w, h = frames[0].size
    cols = cols or len(frames)
    rows = (len(frames) + cols - 1) // cols
    sheet = Image.new('RGBA', (cols * (w + 2) + 2, rows * (h + 2) + 2), bg)
    for i, f in enumerate(frames):
        sheet.alpha_composite(f, (2 + (i % cols) * (w + 2), 2 + (i // cols) * (h + 2)))
    return upscale(sheet, k)


def gif(frames, path, fps, k=3, bg=None, loop=True):
    out = []
    for f in frames:
        if bg is not None:
            b = bg.copy(); b.alpha_composite(f) if b.size == f.size else None
            f = b
        out.append(upscale(f.convert('RGBA'), k).convert('RGB'))
    out[0].save(path, save_all=True, append_images=out[1:], duration=int(1000 / fps),
                loop=0 if loop else 1, disposal=2)
