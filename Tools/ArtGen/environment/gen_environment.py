#!/usr/bin/env python3
"""PAW NOON environment generator.  Re-run to regenerate every file in
Assets/PawNoon/Resources/Art/Environment/ exactly (deterministic), plus previews in work/env/.

    python3 project/Tools/ArtGen/environment/gen_environment.py
"""
import json, os, sys
_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _HERE)
from PIL import Image, ImageDraw
from envlib import W, H, proj, check_palette, upscale, save_png, PA
from layers_back import make_sky, make_sun, make_mesas_far, make_mesas_near, make_town_far
from layer_ground import make_ground
from layers_buildings import (make_buildings_left, make_buildings_right, XFL, XFR, PR,
                              GUNS_BRACKET_Z, GUNS_BRACKET_Y)
from layers_back import SUN
from sprites import (make_foreground, make_clouds, make_lantern, make_sign_swing,
                     make_tumbleweed, make_vulture, make_flag)

ROOT = os.path.abspath(os.path.join(_HERE, '..', '..', '..'))
OUT = os.path.join(ROOT, 'Assets', 'PawNoon', 'Resources', 'Art', 'Environment')
WORK = os.path.abspath(os.path.join(ROOT, '..', 'work', 'env'))

HAZE_OK = {'sun'}       # layers allowed partial alpha


def r(v):
    return int(round(v))


def main():
    os.makedirs(OUT, exist_ok=True); os.makedirs(WORK, exist_ok=True)
    bl = make_buildings_left()
    br, _ = make_buildings_right()
    layers = [
        ('sky', make_sky(), 0, False),
        ('sun', make_sun(), 5, False),
        ('mesas_far', make_mesas_far(), 20, True),
        ('mesas_near', make_mesas_near(), 30, True),
        ('town_far', make_town_far(), 40, True),
        ('ground', make_ground(), 50, False),
        ('buildings_left', bl, 60, False),
        ('buildings_right', br, 60, False),
        ('foreground', make_foreground(), 85, False),
    ]
    imgs = {}
    for name, cv, order, heat in layers:
        im = cv.image()
        check_palette(im, allow_partial_alpha=name in HAZE_OK, name=name)
        save_png(im, os.path.join(OUT, name + '.png'))
        imgs[name] = im

    clouds = make_clouds()
    for n, cv in clouds.items():
        im = cv.image(); check_palette(im, name=n); save_png(im, os.path.join(OUT, n + '.png'))

    sheets_img = {
        'lantern': make_lantern(), 'sign_swing': make_sign_swing(), 'tumbleweed': make_tumbleweed(),
        'vulture': make_vulture(), 'flag': make_flag(),
    }
    for n, im in sheets_img.items():
        check_palette(im, name=n); save_png(im, os.path.join(OUT, n + '.png'))

    sheets = {
        'lantern':    {'file': 'lantern', 'cell': [9, 15], 'frames': 4, 'fps': 6, 'pivot': [4, 0]},
        'sign_swing': {'file': 'sign_swing', 'cell': [60, 36], 'frames': 6, 'fps': 5, 'pivot': [30, 0]},
        'tumbleweed': {'file': 'tumbleweed', 'cell': [28, 28], 'frames': 8, 'fps': 12, 'pivot': [14, 27]},
        'vulture':    {'file': 'vulture', 'cell': [15, 8], 'frames': 4, 'fps': 5, 'pivot': [7, 4]},
        'flag':       {'file': 'flag', 'cell': [12, 8], 'frames': 4, 'fps': 6, 'pivot': [0, 7]},
    }

    # ------------------------------------------------------------- prop placements (pivot coords)
    props = []
    for (X, Z, ph) in ((-XFL + 0.35, 6.3, 0.0), (-XFL + 0.35, 10.0, 0.5)):
        x, y = proj(X, PR, Z); props.append({'sheet': 'lantern', 'x': r(x), 'y': r(y) + 1, 'order': 62, 'phase': ph})
    for (X, Z, ph) in ((XFR - 0.35, 6.2, 0.25), (XFR - 0.35, 8.2, 0.75)):
        x, y = proj(X, PR, Z); props.append({'sheet': 'lantern', 'x': r(x), 'y': r(y) + 1, 'order': 62, 'phase': ph})
    xa, ya = proj(XFR, GUNS_BRACKET_Y, GUNS_BRACKET_Z); xb, _ = proj(0.85, GUNS_BRACKET_Y, GUNS_BRACKET_Z)
    props.append({'sheet': 'sign_swing', 'x': r((xa + xb) / 2), 'y': r(ya) + 1, 'order': 64, 'phase': 0.0})
    fx, fy = proj(XFR + 0.15, 6.6, 7.5)
    props.append({'sheet': 'flag', 'x': r(fx) + 2, 'y': r(fy) - 1, 'order': 63, 'phase': 0.0})

    cloud_place = [
        {'file': 'cloud_a', 'x': 104, 'y': 134, 'speed': 1.0, 'order': 10},
        {'file': 'cloud_b', 'x': 6, 'y': 52, 'speed': 1.4, 'order': 10},
        {'file': 'cloud_c', 'x': 236, 'y': 58, 'speed': 1.8, 'order': 10},
        {'file': 'cloud_d', 'x': 168, 'y': 88, 'speed': 2.4, 'order': 10},
        {'file': 'cloud_c', 'x': 300, 'y': 196, 'speed': 2.0, 'order': 10},
    ]
    env = {
        'canvas': [W, H],
        'layers': [{'name': n, 'file': n, 'order': o, 'heat': h} for n, _, o, h in layers],
        'sheets': sheets,
        'props': props,
        'clouds': cloud_place,
        'tumbleweed': {'sheet': 'tumbleweed', 'laneY': [452, 500], 'order': 78},
        'vulture': {'sheet': 'vulture', 'center': [176, 112], 'radius': [52, 14], 'order': 12},
        'anchors': {'banditFeet': [200, 432], 'heroBottomLeft': [14, 640], 'vanishingPoint': [196, 296],
                    'sun': list(SUN)},
        'notes': {
            'propXY': 'canvas coords (top-left origin, y down) where the sheet pivot is placed',
            'clouds': 'x,y = cloud sprite top-left; drift right at speed px/s and wrap across 288',
            'sun': 'sun layer uses alpha steps (25/50/75%) for the glow; may pulse',
        },
    }
    with open(os.path.join(OUT, 'environment.json'), 'w') as f:
        json.dump(env, f, indent=1)

    # ------------------------------------------------------------- composite preview
    def cell(name, i=0):
        im = sheets_img[name]; cw, ch = sheets[name]['cell']
        return im.crop((i * cw, 0, (i + 1) * cw, ch))

    scene = Image.new('RGBA', (W, H), (0, 0, 0, 255))
    draw_list = []
    for n, _, o, _h in layers:
        draw_list.append((o, 0, imgs[n], (0, 0)))
    for c in cloud_place:
        draw_list.append((c['order'], 1, clouds[c['file']].image(), (c['x'], c['y'])))
    for p in props:
        sh = sheets[p['sheet']]
        draw_list.append((p['order'], 2, cell(p['sheet']), (p['x'] - sh['pivot'][0], p['y'] - sh['pivot'][1])))
    draw_list.append((12, 3, cell('vulture', 0), (176 + 40 - 7, 112 - 8 - 4)))
    draw_list.append((78, 3, cell('tumbleweed', 1), (292 - 14, 500 - 27)))
    draw_list.sort(key=lambda t: (t[0], t[1]))
    for o, _, im, (x, y) in draw_list:
        if (x, y) == (0, 0) and im.size == (W, H):
            scene.alpha_composite(im)
        else:
            scene.alpha_composite(im, (x, y))
    save_png(scene, os.path.join(OUT, 'preview_scene.png'))
    save_png(upscale(scene, 4), os.path.join(WORK, 'preview_scene_4x.png'))
    scene.crop((45, 27, 315, 612)).save(os.path.join(WORK, 'crop_iphone.png'))
    scene.crop((45, 80, 315, 560)).save(os.path.join(WORK, 'crop_iphone_se.png'))

    # style test: scene + teammates' characters at their pivots (dark placeholders if missing)
    st = scene.copy()
    base = os.path.abspath(os.path.join(WORK, '..'))
    bpath = os.path.join(base, 'bandit', 'v2_ready.png'); hpath = os.path.join(base, 'hero', 'v2_aim.png')
    if os.path.exists(bpath):
        b = Image.open(bpath).convert('RGBA').crop((0, 0, 128, 128)); st.alpha_composite(b, (200 - 64, 432 - 124))
    else:
        d = ImageDraw.Draw(st); ink = PA['ink2']
        d.ellipse((168, 426, 232, 438), fill=PA['sand3'])
        d.rectangle((182, 372, 218, 430), fill=ink); d.ellipse((182, 340, 218, 376), fill=ink)
        d.rectangle((168, 338, 232, 342), fill=ink); d.rectangle((186, 318, 214, 340), fill=ink)
    if os.path.exists(hpath):
        h = Image.open(hpath).convert('RGBA').crop((0, 0, 200, 300)); st.alpha_composite(h, (14, 340))
    else:
        d = ImageDraw.Draw(st)
        d.polygon([(14, 640), (14, 520), (40, 450), (80, 410), (140, 400), (175, 390), (205, 392),
                   (210, 405), (170, 430), (190, 640)], fill=PA['paintRedD'])
        d.ellipse((50, 340, 170, 420), fill=PA['wood4'])
    save_png(st, os.path.join(WORK, 'v2_styletest.png'))
    st.resize((W // 2, H // 2), Image.NEAREST).save(os.path.join(WORK, 'v2_styletest_half.png'))
    print('ok', len(os.listdir(OUT)), 'files in', OUT)


if __name__ == '__main__':
    main()
