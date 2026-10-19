"""pn_inst.py - synthesized instruments for Paw Noon (all original, numpy/scipy only).

Instruments
- ks_string     : Karplus-Strong plucked string (block-vectorised, fractional delay tuning)
- nylon / twang : guitar notes built on ks_string (pick noise, body or amp/pickup colour)
- voice_line    : continuous monophonic line with portamento, scoops, delayed vibrato;
                  timbres: whistle, harmonica, bowed (low drone/cello/bass)
- bell          : church/clock bell (additive inharmonic partials with beating doublets)
- anvil         : struck steel (inharmonic bar modes)
- harmonic_ping : guitar natural harmonic
- tom, shaker_grain, brush_swish, rattle
- body_ir, spring_ir : convolution colour for nylon guitar body and twang spring reverb
"""
import numpy as np

from pn_dsp import (SR, ns, tt, db2a, mtof, bw, filt, eq, sat, lp_noise, exp_dec, ramp_in,
                    ramp_out, phase_from_freq, tv_biquad)

# Clean-production pass: the breath / bow noise inside the whistle, harmonica and bowed voices is
# kept as part of the instrument tone (band-limited, amplitude-following) but trimmed 12 dB.
BREATH_TRIM = db2a(-12.0)


# ------------------------------------------------------------------ plucked strings
def ks_string(freq, dur, r, tap=0.15, t60=3.0, pick=0.18, exc_lp=0.4, vel=1.0, exc_len=None):
    n = ns(dur)
    D = SR / freq
    N = int(np.floor(D - 1.0))
    fr = (D - 1.0) - N
    h0 = np.array([tap, 1 - 2 * tap, tap, 0.0])
    h1 = np.array([0.0, tap, 1 - 2 * tap, tap])
    c = ((1 - fr) * h0 + fr * h1) * 10 ** (-3.0 / (t60 * freq))
    L = exc_len or (N + 1)
    e = r.uniform(-1, 1, L)
    # velocity -> brightness of the excitation
    p = np.clip(exc_lp + 0.35 * (1 - vel), 0.0, 0.95)
    e = _onepole(e, p)
    P = max(1, int(pick * (N + 1)))
    e = e - np.concatenate([np.zeros(P), e[:-P]])
    e -= e.mean()
    e /= np.max(np.abs(e)) + 1e-12
    x = np.zeros(n)
    x[:min(L, n)] = e[:min(L, n)] * vel
    off = N + 3
    y = np.zeros(n + off)
    for s in range(0, n, N):
        t = min(s + N, n)
        acc = x[s:t].copy()
        for j in range(4):
            acc += c[j] * y[off + s - N - j: off + t - N - j]
        y[off + s: off + t] = acc
    return y[off:]


def _onepole(x, p):
    from scipy.signal import lfilter
    return lfilter([1 - p], [1, -p], x)


def pick_noise(n, r, level=0.25, bright=4000):
    k = ns(0.006)
    z = np.zeros(n)
    nz = bw(r.standard_normal(k), 'bandpass', (bright * 0.4, min(bright * 2.5, 20000)), 2)
    z[:k] = nz * np.exp(-np.arange(k) / (k / 4)) * level
    return z


def nylon(midi, dur, r, vel=0.8, release_at=None, tone=0.5):
    """Nylon-string pluck (dry, before body IR). release_at: seconds -> finger damp."""
    f = float(mtof(midi))
    t60 = np.interp(midi, [40, 64, 84], [4.0, 2.4, 1.3])
    tap = 0.22 - 0.1 * tone * vel
    y = ks_string(f, dur, r, tap=tap, t60=t60, pick=0.13 + 0.05 * r.random(), exc_lp=0.45 - 0.3 * tone, vel=vel)
    y += pick_noise(len(y), r, 0.08 * vel, 3500)
    if release_at is not None and release_at < dur:
        k0 = ns(release_at)
        rel = np.ones(len(y))
        kk = ns(0.09)
        seg = np.exp(-np.arange(len(y) - k0) / max(1, kk / 4))
        rel[k0:] = seg
        y *= rel
    return y


def twang(midi, dur, r, vel=0.9, release_at=None):
    """Baritone electric pluck: bright KS, two slightly detuned polarisations."""
    f = float(mtof(midi))
    t60 = np.interp(midi, [28, 52, 76], [7.0, 4.5, 2.5])
    a = ks_string(f, dur, r, tap=0.06, t60=t60, pick=0.09, exc_lp=0.15, vel=vel)
    b = ks_string(f * 1.0009, dur, r, tap=0.08, t60=t60 * 0.8, pick=0.11, exc_lp=0.2, vel=vel)
    y = a + 0.6 * b
    y += pick_noise(len(y), r, 0.12 * vel, 5000)
    if release_at is not None and release_at < dur:
        k0 = ns(release_at)
        seg = np.ones(len(y))
        seg[k0:] = np.exp(-np.arange(len(y) - k0) / ns(0.03))
        y *= seg
    return y


def twang_amp(x, circular=False):
    """Pickup + small tube amp colour for the twang bus."""
    x = eq(x, [('hp', 70, 0.7, 0), ('peak', 180, 0.8, 2.5), ('peak', 1500, 1.0, 5.0),
               ('lp', 4200, 0.8, 0)], circular=circular)
    return sat(x * 1.6, 1.4) / 1.6


def body_ir(r, gain=1.0):
    """Short acoustic guitar body response (sum of damped modes + direct)."""
    n = ns(0.3)
    t = tt(n)
    ir = np.zeros(n)
    ir[0] = 1.0
    modes = [(100, 0.045, 7), (205, 0.030, 5), (250, 0.028, 4), (395, 0.020, 3),
             (560, 0.016, 2.0), (830, 0.012, 1.6), (1180, 0.008, 1.2), (2100, 0.005, 0.8)]
    for f, tau, g in modes:
        f *= 1 + 0.01 * r.standard_normal()
        a = g / (tau * SR / 2) * gain
        ir += a * np.sin(2 * np.pi * f * t + r.uniform(0, 1)) * np.exp(-t / tau)
    return ir


def spring_ir(r, length=2.6, rt=0.033, decay=0.82, wet_noise=0.5):
    """Spring tank: repeating dispersive chirps + diffuse noise. Returns (2, n)."""
    n = ns(length)
    out = []
    for ch in range(2):
        ir = np.zeros(n)
        k = ns(0.018 + 0.003 * ch)
        cl = ns(0.022)
        tc = tt(cl)
        f = 4200 * (180 / 4200) ** (tc / tc[-1])
        ch_sig = np.sin(phase_from_freq(f)) * np.hanning(cl)
        g = 1.0
        rt_c = rt * (1 + 0.07 * ch)
        while k + cl < n and g > 1e-3:
            ir[k:k + cl] += g * ch_sig * (1 if (k // 7) % 2 else -1)
            k += ns(rt_c * (1 + 0.04 * r.standard_normal()))
            g *= decay
        # diffuse part: darker (300-3500 Hz) and faded in over 40 ms so the spring has no early fizz
        nz = bw(r.standard_normal(n), 'bandpass', (300, 5000), 2) * np.exp(-tt(n) / (length / 6.9))
        nz = bw(nz, 'lowpass', 3500, 4) * (0.5 - 0.5 * np.cos(np.pi * np.clip(tt(n) / 0.04, 0, 1)))
        ir += wet_noise * nz / (np.sqrt(np.sum(nz ** 2)) + 1e-12) * np.sqrt(np.sum(ir ** 2))
        ir = bw(ir, 'highpass', 200, 1)
        out.append(ir / (np.sqrt(np.sum(ir ** 2)) + 1e-12))
    return np.stack(out)


def harmonic_ping(midi, dur, r, vel=0.6):
    f = float(mtof(midi))
    n = ns(dur)
    t = tt(n)
    env = ramp_in(n, 2.5) * np.exp(-t / (dur / 5.5))
    y = (np.sin(2 * np.pi * f * t) + 0.10 * np.sin(2 * np.pi * 2 * f * t) * np.exp(-t / 0.3)
         + 0.04 * np.sin(2 * np.pi * 3 * f * t) * np.exp(-t / 0.15))
    y = y * env * vel
    y += pick_noise(n, r, 0.03 * vel, 6000)
    return y


# ------------------------------------------------------------------ monophonic voice lines
def voice_line(notes, n, r, timbre='whistle', glide_ms=55.0, scoop=0.5, vib_rate=5.3,
               vib_depth=0.2, vib_delay=0.22, att_ms=28.0, rel_ms=90.0, fall=0.0,
               jitter=0.03, bright=1.0):
    """notes: list of (t0_sec, dur_sec, midi, vel). Returns mono signal of length n."""
    notes = sorted(notes, key=lambda z: z[0])
    t = tt(n)
    target = np.full(n, float(notes[0][2]))
    amp = np.zeros(n)
    vib = np.zeros(n)
    scoop_c = np.zeros(n)
    fall_c = np.zeros(n)
    prev_end = -10.0
    for i, (t0, d, m, v) in enumerate(notes):
        a, b = ns(t0), min(n, ns(t0 + d))
        nxt = ns(notes[i + 1][0]) if i + 1 < len(notes) else n
        target[a:nxt] = m
        # amplitude envelope of this note (max-combined -> natural legato dips)
        k = min(n, b + ns(rel_ms / 1000 * 4))
        tl = (np.arange(a, k) - a) / SR
        e = np.minimum(1.0, tl / (att_ms / 1000)) ** 1.5
        e = 0.5 - 0.5 * np.cos(np.pi * np.minimum(1.0, tl / (att_ms / 1000)))
        sustain = 1.0 - 0.10 * np.minimum(1.0, tl / max(d, 0.05))
        e = e * sustain
        rel_idx = tl > d
        e[rel_idx] *= np.exp(-(tl[rel_idx] - d) / (rel_ms / 1000))
        amp[a:k] = np.maximum(amp[a:k], e * v)
        legato = (t0 - prev_end) < 0.06
        if not legato and scoop:
            ks = np.arange(a, min(n, a + ns(0.25)))
            scoop_c[ks] -= scoop * np.exp(-(ks - a) / ns(0.045))
        if d > 0.45 and vib_depth:
            tv = (np.arange(a, k) - a) / SR
            dv = np.clip((tv - vib_delay) / 0.45, 0, 1) * vib_depth
            vib[a:k] = np.maximum(vib[a:k], dv)
        if fall and d > 0.3 and (i + 1 == len(notes) or notes[i + 1][0] - (t0 + d) > 0.1):
            ks = np.arange(max(a, b - ns(0.12)), min(n, b + ns(0.15)))
            fall_c[ks] -= fall * np.clip((ks - (b - ns(0.12))) / ns(0.27), 0, 1) ** 2
        prev_end = t0 + d
    # portamento: one-pole smoothing in pitch domain
    from scipy.signal import lfilter
    p = np.exp(-1.0 / (glide_ms / 1000 * SR / 3))
    pitch = lfilter([1 - p], [1, -p], target - target[0]) + target[0]
    rate = vib_rate * (1 + 0.04 * lp_noise(n, 0.5, r))
    vib_ph = phase_from_freq(rate)
    pitch = pitch + vib * np.sin(vib_ph) + scoop_c + fall_c + jitter * lp_noise(n, 6, r)
    f = mtof(pitch)
    ph = phase_from_freq(f)
    amp = amp * (1 + 0.035 * lp_noise(n, 12, r))
    if timbre == 'whistle':
        tone = np.sin(ph) + 0.025 * np.sin(2 * ph) + 0.006 * np.sin(3 * ph)
        band = np.sin(ph + 0.7) * lp_noise(n, 140, r) * 0.10      # narrow noise around f0
        breath = bw(r.standard_normal(n), 'bandpass', (1800, 9000), 2) * 0.05 * bright * BREATH_TRIM
        att_breath = np.clip(np.gradient(amp) * SR / 40.0, 0, 1)
        y = amp * (tone + band) + breath * (amp ** 1.5 + 2.5 * att_breath)
    elif timbre == 'harmonica':
        y = np.zeros(n)
        for k in range(1, 26):
            fk = k * f
            w = (1.0 / k ** 0.75) * (1.0 + 2.2 * np.exp(-((fk - 1050) / 450) ** 2)
                                      + 1.4 * np.exp(-((fk - 2500) / 700) ** 2))
            w = w * (fk < 9000) * (1.0 + (0.35 if k % 2 else 0.0))
            y += w * np.sin(k * ph + 0.3 * k)
        y /= 6.0
        trem = 1 + 0.18 * np.sin(2 * np.pi * 5.6 * t)
        breath = bw(r.standard_normal(n), 'bandpass', (900, 6000), 2) * 0.06 * BREATH_TRIM
        y = amp * trem * (sat(y * 1.3, 1.2) + breath)
    elif timbre == 'bowed':
        y = np.zeros(n)
        fmean = float(np.median(f))
        for k in range(1, 48):
            if k * fmean > 7000:
                break
            fk = k * fmean
            w = (1.0 / k) * (1.0 + 1.6 * np.exp(-((fk - 280) / 160) ** 2)
                             + 0.9 * np.exp(-((fk - 900) / 350) ** 2)) / (1 + (fk / (1600 * bright)) ** 2)
            jit = 1 + 0.12 * lp_noise(n, 2.5, r)
            y += w * jit * np.sin(k * ph + r.uniform(0, 2 * np.pi))
        bow = bw(r.standard_normal(n), 'bandpass', (700, 4000), 2) * 0.02 * BREATH_TRIM
        y = amp * (y * 0.5 + bow)
    else:
        raise ValueError(timbre)
    return y


# ------------------------------------------------------------------ bells & metal
BELL_PARTIALS = [  # ratio to strike/prime, amp, t60 seconds
    (0.50, 0.45, 9.0), (1.00, 0.55, 6.0), (1.19, 0.50, 5.0), (1.50, 0.22, 4.0),
    (2.00, 0.70, 3.6), (2.51, 0.25, 2.6), (2.66, 0.20, 2.2), (3.01, 0.22, 1.8),
    (4.17, 0.14, 1.2), (5.43, 0.10, 0.8), (6.80, 0.07, 0.5), (8.20, 0.05, 0.35)]


def bell(freq, dur, r, bright=1.0, decay=1.0, partials=BELL_PARTIALS, strike=0.3):
    n = ns(dur)
    t = tt(n)
    y = np.zeros(n)
    for ratio, a, T in partials:
        f = freq * ratio
        if f > 0.45 * SR:
            continue
        aa = a * (bright ** np.log2(max(ratio, 0.5) + 1))
        beat = 0.15 + 0.6 * r.random()
        y += aa * np.exp(-t * 6.9 / (T * decay)) * (
            np.sin(2 * np.pi * f * t + r.uniform(0, 6.28)) +
            0.35 * np.sin(2 * np.pi * (f + beat) * t + r.uniform(0, 6.28)))
    k = ns(0.004)
    clk = np.zeros(n)
    clk[:k] = bw(r.standard_normal(k), 'highpass', 2500, 2) * np.exp(-np.arange(k) / (k / 5))
    return y + strike * clk * bright


def anvil(freq, dur, r, decay=1.0):
    parts = [(1.0, 1.0, 1.4), (2.32, 0.6, 0.9), (3.94, 0.45, 0.6), (5.61, 0.35, 0.4),
             (7.85, 0.25, 0.25), (10.2, 0.18, 0.16)]
    return bell(freq, dur, r, partials=parts, decay=decay, strike=0.5)


# ------------------------------------------------------------------ drums & shakers
def tom(f0, dur, r, bend=0.6, decay=0.45, noise=0.4):
    n = ns(dur)
    t = tt(n)
    f = f0 * (1 + bend * np.exp(-t / 0.035))
    ph = phase_from_freq(f)
    y = np.sin(ph) * np.exp(-t / decay) + 0.35 * np.sin(1.51 * ph + 1) * np.exp(-t / (decay * 0.4))
    y += 0.2 * np.sin(2.3 * ph) * np.exp(-t / (decay * 0.15))
    k = ns(0.05)
    nz = np.zeros(n)
    nz[:k] = bw(r.standard_normal(k), 'lowpass', 1800, 2) * np.exp(-np.arange(k) / ns(0.008))
    return y + noise * nz


def shaker_grain(r, vel=1.0, dur=0.09, lo=3500, hi=11000):
    n = ns(dur)
    t = tt(n)
    env = (1 - np.exp(-t / 0.004)) * np.exp(-t / 0.025)
    y = bw(r.standard_normal(n), 'bandpass', (lo, hi), 2) * env
    # a few seed ticks: band-limited to the shaker band (were single-sample clicks = crackle)
    ticks = np.zeros(n)
    for _ in range(4):
        k = int(r.integers(0, max(1, n // 2)))
        ticks[k] += r.uniform(-1, 1) * 0.5
    y = y + 2.0 * bw(ticks, 'bandpass', (lo, hi), 2)
    return y * vel


def brush_swish(r, vel=1.0, dur=0.32):
    n = ns(dur)
    t = tt(n)
    env = np.sin(np.pi * np.clip(t / dur, 0, 1)) ** 1.5 * np.exp(-t / 0.18)
    fc = 2500 + 2500 * np.sin(np.pi * t / dur)
    y = tv_biquad(r.standard_normal(n), 'bp', fc, 0.9) * env
    tap = np.zeros(n)
    k = ns(0.02)
    tap[:k] = bw(r.standard_normal(k), 'bandpass', (1500, 7000), 2) * np.exp(-np.arange(k) / ns(0.004))
    return (y + 0.6 * tap) * vel


def rattle(dur, r, rate=48.0, lo=3500, hi=9000):
    n = ns(dur)
    t = tt(n)
    am = (0.5 + 0.5 * np.sin(2 * np.pi * rate * t + 2 * lp_noise(n, 3, r))) ** 3
    y = bw(r.standard_normal(n), 'bandpass', (lo, hi), 2) * am
    return y * np.sin(np.pi * t / dur) ** 2


def bowed_cymbal(dur, r, base=2900.0):
    n = ns(dur)
    t = tt(n)
    y = np.zeros(n)
    ratios = [1.0, 1.37, 1.71, 2.13, 2.55, 2.94, 3.42]
    for i, q in enumerate(ratios):
        y += (0.7 ** i) * np.sin(2 * np.pi * base * q * t + r.uniform(0, 6.28)) * (1 + 0.3 * lp_noise(n, 2, r))
    y += 0.3 * bw(r.standard_normal(n), 'bandpass', (3000, 9000), 2)
    env = np.sin(np.pi * t / dur) ** 2 * (t / dur)
    return y * env
