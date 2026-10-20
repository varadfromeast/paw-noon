"""M6 account UI sprites (Sign in with Apple / Google buttons, brand icons, profile icon).

Run from the repo root:  python3 Tools/ArtGen/accounts/gen_account_ui.py
Writes Assets/PawNoon/Resources/Art/UI/{btn_black,btn_black_pressed,btn_white,btn_white_pressed,
ico_apple,ico_google,ico_profile}.png and their ui.json entries, plus a 6x preview in /tmp.

The buttons are the btn_large / btn_large_pressed silhouettes (same 9-slice borders, same 3 px bottom lip),
recoloured: Apple asks for a black button with a white logo, Google for a light neutral one with the
four-colour G. The G uses Google's brand colours (outside the game palette on purpose: brand rule).
"""
import json, os, sys
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
UI = os.path.join(ROOT, "Assets", "PawNoon", "Resources", "Art", "UI")

def hexc(h, a=255):
    h = h.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)

# template classes in btn_large: a outline, b top highlight, c face, d corner dots / lip edge, e lip
SRC = {"#2d1b2e": "a", "#f2674e": "b", "#b2433a": "c", "#a3262a": "d", "#7e2c2b": "e"}
SKINS = {
    # black (Apple): ink outline, charcoal highlight, near-black face, ink lip
    "btn_black": {"a": "#1A1020", "b": "#4E4D60", "c": "#342235", "d": "#241826", "e": "#241826"},
    # light (Google neutral): ink2 outline, white rim, steel0 face, steel lip
    "btn_white": {"a": "#2D1B2E", "b": "#FFFFFF", "c": "#F2F6F8", "d": "#BCC7CF", "e": "#7E8C98"},
}

def recolour(src_name, skin):
    im = Image.open(os.path.join(UI, src_name + ".png")).convert("RGBA")
    out = Image.new("RGBA", im.size, (0, 0, 0, 0))
    for y in range(im.height):
        for x in range(im.width):
            p = im.getpixel((x, y))
            if p[3] == 0:
                continue
            k = SRC.get("#%02x%02x%02x" % p[:3])
            if k is None:
                sys.exit("unexpected colour %s in %s" % (p, src_name))
            out.putpixel((x, y), hexc(skin[k]))
    return out

def from_rows(rows, colours):
    h = len(rows); w = len(rows[0])
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    for y, r in enumerate(rows):
        assert len(r) == w, (y, r)
        for x, ch in enumerate(r):
            if ch != ".":
                im.putpixel((x, y), hexc(colours[ch]))
    return im

# 12 x 12, white on the black button (no outline: the button is the outline)
APPLE = [
    ".......ww...",
    "......www...",
    "......ww....",
    "..www..www..",
    ".wwwwwwwwwww",
    "wwwwwwwwww..",
    "wwwwwwwww...",
    "wwwwwwwww...",
    "wwwwwwwwww..",
    ".wwwwwwwwwww",
    ".wwwwwwwwww.",
    "..www..www..",
]

# 12 x 12 four-colour G: red top, yellow left, green bottom, blue right + bar
GOOGLE = [
    "....rrrr....",
    "..rrrrrrrr..",
    ".rrr....rrr.",
    ".yy.......r.",
    "yy..........",
    "yy....bbbbbb",
    "yy....bbbbbb",
    "yy........bb",
    ".gg......bb.",
    ".ggg....ggb.",
    "..gggggggg..",
    "....gggg....",
]

# 12 x 12 cat head in the ico_* style (cream fill, ink2 outline) for the profile button on the title
PROFILE = [
    ".a........a.",
    "aba......aba",
    "abba....abba",
    "abbbaaaabbba",
    "abbbbbbbbbba",
    "abaabbbbaaba",
    "abaabbbbaaba",
    "abbbbbbbbbba",
    "abbbbaabbbba",
    ".abbbbbbbba.",
    "..aabbbbaa..",
    "....aaaa....",
]

def main():
    sprites = {}
    for name, skin in SKINS.items():
        recolour("btn_large", skin).save(os.path.join(UI, name + ".png"))
        recolour("btn_large_pressed", skin).save(os.path.join(UI, name + "_pressed.png"))
        sprites[name] = {"file": name, "size": [32, 32], "pivot": [0, 0], "border": [8, 8, 8, 11], "contentOffset": [0, -1]}
        sprites[name + "_pressed"] = {"file": name + "_pressed", "size": [32, 32], "pivot": [0, 0], "border": [8, 8, 8, 11], "contentOffset": [0, 1]}
    icons = {
        "ico_apple": from_rows(APPLE, {"w": "#FFFFFF"}),
        "ico_google": from_rows(GOOGLE, {"r": "#EA4335", "y": "#FBBC05", "g": "#34A853", "b": "#4285F4"}),
        "ico_profile": from_rows(PROFILE, {"a": "#2D1B2E", "b": "#FFF8EA"}),
    }
    for name, im in icons.items():
        im.save(os.path.join(UI, name + ".png"))
        sprites[name] = {"file": name, "size": [12, 12], "pivot": [6, 6]}

    path = os.path.join(UI, "ui.json")
    with open(path) as f:
        manifest = json.load(f)
    if "sprites" in manifest:
        sys.exit("ui.json with a 'sprites' wrapper: extend this script")
    # insert each entry before the first existing key that sorts after it (keeps the diff away from the end of
    # the file, where other teams append), byte-compatible with the original json.dump(indent=1) formatting
    merged = {}
    pending = sorted(k for k in sprites if k not in manifest)
    for k, v in manifest.items():
        while pending and pending[0] < k:
            n = pending.pop(0)
            merged[n] = sprites[n]
        merged[k] = sprites.get(k, v)
    for n in pending:
        merged[n] = sprites[n]
    with open(path, "w") as f:
        f.write(json.dumps(merged, indent=1))
    print("wrote", ", ".join(sorted(sprites)))

if __name__ == "__main__":
    main()
