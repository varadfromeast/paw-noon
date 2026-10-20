"""gen_peacemaker.py - the Peacemaker's sounds (SPEC_V2 §6, M7). Same conventions as gen_sfx.py:
48 kHz 16-bit mono WAV, onset at sample 0, peak -1 dBFS.

    python3 Tools/AudioGen/gen_peacemaker.py

Writes Resources/Audio/SFX/
  sfx_peacemaker_draw.wav     heavier draw: a lower, longer leather pull than sfx_holster_draw, then the Colt
                              Single Action's four-click hammer (click-click-click-CLACK) and a faint steel ring.
                              ~0.3 s, the first transient still lands at sample 0 so the draw feels instant.
  sfx_gun_peacemaker_1..3.wav heavier gunshot: the hero's canyon shot repitched ~10 % down, a deeper sub thump,
                              a bright "crack" layer and a short high ring-out (the blue-white flash in sound).
Sources: the CC0 Revolver1-3 recordings in source/ (Resources/Audio/NOTICE.txt), like the standard shots.
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from pn_dsp import *                       # noqa
from gen_sfx import (OUT, LOUD_MID, canyon, load_src, metal_click, mix, noise_burst, repitch,  # noqa
                     thump)


def sfx_peacemaker_draw(seed=172):
    r = rng(seed)
    n = ns(0.34)
    t = tt(n)
    # leather pull: same gesture as sfx_holster_draw, a fifth lower and 40 % longer, with a creak grain
    fc = 480 * (3000 / 480) ** np.clip(t / 0.12, 0, 1)
    env = np.minimum(1, t / 0.004) * np.exp(-((t - 0.06) / 0.048) ** 2) + 0.45 * np.exp(-t / 0.012)
    grain = 1 + 0.6 * lp_noise(n, 90, r)
    pull = tv_biquad(r.standard_normal(n), 'bp', fc, 1.1) * env * grain
    # SAA hammer: three light sear clicks and the full-cock clack, plus a dull body knock
    clicks = mix(n,
                 (0.130, metal_click(r, 0.02, (3600, 5600, 8100), 0.003), 0.35),
                 (0.158, metal_click(r, 0.02, (3500, 5400, 7900), 0.003), 0.35),
                 (0.186, metal_click(r, 0.02, (3700, 5700, 8300), 0.003), 0.40),
                 (0.226, metal_click(r, 0.05, (2500, 3900, 5600, 7300), 0.008), 1.0),
                 (0.226, thump(r, 210, 0.08, 0.02, 0.4), 0.35))
    # steel ring: two inharmonic partials, quiet, decaying with the clack
    k = ns(0.226)
    rt = tt(n - k)
    ring = np.zeros(n)
    ring[k:] = (np.sin(2 * np.pi * 2870 * rt) + 0.6 * np.sin(2 * np.pi * 4410 * rt)) * np.exp(-rt / 0.07)
    y = mix(n, (0, pull, 1.0), (0.0, noise_burst(r, 0.04, 220, 2000, 0.008), 0.5), (0, clicks, 0.32), (0, ring, 0.05))
    return finalize_sfx(y, loud_target=LOUD_MID + 0.5, fade_ms=40)


def gun_peacemaker(src, ratio, sub_f, seed):
    r = rng(seed)
    x = load_src(src)
    x = x[int(np.argmax(np.abs(x) > 0.05 * peak(x))):]
    x = repitch(x, ratio)[:ns(1.1)]
    t = tt(len(x))
    x = eq(x, [('lowshelf', 120, 0.7, 6.0), ('peak', 2400, 1.0, 2.0), ('peak', 480, 1.0, -2.5)])
    x *= 1 + 1.2 * np.exp(-t / 0.009)
    sub = sat(thump(r, sub_f, 1.1, 0.12, 1.4, 0.025) * 2, 1.6)
    crack = noise_burst(r, 0.05, 2500, 12000, 0.004)
    rn = len(x)
    rt = tt(rn)
    ring = (np.sin(2 * np.pi * 3150 * rt) + 0.7 * np.sin(2 * np.pi * 4730 * rt + 0.4)
            + 0.4 * np.sin(2 * np.pi * 6890 * rt)) * np.exp(-rt / 0.11) * np.minimum(1, rt / 0.003)
    y = x / peak(x) + 0.7 * pad_to(sub, rn)[:rn] + 0.35 * pad_to(crack, rn) / peak(crack) + 0.045 * ring
    y *= np.where(t > 0.3, np.exp(-(t - 0.3) / 0.09), 1.0)
    y = sat(y, 1.7)
    y = canyon(y, r, far=0.0)
    return finalize_sfx(y, loud_target=-7.0, tail_db=-58, fade_ms=220, hp=32.0)


def all_sfx():
    return {
        'sfx_peacemaker_draw': sfx_peacemaker_draw,
        'sfx_gun_peacemaker_1': lambda: gun_peacemaker(1, 0.90, 47, 151),
        'sfx_gun_peacemaker_2': lambda: gun_peacemaker(3, 0.88, 45, 152),
        'sfx_gun_peacemaker_3': lambda: gun_peacemaker(2, 0.93, 49, 153),
    }


def main(names=None):
    for name, fn in all_sfx().items():
        if names and name not in names:
            continue
        x = fn()
        write_wav16(os.path.join(OUT, name + '.wav'), x)
        print(f'{name}: {len(x) / SR:.3f} s, peak {a2db(peak(x)):.2f} dBFS, short-term max {short_max(x, 0.1):.1f}, '
              f'DC {np.mean(x):.2e}')


if __name__ == '__main__':
    main(sys.argv[1:] or None)
