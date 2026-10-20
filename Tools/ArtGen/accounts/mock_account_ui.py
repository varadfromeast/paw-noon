"""Pixel-exact preview of the M6 account UI (layout maths mirrored from Code/Runtime/Net/AccountScreens.cs).

    python3 Tools/ArtGen/accounts/mock_account_ui.py [outdir]

Renders, on an iPhone-like visible area (270 x 585 canvas px, safe insets 41 / 23), the KEEP YOUR BOUNTY sheet
(iOS: Apple + Google), the ACCOUNT sheet over Settings, the profile card, the name keypad, the delete confirm and
the title's top bar, at 1x and 3x. Used to review the art and spacing without Unity; Unity remains the truth.
"""
import json, os, sys
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
ART = os.path.join(ROOT, "Assets", "PawNoon", "Resources", "Art")
UI = os.path.join(ART, "UI")
MANIFEST = json.load(open(os.path.join(UI, "ui.json")))

# device: visible rect and safe rect in canvas px (x, y, w, h)
VIS = (45, 27, 270, 585)
SAFE = (45, 27 + 41, 270, 585 - 41 - 23)

def hexc(h, a=255):
    h = h.lstrip("#"); return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)

PAL = dict(Ink="#1A1020", Ink2="#2D1B2E", Cream0="#FFF8EA", Cream1="#F1E2C6", Sand0="#FBE3AE", Sand3="#C38E50",
           Wood3="#6E4428", Wood4="#4A2C1C", Brass0="#FFEB99", PaintRed="#B2433A", Sun1="#FFF2A8", SigRed="#EE4B3E",
           SigGreen="#6BDB5E", White="#FFFFFF", Green2="#577E3C")
C = {k: hexc(v) for k, v in PAL.items()}

class Font:
    def __init__(self, name):
        self.meta = json.load(open(os.path.join(ART, "Fonts", name + ".json")))
        self.img = Image.open(os.path.join(ART, "Fonts", name + ".png")).convert("RGBA")
        self.line = self.meta.get("lineHeight", 8)
        self.g = self.meta["glyphs"]
        self.digit = self.meta.get("digitAdv", max(self.g[d]["adv"] for d in "0123456789"))
    def glyph(self, c):
        if c in self.g: return self.g[c]
        alt = c.lower() if c.isupper() else c.upper()
        return self.g.get(alt, {"x": 0, "y": 0, "w": 0, "h": 0, "ox": 0, "oy": 0, "adv": max(2, self.line // 2)})
    def adv(self, c, mono=False):
        g = self.glyph(c)
        if mono and c.isdigit(): return self.digit
        return g.get("adv", g["w"] + 1)
    def width(self, s, mono=False):
        return max(0, sum(self.adv(c, mono) for c in s) - 1)

SMALL, MEDIUM = Font("font_small"), Font("font_medium")

def text(cv, s, font, x, y, col, align="left", outline=C["Ink"], shadow=C["Ink2"], plain=False, mono=False):
    w = font.width(s, mono)
    start = x if align == "left" else (x - w // 2 if align == "center" else x - w)
    layers = []
    if plain:
        pass
    else:
        layers += [((dx, 2), shadow) for dx in (-1, 0, 1)] + [((dx, dy), outline) for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1))]
    layers.append(((0, 0), col))
    for (ox, oy), colr in layers:
        pen = start
        for c in s:
            g = font.glyph(c)
            shift = (font.digit - g.get("adv", g["w"] + 1)) // 2 if (mono and c.isdigit()) else 0
            if g["w"] > 0 and g["h"] > 0:
                gl = font.img.crop((g["x"], g["y"], g["x"] + g["w"], g["y"] + g["h"]))
                tint = Image.new("RGBA", gl.size, colr)
                tint.putalpha(gl.getchannel("A"))
                cv.alpha_composite(tint, (pen + shift + g.get("ox", 0) + ox, y + g.get("oy", 0) + oy))
            pen += font.adv(c, mono)
    return w

def ui(name):
    return Image.open(os.path.join(UI, name + ".png")).convert("RGBA")

def nine(cv, name, r):
    m = MANIFEST[name]; img = ui(name)
    l, t, rr, b = m.get("border", [0, 0, 0, 0]); x, y, w, h = r
    W, H = img.size
    out = Image.new("RGBA", (w, h))
    xs = [(0, l, 0, l), (l, W - rr, l, w - rr), (W - rr, W, w - rr, w)]
    ys = [(0, t, 0, t), (t, H - b, t, h - b), (H - b, H, h - b, h)]
    for sx0, sx1, dx0, dx1 in xs:
        for sy0, sy1, dy0, dy1 in ys:
            if dx1 > dx0 and dy1 > dy0 and sx1 > sx0 and sy1 > sy0:
                out.paste(img.crop((sx0, sy0, sx1, sy1)).resize((dx1 - dx0, dy1 - dy0), Image.NEAREST), (dx0, dy0))
    cv.alpha_composite(out, (x, y))

def icon(cv, name, cx, cy):
    im = ui(name); cv.alpha_composite(im, (cx - im.width // 2, cy - im.height // 2))

def fill(cv, r, col):
    x, y, w, h = r
    cv.alpha_composite(Image.new("RGBA", (w, h), col), (x, y))

def dim(cv):
    fill(cv, VIS, (0x1A, 0x10, 0x20, 150))

def button(cv, r, label, up="btn_large"):
    nine(cv, up, r)
    co = MANIFEST[up].get("contentOffset", [0, 0])
    x, y, w, h = r
    cy = y + h // 2 + co[1]
    text(cv, label, MEDIUM, x + w // 2 + co[0], cy - MEDIUM.line // 2, C["Cream0"], "center")

def brand(cv, r, dark, ico, label):
    skin = "btn_black" if dark else "btn_white"
    nine(cv, skin, r)
    co = MANIFEST[skin]["contentOffset"]
    x, y, w, h = r
    cy = y + h // 2 + co[1]
    lw = MEDIUM.width(label)
    x0 = x + (w - (12 + 6 + lw)) // 2
    icon(cv, ico, x0 + 6, cy)
    if dark: text(cv, label, MEDIUM, x0 + 18, cy - MEDIUM.line // 2, C["White"])
    else: text(cv, label, MEDIUM, x0 + 18, cy - MEDIUM.line // 2, C["Ink2"], plain=True)

def icon_button(cv, r, ico):
    nine(cv, "btn_icon", r) if "border" in MANIFEST["btn_icon"] else cv.alpha_composite(ui("btn_icon").resize((r[2], r[3]), Image.NEAREST), (r[0], r[1]))
    co = MANIFEST["btn_icon"].get("contentOffset", [0, 0])
    icon(cv, ico, r[0] + r[2] // 2 + co[0], r[1] + r[3] // 2 + co[1])

def thumb_top(h):
    return max(SAFE[1] + 8, SAFE[1] + SAFE[3] - 12 - h)

def center_top(h):
    y = max(SAFE[1] + 8, VIS[1] + (VIS[3] - h) // 2 - 10)
    if y + h > SAFE[1] + SAFE[3] - 4: y = max(SAFE[1] + 2, SAFE[1] + SAFE[3] - 4 - h)
    return y

CX = VIS[0] + VIS[2] // 2

def backdrop():
    cv = Image.open(os.path.join(ART, "Environment", "plate.png")).convert("RGBA")
    return cv

def title_bar(cv):
    sx, sy, sw, sh = SAFE
    logo = ui("logo_v3") if os.path.exists(os.path.join(UI, "logo_v3.png")) else ui("logo")
    cv.alpha_composite(logo, (CX - logo.width // 2, max(140, sy + 40) - logo.height // 2 + 0))
    icon_button(cv, (sx + sw - 4 - 30, sy + 2, 30, 30), "ico_gear")
    icon_button(cv, (sx + sw - 4 - 30 - 4 - 30, sy + 2, 30, 30), "ico_profile")

# ------------------------------------------------------------------------------------------------ sheet

def sheet(cv, title, big, l1, l2, apple, google, delete, close, status=None, status_col=None, with_dim=True):
    BtnH = 30
    h = 12 + MEDIUM.line
    if big: h += 7 + MEDIUM.line
    if l1: h += 7 + SMALL.line
    if l2: h += 3 + SMALL.line
    h += 10
    if apple: h += BtnH + 6
    if google: h += BtnH + 6
    h += SMALL.line + 8
    if delete: h += BtnH + 6
    h += BtnH + 12
    w = min(216, VIS[2] - 16); x = CX - w // 2; y = thumb_top(h)
    if with_dim: dim(cv)
    nine(cv, "panel_wood", (x, y, w, h))
    cx = x + w // 2; cy = y + 12
    text(cv, title, MEDIUM, cx, cy, C["Brass0"], "center"); cy += MEDIUM.line
    if big: cy += 7; text(cv, big, MEDIUM, cx, cy, C["Sun1"], "center"); cy += MEDIUM.line
    if l1: cy += 7; text(cv, l1, SMALL, cx, cy, C["Cream1"], "center"); cy += SMALL.line
    if l2: cy += 3; text(cv, l2, SMALL, cx, cy, C["Cream1"], "center"); cy += SMALL.line
    cy += 10; bw = w - 28
    if apple: brand(cv, (cx - bw // 2, cy, bw, BtnH), True, "ico_apple", "SIGN IN WITH APPLE"); cy += BtnH + 6
    if google: brand(cv, (cx - bw // 2, cy, bw, BtnH), False, "ico_google", "SIGN IN WITH GOOGLE"); cy += BtnH + 6
    cy += 2
    if status: text(cv, status, SMALL, cx, cy, status_col or C["Sand0"], "center")
    cy += SMALL.line + 6
    if delete: button(cv, (cx - bw // 2, cy, bw, BtnH), "DELETE ACCOUNT", "btn_large"); cy += BtnH + 6
    button(cv, (cx - bw // 2, cy, bw, BtnH), close, "btn_large_alt")
    return (x, y, w, h)

# ------------------------------------------------------------------------------------------------ profile

def profile(cv, name, sub, vals, status=""):
    Rows, RowH = 5, 14
    h = 66 + Rows * RowH + 4 + SMALL.line + 6 + 26 + 10
    w = min(216, VIS[2] - 16); x = CX - w // 2; y = center_top(h)
    dim(cv)
    nine(cv, "panel_paper", (x, y, w, h))
    cx = x + w // 2
    icon_button(cv, (x + 6, y + 6, 26, 26), "ico_back")
    text(cv, "WANTED", MEDIUM, cx, y + 14, C["PaintRed"], "center", plain=True)
    text(cv, name, MEDIUM, cx, y + 32, C["Ink"], "center", plain=True)
    text(cv, sub, SMALL, cx, y + 45, C["Wood3"], "center", plain=True)
    fill(cv, (x + 14, y + 57, w - 28, 1), C["Sand3"])
    keys = ["RATING", "BEST", "DUELS", "WINS", "LIGHTNING"]
    ry = y + 64
    for i in range(Rows):
        text(cv, keys[i], MEDIUM, x + 14, ry + i * RowH, C["Wood3"], plain=True)
        text(cv, vals[i], MEDIUM, x + w - 14, ry + i * RowH, C["Ink"], "right", plain=True, mono=True)
    sy = ry + Rows * RowH + 2
    if status: text(cv, status, SMALL, cx, sy, C["Wood3"], "center", plain=True)
    by = sy + SMALL.line + 6; bw = (w - 28 - 6) // 2
    button(cv, (x + 14, by, bw, 26), "EDIT NAME", "btn_large")
    button(cv, (x + w - 14 - bw, by, bw, 26), "ACCOUNT", "btn_large_alt")

# ------------------------------------------------------------------------------------------------ keypad

def keypad(cv, typed, pressed=None, hint=None, hint_col=None):
    KeyH, Gap, FieldH, BtnH, Cols = 26, 3, 22, 30, 6
    GridH = 5 * (KeyH + Gap) + 4 + 2 * KeyH + Gap
    h = 10 + MEDIUM.line + 6 + FieldH + 4 + SMALL.line + 6 + GridH + 8 + BtnH + 12
    w = min(232, VIS[2] - 16); x = CX - w // 2; y = center_top(h)
    dim(cv)
    nine(cv, "panel_wood", (x, y, w, h))
    cx = x + w // 2; cy = y + 10
    text(cv, "YOUR NAME", MEDIUM, cx, cy, C["Brass0"], "center"); cy += MEDIUM.line + 6
    fr = (x + 12, cy, w - 24, FieldH)
    nine(cv, "panel_paper", fr)
    ty = fr[1] + (FieldH - MEDIUM.line) // 2 + 1
    tw = text(cv, typed, MEDIUM, cx, ty, C["Ink"], "center", plain=True) if typed else 0
    fill(cv, (cx + (tw + 1) // 2 + 1, ty - 1, 1, MEDIUM.line - 1), C["Ink"])
    cy += FieldH + 4
    text(cv, hint or "3-14 LETTERS, NUMBERS, SPACES", SMALL, cx, cy, hint_col or C["Sand0"], "center")
    cy += SMALL.line + 6
    kw = (w - 24 - (Cols - 1) * Gap) // Cols; gw = Cols * kw + (Cols - 1) * Gap; gx = cx - gw // 2
    labels = [chr(65 + i) for i in range(26)] + [str(i) for i in range(10)] + ["SPACE", "DEL"]
    rects = [(gx + (i % Cols) * (kw + Gap), cy + (i // Cols) * (KeyH + Gap), kw, KeyH) for i in range(26)]
    dy = cy + 5 * (KeyH + Gap) + 4
    dkw = (gw - 4 * Gap) // 5
    dx = gx + (gw - (5 * dkw + 4 * Gap)) // 2
    rects += [(dx + (d % 5) * (dkw + Gap), dy + (d // 5) * (KeyH + Gap), dkw, KeyH) for d in range(10)]
    row4 = cy + 4 * (KeyH + Gap)
    rects += [(gx + 2 * (kw + Gap), row4, 2 * kw + Gap, KeyH), (gx + 4 * (kw + Gap), row4, 2 * kw + Gap, KeyH)]
    for i, r in enumerate(rects):
        down = labels[i] == pressed
        nine(cv, "seg_on" if down else "seg_off", r)
        col = C["Brass0"] if labels[i].isdigit() else C["Cream0"]
        text(cv, labels[i], MEDIUM, r[0] + r[2] // 2, r[1] + (r[3] - MEDIUM.line) // 2 + (1 if down else 0), col, "center")
    by = dy + 2 * KeyH + Gap + 8; bw = (gw - 6) // 2
    button(cv, (gx, by, bw, BtnH), "CANCEL", "btn_large_alt")
    button(cv, (gx + gw - bw, by, bw, BtnH), "SAVE", "btn_large")

# ------------------------------------------------------------------------------------------------ confirm

def confirm(cv, title, big, l1, l2, left, right, with_dim=True):
    BtnH = 30
    h = 12 + 10 + 8 + 10 + 7 + 8 + 3 + 8 + 12 + BtnH + 12
    w = min(216, VIS[2] - 16); x = CX - w // 2; y = thumb_top(h)
    if with_dim: dim(cv)
    nine(cv, "panel_wood", (x, y, w, h))
    cx = x + w // 2
    text(cv, title, MEDIUM, cx, y + 12, C["Brass0"], "center")
    text(cv, big, MEDIUM, cx, y + 30, C["Sun1"], "center")
    if l1: text(cv, l1, SMALL, cx, y + 47, C["Cream1"], "center")
    if l2: text(cv, l2, SMALL, cx, y + 58, C["Cream1"], "center")
    by = y + h - 12 - BtnH; bw = (w - 28 - 6) // 2
    if left:
        button(cv, (x + 14, by, bw, BtnH), left, "btn_large_alt")
        button(cv, (x + w - 14 - bw, by, bw, BtnH), right, "btn_large")
    else:
        button(cv, (x + 14, by, w - 28, BtnH), right, "btn_large_alt")

# ------------------------------------------------------------------------------------------------ settings (with ACCOUNT)

def settings(cv):
    RowsTop, RowH = 36, 26
    w = min(232, VIS[2] - 16)
    h = RowsTop + 5 * RowH + 6 + 13 + 4 * 10 + 10 + 26 + 8
    x = CX - w // 2; y = center_top(h)
    dim(cv)
    nine(cv, "panel_paper", (x, y, w, h))
    icon_button(cv, (x + 6, y + 6, 26, 26), "ico_back")
    text(cv, "SETTINGS", MEDIUM, x + w // 2, y + 14, C["Ink"], "center", plain=True)
    names = ["Music", "Music style", "Sound FX", "Haptics", "Reduced Motion"]
    for i, n in enumerate(names):
        ry = y + RowsTop + i * RowH
        text(cv, n, MEDIUM, x + 30, ry + (RowH - MEDIUM.line) // 2, C["Ink"], plain=True)
    hy = y + RowsTop + 5 * RowH + 6
    text(cv, "HOW TO PLAY", MEDIUM, x + 14, hy, C["Ink"], plain=True)
    for i, l in enumerate(["3 - 2 - 1 ... DRAW!", "Swipe up from the holster.", "Faster draw = more damage.", "Early draw = jam."]):
        text(cv, l, SMALL, x + 14, hy + 13 + i * 10, C["Wood3"], plain=True)
    button(cv, (x + w // 2 - 70, hy + 13 + 40 + 6, 140, 26), "ACCOUNT", "btn_large_alt")

def frame_vis(cv):
    return cv.crop((VIS[0], VIS[1], VIS[0] + VIS[2], VIS[1] + VIS[3]))

def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "/tmp/pawnoon_m6_mock"
    os.makedirs(out, exist_ok=True)
    shots = {}

    cv = backdrop(); title_bar(cv)
    sheet(cv, "KEEP YOUR BOUNTY", "YOUR BEST: 0.198 s", "Link Apple or Google so your", "bounty follows you to any phone.", True, True, False, "LATER")
    shots["bounty_ios"] = cv

    cv = backdrop(); title_bar(cv)
    sheet(cv, "KEEP YOUR BOUNTY", "YOUR BEST: 0.198 s", "Link Apple or Google so your", "bounty follows you to any phone.", False, True, False, "LATER",
          status="WAITING FOR GOOGLE...", status_col=C["Sun1"])
    shots["bounty_android_busy"] = cv

    cv = backdrop(); title_bar(cv); settings(cv)
    sheet(cv, "ACCOUNT", "STRANGER #0042", "GUEST ON THIS PHONE", "Link to keep your bounty safe.", True, True, True, "CLOSE", with_dim=False)
    shots["account_guest"] = cv

    cv = backdrop(); title_bar(cv); settings(cv)
    sheet(cv, "ACCOUNT", "BILLY THE CAT", "LINKED WITH APPLE", "Your bounty is safe on any phone.", False, False, True, "DONE",
          status="LINKED! YOUR BOUNTY IS SAFE.", status_col=C["SigGreen"], with_dim=False)
    shots["account_linked"] = cv

    cv = backdrop(); title_bar(cv)
    profile(cv, "STRANGER #0042", "WHISKERS   GUEST", ["1234", "0.198 s", "12", "7 (58%)", "3"], "OFFLINE: LAST KNOWN")
    shots["profile"] = cv

    cv = backdrop(); title_bar(cv)
    profile(cv, "STRANGER #0042", "WHISKERS   GUEST", ["1234", "0.198 s", "12", "7 (58%)", "3"])
    keypad(cv, "BILLY THE CAT", pressed="T")
    shots["keypad"] = cv

    cv = backdrop(); title_bar(cv)
    keypad(cv, "BI", hint="AT LEAST 3 CHARACTERS", hint_col=C["SigRed"])
    shots["keypad_error"] = cv

    cv = backdrop(); title_bar(cv); settings(cv)
    sheet(cv, "ACCOUNT", "STRANGER #0042", "GUEST ON THIS PHONE", "Link to keep your bounty safe.", True, True, True, "CLOSE", with_dim=False)
    confirm(cv, "DELETE ACCOUNT?", "YOUR BOUNTY GOES TOO", "Name, rating, duels and ghosts", "are removed for good.", "KEEP IT", "DELETE")
    shots["delete1"] = cv

    cv = backdrop(); title_bar(cv)
    sheet(cv, "KEEP YOUR BOUNTY", "YOUR BEST: 0.198 s", "Link Apple or Google so your", "bounty follows you to any phone.", True, True, False, "LATER")
    confirm(cv, "ALREADY WANTED", "THAT ACCOUNT HAS A BOUNTY", "Switch to it? This guest's duels", "stay behind on the server.", "STAY GUEST", "SWITCH")
    shots["switch"] = cv

    for k, v in shots.items():
        f = frame_vis(v)
        f.save(os.path.join(out, k + ".png"))
        f.resize((f.width * 3, f.height * 3), Image.NEAREST).save(os.path.join(out, k + "_3x.png"))
    # contact sheet (1x, side by side)
    names = list(shots)
    sheet_img = Image.new("RGBA", (len(names) * (VIS[2] + 6), VIS[3]), (0, 0, 0, 255))
    for i, k in enumerate(names):
        sheet_img.alpha_composite(frame_vis(shots[k]), (i * (VIS[2] + 6), 0))
    sheet_img.save(os.path.join(out, "contact.png"))
    print("wrote", out)

if __name__ == "__main__":
    main()
