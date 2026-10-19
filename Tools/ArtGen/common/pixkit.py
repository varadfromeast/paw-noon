"""Shared helpers for Paw Noon art generators. Import with:
    import sys; sys.path.insert(0, '<project>/Tools/ArtGen/common'); from pixkit import *
"""
import json, os
from PIL import Image
import numpy as np

_HERE = os.path.dirname(os.path.abspath(__file__))
PALETTE_HEX = json.load(open(os.path.join(_HERE, '..', 'palette.json')))
import glob as _glob
for _f in sorted(_glob.glob(os.path.join(_HERE, '..', 'palette_ext_*.json'))):
    for _k, _v in json.load(open(_f)).items():
        PALETTE_HEX.setdefault(_k, _v.upper())

def hex2rgb(h):
    h = h.lstrip('#'); return tuple(int(h[i:i+2], 16) for i in (0, 2, 4))

P = {k: hex2rgb(v) for k, v in PALETTE_HEX.items()}          # name -> (r,g,b)
PA = {k: v + (255,) for k, v in P.items()}                     # name -> (r,g,b,255)
PALETTE_SET = set(P.values())

def check_palette(img, allow_partial_alpha=False, name=''):
    """Raise if img uses colors outside the palette (fully transparent pixels ignored)."""
    a = np.asarray(img.convert('RGBA'))
    alpha = a[..., 3]
    if not allow_partial_alpha:
        bad_a = np.unique(alpha[(alpha != 0) & (alpha != 255)])
        if len(bad_a):
            raise ValueError(f'{name}: partial alpha values {bad_a[:8]}')
    vis = a[alpha > 0][:, :3]
    cols = {tuple(c) for c in np.unique(vis.reshape(-1, 3), axis=0)}
    stray = cols - PALETTE_SET
    if stray:
        raise ValueError(f'{name}: {len(stray)} off-palette colors, e.g. {list(stray)[:6]}')
    return True

def snap_to_palette(img):
    """Map every visible pixel to the nearest palette color (use sparingly, e.g. after compositing)."""
    a = np.asarray(img.convert('RGBA')).copy()
    pal = np.array(list(PALETTE_SET), dtype=np.int32)
    rgb = a[..., :3].reshape(-1, 3).astype(np.int32)
    d = ((rgb[:, None, :] - pal[None, :, :]) ** 2).sum(-1)
    a[..., :3] = pal[d.argmin(1)].reshape(a.shape[0], a.shape[1], 3)
    return Image.fromarray(a.astype(np.uint8), 'RGBA')

def upscale(img, k=4):
    return img.resize((img.width * k, img.height * k), Image.NEAREST)

def strip(frames):
    """Join equal-size RGBA frames into one horizontal strip."""
    w, h = frames[0].size
    out = Image.new('RGBA', (w * len(frames), h), (0, 0, 0, 0))
    for i, f in enumerate(frames):
        out.paste(f, (i * w, 0))
    return out

def save_png(img, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.convert('RGBA').save(path, optimize=True)
