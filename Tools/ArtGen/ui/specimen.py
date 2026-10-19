"""Font specimen sheet (work/ui/font_specimen_4x.png) for visual QA."""
import os
from PIL import Image
from uikit import Canvas, draw_text, upscale, WORK, P

LINES = [
    ('font_small', ["ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz",
                    "0123456789 .,:;!?'\"-+/%()&#×Δ$=", "TAP ANYWHERE TO DRAW  Best 0.231s",
                    "Music  SFX  Haptics  Reduced Motion"]),
    ('font_medium', ["ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz",
                     "0123456789 .,:;!?'\"-+/%()&#×Δ$=", "YOU 0.214382 s",
                     "BLACKPAW 0.288105 s", "Δ 73.72 ms FASTER  ROUND 3", "Blackpaw wins! -30 HP"]),
    ('font_timer', ["0.000000 s", "0.214382 s", "1.500000", "+73.72 ms -×Δ", "0123456789"]),
]


def main():
    W, H = 240, 240
    cv = Canvas(W, H)
    cv.paint(__import__('numpy').ones((H, W), bool), 'sky2')
    y = 3
    for font, lines in LINES:
        from uikit import load_font
        meta, _ = load_font(font)
        for i, t in enumerate(lines):
            if font == 'font_timer' and i % 2 == 0:
                draw_text(cv, t, font, 4, y, 'cream0', outline='ink', shadow='ink')
            elif font == 'font_timer':
                draw_text(cv, t, font, 4, y, 'sun1', outline='ink2', shadow='ink2')
            else:
                draw_text(cv, t, font, 4, y, 'white', outline='ink', shadow='ink')
            y += meta['lineHeight'] + 2
        y += 3
    os.makedirs(WORK, exist_ok=True)
    upscale(cv.img(), 4).save(os.path.join(WORK, 'font_specimen_4x.png'))
    print('specimen height used', y)


if __name__ == '__main__':
    main()
