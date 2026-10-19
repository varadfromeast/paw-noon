"""Generate the bitmap fonts: font_small (Tiny5, OFL, rasterised at its native 8 px size, no AA),
font_medium (hand-drawn, 7 px caps) and font_timer (hand-drawn 15 px monospace digits).

Output: Resources/Art/Fonts/<font>.png (white glyphs on transparent) + <font>.json
JSON schema (SPEC 4.3): {"lineHeight","baseline","spacing","glyphs":{ch:{x,y,w,h,ox,oy,adv}}}
  - glyph is drawn at (penX + ox, lineTop + oy); pen advances by adv (adv already includes the
    1 px letter gap given in "spacing").
"""
import json, os, shutil
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from uikit import FONT_DIR, ROOT, save_checked
import glyphs_medium, glyphs_timer

OFL = os.path.join(ROOT, 'ref', 'gfonts', 'ofl')
CHARSET = ("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
           ".,:;!?'\"-+/%()&#×Δ$=")


def ascii_bitmap(rows):
    return np.array([[c == '#' for c in r] for r in rows], bool)


# ---------------------------------------------------------------- Tiny5 rasteriser
def rasterize_ttf(path, size, chars, baseline_y=20):
    f = ImageFont.truetype(path, size)
    out = {}
    for ch in chars:
        W, H = 40, 40
        mono = Image.new('L', (W, H), 0)
        d = ImageDraw.Draw(mono)
        d.fontmode = '1'
        d.text((10, baseline_y), ch, font=f, fill=255, anchor='ls')
        aa = Image.new('L', (W, H), 0)
        ImageDraw.Draw(aa).text((10, baseline_y), ch, font=f, fill=255, anchor='ls')
        a = np.asarray(aa)
        if ((a > 0) & (a < 255)).any():
            raise RuntimeError(f'{os.path.basename(path)} glyph {ch!r} is not pixel-aligned at {size}px')
        m = np.asarray(mono) > 127
        assert (m == (a > 127)).all()
        adv = f.getlength(ch)
        assert abs(adv - round(adv)) < 1e-6, (ch, adv)
        if m.any():
            ys, xs = np.nonzero(m)
            x0, y0, x1, y1 = xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
            out[ch] = dict(bmp=m[y0:y1, x0:x1], ox=int(x0 - 10), top=int(y0 - baseline_y), adv=int(round(adv)))
        else:
            out[ch] = dict(bmp=np.zeros((0, 0), bool), ox=0, top=0, adv=int(round(adv)))
    return out


# ---------------------------------------------------------------- packing / writing
def pack_and_write(name, glyphs, line_height, baseline, spacing, extra=None, atlas_w=128):
    """glyphs: ch -> dict(bmp, ox, oy, adv)"""
    order = sorted(glyphs.keys(), key=lambda c: (-glyphs[c]['bmp'].shape[0], ord(c)))
    x = y = 0
    row_h = 0
    pos = {}
    for ch in order:
        b = glyphs[ch]['bmp']
        h, w = b.shape
        if w == 0:
            pos[ch] = (0, 0)
            continue
        if x + w > atlas_w:
            x = 0
            y += row_h + 1
            row_h = 0
        pos[ch] = (x, y)
        x += w + 1
        row_h = max(row_h, h)
    atlas_h = y + row_h
    atlas_h = max(8, int(2 ** np.ceil(np.log2(atlas_h)))) if atlas_h > 0 else 8
    img = np.zeros((atlas_h, atlas_w, 4), np.uint8)
    meta = {'lineHeight': line_height, 'baseline': baseline, 'spacing': spacing}
    if extra:
        meta.update(extra)
    meta['glyphs'] = {}
    for ch in sorted(glyphs.keys(), key=ord):
        gl = glyphs[ch]
        b = gl['bmp']
        h, w = b.shape
        px, py = pos[ch]
        if w:
            img[py:py + h, px:px + w][b] = (255, 255, 255, 255)
        meta['glyphs'][ch] = {'x': int(px), 'y': int(py), 'w': int(w), 'h': int(h),
                              'ox': int(gl['ox']), 'oy': int(gl['oy']), 'adv': int(gl['adv'])}
    save_checked(Image.fromarray(img, 'RGBA'), os.path.join(FONT_DIR, name + '.png'))
    with open(os.path.join(FONT_DIR, name + '.json'), 'w') as fp:
        json.dump(meta, fp, ensure_ascii=False, indent=1)
    return meta


def build_small():
    path = os.path.join(OFL, 'tiny5', 'Tiny5[BLED,JITT,ROND,wdth,wght].ttf')
    raw = rasterize_ttf(path, 8, CHARSET + ' ')
    # Hand-drawn replacements where Tiny5's glyph reads poorly at 1x
    over = {
        'Δ': (-5, ["..#..", ".#.#.", ".#.#.", "#...#", "#####"]),
        '%': (-5, ["#..#", "..#.", ".#..", "#..#", "...."]),
    }
    over['%'] = (-5, ["#...#", "...#.", "..#..", ".#...", "#...#"])
    for ch, (top, rows) in over.items():
        b = ascii_bitmap(rows)
        while not b[-1].any():
            b = b[:-1]
        raw[ch] = dict(bmp=b, ox=0, top=top, adv=b.shape[1] + 1)
    asc = -min(g['top'] for g in raw.values() if g['bmp'].size)
    desc = max(g['top'] + g['bmp'].shape[0] for g in raw.values() if g['bmp'].size)
    baseline = asc
    glyphs = {ch: dict(bmp=g['bmp'], ox=g['ox'], oy=g['top'] + baseline, adv=g['adv']) for ch, g in raw.items()}
    cap = -raw['H']['top']
    meta = pack_and_write('font_small', glyphs, line_height=asc + desc + 1, baseline=baseline, spacing=1,
                          extra={'capHeight': cap, 'source': 'Tiny5 (OFL) @8px, no AA; Delta/% hand-drawn'})
    return meta


def build_medium():
    glyphs = {}
    for ch, (oy, rows) in glyphs_medium.G.items():
        b = ascii_bitmap(rows)
        glyphs[ch] = dict(bmp=b, ox=0, oy=oy, adv=b.shape[1] + 1)
    # tabular digits: every digit advances 6
    for d in '0123456789':
        assert glyphs[d]['bmp'].shape[1] == 5
    glyphs[' '] = dict(bmp=np.zeros((0, 0), bool), ox=0, oy=0, adv=4)
    return pack_and_write('font_medium', glyphs, line_height=10, baseline=7, spacing=1,
                          extra={'capHeight': 7, 'monospaceDigits': True, 'digitAdv': 6,
                                 'source': 'hand-drawn (Paw Noon)'})


def build_timer():
    glyphs = {}
    for ch, (oy, ox, adv, rows) in glyphs_timer.G.items():
        b = ascii_bitmap(rows)
        glyphs[ch] = dict(bmp=b, ox=ox, oy=oy, adv=adv)
    glyphs[' '] = dict(bmp=np.zeros((0, 0), bool), ox=0, oy=0, adv=5)
    return pack_and_write('font_timer', glyphs, line_height=17, baseline=15, spacing=1,
                          extra={'capHeight': 15, 'monospaceDigits': True, 'digitAdv': 10,
                                 'source': 'hand-drawn (Paw Noon)'})


def copy_licenses():
    dst = os.path.join(FONT_DIR, 'licenses')
    os.makedirs(dst, exist_ok=True)
    shutil.copy(os.path.join(OFL, 'tiny5', 'OFL.txt'), os.path.join(dst, 'Tiny5-OFL.txt'))
    with open(os.path.join(dst, 'README.txt'), 'w') as fp:
        fp.write("font_small  : glyphs rasterised from Tiny5 (SIL Open Font License 1.1, see Tiny5-OFL.txt);\n"
                 "              Delta and % redrawn by hand.\n"
                 "font_medium : hand-drawn for Paw Noon (no third-party font data).\n"
                 "font_timer  : hand-drawn for Paw Noon (no third-party font data).\n"
                 "Display lettering in Art/UI (cue_*, logo, banners, stamps) is hand-built geometry, no font data.\n")


def main():
    os.makedirs(FONT_DIR, exist_ok=True)
    s = build_small()
    m = build_medium()
    t = build_timer()
    copy_licenses()
    print('font_small', s['lineHeight'], s['baseline'], len(s['glyphs']))
    print('font_medium', m['lineHeight'], m['baseline'], len(m['glyphs']))
    print('font_timer', t['lineHeight'], t['baseline'], len(t['glyphs']))


if __name__ == '__main__':
    main()
