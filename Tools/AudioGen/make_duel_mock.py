"""make_duel_mock.py - 14 s preview mix of one duel round (score v3, clean-production pass) from the
delivered assets.

    python3 Tools/AudioGen/make_duel_mock.py [out.mp3]   (default ../work/share/paw_noon_duel_v4.mp3)

Timeline: composed bed music_duel_a 0-4.5 s (crossfade out from 3.0 s); church tolls 3/2/1 at
1.4/2.1/2.8 s, each with sfx_tick_sub under it; at 3.0 s crossfade into music_duel_hold (ducked)
+ heartbeat; DRAW ting at 6.2 s (hold + heartbeat cut), holster whip +0.12 s, hero shot +0.23 s,
bandit shot +0.30 s, canyon echoes ring out; music_duel_result tail from 7.4 s. Faint duel wind
under everything, as in game.
"""
import os
import subprocess
import sys
import tempfile

import numpy as np
from scipy.io import wavfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from pn_dsp import SR, ns, tt, decode, read_wav, pan, soft_limit_tp, lufs, db2a, peak  # noqa

AUDIO = os.path.join(HERE, '..', '..', 'Assets', 'PawNoon', 'Resources', 'Audio')
DUR = 14.0
TICKS = (1.4, 2.1, 2.8)
HOLD = 3.0
DRAW = 6.2
RESULT = DRAW + 1.2


def sfx(name):
    return read_wav(os.path.join(AUDIO, 'SFX', name + '.wav'))[1]


def clip(sub, name):
    return decode(os.path.join(AUDIO, sub, name + '.ogg'))[1]


def looped(x, start, n):
    idx = (np.arange(n) + ns(start)) % x.shape[1]
    return x[:, idx]


def main(out):
    n = ns(DUR)
    t = tt(n)
    mix = np.zeros((2, n))

    def put(x, t0, g, p=0.0):
        a = ns(t0)
        k = min(x.shape[-1], n - a)
        mix[:, a:a + k] += (pan(x[:k], p) if x.ndim == 1 else x[:, :k]) * g

    mix += looped(clip('Loops', 'amb_duel'), 44.0, n) * 0.35
    bed_g = np.interp(t, [0, HOLD, HOLD + 1.5, DUR], [0.8, 0.8, 0.0, 0.0])
    mix += pad(clip('Music', 'music_duel_a'), n) * bed_g
    hold = np.zeros((2, n))
    a = ns(HOLD)
    hold[:, a:] = looped(clip('Music', 'music_duel_hold'), 0.0, n - a)
    hold_g = np.interp(t, [0, HOLD, HOLD + 1.0, DRAW - 0.02, DRAW + 0.05, DUR], [0, 0, 0.6, 0.6, 0, 0])
    mix += hold * hold_g
    hb = np.zeros((2, n))
    hb[:, a:] = looped(clip('Loops', 'heartbeat'), 0.0, n - a)
    hb_g = np.interp(t, [0, HOLD, HOLD + 0.6, DRAW - 0.02, DRAW + 0.03, DUR], [0, 0, 0.6, 0.6, 0, 0])
    mix += hb * hb_g
    for t0, (name, g) in zip(TICKS, [('sfx_tick_3', 0.55), ('sfx_tick_2', 0.66), ('sfx_tick_1', 0.8)]):
        put(sfx(name), t0, g)
        put(sfx('sfx_tick_sub'), t0, 0.55)
    put(sfx('sfx_draw'), DRAW, 0.8)
    put(sfx('sfx_holster_draw'), DRAW + 0.12, 0.45, -0.25)
    put(sfx('sfx_gun_hero_1'), DRAW + 0.23, 1.1, -0.15)
    put(sfx('sfx_gun_bandit_2'), DRAW + 0.30, 0.8, 0.2)
    put(clip('Music', 'music_duel_result'), RESULT, 0.75)
    mix *= db2a(-14.0 - lufs(mix))
    mix = soft_limit_tp(mix, -1.5)          # clean true-peak limiter (was a fast sample-peak limiter)
    k = ns(0.4)
    mix[:, -k:] *= np.linspace(1, 0, k)
    mix[:, :ns(0.02)] *= np.linspace(0, 1, ns(0.02))
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with tempfile.TemporaryDirectory() as td:
        w = os.path.join(td, 'mock.wav')
        wavfile.write(w, SR, mix.T.astype(np.float32))
        subprocess.run(['ffmpeg', '-nostdin', '-y', '-v', 'error', '-i', w, '-c:a', 'libmp3lame', '-b:a', '192k',
                        '-map_metadata', '-1', out], check=True)
    print(out, f'lufs={lufs(mix):.1f} peak={20*np.log10(peak(mix)):.1f}dB')
    return mix


def pad(x, n):
    y = np.zeros((2, n))
    k = min(n, x.shape[1])
    y[:, :k] = x[:, :k]
    return y


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else
         os.path.normpath(os.path.join(HERE, '..', '..', '..', 'work', 'share', 'paw_noon_duel_v4.mp3')))
