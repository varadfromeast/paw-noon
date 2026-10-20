"""font_code: the big room-code lettering for friend duels (M5). Jersey 15 (SIL OFL 1.1) rasterised at its
native pixel size (27 px -> 15 px caps, the same height as font_timer's digits), no anti-aliasing.
Glyphs: A-Z, 0-9, ':' and '-' only (room codes, the waiting countdown).

Output: Resources/Art/Fonts/font_code.png + font_code.json (same schema as the other fonts) and the licence
copy in Fonts/licenses/. Run from anywhere:  python3 Tools/ArtGen/ui/gen_font_code.py
The OFL fonts live in <workspace>/ref/gfonts/ofl (PN_OFL_DIR overrides).
"""
import os
import shutil
import numpy as np
from PIL import Image, ImageDraw, ImageFont
import gen_fonts
from uikit import FONT_DIR, ROOT

CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789:-"
CANDIDATES = [os.environ.get('PN_OFL_DIR', ''), os.path.join(ROOT, 'ref', 'gfonts', 'ofl'),
              os.path.join(ROOT, 'pawnoon', 'ref', 'gfonts', 'ofl')]


def ofl_dir():
    for d in CANDIDATES:
        if d and os.path.exists(os.path.join(d, 'jersey15', 'Jersey15-Regular.ttf')):
            return d
    raise SystemExit('Jersey15-Regular.ttf not found (set PN_OFL_DIR to ref/gfonts/ofl)')


def rasterize(path, size, chars, baseline_y=30, max_aa=2):
    """Like gen_fonts.rasterize_ttf, but a glyph may carry up to `max_aa` anti-aliased edge pixels (Jersey 15's
    Q tail has one at 27 px); they are thresholded at 50 % so the result is still a clean 1-bit glyph."""
    f = ImageFont.truetype(path, size)
    out = {}
    for ch in chars:
        aa = Image.new('L', (60, 60), 0)
        ImageDraw.Draw(aa).text((10, baseline_y), ch, font=f, fill=255, anchor='ls')
        a = np.asarray(aa)
        n_aa = int(((a > 0) & (a < 255)).sum())
        if n_aa > max_aa:
            raise RuntimeError(f'{os.path.basename(path)} glyph {ch!r}: {n_aa} anti-aliased pixels at {size}px')
        m = a >= 128
        adv = f.getlength(ch)
        assert abs(adv - round(adv)) < 1e-6, (ch, adv)
        if m.any():
            ys, xs = np.nonzero(m)
            x0, y0, x1, y1 = xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
            out[ch] = dict(bmp=m[y0:y1, x0:x1], ox=int(x0 - 10), top=int(y0 - baseline_y), adv=int(round(adv)))
        else:
            out[ch] = dict(bmp=np.zeros((0, 0), bool), ox=0, top=0, adv=int(round(adv)))
    return out


def main():
    ofl = ofl_dir()
    raw = rasterize(os.path.join(ofl, 'jersey15', 'Jersey15-Regular.ttf'), 27, CHARS + ' ')
    asc = -min(g['top'] for g in raw.values() if g['bmp'].size)
    desc = max(g['top'] + g['bmp'].shape[0] for g in raw.values() if g['bmp'].size)
    glyphs = {ch: dict(bmp=g['bmp'], ox=g['ox'], oy=g['top'] + asc, adv=g['adv']) for ch, g in raw.items()}
    cap = -raw['H']['top']
    meta = gen_fonts.pack_and_write('font_code', glyphs, line_height=asc + desc + 1, baseline=asc, spacing=1,
                                    extra={'capHeight': cap, 'source': 'Jersey 15 (OFL) @27px, no AA'})
    lic = os.path.join(FONT_DIR, 'licenses')
    os.makedirs(lic, exist_ok=True)
    shutil.copy(os.path.join(ofl, 'jersey15', 'OFL.txt'), os.path.join(lic, 'Jersey15-OFL.txt'))
    readme = os.path.join(lic, 'README.txt')
    line = "font_code   : glyphs rasterised from Jersey 15 (SIL Open Font License 1.1, see Jersey15-OFL.txt).\n"
    text = open(readme).read() if os.path.exists(readme) else ''
    if line not in text:
        with open(readme, 'a') as fp:
            fp.write(line)
    print('font_code', meta['lineHeight'], meta['baseline'], cap, len(meta['glyphs']))


if __name__ == '__main__':
    main()
