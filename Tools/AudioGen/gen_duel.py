"""gen_duel.py - Paw Noon duel score v3 (composed, not ambient).

    python3 Tools/AudioGen/gen_duel.py [name ...]

Writes
  Music/music_duel_a.ogg      standoff/countdown bed: 16 bars of 4/4 at 76 bpm in A minor (50.5 s
                              seamless loop = two 8-bar phrases; the identity is complete in bar 1).
  Music/music_duel_hold.ogg   20 s suspense loop for the hold after "1" (drone + one high string
                              harmonic, minor-2nd rub swelling to 10 s and back).
  Music/music_duel_result.ogg 6 s one-shot resolving A chord + bell for the round result.
  Loops/heartbeat.ogg         v3: close, loud lub-dub at 92 bpm (8 beats, micro-jitter), sub + body.
  SFX/sfx_tick_sub.wav        40-55 Hz 250 ms thump layered under each countdown toll.

Same conventions as gen_music.py: loops are rendered with a tail folded back onto the start
(wrap_tail) and every continuous oscillator / modulation is exactly periodic over the loop, so
the seam is mathematically continuous; all post-fold processing is circular.
"""
import os
import sys

import numpy as np
from scipy.ndimage import uniform_filter1d

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from pn_dsp import *          # noqa
from pn_inst import *         # noqa
from gen_music import Mix, hum, master   # noqa

AUDIO = os.path.normpath(os.path.join(HERE, '..', '..', 'Assets', 'PawNoon', 'Resources', 'Audio'))

BPM = 76.0
BEAT = 60.0 / BPM
BAR = 4 * BEAT
NBARS = 16
SIXTEENTH_HZ = BPM / 60.0 * 4          # 5.067 Hz: twang amp tremolo, exactly 256 cycles per loop

# two 8-bar phrases; the second climbs and ends on E with a b9 rub that resolves into bar 1
CHORDS_A = ['Am', 'Am', 'F', 'E', 'Am', 'Am', 'Dm', 'E',
            'Am', 'F', 'G', 'E', 'F', 'Dm', 'E', 'E']
ROOT2 = {'Am': 45, 'F': 41, 'E': 40, 'Dm': 38, 'G': 43}            # baritone register
THIRD = {'Am': 3, 'F': 4, 'E': 4, 'Dm': 3, 'G': 4}
STEP = {'Am': 2, 'F': 2, 'E': 1, 'Dm': 2, 'G': 2}                   # E: phrygian-dominant F natural
# string section (top voice climbs through each phrase), (top, middle)
STRINGS = [(57, 52), (57, 52), (60, 57), (59, 56), (60, 57), (60, 57), (62, 57), (64, 56),
           (64, 60), (65, 60), (67, 62), (68, 64), (69, 65), (69, 65), (71, 68), (72, 71)]


def bt(bar, beat=0.0):
    return (bar - 1) * BAR + beat * BEAT


# ------------------------------------------------------------------ periodic oscillators
def periodic_freq(f, L):
    """Scale a frequency curve so it completes an integer number of cycles in L samples."""
    cyc = np.sum(f[:L]) / SR
    return f * (max(1, round(cyc)) / cyc)


def saw_stack(fcurve, r, fmax=5000.0, voices=(-0.07, 0.0, 0.06), L=None):
    """Band-limited sawtooth ensemble (additive, 1/k), detune in semitones per voice."""
    n = len(fcurve)
    y = np.zeros(n)
    fmean = float(np.mean(fcurve))
    for dv in voices:
        f = fcurve * 2 ** (dv / 12)
        if L:
            f = periodic_freq(f, L)
        ph = 2 * np.pi * np.concatenate([[0.0], np.cumsum(f[:-1])]) / SR + r.uniform(0, 6.28)
        K = int(fmax / (fmean * 2 ** (dv / 12)))
        for k in range(1, max(2, K)):
            # gentle roll-off towards fmax keeps it string-like rather than buzzy
            w = (1.0 / k) / (1 + (k * fmean / (0.6 * fmax)) ** 4)
            y += w * np.sin(k * ph)
    return y / len(voices)


def tv_lowpass_periodic(x, fc, q=0.8, L=None):
    """Time-varying lowpass that is seamless over period L (run twice, keep the 2nd pass)."""
    L = L or len(x)
    xx = np.concatenate([x[-L:], x]) if len(x) == L else np.concatenate([x[:L], x])
    ff = np.concatenate([fc[-L:], fc]) if len(fc) == L else np.concatenate([fc[:L], fc])
    return tv_biquad(xx, 'lp', ff, q)[L:]


def phrase_curve(L, period, lo, hi, power=1.6, drop_s=0.5):
    """Ramp lo->hi over each `period` seconds, falling back quickly at each phrase start
    (smoothed so the reset is a breath, not a click). Exactly periodic over L."""
    t = np.arange(L) / SR
    p = (t % period) / period
    c = lo + (hi - lo) * p ** power
    return uniform_filter1d(c, size=ns(drop_s), mode='wrap')


def stem_norm(x, target):
    return x * db2a(target - lufs(x))


# ------------------------------------------------------------------ DUEL BED
def music_duel_a(seed=606):
    r = rng(seed)
    L = ns(NBARS * BAR)
    m = Mix(NBARS * BAR, 9.0, loop=True)
    n = m.n
    ir = musical_ir(r, t60=3.0, predelay=0.035, hi_cut=7000)
    spring = spring_ir(r, length=2.4)

    # ---- 1. baritone twang ostinato, dotted gallop, tremolo amp
    for b, ch in enumerate(CHORDS_A, start=1):
        rt = ROOT2[ch]
        pat = [(0.0, rt, 0.75, 1.00), (0.75, rt - 5, 0.25, 0.62), (1.0, rt, 1.0, 0.80),
               (2.0, rt, 0.75, 0.95), (2.75, rt + THIRD[ch], 0.25, 0.70),
               (3.0, rt + STEP[ch], 0.5, 0.74), (3.5, rt - 5, 0.5, 0.66)]
        for beat, mi, d, v in pat:
            t0 = bt(b, beat) + hum(r, 4)
            y = twang(mi, d * BEAT + 0.9, r, v * r.uniform(0.92, 1.04), release_at=d * BEAT * 0.92)
            m.add('twg', y, t0, 0.0, v)
            if b >= 13:                                   # last 4 bars: octave doubling for lift
                y2 = twang(mi + 12, d * BEAT + 0.7, r, v * 0.8, release_at=d * BEAT * 0.9)
                m.add('twg2', y2, t0 + 0.006, 0.0, 0.45 * v)
    tw = m.get('twg')[0] + m.get('twg2')[0]
    tw = twang_amp(tw)
    trem = 1 - 0.55 * (0.5 + 0.5 * np.cos(2 * np.pi * SIXTEENTH_HZ * tt(n)))   # deep 16th tremolo
    tw = eq(tw * trem, [('peak', 3000, 0.9, -5.0), ('lp', 3800, 0.7, 0)])     # keep 2-4 kHz open
    stems = {'twang': tw}

    # ---- 2. strings: pedal A1/A2 saw-stack drone + chord voices, swelling over each phrase
    bright = phrase_curve(L, 8 * BAR, 280.0, 2100.0, power=1.7)
    level = phrase_curve(L, 8 * BAR, 0.18, 1.0, power=1.3)
    t = tt(L)
    ped = np.zeros(L)
    for mi, g in [(33, 0.8), (45, 1.0), (52, 0.45)]:
        f = mtof(mi) * (1 + 0.0015 * smooth_curve(L, 0.15, r))
        ped += g * saw_stack(f, r, fmax=3000, L=L)
    ped = tv_lowpass_periodic(ped, bright * 0.8, 0.7) * (0.15 + 0.85 * level)
    upper = np.zeros(n)
    for voice in (0, 1):
        notes = []
        for b, pair in enumerate(STRINGS, start=1):
            mi = pair[voice]
            if notes and notes[-1][2] == mi and abs(notes[-1][0] + notes[-1][1] - bt(b)) < 1e-6:
                notes[-1][1] += BAR
            else:
                notes.append([bt(b), BAR, mi])
        for t0, d, mi in notes:
            k = ns(d + 1.2)
            tl = tt(k)
            vib = 0.08 * np.clip((tl - 0.6) / 0.8, 0, 1) * np.sin(2 * np.pi * 4.6 * tl + r.uniform(0, 6))
            f = mtof(mi + vib)
            env = (1 - np.exp(-tl / 0.35)) * np.where(tl > d, np.exp(-(tl - d) / 0.45), 1.0)
            y = saw_stack(f, r, fmax=5000) * env
            a = ns(t0)
            upper[a:a + k] += y * (1.0 if voice == 0 else 0.8)
    idx = np.arange(n) % L
    upper = tv_biquad(upper, 'lp', bright[idx] * 1.15, 0.8)   # tail folds back after render
    upper *= (0.1 + 0.9 * level[idx])
    strings = wrap_tail((pad_to(ped, n) + 0.55 * upper)[None, :], L)[0]      # ped is already periodic
    strings = eq(strings, [('hp', 40, 0.7, 0), ('peak', 300, 0.8, 2.0), ('peak', 3000, 0.9, -6.0)],
                 circular=True)
    stems['strings'] = strings

    # ---- 3. deep tom pulse on 2 and 4 (heartbeat-like), pickup ghost notes in phrase 2
    tomb = np.zeros(n)
    for b in range(1, NBARS + 1):
        hits = [(1.0, 0.95, 64), (3.0, 0.78, 60)]
        if b >= 9:
            hits.append((2.75, 0.30, 66))
        if b in (8, 16):
            hits += [(3.5, 0.45, 62), (3.75, 0.55, 58)]
        for beat, v, f0 in hits:
            s = tom(f0, 1.0, r, bend=0.55, decay=0.32, noise=0.35)
            s = bw(s, 'lowpass', 1200, 2)
            a = ns(bt(b, beat) + hum(r, 3)) % L
            k = min(len(s), n - a)
            tomb[a:a + k] += s[:k] * v * r.uniform(0.93, 1.03)
    stems['tom'] = tomb

    # ---- 4. church bell on 1 (sparse: every 4 bars, phrase heads strongest)
    bellb = np.zeros(n)
    for b, g in [(1, 1.0), (5, 0.55), (9, 1.0), (13, 0.6)]:
        y = bell(220.0, 7.0, r, bright=0.7, decay=0.9, strike=0.15)
        y = bw(y, 'lowpass', 2000, 2)
        a = ns(bt(b))
        k = min(len(y), n - a)
        bellb[a:a + k] += y[:k] * g
    stems['bell'] = bellb

    # ---- 5. lead: harmonica quotes the title hook (E A C B) in phrase 1, whistle in phrase 2
    p1 = [(1.0, 1.5, 76), (2.5, 0.5, 81), (3.0, 2.5, 84), (5.5, 2.5, 83),
          (8.5, 1.0, 81), (9.5, 0.5, 83), (10.0, 1.5, 84), (11.5, 0.5, 83), (12.0, 3.4, 80)]
    p2 = [(1.0, 1.5, 76), (2.5, 0.5, 81), (3.0, 2.5, 84), (5.5, 0.5, 83), (6.0, 2.0, 81),
          (8.0, 1.5, 86), (9.5, 0.5, 84), (10.0, 2.0, 83), (12.0, 1.0, 83), (13.0, 3.0, 80)]
    h = voice_line([(b * BEAT + hum(r, 8), d * BEAT * 0.97, mi, 0.8) for b, d, mi in p1], n, r,
                   'harmonica', vib_depth=0.14, scoop=0.7, att_ms=70, rel_ms=260, fall=0.5)
    h = eq(h, [('hp', 300, 0.7, 0), ('peak', 2700, 0.8, -8.0), ('lp', 2600, 0.7, 0)])
    w = voice_line([(bt(9, b) + hum(r, 8), d * BEAT * 0.97, mi, 0.8) for b, d, mi in p2], n, r,
                   'whistle', vib_depth=0.22, scoop=0.6, fall=0.7, bright=0.6)
    w = eq(w, [('peak', 3000, 0.8, -6.0)])
    stems['harmonica'] = h
    stems['whistle'] = w

    # ---- 6. rattlesnake shaker: dotted 8th pattern + sizzle swells at phrase ends
    sh = np.zeros(n)
    for b in range(1, NBARS + 1):
        for beat, v in [(0, 0.9), (0.75, 0.55), (1.0, 0.7), (1.5, 0.4), (2.0, 0.85), (2.75, 0.55),
                        (3.0, 0.7), (3.5, 0.45)]:
            g = shaker_grain(r, v * r.uniform(0.85, 1.1), dur=0.08, lo=5000, hi=11000)
            a = ns(bt(b, beat) + hum(r, 5)) % L
            k = min(len(g), n - a)
            sh[a:a + k] += g[:k]
    for b0, nb in [(7, 2), (15, 2)]:
        rz = rattle(nb * BAR, r, rate=52, lo=4800, hi=10000)
        rz *= np.linspace(0.2, 1.0, len(rz)) ** 2
        a = ns(bt(b0))
        sh[a:a + len(rz)] += 1.4 * rz
    stems['shaker'] = sh

    # ---- balance (stem loudness, dry), pan, sends
    targets = {'twang': -19.5, 'strings': -24.0, 'tom': -22.5, 'bell': -25.5, 'harmonica': -20.5,
               'whistle': -20.0, 'shaker': -29.0}
    pans = {'twang': -0.18, 'strings': 0.0, 'tom': 0.0, 'bell': 0.3, 'harmonica': -0.28,
            'whistle': 0.22, 'shaker': 0.4}
    sends = {'twang': 0.30, 'strings': 0.40, 'tom': 0.55, 'bell': 1.1, 'harmonica': 0.55,
             'whistle': 0.5, 'shaker': 0.25}
    # every stem is folded to one loop period first; everything after is circular
    dry = np.zeros((2, L))
    wet_in = np.zeros((2, L))
    for k, x in stems.items():
        x = x if len(x) == L else wrap_tail(pad_to(x, max(n, len(x)))[None, :], L)[0]
        x = stem_norm(x, targets[k])
        stems[k] = x
        if os.environ.get('PN_STEMS'):                # debug: dump balanced dry stems
            os.makedirs(os.environ['PN_STEMS'], exist_ok=True)
            np.save(os.path.join(os.environ['PN_STEMS'], k + '.npy'), x.astype(np.float32))
        if k == 'strings':                   # natural width: decorrelated twin, mono-safe (no polarity tricks)
            x2 = np.roll(x, ns(0.011))
            st = np.stack([0.8 * x + 0.2 * x2, 0.8 * x2 + 0.2 * x])
        else:
            st = pan(x, pans[k])
        dry += st
        wet_in += st * sends[k]
    tw_spring = convolve_circular(pan(stems['twang'], pans['twang']), spring)
    mix = dry + 0.55 * convolve_circular(wet_in, ir) + 0.35 * tw_spring
    mix = eq(mix, [('peak', 3000, 0.7, -2.5)], circular=True)
    return master(mix, True, -16.0)          # clean master: no hiss / tape drive / 12.5 kHz tape LP


def finish_tp(x, target, tp_db=-1.2, circular=False):
    """Loudness + true-peak finish: re-limit on 4x oversampled peaks until TP <= tp_db."""
    from scipy.signal import resample_poly
    for _ in range(6):
        x = x * db2a(target - lufs(x))
        tp = peak(resample_poly(x, 4, 1, axis=1))
        if tp <= db2a(tp_db):
            break
        x = limiter(x, db2a(tp_db - 0.4) * peak(x) / tp, circular=circular)
    return x


# ------------------------------------------------------------------ HOLD
def music_duel_hold(seed=707, secs=20.0):
    r = rng(seed)
    L = ns(secs)
    t = tt(L)
    ph_dis = np.sin(np.pi * t / secs) ** 2          # 0 at loop start/end, 1 at 10 s
    # drone: A1 + A2 + E3 saw stack, very dark, slow breathing
    breath = 0.8 + 0.2 * np.sin(2 * np.pi * 2 * t / secs - 1.2)
    dr = np.zeros(L)
    for mi, g in [(33, 1.0), (45, 0.7), (52, 0.35)]:
        f = mtof(mi) * (1 + 0.001 * smooth_curve(L, 0.1, r))
        dr += g * saw_stack(f, r, fmax=1800, L=L)
    dr = tv_lowpass_periodic(dr, 260 + 320 * ph_dis, 0.7) * breath
    dr = bw(dr, 'highpass', 35, 2, circular=True)
    # one sustained high string harmonic (A5) with slow vibrato ...
    vib_rate = 4.0 / secs * 18                       # 3.6 Hz, integer cycles per loop
    vib = (0.05 + 0.10 * ph_dis) * np.sin(2 * np.pi * vib_rate * t)

    def harmonic_tone(midi_curve, amp_curve, seed_off):
        rr = rng(seed + seed_off)
        f = periodic_freq(mtof(midi_curve), L)
        ph = 2 * np.pi * np.concatenate([[0.0], np.cumsum(f[:-1])]) / SR
        y = np.sin(ph) + 0.06 * np.sin(2 * ph + 1) + 0.02 * np.sin(3 * ph + 2)
        bow = shaped_noise(L, rr, band_mag(800, 2400, 2))
        bow /= np.std(bow)
        y = y * (1 + 0.04 * smooth_curve(L, 3.0, rr)) + 0.015 * bow
        return y * amp_curve

    hi = harmonic_tone(81 + vib, 0.85 + 0.15 * breath, 1)
    # ... and the rub: Bb5 creeps in from nothing, sharpening from a quarter tone to a full
    # minor 2nd at 10 s (beating speeds up), then retreats.
    rub_pitch = 81 + 0.5 + 0.5 * ph_dis + 0.6 * vib
    rub = harmonic_tone(rub_pitch, ph_dis ** 1.3, 2)
    # low minor-2nd shadow in the drone at the peak (Bb1 against A1)
    sh = saw_stack(mtof(34) * np.ones(L), r, fmax=900, L=L) * ph_dis ** 2
    sh = bw(sh, 'lowpass', 400, 2, circular=True)
    # (clean-production pass: the 5-9 kHz "air" noise bed that sat here was removed; its RNG draw
    # is kept so the reverb seed stream is unchanged)
    shaped_noise(L, r, band_mag(5000, 9000, 2))
    stems = {'drone': (dr, -25.0, 0.0), 'hi': (hi, -27.0, -0.15), 'rub': (rub, -28.5, 0.2),
             'shadow': (sh, -31.0, 0.0)}
    ir = musical_ir(r, t60=4.0, predelay=0.05, hi_cut=6000)
    dry = np.zeros((2, L))
    for k, (x, tg, p) in stems.items():
        dry += pan(stem_norm(x, tg), p)
    mix = dry + 0.6 * convolve_circular(dry, ir)            # (tape wow removed)
    return master(mix, True, -20.0)


# ------------------------------------------------------------------ HEARTBEAT v3
def heartbeat_v3(seed=35, beats=8, bpm=92.0):
    """Close, physical lub-dub: 45 Hz sub thump + 120-180 Hz body knock + chest-wall thud,
    saturated; per-beat micro-jitter in timing, level and lub-dub spacing."""
    r = rng(seed)
    per = 60.0 / bpm
    L = ns(per * beats)
    y = np.zeros(L + ns(1.0))
    for b in range(beats):
        a = ns(b * per + 0.15 + r.normal(0, 0.006))
        v = db2a(r.normal(0, 0.8))
        gap = 0.255 + r.normal(0, 0.008)
        for off, sub_f, body_f, g, tau in [(0.0, 45.0, 150.0, 1.0, 0.075), (gap, 52.0, 175.0, 0.68, 0.055)]:
            k = ns(0.5)
            tl = tt(k)
            f = sub_f * (1 + 0.6 * np.exp(-tl / 0.018))
            sub = np.sin(phase_from_freq(f)) * np.minimum(1, tl / 0.004) * np.exp(-tl / tau)
            fb = body_f * (1 + 0.25 * np.exp(-tl / 0.01))
            body = np.sin(phase_from_freq(fb) + 0.5) * np.minimum(1, tl / 0.002) * np.exp(-tl / (tau * 0.45))
            thud = bw(r.standard_normal(k), 'bandpass', (90, 420), 2) * np.exp(-tl / 0.025)
            s = 1.0 * sub + 0.55 * body + 0.35 * thud / (np.std(thud[:ns(0.03)]) * 4 + 1e-9)
            kk = a + ns(off)
            y[kk:kk + k] += s * g * v
    y = wrap_tail(y[None, :], L)[0]
    y = sat(y / peak(y) * 1.8, 1.6)                       # saturation -> harmonics for small speakers
    y = eq(y, [('peak', 150, 0.9, 3.5), ('peak', 60, 1.0, 2.0), ('lp', 900, 0.7, 0),
               ('hp', 26, 0.7, 0)], circular=True)
    x = finish_tp(np.stack([y, y]), -12.0, -1.6, circular=True)
    return x - x.mean(axis=1, keepdims=True)                # exact DC removal over the loop period


# ------------------------------------------------------------------ RESULT TAIL
def music_duel_result(seed=808):
    r = rng(seed)
    m = Mix(6.0, 0.0, loop=False)
    n = m.n
    # baritone A strum (A2 E3 A3) + nylon open-fifth arpeggio with high A/E: resolves win or lose
    tw = np.zeros(n)
    for i, mi in enumerate([33, 45, 52, 57]):
        y = twang(mi, 5.5, r, 0.95 - 0.08 * i)
        a = ns(0.02 * i)
        tw[a:a + len(y)] += y[:n - a]
    tw = twang_amp(tw)
    trem = 1 - 0.35 * (0.5 + 0.5 * np.cos(2 * np.pi * SIXTEENTH_HZ * tt(n))) * np.exp(-tt(n) / 2.0)
    tw *= trem
    ny = np.zeros(n)
    for i, mi in enumerate([57, 64, 69, 76, 81]):
        y = nylon(mi, 5.0, r, 0.7 - 0.06 * i)
        a = ns(0.35 + 0.11 * i)
        k = min(len(y), n - a)
        ny[a:a + k] += y[:k]
    ny = np.convolve(ny, body_ir(r))[:n]
    bl = bw(bell(220.0, 6.0, r, bright=0.8, strike=0.2), 'lowpass', 3000, 2)
    tm = bw(tom(55, 2.0, r, bend=0.4, decay=0.5, noise=0.3), 'lowpass', 900, 2)
    # strings release E -> A into the chord
    k = n
    tl = tt(k)
    st = np.zeros(n)
    for mi, g in [(45, 1.0), (52, 0.7), (57, 0.6), (64, 0.45)]:
        f = mtof(mi) * np.ones(k)
        st += g * saw_stack(f, r, fmax=2500)
    st = bw(st, 'lowpass', 1400, 2) * (1 - np.exp(-tl / 0.25)) * np.exp(-tl / 1.6)
    stems = [(tw, -19.0, -0.15, 0.4), (ny, -23.0, 0.25, 0.4), (pad_to(bl, n), -24.0, 0.3, 1.0),
             (pad_to(tm, n), -22.0, 0.0, 0.6), (st, -25.0, 0.0, 0.5)]
    ir = musical_ir(r, t60=2.6, predelay=0.03)
    spring = spring_ir(r)
    dry = np.zeros((2, n))
    wet = np.zeros((2, n))
    for x, tg, p, s in stems:
        st2 = pan(stem_norm(x, tg), p)
        dry += st2
        wet += s * st2
    mix = dry + 0.5 * convolve(wet, ir, n) + 0.2 * convolve(pan(stem_norm(tw, -19), -0.15), spring, n)
    x = mix * ramp_out(n, 1800) ** 1.5
    x[:, :ns(0.003)] *= np.linspace(0, 1, ns(0.003))
    return master(x, False, -16.0)          # clean master (no tape drive / hiss / wow)


# ------------------------------------------------------------------ TICK SUB
def sfx_tick_sub(seed=74):
    """Very low 250 ms thump (55 -> 42 Hz) for weight under each countdown toll."""
    n = ns(0.25)
    tl = tt(n)
    f = 42.0 + 13.0 * np.exp(-tl / 0.05)
    y = np.sin(phase_from_freq(f)) * np.minimum(1, tl / 0.003) * np.exp(-tl / 0.075)
    y = sat(y * 1.3, 1.1)
    y = bw(y, 'lowpass', 180, 2)
    y = bw(y, 'highpass', 22, 2)                     # removes the DC of the pitch-dropping half-cycle
    y = y * ramp_out(n, 60)
    y[0] = 0.0
    y = y / peak(y) * db2a(-1.0)
    return y


def sfx_heartbeat(seed=36):
    """One lub-dub of the hold heartbeat (same voice as Loops/heartbeat.ogg), as a one-shot so the
    game can fire each beat itself and drive an accelerating, irregular rhythm."""
    r = rng(seed)
    L = ns(0.85)
    y = np.zeros(L)
    gap = 0.235
    for off, sub_f, body_f, g, tau in [(0.0, 45.0, 150.0, 1.0, 0.075), (gap, 52.0, 175.0, 0.68, 0.055)]:
        k = ns(0.5)
        tl = tt(k)
        f = sub_f * (1 + 0.6 * np.exp(-tl / 0.018))
        sub = np.sin(phase_from_freq(f)) * np.minimum(1, tl / 0.004) * np.exp(-tl / tau)
        fb = body_f * (1 + 0.25 * np.exp(-tl / 0.01))
        body = np.sin(phase_from_freq(fb) + 0.5) * np.minimum(1, tl / 0.002) * np.exp(-tl / (tau * 0.45))
        thud = bw(r.standard_normal(k), 'bandpass', (90, 420), 2) * np.exp(-tl / 0.025)
        sig = 1.0 * sub + 0.55 * body + 0.35 * thud / (np.std(thud[:ns(0.03)]) * 4 + 1e-9)
        kk = ns(0.004) + ns(off)
        n = min(k, L - kk)
        y[kk:kk + n] += sig[:n] * g
    y = sat(y / peak(y) * 1.8, 1.6)
    y = eq(y, [('peak', 150, 0.9, 3.5), ('peak', 60, 1.0, 2.0), ('lp', 900, 0.7, 0), ('hp', 26, 0.7, 0)])
    y = bw(y, 'highpass', 22, 2)
    y = y * ramp_out(len(y), 120)
    y[0] = 0.0
    return y / peak(y) * db2a(-1.0)


OGGS = {'music_duel_a': ('Music', music_duel_a), 'music_duel_hold': ('Music', music_duel_hold),
        'music_duel_result': ('Music', music_duel_result), 'heartbeat': ('Loops', heartbeat_v3)}


def main(names=None):
    for name, (sub, fn) in OGGS.items():
        if names and name not in names:
            continue
        x = fn()
        write_ogg(os.path.join(AUDIO, sub, name + '.ogg'), x, q=8)
        print(f'{name}: {x.shape[-1] / SR:.2f}s lufs={lufs(x):.1f} peak={a2db(peak(x)):.2f}dB')
    if not names or 'sfx_tick_sub' in names:
        x = sfx_tick_sub()
        write_wav16(os.path.join(AUDIO, 'SFX', 'sfx_tick_sub.wav'), x)
        print(f'sfx_tick_sub: {len(x) / SR:.2f}s peak={a2db(peak(x)):.2f}dB')
    if not names or 'sfx_heartbeat' in names:
        x = sfx_heartbeat()
        write_wav16(os.path.join(AUDIO, 'SFX', 'sfx_heartbeat.wav'), x)
        print(f'sfx_heartbeat: {len(x) / SR:.2f}s peak={a2db(peak(x)):.2f}dB')


if __name__ == '__main__':
    main(sys.argv[1:])
