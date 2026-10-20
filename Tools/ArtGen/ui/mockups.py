"""HUD mockups (work/ui/mock_<screen>_<device>.png at 1x and _4x). Layout constants here are the
ones documented in work/ui/LAYOUT.md (visible-area coordinates, top-left origin)."""
import json, os
import numpy as np
from PIL import Image
from uikit import ellipse_mask, Canvas, draw_text, text_width, upscale, WORK, ART, UI_DIR, FX_DIR, P

DEVICES = {  # name: (canvas y of visible top, visible height, safe top, safe bottom)  -- art direction v2
    'iphone': (27, 585, 41, 23),
    'se': (80, 480, 14, 0),
}
VW = 270
CANVAS_X0 = 45
CX = 135   # visible centre x


def ui(name):
    return Image.open(os.path.join(UI_DIR, name + '.png')).convert('RGBA')


def frame(img, cell, i=0):
    return img.crop((i * cell[0], 0, (i + 1) * cell[0], cell[1]))


def nine(img, border, w, h):
    l, t, r, b = border
    W, H = img.size
    out = Image.new('RGBA', (w, h))
    xs = [(0, l, 0, l), (l, W - r, l, w - r), (W - r, W, w - r, w)]
    ys = [(0, t, 0, t), (t, H - b, t, h - b), (H - b, H, h - b, h)]
    for sx0, sx1, dx0, dx1 in xs:
        for sy0, sy1, dy0, dy1 in ys:
            if dx1 > dx0 and dy1 > dy0:
                out.paste(img.crop((sx0, sy0, sx1, sy1)).resize((dx1 - dx0, dy1 - dy0), Image.NEAREST), (dx0, dy0))
    return out


def scene(with_hero=True, bandit_anim='bandit_ready', hero_anim='hero_ready'):
    cv = Canvas(360, 640)
    env = os.path.join(ART, 'Environment')
    prev = os.path.join(env, 'preview_scene.png')
    if os.path.exists(prev) and Image.open(prev).size == (360, 640):
        cv.blit(Image.open(prev).convert('RGBA'), 0, 0)
    else:   # flat placeholder until the v2 environment lands
        yy = np.mgrid[0:640, 0:360][0]
        for y0, c in ((0, 'sky1'), (90, 'sky2'), (170, 'sky3'), (240, 'sky4'), (285, 'sky5'), (300, 'sand1'), (420, 'sand2'), (540, 'sand3')):
            cv.paint(yy >= y0, c)
        cv.paint(ellipse_mask(360, 640, 290, 130, 14, 14), 'sun1')
        cv.paint(ellipse_mask(360, 640, 290, 130, 10, 10), 'sun0')
    for who, anim, anchor in (('Bandit', bandit_anim, (200, 432)), ('Hero', hero_anim, (14, 640))):
        if who == 'Hero' and not with_hero:
            continue
        d = os.path.join(ART, 'Characters', who)
        jp = os.path.join(d, who.lower() + '.json')
        p = os.path.join(d, anim + '.png')
        if not (os.path.exists(jp) and os.path.exists(p)):
            continue
        meta = json.load(open(jp))
        cell, piv = meta['cell'], meta['pivot']
        cv.blit(frame(Image.open(p).convert('RGBA'), cell), anchor[0] - piv[0], anchor[1] - piv[1])
    return cv


def visible(cv, dev):
    y0, h, _, _ = DEVICES[dev]
    v = Canvas(VW, h)
    v.blit(cv.img().crop((CANVAS_X0, y0, CANVAS_X0 + VW, y0 + h)), 0, 0)
    return v


def cy(dev, canvas_y):
    return canvas_y - DEVICES[dev][0]


def hud(v, dev, hp_you=1.0, hp_bot=1.0, hurt=None):
    st = DEVICES[dev][2]
    pf, bd = ui('portrait_frame'), ui('portrait_backdrop')
    for x, who in ((4, 'hero'), (222, 'bandit')):
        v.blit(bd, x + 4, st + 6)
        p = os.path.join(ART, 'Characters', who.capitalize(), f'{who}_portrait{"_hurt" if hurt == who else ""}.png')
        if os.path.exists(p):
            im = Image.open(p).convert('RGBA')
            v.blit(im, x + 4 + (36 - im.width) // 2, st + 6 + (36 - im.height))
        v.blit(pf, x, st + 2)
    for name, x, right in (('YOU', 50, False), ('PAWTRUTH', 218, True)):
        tw = text_width(name, 'font_medium')
        pw = tw + 12
        px = x - pw if right else x
        v.blit(nine(ui('name_plate'), [5, 5, 5, 5], pw, 14), px, st + 2)
        draw_text(v, name, 'font_medium', px + 6, st + 5, 'brass0', outline='ink')
    fr, fill, low = ui('hp_frame'), ui('hp_fill'), ui('hp_fill_low')
    for x, hp, right in ((46, hp_you, False), (138, hp_bot, True)):
        v.blit(fr, x, st + 18)
        w = int(round(62 * hp))
        if w:
            v.blit((low if hp < 0.3 else fill).crop((0, 0, w, 8)), x + 12 + (62 - w if right else 0), st + 23)


def badge(v, dev, text=None, font='font_medium'):
    b = ui('hud_badge')
    bx, by = CX - 76, cy(dev, 190) - 59
    v.blit(b, bx, by)
    if text:
        draw_text(v, text, font, CX, by + 102 + (2 if font == 'font_timer' else 6), 'sun1' if font == 'font_timer' else 'brass0',
                  outline='ink', align='center')
    return bx, by


def center_blit(v, img, cx, cy_):
    v.blit(img, cx - img.width // 2, cy_ - img.height // 2)


def screen(name, dev):
    _, H, st, sb = DEVICES[dev]
    if name == 'title':
        v = visible(scene(with_hero=False, bandit_anim='bandit_idle'), dev)
        logo = ui('logo')
        v.blit(logo, CX - logo.width // 2, st + 34)
        v.blit(ui('btn_icon'), 270 - 4 - 30, st + 2)
        v.blit(ui('ico_gear'), 270 - 4 - 30 + 9, st + 2 + 8)
        bw, bh = 150, 36
        by = H - sb - 84
        v.blit(nine(ui('btn_large'), [8, 8, 8, 11], bw, bh), CX - bw // 2, by)
        draw_text(v, 'PLAY', 'font_medium', CX, by + 12, 'cream0', outline='ink', align='center')
        draw_text(v, 'BEST 0.231 s', 'font_medium', CX, by + bh + 12, 'sun1', outline='ink', shadow='ink', align='center')
    elif name == 'countdown':
        v = visible(scene(), dev)
        hud(v, dev)
        bx, by = badge(v, dev, 'ROUND 1')
        center_blit(v, ui('cue_2'), CX, by + 59)
    elif name == 'draw':
        v = visible(scene(), dev)
        hud(v, dev)
        bx, by = badge(v, dev, '0.183245 s', 'font_timer')
        center_blit(v, frame(ui('cue_burst'), (200, 200), 1), CX, by + 50)
        center_blit(v, ui('cue_draw'), CX, by + 50)
        hx, hy = 220, cy(dev, 520)
        tw = text_width('TAP ANYWHERE', 'font_small')
        draw_text(v, 'TAP ANYWHERE', 'font_small', hx, hy - 7, 'cream0', outline='ink', align='center')
        draw_text(v, 'TO DRAW', 'font_small', hx, hy + 2, 'cream0', outline='ink', align='center')
        v.blit(frame(ui('tap_hint'), (16, 16), 0), hx - tw // 2 - 19, hy - 9)
    elif name == 'result':
        v = visible(scene(hero_anim='hero_aim', bandit_anim='bandit_hit'), dev)
        hud(v, dev, hp_bot=0.70, hurt='bandit')
        pw, ph = 230, 112
        px, py = CX - pw // 2, cy(dev, 215) - ph // 2
        v.blit(nine(ui('panel_paper'), [7, 7, 7, 7], pw, ph), px, py)
        draw_text(v, 'YOU', 'font_medium', px + 12, py + 15, 'ink2')
        draw_text(v, '0.214382 s', 'font_timer', px + pw - 12, py + 11, 'ink2', align='right')
        draw_text(v, 'PAWTRUTH', 'font_medium', px + 12, py + 36, 'wood3')
        draw_text(v, '0.288105 s', 'font_medium', px + pw - 12, py + 36, 'wood3', align='right')
        for x in range(px + 10, px + pw - 10, 2):
            v.px(x, py + 49, 'paintCreamD')
        draw_text(v, '+73.72 ms FASTER', 'font_medium', px + 12, py + 57, 'green3')
        draw_text(v, 'HIT  -30 HP', 'font_medium', px + 12, py + 74, 'red2')
        stamp = ui('grade_great')
        v.blit(stamp, px + pw - stamp.width - 12, py + 70)
    elif name == 'match':
        v = visible(scene(hero_anim='hero_win', bandit_anim='bandit_down'), dev)
        center_blit(v, ui('banner_win'), CX, cy(dev, 170))
        pw, ph = 210, 66
        px, py = CX - pw // 2, cy(dev, 170) + 34
        v.blit(nine(ui('panel_paper'), [7, 7, 7, 7], pw, ph), px, py)
        for i, (k, val) in enumerate((('BEST', '0.198412 s'), ('AVG', '0.244019 s'), ('ALL-TIME', '0.191233 s'))):
            col = 'ink2' if i < 2 else 'wood3'
            draw_text(v, k, 'font_medium', px + 12, py + 11 + i * 16, col)
            draw_text(v, val, 'font_medium', px + pw - 12, py + 11 + i * 16, col, align='right')
        bw, bh = 180, 36
        y2 = H - sb - 10 - bh
        y1 = y2 - bh - 10
        v.blit(nine(ui('btn_large'), [8, 8, 8, 11], bw, bh), CX - bw // 2, y1)
        draw_text(v, 'REMATCH', 'font_medium', CX - 7, y1 + 12, 'cream0', outline='ink', align='center')
        v.blit(ui('ico_retry'), CX + 27, y1 + 10)
        v.blit(nine(ui('btn_large_alt'), [8, 8, 8, 11], bw, bh), CX - bw // 2, y2)
        draw_text(v, 'MENU', 'font_medium', CX - 7, y2 + 12, 'cream0', outline='ink', align='center')
        v.blit(ui('ico_home'), CX + 15, y2 + 10)
    elif name == 'settings':
        v = visible(scene(with_hero=False, bandit_anim='bandit_idle'), dev)
        pw = 230
        rows = [('ico_music_on', 'Music', 'toggle_on'), (None, 'Music style', 'seg'),
                ('ico_sound_on', 'Sound FX', 'toggle_on'), ('ico_haptic_off', 'Haptics', 'toggle_off'),
                ('ico_motion', 'Reduced Motion', 'toggle_off')]
        ph = 44 + len(rows) * 30 + 6
        px, py = CX - pw // 2, max(st + 8, (H - ph) // 2 - 10)
        v.blit(nine(ui('panel_wood'), [7, 7, 7, 7], pw, ph), px, py)
        v.blit(ui('btn_icon'), px + 6, py + 6)
        v.blit(ui('ico_back'), px + 15, py + 14)
        draw_text(v, 'SETTINGS', 'font_medium', CX, py + 17, 'sun1', outline='ink', shadow='ink', align='center')
        y = py + 42
        for ico, label, ctl in rows:
            if ico:
                v.blit(ui(ico), px + 12, y + 8)
            draw_text(v, label, 'font_medium', px + 30, y + 10, 'cream0', outline='ink')
            if ctl == 'seg':
                for j, (lab, on) in enumerate((('ORIGINAL', True), ('CLASSIC', False))):
                    sw = 48
                    sx = px + pw - 12 - 2 * sw - 2 + j * (sw + 2)
                    v.blit(nine(ui('seg_on' if on else 'seg_off'), [5, 5, 5, 5], sw, 24), sx, y + 2)
                    draw_text(v, lab, 'font_small', sx + sw // 2, y + 10 + (1 if on else 0), 'ink2' if on else 'cream0',
                              outline=None if on else 'ink', align='center')
            else:
                v.blit(ui(ctl), px + pw - 12 - 22, y + 8)
            if (y + 28) < py + ph - 8:
                for x in range(px + 10, px + pw - 10):
                    v.px(x, y + 28, 'wood4')
            y += 30
    return v


def safe_guides(v, dev):
    """Overlay the simulated system areas (status bar / Dynamic Island, home indicator)."""
    _, H, st, sb = DEVICES[dev]
    g = v.a.copy()
    g[:st, :, :3] = (g[:st, :, :3] * 0.55).astype(np.uint8)
    if sb:
        g[H - sb:, :, :3] = (g[H - sb:, :, :3] * 0.55).astype(np.uint8)
    img = Image.fromarray(g, 'RGBA')
    if dev == 'iphone':
        a = np.asarray(img).copy()
        a[8:17, 108:162, :3] = 0      # Dynamic Island pill
        a[H - 8:H - 6, 88:182, :3] = 255  # home indicator
        img = Image.fromarray(a, 'RGBA')
    return img


def main():
    os.makedirs(WORK, exist_ok=True)
    names = ['title', 'countdown', 'draw', 'result', 'match', 'settings']
    for dev in DEVICES:
        tiles = []
        for n in names:
            v = screen(n, dev)
            clean = v.img()
            clean.save(os.path.join(WORK, f'mock_{n}_{dev}.png'))
            g = safe_guides(v, dev)
            upscale(g, 4).save(os.path.join(WORK, f'mock_{n}_{dev}_4x.png'))
            tiles.append(g)
        H = tiles[0].height
        sheet = Image.new('RGBA', (len(tiles) * (VW + 4), H), (20, 16, 32, 255))
        for i, t in enumerate(tiles):
            sheet.paste(t, (i * (VW + 4), 0))
        upscale(sheet, 2).save(os.path.join(WORK, f'mock_sheet_{dev}_2x.png'))
    print('mockups written')


if __name__ == '__main__':
    main()
