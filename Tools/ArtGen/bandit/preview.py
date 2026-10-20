"""Preview sheets / GIFs / scene composite for Pawtruth (scratch output only)."""
import os
from PIL import Image
from bkit import PA, upscale, compose, Part

SHADOW = Part("""
..KKKKKKKKKKKKKKKKK..
KKKKKKKKKKKKKKKKKKKKK
..KKKKKKKKKKKKKKKKK..
""", name='shadow', no_outline=True)


def _bg(f, c='sand0'):
    b = Image.new('RGBA', f.size, PA[c]); b.alpha_composite(f); return b


def make(allframes, calm, hurt, work, out):
    os.makedirs(work, exist_ok=True)
    rows = []
    for name, (frames, fps, muz) in allframes.items():
        k = 4
        sh = Image.new('RGBA', (len(frames) * 64 * k, 64 * k + 64 + 4), PA['sand1'])
        for i, f in enumerate(frames):
            b = _bg(f)
            if muz[i] != [-1, -1]:
                b.putpixel(tuple(muz[i]), PA['sigGreen'])
            sh.paste(upscale(b, k), (i * 64 * k, 0)); sh.paste(_bg(f), (i * 64 * k, 64 * k + 2))
        sh.save(os.path.join(work, f'sheet_{name}.png'))
        ims = [upscale(_bg(f), 4).convert('RGB') for f in frames]
        ims[0].save(os.path.join(work, f'anim_{name}.gif'), save_all=True, append_images=ims[1:],
                    duration=int(1000 / fps), loop=0)
    p = Image.new('RGBA', (28 * 8 * 2 + 8, 28 * 8), PA['sand1'])
    p.paste(upscale(_bg(calm, 'shadowPurp'), 8), (0, 0)); p.paste(upscale(_bg(hurt, 'shadowPurp'), 8), (28 * 8 + 8, 0))
    p.save(os.path.join(work, 'portraits_8x.png'))
    # scene composite
    scene_p = os.path.join(out, '..', '..', 'Environment', 'preview_scene.png')
    picks = [('idle', 0), ('ready', 1), ('draw_fire', 2), ('hit', 1), ('down', 7), ('win', 5)]
    shots = []
    for name, i in picks:
        if os.path.exists(scene_p):
            sc = Image.open(scene_p).convert('RGBA')
        else:
            sc = Image.new('RGBA', (288, 512), PA['sky3'])
            sc.paste(Image.new('RGBA', (288, 250), PA['sand0']), (0, 262))
        sha = compose([(SHADOW, 0, 0)], 21, 3)
        sha2 = Image.new('RGBA', sha.size, (0, 0, 0, 0))
        sha2 = Image.blend(sha2, sha, 0.35)
        sc.alpha_composite(sha2, (152 - 10, 299 - 1))
        f = allframes[name][0][i]
        sc.alpha_composite(f, (152 - 32, 300 - 61))
        shots.append(sc.crop((36, 22, 252, 490)))
    W = Image.new('RGBA', (216 * 3 * 2 + 20, 468 * 2 * 2 + 10), PA['ink'])
    for j, s in enumerate(shots):
        W.paste(upscale(s, 2), ((j % 3) * (432 + 10), (j // 3) * (936 + 10)))
    W.save(os.path.join(work, 'scene_check.png'))
    z = shots[0].crop((92, 190, 172, 300))
    upscale(z, 4).save(os.path.join(work, 'scene_zoom.png'))
