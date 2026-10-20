"""M5 screen mockups from the real sprites and bitmap fonts, laid out with the same integer maths as the C#
(M5Card.PlaceCard, FriendCard.Layout, PvpCard.Layout, LeaderboardScreen.Layout, PixelButton, BitmapText,
CodeTiles, PixelKeypad, OnlineResultCard). Unity cannot run on the build agents, so this is how the layouts
were checked pixel by pixel; keep the numbers in step when a C# layout changes.

    python3 Tools/ArtGen/ui/m5_mockups.py [out_dir]      (default Docs/M5/: one contact sheet m5_screens.png;
    PN_MOCK_KEEP=1 keeps the single 1x screens, PN_MOCK_3X=1 adds 3x copies)
"""
import json
import os
import sys
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
ART = os.path.join(PROJECT, 'Assets', 'PawNoon', 'Resources', 'Art')
UI = os.path.join(ART, 'UI')
FONTS = os.path.join(ART, 'Fonts')

# device: (visible rect on the 360 x 640 canvas, safe insets top / bottom), as in mockups.py
DEVICES = {'iphone': ((45, 27, 270, 585), 41, 23), 'se': ((45, 80, 270, 480), 14, 0)}
LOGO_Y = 150
PLAY_Y = 470

C = {
    'ink': (0x1A, 0x10, 0x20), 'ink2': (0x2D, 0x1B, 0x2E), 'cream0': (0xFF, 0xF8, 0xEA), 'cream1': (0xF1, 0xE2, 0xC6),
    'sand0': (0xFB, 0xE3, 0xAE), 'sand3': (0xC3, 0x8E, 0x50), 'wood3': (0x6E, 0x44, 0x28), 'wood4': (0x4A, 0x2C, 0x1C),
    'brass0': (0xFF, 0xEB, 0x99), 'brass1': (0xF2, 0xC1, 0x4E), 'paintred': (0xB2, 0x43, 0x3A), 'green2': (0x57, 0x7E, 0x3C),
    'sigred': (0xEE, 0x4B, 0x3E), 'siggreen': (0x6B, 0xDB, 0x5E), 'sun1': (0xFF, 0xF2, 0xA8),
}

UIJ = json.load(open(os.path.join(UI, 'ui.json')))
_sprites = {}


def sprite(name):
    if name not in _sprites:
        _sprites[name] = Image.open(os.path.join(UI, UIJ[name]['file'] + '.png')).convert('RGBA')
    return _sprites[name]


class Font:
    def __init__(self, name):
        self.meta = json.load(open(os.path.join(FONTS, name + '.json')))
        self.img = Image.open(os.path.join(FONTS, name + '.png')).convert('RGBA')
        self.line = self.meta['lineHeight']
        self.baseline = self.meta['baseline']
        self.digit_adv = self.meta.get('digitAdv', 0)

    def glyph(self, ch):
        g = self.meta['glyphs']
        if ch in g:
            return g[ch]
        alt = ch.lower() if ch.isupper() else ch.upper()
        return g.get(alt, {'x': 0, 'y': 0, 'w': 0, 'h': 0, 'ox': 0, 'oy': 0, 'adv': max(2, self.line // 2)})


SMALL, MEDIUM, TIMER, CODE = Font('font_small'), Font('font_medium'), Font('font_timer'), Font('font_code')


class Canvas:
    def __init__(self, dev, background=True):
        self.vis, top, bottom = DEVICES[dev][0], DEVICES[dev][1], DEVICES[dev][2]
        x, y, w, h = self.vis
        self.safe = (x, y + top, w, h - top - bottom)
        self.img = Image.new('RGBA', (360, 640), (0, 0, 0, 255))
        if background:
            self.img.alpha_composite(Image.open(os.path.join(ART, 'Environment', 'plate.png')).convert('RGBA'))

    @property
    def cx(self):
        return self.vis[0] + self.vis[2] // 2

    def dim(self):
        x, y, w, h = self.vis
        ov = Image.new('RGBA', (w, h), C['ink'] + (150,))
        self.img.alpha_composite(ov, (x, y))

    def rect(self, x, y, w, h, col):
        if w > 0 and h > 0:
            self.img.paste(Image.new('RGBA', (w, h), col + (255,)), (x, y))

    def sliced(self, name, x, y, w, h):
        im = sprite(name)
        e = UIJ[name]
        if 'border' not in e:
            self.img.alpha_composite(im, (x, y))
            return
        l, t, r, b = e['border']
        W, H = im.size
        xs = [(0, l, 0, l), (l, W - r, l, w - r), (W - r, W, w - r, w)]
        ys = [(0, t, 0, t), (t, H - b, t, h - b), (H - b, H, h - b, h)]
        for sx0, sx1, dx0, dx1 in xs:
            for sy0, sy1, dy0, dy1 in ys:
                if dx1 > dx0 and dy1 > dy0:
                    self.img.alpha_composite(im.crop((sx0, sy0, sx1, sy1)).resize((dx1 - dx0, dy1 - dy0), Image.NEAREST), (x + dx0, y + dy0))

    def at_pivot(self, name, x, y):
        e = UIJ[name]
        px, py = e.get('pivot', [0, 0])
        self.img.alpha_composite(sprite(name), (x - px, y - py))

    # ---- BitmapText
    def measure(self, s, f, mono=False):
        pen = 0
        for ch in s:
            g = f.glyph(ch)
            pen += f.digit_adv if (mono and f.digit_adv and ch.isdigit()) else g['adv']
        return max(0, pen - 1)

    def text(self, s, f, x, y, fill, align='left', outline=True, shadow=True, ocol='ink', scol='ink2', mono=False):
        w = self.measure(s, f, mono)
        sx = x if align == 'left' else (x - w // 2 if align == 'center' else x - w)
        layers = []
        if shadow:
            layers += [((-1, 2), scol), ((0, 2), scol), ((1, 2), scol)] if outline else [((0, 1), scol)]
        if outline:
            layers += [((-1, 0), ocol), ((1, 0), ocol), ((0, -1), ocol), ((0, 1), ocol)]
        layers.append(((0, 0), fill))
        for (dx, dy), col in layers:
            rgb = C[col] if isinstance(col, str) else col
            pen = sx
            for ch in s:
                g = f.glyph(ch)
                adv, shift = g['adv'], 0
                if mono and f.digit_adv and ch.isdigit():
                    shift = (f.digit_adv - g['adv']) // 2
                    adv = f.digit_adv
                if g['w'] and g['h']:
                    m = f.img.crop((g['x'], g['y'], g['x'] + g['w'], g['y'] + g['h']))
                    solid = Image.new('RGBA', m.size, rgb + (255,))
                    self.img.paste(solid, (pen + shift + g['ox'] + dx, y + g['oy'] + dy), m)
                pen += adv
        return w

    def ink(self, s, f, x, y, col='ink', align='left', mono=False):
        return self.text(s, f, x, y, col, align, outline=False, shadow=False, mono=mono)

    # ---- PixelButton
    def button(self, up, label, icon, x, y, w, h):
        e = UIJ[up]
        co = e.get('contentOffset', [0, 0])
        if label is None:
            self.img.alpha_composite(sprite(up), (x, y))
            cx, cy = x + w // 2 + co[0], y + h // 2 + co[1]
            self.at_pivot(icon, cx, cy)
            return
        self.sliced(up, x, y, w, h)
        cx, cy = x + w // 2 + co[0], y + h // 2 + co[1]
        lw = self.measure(label, MEDIUM)
        iw = 14 if icon else 0
        lx = cx - (lw + iw) // 2 + lw // 2
        self.text(label, MEDIUM, lx, cy - MEDIUM.line // 2, 'cream0', 'center')
        if icon:
            self.at_pivot(icon, lx + lw // 2 + 4 + 6, cy)

    def seg(self, on, x, y, w, h):
        self.sliced('seg_on' if on else 'seg_off', x, y, w, h)

    def save(self, path, scale=3):
        x, y, w, h = self.vis
        crop = self.img.crop((x, y, x + w, y + h))
        crop.save(path + '.png', optimize=True)
        if os.environ.get('PN_MOCK_3X'):
            crop.resize((w * scale, h * scale), Image.NEAREST).save(path + '@3x.png')


# ------------------------------------------------------------------------------------------- M5Card frame
def card(cv, w, h, panel='panel_wood', title='', dark=False):
    x0, y0, vw, vh = cv.vis
    sx, sy, sw, sh = cv.safe
    w = min(w, vw - 16)
    x = cv.cx - w // 2
    y = max(LOGO_Y + 64, sy + sh - 12 - h)
    if y + h > sy + sh - 4:
        y = max(sy + 4, sy + sh - 4 - h)
    cv.sliced(panel, x, y, w, h)
    cv.button('btn_icon', None, 'ico_back', x + 6, y + 6, 30, 30)
    if dark:
        cv.ink(title, MEDIUM, x + w // 2, y + 14, align='center')
    else:
        cv.text(title, MEDIUM, x + w // 2, y + 14, 'brass0', 'center')
    return x, y, w, h


def title_screen(cv, friend=True, controls=True):
    """controls=False: an online card is up, so the title hides PLAY / PVP / toggle / BEST / gear / trophy (SetOnlineBusy)."""
    x0, y0, vw, vh = cv.vis
    sx, sy, sw, sh = cv.safe
    cx = cv.cx
    logo = 'logo_v3' if 'logo_v3' in UIJ else 'logo'
    ly = max(LOGO_Y, sy + 40 + UIJ[logo]['size'][1] // 2)
    cv.at_pivot(logo, cx, ly)
    if not controls:
        return
    play_top = min(PLAY_Y - 18, sy + sh - 118)
    cv.button('panel_wood', 'PLAY', None, cx - 76, play_top, 152, 36)
    py = play_top + 40
    if friend:
        cv.button('panel_wood', 'PVP', None, cx - 76, py, 92, 30)
        cv.button('panel_wood', 'FRIEND', None, cx + 20, py, 56, 30)
    else:
        cv.button('panel_wood', 'PVP', None, cx - 76, py, 152, 30)
    segy = py + 35
    cv.seg(False, cx - 1 - 48, segy, 48, 20)
    cv.seg(True, cx + 1, segy, 48, 20)
    ty = segy + (20 - SMALL.line) // 2 + 1
    cv.text('EASY', SMALL, cx - 1 - 24, ty, 'cream1', 'center')
    cv.text('HARD', SMALL, cx + 1 + 24, ty, 'cream0', 'center')
    cv.text('BEST 0.231 s', MEDIUM, cx, segy + 25, 'sun1', 'center')
    cv.button('btn_icon', None, 'ico_gear', sx + sw - 34, sy + 2, 30, 30)
    cv.button('btn_icon', None, 'ico_trophy', sx + 4, sy + 2, 30, 30)


def pvp_card(cv, ranked, linked, flash=False):
    x, y, w, h = card(cv, 196, 150, title='PVP DUEL')
    cx = x + w // 2
    seg_w = 74
    cv.seg(not ranked, cx - 1 - seg_w, y + 40, seg_w, 20)
    cv.seg(ranked, cx + 1, y + 40, seg_w, 20)
    ty = y + 40 + (20 - SMALL.line) // 2
    cv.text('CASUAL', SMALL, cx - 1 - seg_w // 2, ty, 'cream0' if not ranked else 'cream1', 'center')
    cv.text('RANKED', SMALL, cx + 1 + seg_w // 2, ty, ('cream0' if ranked else 'cream1') if linked else 'sand3', 'center')
    d1, d2 = (("Rated. Standard guns for all.", "Climb this week's board.") if ranked
              else ("Unrated. Hard rules, 3 rounds.", "Most damage wins."))
    cv.text(d1, SMALL, cx, y + 68, 'cream1', 'center')
    cv.text(d2, SMALL, cx, y + 78, 'sand0', 'center')
    if not linked:
        cv.text('SIGN IN TO RANK', SMALL, cx, y + 91, 'sigred' if flash else 'brass1', 'center')
    bw = min(164, w - 24)
    cv.button('btn_large', 'FIND RIVAL', None, cx - bw // 2, y + 106, bw, 30)


def tiles(cv, cx, y, code, cursor=-1):
    T, G = 30, 6
    x = cx - (4 * T + 3 * G) // 2
    for i in range(4):
        cv.seg(i == cursor, x + i * (T + G), y, T, T)
        if code and i < len(code):
            cv.text(code[i], CODE, x + i * (T + G) + T // 2, y + (T - 3 - CODE.baseline) // 2, 'cream0', 'center')


def keypad(cv, cx, y, pressed=-1):
    KW, KH, G = 22, 20, 2
    ox = cx - (7 * KW + 6 * G) // 2
    for i in range(27):
        if i == 26:
            col, row, span = 5, 3, 2
        else:
            col, row, span = i % 7, i // 7, 1
        rx, ry, rw = ox + col * (KW + G), y + row * (KH + G), span * KW + (span - 1) * G
        sink = 1 if i == pressed else 0
        cv.seg(i == pressed, rx, ry + sink, rw, KH)
        label = 'DEL' if i == 26 else chr(ord('A') + i)
        cv.text(label, MEDIUM, rx + rw // 2, ry + (KH - 3 - MEDIUM.line) // 2 + 1 + sink, 'cream0', 'center')


def friend_menu(cv):
    x, y, w, h = card(cv, 196, 150, title='FRIEND DUEL')
    cx = x + w // 2
    bw = min(164, w - 24)
    cv.text('One code. One friend. Your ghost.', SMALL, cx, y + 40, 'sand0', 'center')
    cv.button('btn_large', 'CREATE ROOM', None, cx - bw // 2, y + 52, bw, 30)
    cv.text('Get a code to send a friend.', SMALL, cx, y + 85, 'cream1', 'center')
    cv.button('btn_large_alt', 'JOIN ROOM', None, cx - bw // 2, y + 98, bw, 30)
    cv.text('Type the code a friend sent.', SMALL, cx, y + 131, 'cream1', 'center')


def friend_created(cv, code='HOWL', status=('CLOSES IN 9:58', 'brass1')):
    x, y, w, h = card(cv, 196, 156, title='YOUR ROOM')
    cx = x + w // 2
    tiles(cv, cx, y + 40, code)
    cv.text('Send the code, then win a duel:', SMALL, cx, y + 76, 'cream1', 'center')
    cv.text('your friend faces that ghost.', SMALL, cx, y + 86, 'sand0', 'center')
    cv.text(status[0], SMALL, cx, y + 99, status[1], 'center')
    sw, dw, gap = 80, 84, 8
    x0 = cx - (sw + gap + dw) // 2
    cv.button('btn_large_alt', 'SHARE', 'ico_share', x0, y + 114, sw, 30)
    cv.button('btn_large', 'DUEL', None, x0 + sw + gap, y + 114, dw, 30)


def friend_join(cv, typed='HO', hint=('Type the 4 letters.', 'sand0'), pressed=-1):
    h = 40 + 30 + 18 + (4 * 20 + 3 * 2) + 8 + 30 + 12
    x, y, w, h = card(cv, 196, h, title='JOIN A ROOM')
    cx = x + w // 2
    tiles(cv, cx, y + 40, typed, -1 if len(typed) == 4 else len(typed))
    hy = y + 40 + 30 + 5
    cv.text(hint[0], SMALL, cx, hy, hint[1], 'center')
    ky = hy + 13
    keypad(cv, cx, ky, pressed)
    if len(typed) == 4:
        cv.button('btn_large', 'JOIN', None, cx - 60, ky + 86 + 8, 120, 30)
    else:
        cv.button('btn_large_alt', 'CANCEL', None, cx - 60, ky + 86 + 8, 120, 30)


def friend_waiting(cv, code='HOWL', host='PARDNER PETE'):
    x, y, w, h = card(cv, 196, 156, title='WAITING')
    cx = x + w // 2
    tiles(cv, cx, y + 40, code)
    cv.text('WAITING FOR ' + host + '..', SMALL, cx, y + 76, 'cream1', 'center')
    cv.text('They must finish one duel first.', SMALL, cx, y + 86, 'sand0', 'center')
    cv.text('CLOSES IN 9:01', SMALL, cx, y + 99, 'brass1', 'center')
    cv.button('btn_large_alt', 'CANCEL', None, cx - 60, y + 114, 120, 30)


def leaderboard(cv, ranked=True, first=0, state='ready'):
    sys.path.insert(0, HERE)
    vx, vy, vw, vh = cv.vis
    sx, sy, sw, sh = cv.safe
    w = min(232, vw - 16)
    h = min(420, sh - 16)
    x = cv.cx - w // 2
    y = max(sy + 8, sy + (sh - h) // 2)
    cv.sliced('panel_paper', x, y, w, h)
    cv.button('btn_icon', None, 'ico_back', x + 6, y + 6, 30, 30)
    cv.ink('LEADERBOARD', MEDIUM, x + w // 2, y + 14, align='center')
    tw = (w - 24 - 2) // 2
    cv.seg(ranked, x + 12, y + 38, tw, 20)
    cv.seg(not ranked, x + 12 + tw + 2, y + 38, tw, 20)
    ty = y + 38 + (20 - SMALL.line) // 2
    cv.text('WEEKLY RANKED', SMALL, x + 12 + tw // 2, ty, 'cream0' if ranked else 'cream1', 'center')
    cv.text('FASTEST DRAW', SMALL, x + 12 + tw + 2 + tw // 2, ty, 'cream1' if ranked else 'cream0', 'center')
    rows = fake_board(ranked)
    sub = 'WEEK 43   412 GUNS   MIN 5 DUELS' if ranked else 'ALL TIME   1873 GUNS'
    rank_x, name_x, value_x = x + 34, x + 40, x + w - 16
    list_top = y + 87
    pinned_top = y + h - 10 - 12
    visible = max(1, (pinned_top - 5 - list_top) // 12)
    if state == 'ready':
        cv.ink(sub, SMALL, x + w // 2, y + 63, col='wood3', align='center')
        cv.ink('#', SMALL, rank_x, y + 76, col='wood4', align='right')
        cv.ink('NAME', SMALL, name_x, y + 76, col='wood4')
        cv.ink('RATING' if ranked else 'BEST DRAW', SMALL, value_x, y + 76, col='wood4', align='right')
        you_index = -1 if ranked else 11
        for i in range(visible):
            idx = first + i
            if idx >= len(rows):
                break
            ry = list_top + i * 12
            if idx == you_index:
                cv.rect(x + 8, ry, w - 20, 12, C['brass1'])
            elif idx % 2 == 1:
                cv.rect(x + 8, ry, w - 20, 12, C['cream1'])
            r, n, v = rows[idx]
            cv.ink(r, MEDIUM, rank_x, ry + 1, align='right', mono=True)
            cv.ink(n, MEDIUM, name_x, ry + 1)
            cv.ink(v, MEDIUM, value_x, ry + 1, align='right', mono=True)
        # scrollbar
        n = len(rows)
        mx = max(0, n - visible)
        if mx:
            th = max(6, visible * 12 * visible // n)
            tt = list_top + (visible * 12 - th) * first // mx
            cv.rect(x + w - 10, list_top, 2, visible * 12, C['cream1'])
            cv.rect(x + w - 10, tt, 2, th, C['wood3'])
        cv.rect(x + 8, pinned_top - 3, w - 16, 1, C['wood3'])
        cv.rect(x + 8, pinned_top, w - 16, 12, C['brass1'])
        yr, yn, yv = ('87.', 'YOU', '1214') if ranked else ('12.', 'YOU', rows[11][2])
        cv.ink(yr, MEDIUM, rank_x, pinned_top + 1, align='right', mono=True)
        cv.ink(yn, MEDIUM, name_x, pinned_top + 1)
        cv.ink(yv, MEDIUM, value_x, pinned_top + 1, align='right', mono=True)
    else:
        big, small = ('LOADING...', '') if state == 'loading' else ('NO SIGNAL', 'Tap the tab to try again.')
        cv.ink(big, MEDIUM, x + w // 2, list_top + 40, align='center')
        cv.ink(small, SMALL, x + w // 2, list_top + 54, col='wood3', align='center')


def fake_board(ranked):
    """Same values as NetMock.Leaderboard (System.Random differs from Python's, so the steps are a fixed pattern)."""
    names = ["MARSHAL MITTENS", "KIT CARSON", "DUSTY", "OLD TOM", "CALAMITY PAWS", "WHISKER WYATT", "DOC PURRLIDAY",
             "BELLE STARR", "TABBY TWOFEET", "SUNDANCE", "BLACK BART", "ANNIE OAKPAW", "JESSE MEWS", "STRANGER #4471"]
    out = []
    v = 1702 if ranked else 151_300_000
    rank = 1
    for i in range(50):
        if i:
            step = (i * 5) % 9 if ranked else ((i * 377_000) % 1_400_000)
            if step:
                rank = i + 1
            v += -step if ranked else step
        name = 'STRANGER #2026' if (not ranked and i == 11) else names[(i * 7 + 3) % len(names)][:14]
        val = str(v) if ranked else '%.3f s' % (v / 1e9)
        out.append(('%d.' % rank, name, val))
    return out


def rival_card(cv):
    """OnlineCard (M4) with the M5 lines: CAT + RATING, RANKED + YOUR RATING."""
    lines = [('YOUR RIVAL', MEDIUM, 'brass0', 12), ('OLD TOM', MEDIUM, 'sun1', 8),
             ('CAT: BLACKPAW   RATING 1214', SMALL, 'cream1', 6), ('RANKED   YOUR RATING 1202', SMALL, 'sand0', 3)]
    h = 12 + MEDIUM.line + 8 + MEDIUM.line + 6 + SMALL.line + 3 + SMALL.line + 10 + 30 + 12
    vx, vy, vw, vh = cv.vis
    sx, sy, sw, sh = cv.safe
    w = min(196, vw - 16)
    x = cv.cx - w // 2
    y = max(LOGO_Y + 64, sy + sh - 12 - h)
    cv.sliced('panel_wood', x, y, w, h)
    cy = y
    for i, (s, f, col, gap) in enumerate(lines):
        cy += gap
        cv.text(s, f, x + w // 2, cy, col, 'center')
        cy += f.line
    cy += 10
    cv.button('btn_large', 'DUEL', None, x + w // 2 - 75, cy, 150, 30)


def result_card(cv, win=True):
    """MatchResultScreen banner + stats geometry and the OnlineResultCard with the M5 rating line."""
    vx, vy, vw, vh = cv.vis
    sx, sy, sw, sh = cv.safe
    cx = cv.cx
    fh = UIJ['portrait_frame']['size'][1] if 'size' in UIJ['portrait_frame'] else UIJ['portrait_frame']['cell'][1]
    rs = UIJ['round_sign']
    rh = rs['size'][1] if 'size' in rs else rs['cell'][1]
    hud_bottom = sy + 4 + fh + 2 + rh + 4
    top = max(hud_bottom, 150 - 30)
    bd = 'banner_win' if win else 'banner_lose'
    be = UIJ[bd]
    bh = be['size'][1] if 'size' in be else be['cell'][1]
    cv.at_pivot(bd, cx - (be['size'][0] if 'size' in be else be['cell'][0]) // 2 + be['pivot'][0], top + be['pivot'][1])
    py = top + bh + 8
    cv.sliced('panel_paper', cx - 90, py, 180, 62)
    for i, (k, v) in enumerate((('BEST', '0.198412 s'), ('AVG', '0.244019 s'), ('ALL-TIME', '0.191233 s'))):
        cv.ink(k, MEDIUM, cx - 80, py + 9 + i * 15)
        cv.ink(v, MEDIUM, cx + 80, py + 9 + i * 15, align='right', mono=True)
    hz = UIJ['holster_zone']
    hzh = hz['size'][1] if 'size' in hz else hz['cell'][1]
    menu_y = sy + sh - 6 - hzh - 6 - 30
    rematch_y = menu_y - 6 - 30
    cv.button('btn_large', 'NEW RIVAL', 'ico_retry', cx - 82, rematch_y, 164, 30)
    cv.button('btn_large_alt', 'MENU', 'ico_home', cx - 82, menu_y, 164, 30)
    content_bottom = top + bh + 8 + 62 + 4 + 12
    H = 45
    ty = min(content_bottom + 4, rematch_y - 8 - H)
    cv.sliced('panel_paper', cx - 90, ty, 180, H)
    cv.ink('VS OLD TOM', MEDIUM, cx, ty + 6, align='center')
    cv.ink('SERVER: YOU WIN', SMALL, cx, ty + 6 + MEDIUM.line + 3, col='green2', align='center')
    cv.ink('RATING 1214 (+12)', SMALL, cx, ty + 6 + MEDIUM.line + 3 + SMALL.line + 3, col='green2', align='center')
    return content_bottom, ty, rematch_y


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(PROJECT, 'Docs', 'M5')
    os.makedirs(out, exist_ok=True)
    shots = []

    def shot(name, dev, fn, *a, **k):
        cv = Canvas(dev)
        title_screen(cv, controls=fn is None)
        if fn is not None:
            cv.dim()
            fn(cv, *a, **k)
        cv.save(os.path.join(out, name + '_' + dev))
        shots.append(name + '_' + dev)

    for dev in DEVICES:
        shot('title', dev, None)
        shot('pvp_locked', dev, pvp_card, False, False)
        shot('pvp_ranked', dev, pvp_card, True, True)
        shot('friend', dev, friend_menu)
        shot('room', dev, friend_created)
        shot('join', dev, friend_join)
        shot('waiting', dev, friend_waiting)
        shot('leaderboard', dev, leaderboard, True, 0)
        shot('fastest', dev, leaderboard, False, 5)
        shot('rival', dev, rival_card)
    cv = Canvas('se')
    title_screen(cv, controls=False)
    cv.dim()
    friend_join(cv, 'HOWL', pressed=26)
    cv.save(os.path.join(out, 'join_full_se'))
    cv = Canvas('iphone')
    print('result card (content bottom, card top, rematch top):', result_card(cv))
    cv.save(os.path.join(out, 'result_iphone'))
    cv = Canvas('se')
    print('result card se:', result_card(cv, False))
    cv.save(os.path.join(out, 'result_se'))
    # one contact sheet for the docs (iPhone faces + the SE join card and result strip)
    names = [n for n in shots if n.endswith('_iphone')] + ['join_full_se', 'result_iphone', 'result_se']
    ims = [Image.open(os.path.join(out, n + '.png')) for n in names]
    cols = 6
    cw, ch = 270, max(i.height for i in ims)
    rows = (len(ims) + cols - 1) // cols
    sheet = Image.new('RGB', (cols * (cw + 6), rows * (ch + 6)), (20, 14, 22))
    for k, im in enumerate(ims):
        sheet.paste(im, ((k % cols) * (cw + 6), (k // cols) * (ch + 6)))
    sheet.save(os.path.join(out, 'm5_screens.png'), optimize=True)
    if os.environ.get('PN_MOCK_KEEP') is None:
        for n in shots + ['join_full_se', 'result_iphone', 'result_se']:
            os.remove(os.path.join(out, n + '.png'))
    print('\n'.join(shots))


if __name__ == '__main__':
    main()
