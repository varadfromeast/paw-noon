"""gen_sfx.py - all Paw Noon SFX (duel layer v2: church tolls, single-ting DRAW,
canyon-echo gunshots, holster_draw; v1 designs kept in this file for reference): 48 kHz 16-bit mono WAV, onset at sample 0, peak -1 dBFS.

Gunshots are processed from the CC0 Free Firearm Sound Library recordings vendored in
source/Revolver1-3.wav (see Resources/Audio/NOTICE.txt). Everything else is synthesized.
"""
import os
import sys

import numpy as np
from scipy.signal import resample_poly

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pn_dsp import *      # noqa
from pn_inst import *     # noqa

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', '..', 'Assets', 'PawNoon', 'Resources', 'Audio', 'SFX')
SRC = os.path.join(HERE, 'source')

LOUD_BIG, LOUD_MID, LOUD_UI = -9.0, -12.0, -17.0   # short-term (100 ms) loudness targets


def mix(n, *parts):
    y = np.zeros(n)
    for t0, x, g in parts:
        a = ns(t0)
        k = min(len(x), n - a)
        y[a:a + k] += g * x[:k]
    return y


def dec(n, tau):
    return np.exp(-tt(n) / tau)


def noise_burst(r, dur, lo, hi, tau, order=2):
    n = ns(dur)
    return bw(r.standard_normal(n), 'bandpass', (lo, hi), order) * dec(n, tau)


def thump(r, f0, dur, tau, bend=1.0, btau=0.03):
    n = ns(dur)
    t = tt(n)
    f = f0 * (1 + bend * np.exp(-t / btau))
    return np.sin(phase_from_freq(f)) * dec(n, tau)


def metal_click(r, dur=0.03, freqs=(2800, 4500, 6200, 8300), tau=0.006):
    n = ns(dur)
    t = tt(n)
    y = sum(np.sin(2 * np.pi * f * (1 + 0.02 * r.standard_normal()) * t) * np.exp(-t / (tau * (1.3 - i * 0.15)))
            for i, f in enumerate(freqs))
    y += 1.5 * noise_burst(r, dur, 2000, 15000, 0.0015)
    return y


# ------------------------------------------------------------------ countdown & cue
def tick(level, seed):
    r = rng(seed)
    f = {3: mtof(52), 2: mtof(53), 1: mtof(56)}[level]          # E3, F3, G#3 -> resolves to A on DRAW
    dur = {3: 0.95, 2: 1.0, 1: 1.7}[level]
    n = ns(dur)
    b = bell(f, dur, r, bright=0.8 + 0.15 * (3 - level), decay=0.35 + 0.1 * (3 - level))
    a = anvil(f * 8, dur, r, decay=0.25 + 0.1 * (3 - level))
    th = thump(r, f / 2, dur, 0.09 + 0.04 * (3 - level), 0.8)
    y = b + (0.25 + 0.2 * (3 - level)) * a + (0.5 + 0.35 * (3 - level)) * th
    if level == 1:
        y += 0.35 * bell(f * 2, dur, r, bright=1.2, decay=0.6)
    y = sat(y / peak(y) * (1.2 + 0.5 * (3 - level)), 1.0)
    return finalize_sfx(y, loud_target={3: -11.0, 2: -10.0, 1: -8.5}[level])


def sfx_draw(seed=7):
    r = rng(seed)
    n = ns(1.4)
    t = tt(n)
    # whip crack: N-wave + snap, all at sample 0
    crack = np.zeros(n)
    k = ns(0.0012)
    crack[:k] = np.linspace(1, -1, k)
    crack += 1.2 * noise_burst(r, 1.4, 2500, 16000, 0.006)
    crack += 0.25 * noise_burst(r, 1.4, 1200, 6000, 0.03)
    # bright clang: A5 + E6 bells, struck hard
    clang = bell(mtof(81), 1.4, r, bright=1.6, decay=0.35, strike=1.0) + \
        0.7 * bell(mtof(88) * 1.003, 1.4, r, bright=1.4, decay=0.3) + \
        0.5 * anvil(mtof(93), 1.4, r, decay=0.3)
    # low boom with harmonics for phone speakers
    boom = thump(r, 62, 1.4, 0.075, 1.5, 0.03)
    boom = sat(boom * 2.0, 1.3) + 0.4 * noise_burst(r, 1.4, 40, 300, 0.04)
    boom = bw(boom, 'highpass', 45, 2)
    clang = eq(clang, [('peak', 2600, 0.8, 4.0), ('highshelf', 6000, 0.7, 3.0)])
    y = 1.0 * crack / peak(crack) + 1.0 * clang / peak(clang) + 0.45 * boom / peak(boom)
    y = sat(y, 1.2)
    return finalize_sfx(y, loud_target=-7.5)


# ------------------------------------------------------------------ gunshots
def load_src(i):
    sr, x = read_wav(os.path.join(SRC, f'Revolver{i}.wav'))
    assert sr == SR
    return x


def repitch(x, ratio):
    """ratio > 1 = higher/shorter (resample)."""
    up, down = int(round(1000 / ratio)), 1000
    return resample_poly(x, up, down)


def gun_hero(i, src, ratio, sub_f, seed):
    r = rng(seed)
    x = load_src(src)
    x = x[int(np.argmax(np.abs(x) > 0.05 * peak(x))):]
    x = repitch(x, ratio)[:ns(1.2)]
    n = len(x)
    t = tt(n)
    x = eq(x, [('lowshelf', 140, 0.7, 4.0), ('peak', 2600, 1.0, 2.5), ('peak', 450, 1.0, -2.0)])
    x *= 1 + 1.0 * np.exp(-t / 0.008)                         # transient emphasis
    sub = sat(thump(r, sub_f, 1.2, 0.09, 1.0, 0.02) * 2, 1.5)
    body = noise_burst(r, 1.2, 100, 900, 0.03)
    y = x / peak(x) + 0.55 * sub[:n] + 0.25 * body[:n] / peak(body)
    y *= np.where(t > 0.45, np.exp(-(t - 0.45) / 0.18), 1.0)  # shorten natural tail
    y = sat(y, 1.6)
    return finalize_sfx(y, loud_target=-7.0)


def gun_bandit(i, src, ratio, seed):
    r = rng(seed)
    x = load_src(src)
    x = x[int(np.argmax(np.abs(x) > 0.05 * peak(x))):]
    x = repitch(x, ratio)[:ns(1.3)]
    x = bw(x, 'highpass', 170, 2)
    x = bw(x, 'lowpass', 5500, 2)
    x = eq(x, [('peak', 1200, 0.8, 2.0)])
    x = sat(x / peak(x) * 1.5, 1.2)
    taps = [(0.011, 0.45, 5000), (0.023, 0.38, 4500), (0.037, 0.3, 4000), (0.052, 0.25, 3500),
            (0.071, 0.2, 3000), (0.16, 0.28, 2500), (0.29, 0.16, 2000)]
    y = early_reflections(x, taps)
    ir = reverb_ir(r, t60=1.3, predelay=0.02, stereo=False)
    wet = np.convolve(y, ir[0])[:len(y) + ns(0.6)]
    y = pad_to(y, len(wet)) + 0.35 * wet / peak(wet) * peak(y)
    t = tt(len(y))
    y *= np.where(t > 0.9, np.exp(-(t - 0.9) / 0.15), 1.0)
    return finalize_sfx(y, loud_target=-10.5)


# ------------------------------------------------------------------ weapon foley
def sfx_cock(seed=21):
    r = rng(seed)
    n = ns(0.3)
    y = mix(n, (0.0, metal_click(r, 0.04, (2600, 4100, 5900, 7600)), 0.7),
            (0.03, metal_click(r, 0.02, (5200, 7400, 9800)), 0.2),
            (0.048, metal_click(r, 0.02, (5000, 7100, 9500)), 0.2),
            (0.085, metal_click(r, 0.06, (3100, 4700, 6600, 8800), tau=0.009), 1.0),
            (0.085, thump(r, 260, 0.1, 0.02, 0.3), 0.3))
    return finalize_sfx(y, loud_target=LOUD_MID)


def sfx_holster(seed=22):
    r = rng(seed)
    n = ns(0.55)
    t = tt(n)
    env = np.minimum(1, t / 0.02) * np.exp(-((t - 0.12) / 0.14) ** 2) + 0.3 * np.exp(-t / 0.02)
    fc = 600 + 1600 * np.exp(-((t - 0.15) / 0.12) ** 2)
    whoosh = tv_biquad(r.standard_normal(n), 'bp', fc, 0.8)
    grain = 1 + 0.7 * lp_noise(n, 80, r)
    y = whoosh * env * grain
    y = mix(n, (0, y, 1.0), (0.0, noise_burst(r, 0.05, 300, 3000, 0.008), 0.6),
            (0.33, thump(r, 120, 0.2, 0.04, 0.5), 0.6), (0.33, metal_click(r, 0.03), 0.15))
    return finalize_sfx(y, loud_target=LOUD_MID)


def sfx_misfire(seed=23):
    r = rng(seed)
    n = ns(0.7)
    pops = np.zeros(n)
    for k in np.sort(r.uniform(0.1, 0.5, 14)):
        a = ns(k)
        p = noise_burst(r, 0.01, 800, 6000, 0.002)
        pops[a:a + len(p)] += p * r.uniform(0.2, 0.6) * np.exp(-(k - 0.1) / 0.2)
    fizz = noise_burst(r, 0.6, 1500, 7000, 0.15) * np.minimum(1, tt(ns(0.6)) / 0.05)
    y = mix(n, (0.0, metal_click(r, 0.04, (2200, 3500, 5100), tau=0.004), 1.0),
            (0.07, noise_burst(r, 0.15, 200, 2000, 0.03), 0.5), (0.0, pops, 1.0), (0.08, fizz, 0.12))
    return finalize_sfx(y, loud_target=LOUD_MID)


def sfx_ricochet(seed=24):
    r = rng(seed)
    n = ns(0.95)
    t = tt(n)
    f = 4300 * (950 / 4300) ** np.minimum(t / 0.75, 1) * (1 + 0.03 * np.sin(2 * np.pi * 31 * t))
    ph = phase_from_freq(f)
    whine = (np.sin(ph) + 0.2 * np.sin(2 * ph)) * (1 + 0.5 * lp_noise(n, 200, r) * 0.3)
    whine *= np.minimum(1, t / 0.01) * np.exp(-t / 0.35)
    y = mix(n, (0, metal_click(r, 0.03, (3800, 5600, 7200)), 0.9), (0.012, whine, 0.6))
    ir = reverb_ir(r, t60=0.8, stereo=False)
    y = y + 0.25 * np.convolve(y, ir[0])[:n]
    return finalize_sfx(y, loud_target=LOUD_MID)


def sfx_shell(seed=25):
    r = rng(seed)
    n = ns(0.95)
    y = np.zeros(n)
    t0, gap, g = 0.0, 0.17, 1.0
    while t0 < 0.85 and g > 0.03:
        k = 1 + 0.03 * r.standard_normal()
        tink = bell(3150 * k, 0.25, r, partials=[(1, 1, .15), (1.65, .6, .1), (2.45, .5, .07), (3.1, .3, .05)],
                    strike=0.6)
        a = ns(t0)
        m = min(len(tink), n - a)
        y[a:a + m] += tink[:m] * g
        t0 += gap
        gap *= 0.68
        g *= 0.65
    return finalize_sfx(y, loud_target=LOUD_MID)


# ------------------------------------------------------------------ impacts & bodies
def sfx_hit(seed=26):
    r = rng(seed)
    n = ns(0.5)
    y = mix(n, (0, sat(thump(r, 60, 0.5, 0.12, 1.4, 0.025) * 2, 1.5), 1.0),
            (0, noise_burst(r, 0.2, 250, 2500, 0.025), 0.8),
            (0.01, noise_burst(r, 0.3, 300, 1800, 0.07), 0.25),
            (0, thump(r, 480, 0.15, 0.05, 0.5, 0.01), 0.18))
    return finalize_sfx(sat(y, 1.2), loud_target=LOUD_BIG)


def formant_voice(r, n, f0, formants, trill=None, jitter=0.01):
    """Glottal pulse train through time-varying formant bandpasses (cat vocal)."""
    f0 = f0 * (1 + jitter * lp_noise(n, 20, r))
    ph = phase_from_freq(f0) / (2 * np.pi)
    src = (ph % 1.0)
    src = (src - 0.5) * 2                                   # band-unlimited saw is ok at these levels
    src = bw(src, 'lowpass', 7000, 2)
    if trill is not None:
        src *= trill
    y = np.zeros(n)
    for fc, q, g in formants:
        y += g * tv_biquad(src, 'bp', fc, q)
    return y


def sfx_hit_cat(seed=27):
    r = rng(seed)
    # hiss
    nh = ns(0.17)
    th = tt(nh)
    hiss = tv_biquad(r.standard_normal(nh), 'bp', 4200 + 1500 * th / 0.17, 1.2)
    hiss += 0.4 * bw(r.standard_normal(nh), 'highpass', 2500, 2)
    hiss *= np.minimum(1, th / 0.003) * np.exp(-th / 0.07)
    # mrrp?
    nm = ns(0.36)
    tm = tt(nm)
    f0 = 520 + 120 * np.sin(np.pi * tm / 0.36) + 160 * np.clip((tm - 0.22) / 0.14, 0, 1)
    trill = 1 - 0.55 * (0.5 + 0.5 * np.sin(2 * np.pi * 27 * tm)) * ((tm > 0.05) & (tm < 0.22))
    s = np.clip(tm / 0.36, 0, 1)
    f1 = 450 + 500 * np.sin(np.pi * s) ** 0.6
    f2 = 1500 + 600 * s
    v = formant_voice(r, nm, f0, [(f1, 4.0, 1.0), (f2, 6.0, 0.6), (3600, 8.0, 0.25)], trill)
    v *= np.minimum(1, tm / 0.03) * np.where(tm > 0.31, np.exp(-(tm - 0.31) / 0.012), 1.0)
    n = ns(0.6)
    y = mix(n, (0, hiss, 0.55), (0.16, v / peak(v), 1.0))
    return finalize_sfx(y, loud_target=LOUD_MID)


def sfx_bodyfall(seed=28):
    r = rng(seed)
    n = ns(1.0)
    t = tt(n)
    dust = bw(r.standard_normal(n), 'bandpass', (1200, 6500), 2) * np.minimum(1, t / 0.06) * np.exp(-t / 0.25)
    y = mix(n, (0, sat(thump(r, 70, 0.6, 0.11, 0.8) * 2, 1.4), 1.0),
            (0, noise_burst(r, 0.3, 60, 500, 0.05), 0.9),
            (0.13, thump(r, 95, 0.3, 0.06, 0.5), 0.5), (0.13, noise_burst(r, 0.2, 80, 600, 0.03), 0.4),
            (0.02, dust, 0.12))
    return finalize_sfx(y, loud_target=LOUD_MID)


def sfx_spurs(seed=29):
    r = rng(seed)
    n = ns(0.75)

    def jingle(k0):
        z = np.zeros(n)
        for j in range(4):
            c = bell(4100 * (1 + 0.04 * r.standard_normal()), 0.3, r,
                     partials=[(1, 1, .25), (1.44, .6, .18), (1.83, .5, .12), (2.2, .3, .08)], strike=0.5)
            a = ns(k0 + j * r.uniform(0.012, 0.025))
            m = min(len(c), n - a)
            z[a:a + m] += c[:m] * 0.7 ** j
        return z
    y = mix(n, (0, thump(r, 110, 0.15, 0.03, 0.4), 0.4), (0, noise_burst(r, 0.05, 200, 2000, 0.01), 0.3),
            (0, jingle(0.0), 1.0), (0.34, thump(r, 110, 0.15, 0.03, 0.4), 0.3), (0, jingle(0.34), 0.75))
    return finalize_sfx(y, loud_target=LOUD_MID)


def sfx_stamp(seed=30):
    r = rng(seed)
    n = ns(0.35)
    y = mix(n, (0, thump(r, 170, 0.3, 0.05, 0.4, 0.01), 1.0), (0, thump(r, 90, 0.3, 0.07, 0.3), 0.6),
            (0, noise_burst(r, 0.05, 800, 4000, 0.008), 0.9),
            (0, bell(300, 0.3, r, partials=[(1, 1, .12), (1.83, .7, .08)], strike=0), 0.3))
    return finalize_sfx(sat(y, 1.3), loud_target=LOUD_BIG)


def sfx_whoosh(seed=31):
    r = rng(seed)
    n = ns(0.45)
    t = tt(n)
    env = np.exp(-((t - 0.13) / 0.09) ** 2) * (t >= 0)
    env = np.maximum(env, 0.18 * np.exp(-t / 0.05))
    fc = 500 * (2800 / 500) ** np.clip(t / 0.15, 0, 1) * np.where(t > 0.15, (900 / 2800) ** ((t - 0.15) / 0.3), 1)
    y = tv_biquad(r.standard_normal(n), 'bp', fc, 1.1) * env
    return finalize_sfx(y, loud_target=LOUD_MID, onset_db=-30)


def sfx_lightning(seed=32):
    r = rng(seed)
    n = ns(1.2)
    zap, _ = chirp_exp(ns(0.05), 6000, 900)
    zap *= dec(len(zap), 0.02)
    crack = np.zeros(n)
    for k in np.sort(r.uniform(0, 0.45, 40)):
        a = ns(k)
        p = noise_burst(r, 0.004, 3000, 16000, 0.0008)
        crack[a:a + len(p)] += p * r.uniform(0.2, 1.0) * np.exp(-k / 0.18)
    parts = [(0.04, mtof(93)), (0.09, mtof(97)), (0.14, mtof(100)), (0.19, mtof(105))]
    y = mix(n, (0, zap, 0.8), (0, crack, 1.0),
            *[(t0, bell(f, 0.8, r, partials=[(1, 1, .5), (2.0, .3, .3), (3.0, .1, .2)], strike=0.2), 0.35)
              for t0, f in parts])
    for k in np.sort(r.uniform(0.2, 1.0, 18)):
        g = bell(r.uniform(5000, 9000), 0.2, r, partials=[(1, 1, .12)], strike=0)
        a = ns(k)
        m = min(len(g), n - a)
        y[a:a + m] += g[:m] * 0.08 * np.exp(-(k - 0.2) / 0.4)
    return finalize_sfx(y, loud_target=LOUD_MID)


def sfx_vulture(seed=33):
    r = rng(seed)

    def croak(dur, f0a, f0b):
        nn = ns(dur)
        tc = tt(nn)
        f0 = f0a * (f0b / f0a) ** (tc / dur)
        rough = 1 + 0.6 * np.sin(2 * np.pi * 43 * tc) * (0.5 + 0.5 * lp_noise(nn, 10, r))
        v = formant_voice(r, nn, f0, [(900, 3, 1.0), (1500, 4, 0.7), (2800, 6, 0.4)], rough, jitter=0.05)
        v += 0.3 * bw(r.standard_normal(nn), 'bandpass', (800, 3500), 2) * rough
        return v * np.minimum(1, tc / 0.01) * np.sin(np.pi * np.clip(tc / dur, 0, 1)) ** 0.4

    n = ns(1.4)
    y = mix(n, (0, croak(0.6, 290, 220), 1.0), (0.75, croak(0.35, 260, 210), 0.6))
    y = bw(y, 'lowpass', 5000, 2)
    ir = reverb_ir(r, t60=1.0, stereo=False)
    y = y + 0.3 * np.convolve(y, ir[0])[:n] * 3
    return finalize_sfx(y, loud_target=LOUD_MID)


# ------------------------------------------------------------------ UI
def wood_tok(r, f, dur=0.09):
    return bell(f, dur, r, partials=[(1, 1, .05), (2.6, .5, .025), (3.9, .25, .015)], strike=0.8)


def sfx_hp_tick(seed=40):
    r = rng(seed)
    n = ns(0.05)
    t = tt(n)
    y = np.sign(np.sin(2 * np.pi * mtof(88) * t)) * 0.3 + np.sin(2 * np.pi * mtof(88) * t)
    y = bw(y, 'lowpass', 5000, 2) * dec(n, 0.012)
    return finalize_sfx(y, loud_target=None, fade_ms=8)


def sfx_ui_tap(seed=41):
    r = rng(seed)
    return finalize_sfx(wood_tok(r, 1250), loud_target=LOUD_UI, fade_ms=10)


def sfx_ui_back(seed=42):
    r = rng(seed)
    n = ns(0.2)
    y = mix(n, (0, wood_tok(r, 1250), 1.0), (0.07, wood_tok(r, 880), 0.85))
    return finalize_sfx(y, loud_target=LOUD_UI, fade_ms=10)


def sfx_ui_toggle(seed=43):
    r = rng(seed)
    n = ns(0.12)
    y = mix(n, (0, metal_click(r, 0.03, (3000, 4600, 6400), 0.003), 0.8), (0.028, wood_tok(r, 1660, 0.07), 0.7))
    return finalize_sfx(y, loud_target=LOUD_UI, fade_ms=10)


# ------------------------------------------------------------------ outcome moments (Docs/FEEL.md)
def sfx_streak(seed=80):
    """Win streak tag: two rising brass-bell notes (E6 -> A6, 90 ms apart) over a wood tok. The engine raises the
    pitch a step per streak level, so x2, x3, x4 climb."""
    r = rng(seed)
    n = ns(0.75)
    p = [(1, 1, .45), (2.0, .45, .25), (3.0, .18, .12), (4.2, .08, .06)]
    y = mix(n, (0, wood_tok(r, 1660, 0.06), 0.55),
            (0.0, bell(mtof(88), 0.5, r, partials=p, strike=0.5), 0.75),
            (0.09, bell(mtof(93), 0.5, r, partials=p, strike=0.5), 1.0),
            (0.09, bell(mtof(105), 0.25, r, partials=[(1, 1, .05)], strike=0), 0.12))
    return finalize_sfx(y, loud_target=LOUD_MID)


def sfx_new_best(seed=81):
    """NEW BEST ribbon: a quick rising A-major arpeggio (A5 C#6 E6 A6) on bright bells, a soft air swell under it
    and a few high glints trailing off (no lightning crackle, so it never reads as the LIGHTNING stamp)."""
    r = rng(seed)
    n = ns(1.1)
    t = tt(n)
    p = [(1, 1, .35), (2.0, .4, .2), (3.0, .15, .1), (5.4, .05, .05)]
    notes = [(0.0, 81), (0.06, 85), (0.12, 88), (0.18, 93)]
    air = bw(r.standard_normal(n), 'bandpass', (2500, 9000), 2) * np.exp(-((t - 0.16) / 0.12) ** 2) * 0.12
    y = mix(n, (0, air, 1.0), *[(t0, bell(mtof(m), 0.9, r, partials=p, strike=0.35), 0.5 + 0.12 * i)
                                for i, (t0, m) in enumerate(notes)])
    for k in np.sort(r.uniform(0.22, 0.8, 9)):
        g = bell(r.uniform(4200, 7800), 0.25, r, partials=[(1, 1, .1)], strike=0) * ramp_in(ns(0.25), 3)
        a = ns(k)
        m = min(len(g), n - a)
        y[a:a + m] += g[:m] * 0.1 * np.exp(-(k - 0.22) / 0.35)
    return finalize_sfx(y, loud_target=LOUD_MID)


# ================================================================== duel layer v2 (minimal, cinematic)
TOLL_HZ = float(mtof(52))      # one deep church bell, strike E3 (hum E2); same bell for all three


def church_toll(level, seed=70):
    """level 3 = farthest/darkest ... 1 = closest/brightest. Same bell, same pitch."""
    r = rng(seed)                       # same seed -> same bell every time
    dur = 4.5
    y = bell(TOLL_HZ, dur, r, bright=0.9, decay=1.0, strike=0.25)
    close = {3: 0.0, 2: 0.5, 1: 1.0}[level]
    lp = 1400 + 3200 * close
    y = bw(y, 'lowpass', lp, 2)
    y = eq(y, [('peak', 900, 0.8, 1.5 + 2.5 * close), ('hp', 60, 0.7, 0)])
    ir = reverb_ir(rng(seed + 1), t60=3.2, predelay=0.03 + 0.03 * (1 - close), stereo=False, hi_cut=4500)
    wet = np.convolve(y, ir[0])[:ns(dur)]
    wet = wet / peak(wet) * peak(y)
    dry_g, wet_g = 0.35 + 0.65 * close, 0.9 - 0.45 * close
    out = dry_g * y + wet_g * wet
    t = tt(len(out))
    if level == 1:                      # keep the hold clean: tail gone by ~1.6 s
        out *= np.where(t > 0.5, np.exp(-(t - 0.5) / 0.38), 1.0)
    else:
        out *= np.where(t > 1.2, np.exp(-(t - 1.2) / 0.7), 1.0)
    return finalize_sfx(out, loud_target={3: -15.0, 2: -13.0, 1: -11.0}[level], tail_db=-60)


def sfx_draw_ting(seed=71):
    """ONE bright high anvil/bell strike, instant attack, short, nothing else."""
    r = rng(seed)
    parts = [(1.0, 1.0, 0.9), (2.76, 0.45, 0.45), (5.40, 0.25, 0.25), (8.93, 0.12, 0.12)]
    y = bell(float(mtof(93)), 0.9, r, partials=parts, strike=0.6)    # A6 'ting'
    y = bw(y, 'highpass', 400, 2)
    return finalize_sfx(y, loud_target=-10.0, tail_db=-55, fade_ms=60)


def canyon(x, r, far=0.0):
    """Discrete slapbacks off distant rock walls, each darker; decays over ~2.5 s."""
    taps = [(0.38 + 0.08 * far, 0.36 + 0.06 * far, 4200), (0.93 + 0.1 * far, 0.22 + 0.04 * far, 2600),
            (1.62 + 0.1 * far, 0.12 + 0.03 * far, 1500)]
    n = len(x) + ns(2.9)
    y = pad_to(x, n)
    ir = reverb_ir(r, t60=0.9, predelay=0.0, stereo=False, er_taps=0)[0]
    for d, g, lp in taps:
        e = bw(x, 'lowpass', lp, 2)
        e = bw(e, 'highpass', 150, 1)
        e = 0.6 * e + 0.6 * np.convolve(e, ir)[:len(e)] * peak(e) / (peak(np.convolve(e, ir)) + 1e-12)
        k = ns(d)
        m = min(len(e), n - k)
        y[k:k + m] += g * e[:m]
    tail = np.convolve(y, ir)[:n]
    y = y + 0.12 * tail / peak(tail) * peak(y)
    return y


def gun_hero_canyon(src, ratio, sub_f, seed):
    r = rng(seed)
    x = load_src(src)
    x = x[int(np.argmax(np.abs(x) > 0.05 * peak(x))):]
    x = repitch(x, ratio)[:ns(1.0)]
    t = tt(len(x))
    x = eq(x, [('lowshelf', 140, 0.7, 4.0), ('peak', 2600, 1.0, 2.5), ('peak', 450, 1.0, -2.0)])
    x *= 1 + 1.0 * np.exp(-t / 0.008)
    sub = sat(thump(r, sub_f, 1.0, 0.08, 1.0, 0.02) * 2, 1.5)
    y = x / peak(x) + 0.5 * sub[:len(x)]
    y *= np.where(t > 0.25, np.exp(-(t - 0.25) / 0.08), 1.0)   # dry the source room, canyon does the rest
    y = sat(y, 1.5)
    y = canyon(y, r, far=0.0)
    return finalize_sfx(y, loud_target=-7.5, tail_db=-58, fade_ms=200)


def gun_bandit_canyon(src, ratio, seed):
    r = rng(seed)
    x = load_src(src)
    x = x[int(np.argmax(np.abs(x) > 0.05 * peak(x))):]
    x = repitch(x, ratio)[:ns(1.0)]
    t = tt(len(x))
    x = bw(x, 'highpass', 170, 2)
    x = bw(x, 'lowpass', 5000, 2)
    x = sat(x / peak(x) * 1.5, 1.2)
    x *= np.where(t > 0.25, np.exp(-(t - 0.25) / 0.08), 1.0)
    x = early_reflections(x, [(0.013, 0.4, 4500), (0.029, 0.3, 3800), (0.047, 0.22, 3200)])
    y = canyon(x, r, far=1.0)
    return finalize_sfx(y, loud_target=-10.5, tail_db=-58, fade_ms=200)


def sfx_holster_draw(seed=72):
    """Leather whip of a revolver yanked up out of the holster: ~0.2 s."""
    r = rng(seed)
    n = ns(0.22)
    t = tt(n)
    fc = 700 * (3800 / 700) ** np.clip(t / 0.09, 0, 1)
    env = np.minimum(1, t / 0.004) * np.exp(-((t - 0.05) / 0.045) ** 2) + 0.4 * np.exp(-t / 0.01)
    y = tv_biquad(r.standard_normal(n), 'bp', fc, 1.0) * env * (1 + 0.5 * lp_noise(n, 120, r))
    y = mix(n, (0, y, 1.0), (0.0, noise_burst(r, 0.03, 300, 2500, 0.006), 0.5),
            (0.085, metal_click(r, 0.03, (3300, 5200, 7400), 0.004), 0.12))
    return finalize_sfx(y, loud_target=LOUD_MID, fade_ms=30)


def _tick_sub():
    from gen_duel import sfx_tick_sub
    return sfx_tick_sub()


def all_sfx():
    d = {'sfx_tick_3': lambda: church_toll(3), 'sfx_tick_2': lambda: church_toll(2), 'sfx_tick_1': lambda: church_toll(1),
         'sfx_draw': sfx_draw_ting, 'sfx_holster_draw': sfx_holster_draw,
         'sfx_gun_hero_1': lambda: gun_hero_canyon(1, 1.00, 58, 51),
         'sfx_gun_hero_2': lambda: gun_hero_canyon(3, 0.97, 55, 52),
         'sfx_gun_hero_3': lambda: gun_hero_canyon(2, 1.04, 62, 53),
         'sfx_gun_bandit_1': lambda: gun_bandit_canyon(2, 0.95, 61),
         'sfx_gun_bandit_2': lambda: gun_bandit_canyon(3, 0.92, 62),
         'sfx_gun_bandit_3': lambda: gun_bandit_canyon(1, 0.98, 63),
         'sfx_cock': sfx_cock, 'sfx_holster': sfx_holster, 'sfx_misfire': sfx_misfire, 'sfx_hit': sfx_hit,
         'sfx_hit_cat': sfx_hit_cat, 'sfx_shell': sfx_shell,
         'sfx_bodyfall': sfx_bodyfall, 'sfx_spurs': sfx_spurs, 'sfx_stamp': sfx_stamp, 'sfx_whoosh': sfx_whoosh,
         'sfx_lightning': sfx_lightning, 'sfx_hp_tick': sfx_hp_tick, 'sfx_ui_tap': sfx_ui_tap,
         'sfx_ui_back': sfx_ui_back, 'sfx_ui_toggle': sfx_ui_toggle,
         'sfx_tick_sub': _tick_sub,
         'sfx_streak': sfx_streak, 'sfx_new_best': sfx_new_best}
    return d


def main(names=None):
    for name, fn in all_sfx().items():
        if names and name not in names:
            continue
        x = fn()
        write_wav16(os.path.join(OUT, name + '.wav'), x)
        print(f'{name:18s} {len(x) / SR:5.2f}s  st100={short_max(x):6.1f}  M={momentary_max(x):6.1f}')


if __name__ == '__main__':
    main(sys.argv[1:])
