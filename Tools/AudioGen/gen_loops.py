"""gen_loops.py - ambience loops: amb_wind, amb_town, heartbeat (tumbleweed_roll is retired, see LOOPS).

All loops are built from periodic noise (FFT-shaped), periodic modulation curves and
events folded around the loop boundary (wrap_tail), so every file loops seamlessly.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pn_dsp import *      # noqa
from pn_inst import tom   # noqa

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Assets', 'PawNoon',
                   'Resources', 'Audio', 'Loops')


def amb_wind(seed=11, secs=48.0):
    r = rng(seed)
    n = ns(secs)
    gust = 0.55 + 0.45 * smooth_curve(n, 0.12, r)            # slow gusts
    gust = np.clip(gust * (1 + 0.15 * smooth_curve(n, 0.9, r)), 0.08, 1.2)
    out = np.zeros((2, n))
    for ch in range(2):
        common = shaped_noise(n, rng(seed + 100), pink_mag(1.3, 40, 1400))
        own = shaped_noise(n, r, pink_mag(1.3, 40, 1400))
        base = (0.6 * common + 0.4 * own)
        base /= np.std(base)
        bright = shaped_noise(n, r, band_mag(500, 2600, 2))
        bright /= np.std(bright)
        y = base * gust ** 1.4 + 0.5 * bright * gust ** 3
        for fc, bwid in [(380, 18), (530, 25), (760, 30), (1080, 40)]:   # whistling resonances
            tone = shaped_noise(n, r, lambda f: np.exp(-0.5 * ((f - fc * (1 + 0.02 * ch)) / bwid) ** 2))
            tone /= np.std(tone)
            env = np.clip(smooth_curve(n, 0.07, r), 0, 1) * gust ** 3
            y += 0.35 * tone * env
        sand = shaped_noise(n, r, band_mag(3500, 9000, 3))
        sand /= np.std(sand)
        smooth_curve(n, 25, r)             # (was a 25 Hz "grain" AM on the sand = crackle; draw kept)
        y += 0.10 * sand * gust ** 3
        out[ch] = y
    return normalize_lufs(out, -24.0, -3.0, circular=True)


def creak(r, dur=1.0, f0=60.0):
    n = ns(dur)
    t = tt(n)
    rate = f0 * (1 + 0.6 * smooth_curve(n, 3, r, periodic=False)) * (1 + 0.5 * np.sin(np.pi * t / dur))
    ph = np.cumsum(rate / SR)
    imp = np.zeros(n)
    idx = np.nonzero(np.diff(np.floor(ph)) > 0)[0]
    imp[idx] = r.uniform(0.5, 1.0, len(idx))
    ir = np.zeros(ns(0.03))
    ti = tt(len(ir))
    for f, tau in [(320, 0.012), (720, 0.008), (1350, 0.005), (2400, 0.003)]:
        ir += np.sin(2 * np.pi * f * ti) * np.exp(-ti / tau)
    y = np.convolve(imp, ir)[:n]
    return y * np.sin(np.pi * t / dur) ** 0.7


def squeak(r, dur=0.45, f0=1150.0, up=True):
    n = ns(dur)
    t = tt(n)
    arch = np.sin(np.pi * t / dur)
    f = f0 * (1 + (0.18 if up else -0.12) * t / dur + 0.05 * arch)
    jit = 1 + 0.4 * (0.5 + 0.5 * np.sin(2 * np.pi * 38 * t)) * lp_noise(n, 30, r)
    ph = phase_from_freq(f)
    y = (np.sin(ph) + 0.5 * np.sin(2 * ph) + 0.25 * np.sin(3 * ph)) * jit
    return y * arch ** 1.5


def hawk(r, dur=1.6):
    n = ns(dur)
    t = tt(n)
    f = 3000 * (2100 / 3000) ** (t / dur) * (1 + 0.04 * np.exp(-t / 0.08))
    f = f * (1 + 0.012 * lp_noise(n, 60, r))
    ph = phase_from_freq(f)
    y = np.sin(ph) + 0.4 * np.sin(2 * ph) + 0.2 * np.sin(3 * ph)
    y = y * (1 + 0.6 * lp_noise(n, 300, r))
    env = (1 - np.exp(-t / 0.03)) * np.exp(-t / (dur * 0.45))
    return y * env


def amb_town(seed=22, secs=45.0):
    r = rng(seed)
    L = ns(secs)
    n = L + ns(6.0)
    buf = np.zeros((2, n))
    ir = reverb_ir(r, t60=1.6, predelay=0.03)

    def put(x, t0, p, g, lp=None):
        if lp:
            x = bw(x, 'lowpass', lp, 2)
        a = ns(t0) % L
        k = min(len(x), n - a)
        buf[:, a:a + k] += pan(x[:k], p) * g

    for t0, d, f0, p in [(3.0, 1.1, 55, -0.6), (19.5, 0.8, 75, 0.5), (33.0, 1.4, 48, -0.3)]:
        put(creak(r, d, f0), t0, p, 0.5, 4000)
    for t0, swings, p in [(8.0, 3, 0.55), (26.0, 4, 0.6), (41.5, 3, 0.5)]:   # last one wraps
        for s in range(swings):
            put(squeak(r, 0.4, 1150 + 60 * (s % 2), up=s % 2 == 0), t0 + s * 0.85, p, 0.12 * 0.8 ** s, 5000)
    put(hawk(r, 1.5), 14.0, -0.4, 0.05, 6000)
    put(hawk(r, 1.2), 15.9, -0.35, 0.035, 6000)
    put(hawk(r, 1.4), 37.0, 0.3, 0.03, 5000)
    wet = convolve(buf, ir, n)
    x = wrap_tail(buf + 0.8 * wet, L)
    bed = np.stack([shaped_noise(L, r, pink_mag(1.5, 60, 2500)) for _ in range(2)])
    bed /= np.std(bed)
    x += bed * 0.004
    return normalize_lufs(x, -30.0, -6.0, circular=True)


def heartbeat(seed=33, beats=12, bpm=80.0):
    r = rng(seed)
    per = ns(60.0 / bpm)
    L = per * beats
    n = L + ns(1.0)
    y = np.zeros(n)
    for b in range(beats):
        a = b * per + int(r.normal(0, ns(0.003)))
        a = a % L
        v = r.uniform(0.9, 1.0)
        for off, f0, g, dec in [(0.0, 48, 1.0, 0.11), (0.29, 56, 0.7, 0.08)]:
            s = tom(f0, 0.45, r, bend=0.5, decay=dec, noise=0.25)
            s = bw(s, 'lowpass', 400, 2)
            k = a + ns(off)
            y[k:k + len(s)] += s * g * v
    y = wrap_tail(y[None, :], L)[0]
    y = sat(y / peak(y) * 1.6, 1.6)        # harmonics so it reads on phone speakers
    y = eq(y, [('peak', 140, 0.9, 4.0), ('hp', 30, 0.7, 0), ('lp', 1200, 0.7, 0)], circular=True)
    x = np.stack([y, y])
    return normalize_lufs(x, -20.0, -1.5, circular=True)


def tumbleweed_roll(seed=44, secs=8.0):
    r = rng(seed)
    L = ns(secs)
    n = L + ns(0.5)
    t = tt(L)
    roll = 0.5 + 0.5 * np.abs(np.sin(np.pi * 13 * t / secs))     # 13 rotations per loop
    out = np.zeros((2, n))
    for ch in range(2):
        y = np.zeros(n)
        times = np.nonzero(r.random(L) < (220 / SR) * (0.4 + roll))[0]
        for k in times:
            m = ns(0.004)
            fc = r.uniform(2000, 7000)
            c = bw(r.standard_normal(m), 'bandpass', (fc * 0.7, min(fc * 1.4, 20000)), 1)
            c *= np.exp(-np.arange(m) / (m / 4)) * r.pareto(2.5) * 0.3
            y[k:k + m] += c
        rust = shaped_noise(L, r, band_mag(1200, 6000, 2))
        rust /= np.std(rust)
        y[:L] += 0.08 * rust * roll
        scrape = shaped_noise(L, r, band_mag(250, 900, 2))
        scrape /= np.std(scrape)
        y[:L] += 0.05 * scrape * (0.6 + 0.4 * smooth_curve(L, 0.5, r))
        out[ch] = y
    for t0 in (0.7, 2.9, 4.4, 6.8):
        th = tom(140, 0.25, r, bend=0.3, decay=0.05, noise=0.6)
        dust = bw(r.standard_normal(ns(0.3)), 'bandpass', (1500, 7000), 2) * np.exp(-tt(ns(0.3)) / 0.08)
        a = ns(t0)
        out[:, a:a + len(th)] += 0.25 * th
        out[:, a:a + len(dust)] += 0.05 * dust
    x = wrap_tail(out, L)
    x = bw(x, 'highpass', 60, 2, circular=True)
    return normalize_lufs(x, -24.0, -3.0, circular=True)


def amb_duel(seed=55, secs=60.0):
    """Duel bed v2: bleached high-noon street. Dry gusting wind + sand hiss, one distant
    hinge creak every ~15-20 s, one very far hawk, lots of space."""
    r = rng(seed)
    L = ns(secs)
    gust = 0.5 + 0.5 * smooth_curve(L, 0.09, r)
    gust = np.clip(gust * (1 + 0.12 * smooth_curve(L, 0.7, r)), 0.12, 1.1)
    bed = np.zeros((2, L))
    common = shaped_noise(L, rng(seed + 1), pink_mag(1.4, 50, 1100))
    for ch in range(2):
        own = shaped_noise(L, r, pink_mag(1.4, 50, 1100))
        base = 0.65 * common + 0.35 * own
        base /= np.std(base)
        air = shaped_noise(L, r, band_mag(600, 2400, 2))
        air /= np.std(air)
        sand = shaped_noise(L, r, band_mag(3800, 10000, 3))
        sand /= np.std(sand)
        smooth_curve(L, 30, r)             # (was a 30 Hz "grain" AM on the sand = crackle; draw kept)
        bed[ch] = base * gust ** 1.6 + 0.35 * air * gust ** 3 + 0.12 * sand * gust ** 2.5
    bed = bw(bed, 'highpass', 70, 2, circular=True)
    n = L + ns(5.0)
    ev = np.zeros((2, n))

    def put(x, t0, p, g):
        a = ns(t0) % L
        k = min(len(x), n - a)
        ev[:, a:a + k] += pan(x[:k], p) * g

    for t0, f0, p in [(7.5, 980, -0.55), (25.0, 1060, 0.5), (42.5, 920, -0.35), (57.0, 1010, 0.45)]:
        sq = bw(squeak(r, 0.55, f0, up=True), 'lowpass', 3500, 2)   # one slow distant hinge creak
        put(sq, t0, p, 0.035)
    put(bw(hawk(r, 1.4), 'lowpass', 4500, 2), 34.0, 0.25, 0.012)
    ir = musical_ir(r, t60=1.8, predelay=0.04)
    ev = ev * 0.35 + convolve(ev, ir, n)
    x = bed * 0.02 + wrap_tail(ev, L)
    return normalize_lufs(x, -30.0, -6.0, circular=True)      # -4 dB vs v2 (was -26 LUFS)


def heartbeat_v2(seed=34, beats=12, bpm=92.0):
    """One close, deep, clean heartbeat (no layers) at ~92 bpm."""
    r = rng(seed)
    per = ns(60.0 / bpm)
    L = per * beats
    y = np.zeros(L + ns(1.0))
    for b in range(beats):
        a = b * per + ns(0.2)              # seam falls in the silence before a lub
        v = 1.0 - 0.04 * (b % 3 == 2)
        for off, f0, g, tau in [(0.0, 52, 1.0, 0.075), (0.26, 60, 0.62, 0.06)]:
            n = ns(0.4)
            t = tt(n)
            f = f0 * (1 + 0.45 * np.exp(-t / 0.02))
            s = np.sin(phase_from_freq(f)) * np.minimum(1, t / 0.006) * np.exp(-t / tau)
            k = a + ns(off)
            y[k:k + n] += s * g * v
    y = wrap_tail(y[None, :], L)[0]
    y = sat(y / peak(y) * 1.4, 1.3)            # gentle harmonics so it reads on phone speakers
    y = eq(y, [('peak', 120, 1.0, 3.0), ('lp', 700, 0.7, 0), ('hp', 28, 0.7, 0)], circular=True)
    return normalize_lufs(np.stack([y, y]), -20.0, -1.5, circular=True)


from gen_duel import heartbeat_v3   # noqa: E402  (v3: louder, physical; see gen_duel.py)

LOOPS = {'amb_wind': amb_wind, 'amb_town': amb_town, 'heartbeat': heartbeat_v3, 'amb_duel': amb_duel}
# tumbleweed_roll() is kept but no longer written: nothing played the loop (cleanup 21 Oct removed the file)


def main(names=None):
    for name, fn in LOOPS.items():
        if names and name not in names:
            continue
        x = fn()
        write_ogg(os.path.join(OUT, name + '.ogg'), x, q=8)
        print(f'{name}: {x.shape[-1] / SR:.2f}s lufs={lufs(x):.1f} peak={a2db(peak(x)):.2f}dB')


if __name__ == '__main__':
    main(sys.argv[1:])
