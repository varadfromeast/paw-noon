"""App icon: 64x64 design (the Kid's face under his hat, high-noon sky) -> icon_64.png and
icon_1024.png (x16 nearest). Opaque, no text, fills the square."""
import os
import numpy as np
from PIL import Image
from uikit import Canvas, ICON_DIR, ellipse_mask, poly_mask, dilate, shift, edge_in, save_checked, upscale
from gen_ui_display import hat_sprite


def main():
    S = 64
    cv = Canvas(S, S)
    yy, xx = np.mgrid[0:S, 0:S]
    # sky bands with 2x2 checker transitions
    bands = [(0, 'sky1'), (14, 'sky2'), (28, 'sky3'), (40, 'sky4')]
    for i, (y0, col) in enumerate(bands):
        m = yy >= y0
        cv.paint(m, col)
        if i:
            chk = (yy == y0) & ((xx + yy) % 2 == 0) | (yy == y0 + 1) & ((xx + yy) % 2 == 1) & False
            cv.paint(chk, bands[i - 1][1])
    # sun upper right
    cv.paint(ellipse_mask(S, S, 52, 11, 9, 9), 'sun2')
    cv.paint(ellipse_mask(S, S, 52, 11, 7, 7), 'sun1')
    cv.paint(ellipse_mask(S, S, 52, 11, 5, 5), 'sun0')
    # mesas
    mesa = poly_mask(S, S, [(0, 50), (4, 44), (16, 44), (19, 48), (40, 48), (43, 42), (56, 42), (60, 47), (64, 47), (64, 64), (0, 64)])
    cv.paint(mesa, 'rock2')
    cv.paint(mesa & ~shift(mesa, 0, 1), 'rock0')
    cv.paint(mesa & (yy >= 54), 'sand1')
    # cat head
    hx, hy = 32, 41
    head = ellipse_mask(S, S, hx, hy, 17, 14, p=2.3)
    cheeks = ellipse_mask(S, S, hx - 11, hy + 6, 8, 6) | ellipse_mask(S, S, hx + 11, hy + 6, 8, 6)
    ears = poly_mask(S, S, [(15, 34), (17, 18), (27, 28)]) | poly_mask(S, S, [(49, 34), (47, 18), (37, 28)])
    ears = np.zeros((S, S), bool)  # the ears poke through the hat instead
    face = head | cheeks
    # bandana
    band = poly_mask(S, S, [(14, 54), (50, 54), (44, 64), (20, 64)])
    cv.paint(dilate(face | band, 1), 'ink2')
    cv.paint(face, 'tabby2')
    cv.paint(face & (xx > hx + 6) & ~ears, 'tabby3')
    cv.paint(face & ~shift(face, 0, 1) | (face & (yy < 30) & (xx < hx)), 'tabby1')
    for side in (-1, 1):
        inner = poly_mask(S, S, [(hx + side * 15, 32), (hx + side * 14.5, 22), (hx + side * 8, 29)])
        cv.paint(inner & ears, 'tabby0')
    # stripes on forehead and cheeks
    for (x0, y0, x1, y1) in ((30, 28, 30, 33), (34, 28, 34, 33), (26, 29, 27, 32), (37, 29, 38, 32)):
        for y in range(y0, y1):
            cv.px(x0 if y % 2 else x1, y, 'tabby4')
    for side in (-1, 1):
        for i, y in enumerate((44, 47)):
            for k in range(3):
                cv.px(hx + side * (15 - k), y + (1 if k == 2 else 0), 'tabby4')
    # muzzle
    muz = ellipse_mask(S, S, hx, hy + 8, 8, 5)
    cv.paint(muz, 'cream0')
    cv.paint(muz & ~shift(muz, 0, -1), 'cream2')
    # eyes: determined squint
    for side in (-1, 1):
        ex = hx + side * 8
        eye = ellipse_mask(S, S, ex, hy + 1, 3.6, 2.6)
        cv.paint(dilate(eye, 1, diag=False), 'ink2')
        cv.paint(eye, 'eyeGreen')
        cv.px(ex, hy + 1, 'ink'); cv.px(ex, hy + 2, 'ink'); cv.px(ex, hy, 'ink')
        cv.px(ex - side, hy, 'white')
        # brow lid (squint)
        for k in range(-4, 5):
            cv.px(ex + k, hy - 2 + (1 if k * side < -2 else 0), 'ink2')
    # nose + mouth
    for (x, y, c) in ((31, 46, 'red1'), (32, 46, 'red1'), (33, 46, 'red1'), (32, 47, 'red2'),
                      (32, 48, 'ink2'), (31, 49, 'ink2'), (33, 49, 'ink2'), (30, 49, 'ink2'), (34, 49, 'ink2')):
        cv.px(x, y, c)
    # whiskers
    for side in (-1, 1):
        for i, dy in enumerate((-1, 1)):
            for k in range(6):
                cv.px(hx + side * (11 + k), hy + 8 + dy + (dy if k > 3 else 0), 'cream0')
    # bandana
    cv.paint(band, 'paintRed')
    cv.paint(band & ~shift(band, 0, 1), 'red0')
    for x in range(18, 48, 4):
        cv.px(x, 58, 'paintCream'); cv.px(x + 1, 59, 'paintCream')
    cv.paint(band & ~shift(band, 0, -1), 'paintRedD')
    # hat (shared with the logo), slightly wider here
    hat = hat_sprite(60)
    cv.blit(hat, 2, 10)
    img = cv.img()
    a = np.asarray(img)
    assert (a[..., 3] == 255).all(), 'icon must be opaque'
    img = Image.fromarray(a[..., :3].copy(), 'RGB').convert('RGBA')
    os.makedirs(ICON_DIR, exist_ok=True)
    save_checked(img, os.path.join(ICON_DIR, 'icon_64.png'))
    big = upscale(img, 16).convert('RGB')
    big.save(os.path.join(ICON_DIR, 'icon_1024.png'))
    print('icon ok')


if __name__ == '__main__':
    main()
