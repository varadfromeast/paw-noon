"""gen_voice.py - per-character "meow signature" cues (SPEC_V2 §8.2).

    python3 Tools/AudioGen/gen_voice.py [name ...]

Writes (48 kHz 16-bit mono WAV, onset at sample 0, peak -1 dBFS, ~0.35 s):
  SFX/sfx_meow_whiskers.wav   Whiskers (white cat): three bright 25 %-pulse notes B5 E6 C#6,
                              "mi-YAO-w", open formant, quick upward scoops.
  SFX/sfx_meow_pawtruth.wav   Pawtruth (black cat): three low square notes E4 A4 D4, darker
                              formant, a sub-octave pulse and a 31 Hz throat growl (AM) for rasp.

Pixel-game flavour: band-limited (polyBLEP) pulse oscillators whose pitch and duty are updated
in 60 Hz "driver frame" steps like an 8-bit sound driver, so the scoops step instead of glide
(the vowel filter moves continuously: stepping it would click).
Clean on purpose: no noise sources at all, so there is no hiss; the rasp is amplitude modulation
and a sub-octave, both pitched. Played by the game when the opponent's speech bubble pops
(PawAudio.PlayMeow, volume 0.6, pitch +-4 %).
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from pn_dsp import *      # noqa

OUT = os.path.normpath(os.path.join(HERE, '..', '..', 'Assets', 'PawNoon', 'Resources', 'Audio', 'SFX'))
FRAME_HZ = 60.0           # sound-driver tick: pitch / duty / filter update rate


def _blep(t, dt):
    """polyBLEP residual for a unit step at phase 0 (t in [0,1), dt = f / SR)."""
    y = np.zeros_like(t)
    a = t < dt
    x = t[a] / dt[a]
    y[a] = x + x - x * x - 1.0
    b = t > 1.0 - dt
    x = (t[b] - 1.0) / dt[b]
    y[b] = x * x + x + x + 1.0
    return y


def pulse(freq, duty):
    """Band-limited pulse, freq and duty per sample (arrays). Output roughly in [-1, 1], zero mean."""
    dt = np.clip(freq / SR, 1e-6, 0.45)
    ph = np.cumsum(dt) % 1.0
    naive = np.where(ph < duty, 1.0, -1.0)
    y = naive + _blep(ph, dt) - _blep((ph - duty) % 1.0, dt)
    return y - (2.0 * duty - 1.0)          # remove the duty-dependent DC


def stepped(curve):
    """Hold a per-sample control curve for one driver frame at a time (8-bit driver feel)."""
    hop = int(round(SR / FRAME_HZ))
    idx = (np.arange(len(curve)) // hop) * hop
    return curve[idx]


def note(f0, dur, duty, scoop, bend_end, vowel, rel=0.035, growl=0.0, sub=0.0):
    """One "mew": pitch scoops up (scoop = start ratio), holds, bends to bend_end at the tail.
    vowel = (closed_hz, open_hz): lowpass cutoff opens then closes (m -> ya -> w)."""
    n = ns(dur)
    t = tt(n)
    s = t / dur
    # pitch: scoop up over the first 28 %, slight sag toward the end
    up = np.clip(s / 0.28, 0, 1)
    ratio = scoop + (1.0 - scoop) * (1 - (1 - up) ** 2)
    tail = np.clip((s - 0.6) / 0.4, 0, 1)
    ratio *= 1.0 + (bend_end - 1.0) * tail ** 1.5
    f = stepped(f0 * ratio)
    d = stepped(np.full(n, duty) + 0.06 * np.sin(np.pi * s))          # duty widens mid-note (vowel opens)
    y = pulse(f, d)
    if sub > 0:
        y += sub * pulse(f * 0.5, np.full(n, 0.5))
    # vowel: closed -> open -> closed lowpass (continuous; only pitch and duty step)
    lo, hi = vowel
    cut = lo + (hi - lo) * np.sin(np.pi * np.clip(s * 1.15, 0, 1)) ** 0.8
    y = tv_biquad(y, 'lp', cut, 0.9, hop=16)          # smooth: stepping the filter would click
    y = tv_biquad(y, 'lp', cut * 1.6, 0.6, hop=16)
    if growl > 0:
        # throat rasp: pitched AM at ~31 Hz (pure, no noise), strongest in the open middle
        g = 0.5 + 0.5 * np.sin(2 * np.pi * 31.0 * t)
        y *= 1.0 - growl * g * np.sin(np.pi * s) ** 0.5
    env = np.sin(0.5 * np.pi * np.minimum(1.0, t / 0.008)) ** 2 * np.where(t > dur - rel, np.clip((dur - t) / rel, 0, 1) ** 1.5, 1.0)
    return y * env


def place(n, parts):
    y = np.zeros(n)
    for t0, x, g in parts:
        a = ns(t0)
        k = min(len(x), n - a)
        y[a:a + k] += g * x[:k]
    return y


def meow_whiskers():
    m = mtof
    a = note(m(83), 0.085, 0.25, 0.86, 0.98, (2200, 5200))
    b = note(m(88), 0.095, 0.25, 0.80, 0.97, (2600, 6400))
    c = note(m(85), 0.150, 0.22, 0.90, 0.88, (2400, 5600), rel=0.06)
    y = place(ns(0.36), [(0.0, a, 0.85), (0.095, b, 1.0), (0.200, c, 0.9)])
    y = eq(y, [('peak', 3200, 1.2, 2.0), ('highshelf', 9000, 0.7, -3.0)])
    return finalize_sfx(y, peak_db=-1.0, loud_target=None, fade_ms=12, hp=120.0)


def meow_pawtruth():
    m = mtof
    a = note(m(64), 0.090, 0.50, 0.84, 0.97, (700, 2000), growl=0.35, sub=0.25)
    b = note(m(69), 0.100, 0.42, 0.78, 0.96, (900, 2600), growl=0.45, sub=0.30)
    c = note(m(62), 0.150, 0.50, 0.92, 0.84, (750, 2100), rel=0.06, growl=0.55, sub=0.35)
    y = place(ns(0.36), [(0.0, a, 0.85), (0.100, b, 1.0), (0.208, c, 0.95)])
    y = eq(y, [('lowshelf', 180, 0.7, -2.0), ('peak', 1400, 1.0, 2.5), ('highshelf', 6000, 0.7, -4.0)])
    return finalize_sfx(y, peak_db=-1.0, loud_target=None, fade_ms=12, hp=70.0)


ALL = {'sfx_meow_whiskers': meow_whiskers, 'sfx_meow_pawtruth': meow_pawtruth}


def main(names=None):
    for name, fn in ALL.items():
        if names and name not in names:
            continue
        x = fn()
        write_wav16(os.path.join(OUT, name + '.wav'), x)
        dc = float(np.mean(x))
        print(f'{name:20s} {len(x) / SR:5.3f}s  peak={a2db(peak(x)):6.2f} dBFS  dc={dc:+.6f}  st100={short_max(x):6.1f}')


if __name__ == '__main__':
    main(sys.argv[1:])
